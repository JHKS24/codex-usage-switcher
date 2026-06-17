using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal readonly record struct ClaudePkce(string Verifier, string State, string AuthorizeUrl);

internal readonly record struct ClaudeOAuthInput(string? Code, string? State, string? Error);

internal readonly record struct ClaudeExchangeResult(ClaudeCredentials? Credentials, string? Error);

// Drives the Claude OAuth login: builds a PKCE authorize URL, parses the pasted code, and exchanges
// it for credentials. Same endpoints, client id, redirect, and scopes as the original tool. The
// verifier/code never leave this exchange and are never logged.
internal sealed class ClaudeOAuthClient
{
    private const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string RedirectUri = "https://platform.claude.com/oauth/code/callback";
    private const string UserAgent = "ClaudeUsageBar/0.6";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IHttpJsonClient _http;

    public ClaudeOAuthClient(IHttpJsonClient http) => _http = http;

    public static ClaudePkce GeneratePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new ClaudePkce(verifier, state, BuildAuthorizeUrl(challenge, state));
    }

    public static string BuildAuthorizeUrl(string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = string.Join(' ', ClaudeCredentialStore.DefaultScopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var encoded = string.Join('&', query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return $"{AuthorizeEndpoint}?{encoded}";
    }

    // Extracts the code (and optional state) from whatever the user pasted: a bare code, a
    // "code#state" pair, or the full redirect URL. Rejects obvious mistakes (a command, a path).
    public static ClaudeOAuthInput ParseInput(string raw)
    {
        var value = (raw ?? "").Trim();
        if (!LooksLikeCode(value))
        {
            return new ClaudeOAuthInput(null, null, "paste only the OAuth code shown in the browser");
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return FromUrl(value);
        }

        var parts = value.Split('#', 2);
        return new ClaudeOAuthInput(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null, null);
    }

    public async Task<ClaudeExchangeResult> ExchangeAsync(string code, string state, string verifier, CancellationToken cancellationToken)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        });

        var response = await _http.SendAsync(
            new HttpJsonRequest(HttpMethod.Post, TokenEndpoint, [new("User-Agent", UserAgent)], body, Timeout),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return new ClaudeExchangeResult(null, ExchangeError(response));
        }

        var credentials = ClaudeToken.FromResponse(response.Body, fallback: null, DateTimeOffset.UtcNow);
        return credentials is null
            ? new ClaudeExchangeResult(null, "token response did not include an access token")
            : new ClaudeExchangeResult(credentials, null);
    }

    private static string ExchangeError(HttpJsonResponse response)
    {
        if (!response.TransportOk)
        {
            return "network error; check your connection and try again";
        }

        if (response.StatusCode == 429)
        {
            return "token endpoint rate limited; wait, then start a fresh login";
        }

        return response.Body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            ? "stale or expired code; start a fresh login and paste the new code once"
            : $"token exchange failed (HTTP {response.StatusCode})";
    }

    private static ClaudeOAuthInput FromUrl(string value)
    {
        var queryStart = value.IndexOf('?');
        if (queryStart < 0)
        {
            return new ClaudeOAuthInput(null, null, "redirect URL has no code");
        }

        string? code = null;
        string? state = null;
        foreach (var pair in value[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "code")
            {
                code = WebUtility.UrlDecode(kv[1]).Trim();
            }
            else if (kv[0] == "state")
            {
                state = WebUtility.UrlDecode(kv[1]).Trim();
            }
        }

        return code is null ? new ClaudeOAuthInput(null, null, "redirect URL has no code") : new ClaudeOAuthInput(code, state, null);
    }

    private static bool LooksLikeCode(string value)
    {
        if (value.Length < 20)
        {
            return false;
        }

        var lowered = value.ToLowerInvariant();
        if (lowered.Contains("codex-desktop-switch") || lowered.Contains("claude-login"))
        {
            return false;
        }

        return !value.StartsWith('/') && !value.StartsWith('~') && !value.Any(char.IsWhiteSpace);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
