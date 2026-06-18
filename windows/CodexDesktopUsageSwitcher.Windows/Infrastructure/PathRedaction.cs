using System.Text.RegularExpressions;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Strips local filesystem paths out of any text that could reach a log, an error message, or the
// UI. The user's home directory (which contains their account name) becomes "~", and any other
// absolute Windows path collapses to just its final segment. Used at every boundary that surfaces
// an exception message or a diagnostic so a username or directory layout is never disclosed.
internal static partial class PathRedaction
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        if (!string.IsNullOrEmpty(Home))
        {
            result = result.Replace(Home, "~", StringComparison.OrdinalIgnoreCase);
        }

        // Backstop: collapse any remaining absolute Windows path (e.g. C:\Program Files\...) to its
        // last segment so only a file/folder name — never the full local layout — can surface.
        return DrivePathRegex().Replace(result, match =>
        {
            var trimmed = match.Value.TrimEnd('\\', '/');
            var name = trimmed.Length == 0 ? match.Value : Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? match.Value : name;
        });
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s\r\n""'<>|]+")]
    private static partial Regex DrivePathRegex();
}
