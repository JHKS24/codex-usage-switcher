using System.Text.Json;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Single place that turns an OAuth token response into credentials, shared by the usage client's
// refresh and the login client's code exchange. expires_in becomes an absolute expiry; a missing
// refresh token or scope list falls back to the prior credentials. Returns null without an
// access_token. Never logs token values.
internal static class ClaudeToken
{
    public static ClaudeCredentials? FromResponse(string body, ClaudeCredentials? fallback, DateTimeOffset now)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        var accessToken = Str(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        long? expiresAt = Num(root, "expires_in") is { } seconds
            ? now.ToUnixTimeSeconds() + (long)seconds
            : fallback?.ExpiresAtEpoch;
        var scope = Str(root, "scope");
        var scopes = string.IsNullOrWhiteSpace(scope)
            ? fallback?.Scopes ?? ClaudeCredentialStore.DefaultScopes
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new ClaudeCredentials(accessToken, Str(root, "refresh_token") ?? fallback?.RefreshToken, expiresAt, scopes);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
