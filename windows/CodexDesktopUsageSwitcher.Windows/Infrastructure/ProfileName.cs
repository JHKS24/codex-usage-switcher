using System.Text.RegularExpressions;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Profile-name validation: must start with a letter/digit, then letters/digits/underscore/dash/
// dot, up to 64 chars. Matches the original tool's PROFILE_RE so existing profile directories
// remain valid, and bounds names used to build filesystem paths (no traversal / separators).
internal static partial class ProfileName
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? name) => !string.IsNullOrEmpty(name) && Pattern().IsMatch(name);
}
