using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class CodexUsageClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeHttp(HttpJsonResponse response) : IHttpJsonClient
    {
        public HttpJsonRequest? Last { get; private set; }

        public Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(response);
        }
    }

    private static CodexAuthSummary Auth() => new("me@x.com", "plus", "Acme", "acc-1", "tok-secret", null);

    private static FakeHttp Ok(string body) => new(new HttpJsonResponse(true, 200, body, null));

    [Fact]
    public async Task Fetch_maps_remaining_percent_reset_and_plan()
    {
        const string body = """
        {"email":"server@x.com","plan_type":"pro","rate_limit":{
          "primary_window":{"used_percent":25,"reset_after_seconds":3600},
          "secondary_window":{"used_percent":90,"reset_after_seconds":7200}}}
        """;
        var http = Ok(body);

        var row = await new CodexUsageClient(http).FetchAsync("work", Auth(), Now, CancellationToken.None);

        Assert.Null(row.Error);
        Assert.Equal(75, row.FiveHourLeft); // 100 - 25
        Assert.Equal(10, row.WeeklyLeft); // 100 - 90
        Assert.Equal("pro", row.Plan); // server plan_type overrides auth plan
        Assert.Equal("server@x.com", row.Account);
        Assert.NotNull(row.FiveHourResetIso);
        Assert.NotNull(row.WeeklyResetIso);
    }

    [Fact]
    public async Task Fetch_accepts_camelCase_keys()
    {
        const string body = """
        {"rateLimit":{"primaryWindow":{"usedPercent":40,"resetAfterSeconds":60},
          "secondaryWindow":{"usedPercent":0}}}
        """;

        var row = await new CodexUsageClient(Ok(body)).FetchAsync("work", Auth(), Now, CancellationToken.None);

        Assert.Equal(60, row.FiveHourLeft);
        Assert.Equal(100, row.WeeklyLeft);
        Assert.Null(row.WeeklyResetIso); // no reset_after -> no reset timestamp
    }

    [Fact]
    public async Task Fetch_sends_bearer_and_account_headers()
    {
        var http = Ok("{}");

        await new CodexUsageClient(http).FetchAsync("work", Auth(), Now, CancellationToken.None);

        Assert.NotNull(http.Last);
        Assert.Contains(http.Last!.Value.Headers, h => h.Key == "Authorization" && h.Value == "Bearer tok-secret");
        Assert.Contains(http.Last!.Value.Headers, h => h.Key == "ChatGPT-Account-Id" && h.Value == "acc-1");
    }

    [Fact]
    public async Task Fetch_reports_expired_token_on_401()
    {
        var row = await new CodexUsageClient(new FakeHttp(new HttpJsonResponse(true, 401, "", null)))
            .FetchAsync("work", Auth(), Now, CancellationToken.None);
        Assert.Contains("expired", row.Error);
    }

    [Fact]
    public async Task Fetch_reports_status_code_on_server_error()
    {
        var row = await new CodexUsageClient(new FakeHttp(new HttpJsonResponse(true, 503, "", null)))
            .FetchAsync("work", Auth(), Now, CancellationToken.None);
        Assert.Contains("503", row.Error);
    }

    [Fact]
    public async Task Fetch_degrades_on_transport_failure()
    {
        var row = await new CodexUsageClient(new FakeHttp(new HttpJsonResponse(false, 0, "", "offline")))
            .FetchAsync("work", Auth(), Now, CancellationToken.None);
        Assert.Contains("failed", row.Error);
        Assert.Null(row.FiveHourLeft);
    }

    [Fact]
    public async Task Fetch_rejects_missing_credentials_without_calling_http()
    {
        var http = Ok("{}");
        var noToken = new CodexAuthSummary("me@x.com", "plus", "Acme", null, null, null);

        var row = await new CodexUsageClient(http).FetchAsync("work", noToken, Now, CancellationToken.None);

        Assert.Contains("missing auth tokens", row.Error);
        Assert.Null(http.Last); // never hit the network without a token
    }

    [Fact]
    public async Task Fetch_propagates_auth_read_error()
    {
        var broken = new CodexAuthSummary(null, null, null, null, null, "auth.json is unreadable");
        var row = await new CodexUsageClient(Ok("{}")).FetchAsync("work", broken, Now, CancellationToken.None);
        Assert.Contains("unreadable", row.Error);
    }
}
