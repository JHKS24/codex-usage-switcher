using System.Text.Json;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// One profile's Codex usage, shaped to the JSON the rest of the app already consumes
// (profile/account/plan/five_hour_left/.../error). Percentages are "remaining", rounded.
internal readonly record struct CodexUsageRow(
    string Profile,
    string Account,
    string? Plan,
    int? FiveHourLeft,
    string? FiveHourResetIso,
    int? WeeklyLeft,
    string? WeeklyResetIso,
    string? Error);

// Fetches Codex usage from the ChatGPT backend for a single profile's credentials. The access
// token is sent only as the Authorization header for this one request and is never logged.
// Mirrors the original tool's endpoint, headers, and primary/secondary window normalization.
internal sealed class CodexUsageClient
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string UserAgent = "codex_cli_rs/0.76.0 (Debian 13.0.0; x86_64) WindowsTerminal";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly IHttpJsonClient _http;

    public CodexUsageClient(IHttpJsonClient http) => _http = http;

    public async Task<CodexUsageRow> FetchAsync(string profile, CodexAuthSummary? auth, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = Empty(profile);
        if (auth is null)
        {
            return row with { Error = $"Profile {profile} is missing auth tokens. Log in to this profile." };
        }

        if (auth.Error is not null)
        {
            return row with { Error = $"Profile {profile} {auth.Error}." };
        }

        row = row with { Account = auth.Email ?? "unknown", Plan = auth.Plan };
        if (string.IsNullOrEmpty(auth.AccessToken) || string.IsNullOrEmpty(auth.AccountId))
        {
            return row with { Error = $"Profile {profile} is missing auth tokens. Log in to this profile." };
        }

        var response = await _http.SendAsync(
            new HttpJsonRequest(HttpMethod.Get, UsageUrl,
            [
                new("Authorization", $"Bearer {auth.AccessToken}"),
                new("ChatGPT-Account-Id", auth.AccountId),
                new("User-Agent", UserAgent),
            ], null, RequestTimeout),
            cancellationToken).ConfigureAwait(false);

        if (!response.TransportOk)
        {
            return row with { Error = $"Profile {profile} usage request failed: {response.TransportError}" };
        }

        if (response.StatusCode == 401)
        {
            return row with { Error = $"Profile {profile} token expired. Re-login this profile." };
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return row with { Error = $"Profile {profile} usage request failed with {response.StatusCode}." };
        }

        return Parse(profile, row, response.Body, now);
    }

    private static CodexUsageRow Parse(string profile, CodexUsageRow row, string body, DateTimeOffset now)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return row with { Error = $"Profile {profile} usage response was not valid JSON." };
        }

        var rate = Obj(root, "rate_limit") ?? Obj(root, "rateLimit");
        var primary = NormalizeWindow(Obj(rate, "primary_window") ?? Obj(rate, "primaryWindow"), now);
        var secondary = NormalizeWindow(Obj(rate, "secondary_window") ?? Obj(rate, "secondaryWindow"), now);
        return row with
        {
            Account = Str(root, "email") ?? row.Account,
            Plan = Str(root, "plan_type") ?? Str(root, "planType") ?? row.Plan,
            FiveHourLeft = primary?.RemainingRounded,
            FiveHourResetIso = primary?.ResetAtIso,
            WeeklyLeft = secondary?.RemainingRounded,
            WeeklyResetIso = secondary?.ResetAtIso,
            Error = null,
        };
    }

    private readonly record struct Window(int RemainingRounded, string? ResetAtIso);

    private static Window? NormalizeWindow(JsonElement? element, DateTimeOffset now)
    {
        if (element is not { ValueKind: JsonValueKind.Object } window)
        {
            return null;
        }

        var used = Math.Clamp(Num(window, "used_percent") ?? Num(window, "usedPercent") ?? 0, 0, 100);
        var remaining = (int)Math.Round(100 - used);
        var resetAfter = Num(window, "reset_after_seconds") ?? Num(window, "resetAfterSeconds");
        var resetAt = resetAfter is > 0
            ? now.ToUniversalTime().AddSeconds(resetAfter.Value).ToString("o")
            : null;
        return new Window(remaining, resetAt);
    }

    private static CodexUsageRow Empty(string profile) =>
        new(profile, "unknown", null, null, null, null, null, null);

    private static JsonElement? Obj(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
