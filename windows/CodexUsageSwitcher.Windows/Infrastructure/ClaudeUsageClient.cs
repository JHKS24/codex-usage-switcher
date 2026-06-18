using System.Text.Json;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Claude usage shaped to the JSON the app already consumes: authenticated + an optional error
// classification (login_required / rate_limited / network) + the five-hour and seven-day buckets
// (utilization is the USED percent, mirroring the Anthropic OAuth usage response).
internal readonly record struct ClaudeUsageResult(
    bool Authenticated,
    string? Error,
    double? FiveHourUtilization,
    string? FiveHourResetsAt,
    double? SevenDayUtilization,
    string? SevenDayResetsAt);

// Loads Claude credentials, refreshes them when near expiry, and fetches OAuth usage — same
// endpoints, headers, and error classification as the original tool. Access/refresh tokens are
// sent only on their respective HTTPS calls and never logged.
internal sealed class ClaudeUsageClient
{
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string UserAgent = "ClaudeUsageBar/0.6";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ClaudeCredentialStore _store;
    private readonly IHttpJsonClient _http;

    public ClaudeUsageClient(ClaudeCredentialStore store, IHttpJsonClient http)
    {
        _store = store;
        _http = http;
    }

    public async Task<ClaudeUsageResult> GetUsageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var loaded = _store.Load();
        if (loaded is null)
        {
            return LoggedOut("login_required");
        }

        var (credentials, refreshError) = await EnsureFreshAsync(loaded.Value, now, cancellationToken).ConfigureAwait(false);
        if (refreshError is not null)
        {
            return LoggedOut(refreshError);
        }

        if (credentials is null)
        {
            return LoggedOut("login_required");
        }

        var response = await _http.SendAsync(
            new HttpJsonRequest(HttpMethod.Get, UsageEndpoint,
            [
                new("Authorization", $"Bearer {credentials.Value.AccessToken}"),
                new("anthropic-beta", "oauth-2025-04-20"),
            ], null, Timeout),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            // Authenticated (we had credentials) but the usage fetch itself failed; classify why.
            return new ClaudeUsageResult(true, Classify(response), null, null, null, null);
        }

        return ParseUsage(response.Body);
    }

    private async Task<(ClaudeCredentials? Credentials, string? Error)> EnsureFreshAsync(
        ClaudeCredentials credentials, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiresSoon = credentials.ExpiresAtEpoch is { } epoch && epoch <= now.ToUnixTimeSeconds() + 120;
        if (!expiresSoon)
        {
            return (credentials, null);
        }

        if (string.IsNullOrEmpty(credentials.RefreshToken))
        {
            return (null, null); // can't refresh -> treated as logged out
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["client_id"] = ClientId,
            ["scope"] = string.Join(' ', credentials.Scopes),
        });

        var response = await _http.SendAsync(
            new HttpJsonRequest(HttpMethod.Post, TokenEndpoint,
                [new("User-Agent", UserAgent)], body, Timeout),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return (null, Classify(response));
        }

        var refreshed = ClaudeToken.FromResponse(response.Body, credentials, now);
        if (refreshed is null)
        {
            return (null, "login_required");
        }

        _store.Save(refreshed.Value);
        return (refreshed, null);
    }

    private static ClaudeUsageResult ParseUsage(string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ClaudeUsageResult(true, "network", null, null, null, null);
        }

        var (fiveUtil, fiveReset) = Bucket(root, "five_hour");
        var (sevenUtil, sevenReset) = Bucket(root, "seven_day");
        return new ClaudeUsageResult(true, null, fiveUtil, fiveReset, sevenUtil, sevenReset);
    }

    private static (double? Utilization, string? ResetsAt) Bucket(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (Num(bucket, "utilization"), Str(bucket, "resets_at"));
    }

    // Mirrors the original tool's failure classification: auth problems vs throttling vs everything
    // else (transport, 5xx, parse) which is treated as a transient network condition.
    private static string Classify(HttpJsonResponse response)
    {
        if (!response.TransportOk)
        {
            return "network";
        }

        if (response.StatusCode is 401 or 403 || response.Body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            return "login_required";
        }

        return response.StatusCode == 429 ? "rate_limited" : "network";
    }

    private static ClaudeUsageResult LoggedOut(string error) => new(false, error, null, null, null, null);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
