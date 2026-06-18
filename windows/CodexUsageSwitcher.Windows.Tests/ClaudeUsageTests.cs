using System.Text;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class ClaudeUsageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly ClaudeCredentialStore _store;
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);

    public ClaudeUsageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claude-cred-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "credentials.json");
        _store = new ClaudeCredentialStore(_path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeHttp(params HttpJsonResponse[] responses) : IHttpJsonClient
    {
        private readonly Queue<HttpJsonResponse> _responses = new(responses);
        public List<HttpJsonRequest> Requests { get; } = [];

        public Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new HttpJsonResponse(false, 0, "", "unexpected call"));
        }
    }

    private static HttpJsonResponse Ok(string body) => new(true, 200, body, null);

    // --- ClaudeCredentialStore ---

    [Fact]
    public void Store_loads_camelCase_and_redacts_in_tostring()
    {
        File.WriteAllText(_path,
            """{"accessToken":"AT","refreshToken":"RT","expiresAt":"2026-06-17T09:00:00Z","scopes":["user:profile"]}""",
            Encoding.UTF8);

        var creds = _store.Load();

        Assert.NotNull(creds);
        Assert.Equal("AT", creds!.Value.AccessToken);
        Assert.Equal("RT", creds.Value.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), creds.Value.ExpiresAtEpoch);
        Assert.DoesNotContain("AT", creds.Value.ToString());
        Assert.Contains("[redacted]", creds.Value.ToString());
    }

    [Fact]
    public void Store_returns_null_for_missing_garbled_or_tokenless()
    {
        Assert.Null(_store.Load()); // missing
        File.WriteAllText(_path, "{ not json", Encoding.UTF8);
        Assert.Null(_store.Load()); // garbled
        File.WriteAllText(_path, """{"refreshToken":"RT"}""", Encoding.UTF8);
        Assert.Null(_store.Load()); // no accessToken
    }

    [Fact]
    public void Store_round_trips_via_save()
    {
        _store.Save(new ClaudeCredentials("AT", "RT", Now.ToUnixTimeSeconds(), ["user:profile", "user:inference"]));

        var creds = _store.Load();
        Assert.Equal("AT", creds!.Value.AccessToken);
        Assert.Equal(Now.ToUnixTimeSeconds(), creds.Value.ExpiresAtEpoch);
        Assert.Equal(2, creds.Value.Scopes.Count);
    }

    // --- ClaudeUsageClient ---

    [Fact]
    public async Task Usage_returns_login_required_without_credentials()
    {
        var result = await new ClaudeUsageClient(_store, new FakeHttp()).GetUsageAsync(Now, CancellationToken.None);
        Assert.False(result.Authenticated);
        Assert.Equal("login_required", result.Error);
    }

    [Fact]
    public async Task Usage_parses_buckets_with_valid_credentials()
    {
        _store.Save(new ClaudeCredentials("AT", "RT", null, ClaudeCredentialStore.DefaultScopes)); // null expiry => no refresh
        var http = new FakeHttp(Ok("""{"five_hour":{"utilization":30,"resets_at":"R5"},"seven_day":{"utilization":80,"resets_at":"R7"}}"""));

        var result = await new ClaudeUsageClient(_store, http).GetUsageAsync(Now, CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Null(result.Error);
        Assert.Equal(30, result.FiveHourUtilization);
        Assert.Equal("R5", result.FiveHourResetsAt);
        Assert.Equal(80, result.SevenDayUtilization);
        Assert.Single(http.Requests); // no refresh call
    }

    [Theory]
    [InlineData(401, "login_required")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "network")]
    public async Task Usage_classifies_http_failures_but_stays_authenticated(int status, string expected)
    {
        _store.Save(new ClaudeCredentials("AT", "RT", null, ClaudeCredentialStore.DefaultScopes));
        var http = new FakeHttp(new HttpJsonResponse(true, status, "", null));

        var result = await new ClaudeUsageClient(_store, http).GetUsageAsync(Now, CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task Usage_refreshes_near_expiry_then_calls_usage_with_new_token()
    {
        _store.Save(new ClaudeCredentials("OLD", "RT", Now.ToUnixTimeSeconds() + 60, ClaudeCredentialStore.DefaultScopes));
        var http = new FakeHttp(
            Ok("""{"access_token":"NEW","expires_in":3600}"""),         // refresh
            Ok("""{"five_hour":{"utilization":10,"resets_at":"R"}}"""));  // usage

        var result = await new ClaudeUsageClient(_store, http).GetUsageAsync(Now, CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Equal(10, result.FiveHourUtilization);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", http.Requests[0].Url);
        Assert.Contains(http.Requests[1].Headers, h => h.Key == "Authorization" && h.Value == "Bearer NEW");
        Assert.Equal("NEW", _store.Load()!.Value.AccessToken); // persisted
    }

    [Fact]
    public async Task Usage_logs_out_when_refresh_impossible_or_fails()
    {
        // near expiry, no refresh token -> cannot refresh
        _store.Save(new ClaudeCredentials("OLD", null, Now.ToUnixTimeSeconds() + 60, ClaudeCredentialStore.DefaultScopes));
        var result = await new ClaudeUsageClient(_store, new FakeHttp()).GetUsageAsync(Now, CancellationToken.None);
        Assert.False(result.Authenticated);
        Assert.Equal("login_required", result.Error);

        // near expiry, refresh endpoint says invalid_grant -> login required
        _store.Save(new ClaudeCredentials("OLD", "RT", Now.ToUnixTimeSeconds() + 60, ClaudeCredentialStore.DefaultScopes));
        var http = new FakeHttp(new HttpJsonResponse(true, 400, """{"error":"invalid_grant"}""", null));
        var failed = await new ClaudeUsageClient(_store, http).GetUsageAsync(Now, CancellationToken.None);
        Assert.False(failed.Authenticated);
        Assert.Equal("login_required", failed.Error);
    }
}
