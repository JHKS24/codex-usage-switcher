using System.Security.Cryptography;
using System.Text;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class ClaudeOAuthTests
{
    private sealed class FakeHttp(HttpJsonResponse response) : IHttpJsonClient
    {
        public HttpJsonRequest? Last { get; private set; }

        public Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(response);
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void GeneratePkce_embeds_the_sha256_challenge_and_state_in_the_url()
    {
        var pkce = ClaudeOAuthClient.GeneratePkce();

        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)));
        Assert.Contains($"code_challenge={expectedChallenge}", pkce.AuthorizeUrl);
        Assert.Contains($"state={pkce.State}", pkce.AuthorizeUrl);
        Assert.Contains("code_challenge_method=S256", pkce.AuthorizeUrl);
        Assert.Contains("https://claude.ai/oauth/authorize?", pkce.AuthorizeUrl);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyz", null)]
    [InlineData("abcdefghijklmnopqrst#mystate", "abcdefghijklmnopqrst", "mystate")]
    public void ParseInput_reads_bare_code_and_code_state(string raw, string code, string? state)
    {
        var parsed = ClaudeOAuthClient.ParseInput(raw);
        Assert.Null(parsed.Error);
        Assert.Equal(code, parsed.Code);
        Assert.Equal(state, parsed.State);
    }

    [Fact]
    public void ParseInput_reads_full_redirect_url()
    {
        var parsed = ClaudeOAuthClient.ParseInput("https://platform.claude.com/oauth/code/callback?code=the-long-code-value&state=xyz");
        Assert.Null(parsed.Error);
        Assert.Equal("the-long-code-value", parsed.Code);
        Assert.Equal("xyz", parsed.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("codex-desktop-switch claude-login now please")]
    [InlineData("/home/user/some/path/that/is/long/enough")]
    public void ParseInput_rejects_obvious_mistakes(string raw) =>
        Assert.NotNull(ClaudeOAuthClient.ParseInput(raw).Error);

    [Fact]
    public async Task ExchangeAsync_returns_credentials_on_success()
    {
        var http = new FakeHttp(new HttpJsonResponse(true, 200, """{"access_token":"AT","refresh_token":"RT","expires_in":3600}""", null));

        var result = await new ClaudeOAuthClient(http).ExchangeAsync("code", "state", "verifier", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("AT", result.Credentials!.Value.AccessToken);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", http.Last!.Value.Url);
    }

    [Fact]
    public async Task ExchangeAsync_classifies_invalid_grant_and_rate_limit()
    {
        var stale = await new ClaudeOAuthClient(new FakeHttp(new HttpJsonResponse(true, 400, """{"error":"invalid_grant"}""", null)))
            .ExchangeAsync("c", "s", "v", CancellationToken.None);
        Assert.Null(stale.Credentials);
        Assert.Contains("stale", stale.Error);

        var throttled = await new ClaudeOAuthClient(new FakeHttp(new HttpJsonResponse(true, 429, "", null)))
            .ExchangeAsync("c", "s", "v", CancellationToken.None);
        Assert.Contains("rate limited", throttled.Error);
    }
}
