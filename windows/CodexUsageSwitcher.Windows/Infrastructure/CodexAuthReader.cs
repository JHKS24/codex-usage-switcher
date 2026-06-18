using System.Security.Cryptography;
using System.Text.Json;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// The public identity of a Codex auth.json (email / plan / organization / account id) plus the
// access token needed ONLY for the immediate usage API call. SECURITY: AccessToken is for
// in-memory use by the caller's HTTPS request — never log/persist/surface it. ToString() is
// overridden to redact it so even an accidental interpolation can't leak it; only the public
// identity fields are ever shown in the UI.
internal sealed record CodexAuthSummary(
    string? Email,
    string? Plan,
    string? Organization,
    string? AccountId,
    string? AccessToken,
    string? Error)
{
    public override string ToString() =>
        $"CodexAuthSummary {{ Email = {Email}, Plan = {Plan}, Organization = {Organization}, " +
        $"AccountId = {AccountId}, AccessToken = {(AccessToken is null ? "null" : "[redacted]")}, Error = {Error} }}";
}

internal static class CodexAuthReader
{
    private const string OpenAiAuthClaim = "https://api.openai.com/auth";

    // Reads <homeDir>/auth.json. Missing file -> null; unreadable/garbled -> an Error summary.
    public static CodexAuthSummary? Read(string homeDir)
    {
        var path = Path.Combine(homeDir, SwitcherPaths.AuthName);
        if (!File.Exists(path))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CodexAuthSummary(null, null, null, null, null, "auth.json is unreadable");
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return new CodexAuthSummary(null, null, null, null, null, "auth.json is missing tokens");
        }

        var jwt = DecodeJwtPayload(GetString(tokens, "id_token"));
        var auth = jwt is { ValueKind: JsonValueKind.Object } j &&
                   j.TryGetProperty(OpenAiAuthClaim, out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;
        return new CodexAuthSummary(
            Email: jwt is { ValueKind: JsonValueKind.Object } je ? GetString(je, "email") : null,
            Plan: GetString(auth, "chatgpt_plan_type"),
            Organization: DefaultOrgTitle(auth),
            AccountId: GetString(tokens, "account_id"),
            AccessToken: GetString(tokens, "access_token"),
            Error: null);
    }

    // SHA-256 of a file for identity matching (which profile equals the live auth.json). A
    // concurrent rewrite -> null (no hash), never a crash.
    public static string? Sha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? DefaultOrgTitle(JsonElement auth)
    {
        if (auth.ValueKind != JsonValueKind.Object ||
            !auth.TryGetProperty("organizations", out var orgs) || orgs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? first = null;
        foreach (var org in orgs.EnumerateArray())
        {
            if (org.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            first ??= org;
            if (org.TryGetProperty("is_default", out var d) && d.ValueKind == JsonValueKind.True)
            {
                return GetString(org, "title");
            }
        }

        return first is { } f ? GetString(f, "title") : null;
    }

    private static JsonElement? DecodeJwtPayload(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
