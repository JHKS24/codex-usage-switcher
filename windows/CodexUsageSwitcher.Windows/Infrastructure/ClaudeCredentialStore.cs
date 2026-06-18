using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Claude OAuth credentials, read from / written to the claude-usage-bar credentials.json.
// SECURITY: tokens are for in-memory use on the immediate HTTPS calls; ToString() redacts them so
// they can never leak through logging.
internal readonly record struct ClaudeCredentials(
    string AccessToken,
    string? RefreshToken,
    long? ExpiresAtEpoch,
    IReadOnlyList<string> Scopes)
{
    public override string ToString() =>
        "ClaudeCredentials { AccessToken = [redacted], " +
        $"RefreshToken = {(RefreshToken is null ? "null" : "[redacted]")}, ExpiresAtEpoch = {ExpiresAtEpoch} }}";
}

// Reads and writes the claude-usage-bar credentials.json (camelCase on disk). Writes are atomic
// (temp file + replace). Reading a missing/garbled file returns null rather than throwing, so a
// usage refresh degrades to "login required" instead of crashing.
internal sealed class ClaudeCredentialStore
{
    internal static readonly string[] DefaultScopes = ["user:profile", "user:inference"];

    private readonly string _path;

    public ClaudeCredentialStore(string path) => _path = path;

    public ClaudeCredentials? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var accessToken = Str(root, "accessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        return new ClaudeCredentials(
            accessToken,
            Str(root, "refreshToken"),
            ParseExpiry(root.TryGetProperty("expiresAt", out var e) ? e : default),
            ReadScopes(root));
    }

    public void Save(ClaudeCredentials credentials)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("credentials path has no directory");
        Directory.CreateDirectory(directory);

        var payload = new Dictionary<string, object?>
        {
            ["accessToken"] = credentials.AccessToken,
            ["refreshToken"] = credentials.RefreshToken,
            ["expiresAt"] = credentials.ExpiresAtEpoch is { } epoch
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                : null,
            ["scopes"] = credentials.Scopes,
        };

        var json = JsonSerializer.Serialize(payload);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static long? ParseExpiry(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return epoch;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.ToUnixTimeSeconds();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadScopes(JsonElement root)
    {
        if (!root.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
        {
            return DefaultScopes;
        }

        var list = scopes.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.String)
            .Select(s => s.GetString()!)
            .ToArray();
        return list.Length > 0 ? list : DefaultScopes;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex); // best-effort temp cleanup
        }
    }
}
