using System.Globalization;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal readonly record struct LocalizedString(string En, string Ko);

internal enum AppLanguage
{
    En,
    Ko,
}

// Central UI string table — the single source of truth for user-facing text. The entries live in
// the generated Localizer.Strings.cs; this part holds the lookup logic, the current language, and a
// change event so open windows can re-render when the user switches language. Keys are
// dot-namespaced (common.*, tray.*, popup.*, dashboard.*, settings.*, login.*, error.*, usage.*).
internal static partial class Localizer
{
    private static AppLanguage _language = DetectSystemLanguage();

    public static event Action? LanguageChanged;

    public static AppLanguage Language => _language;

    public static string LanguageCode => _language == AppLanguage.Ko ? "ko" : "en";

    public static AppLanguage DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Ko
            : AppLanguage.En;

    public static AppLanguage Parse(string? code) =>
        string.Equals(code, "ko", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Ko : AppLanguage.En;

    public static void SetLanguage(AppLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;
        LanguageChanged?.Invoke();
    }

    // Localized string for a key. An unknown key returns the key itself — visible during
    // development, and never throws at runtime.
    public static string L(string key) =>
        Entries.TryGetValue(key, out var entry) ? Pick(entry) : key;

    // Localized format string applied to args (e.g. "Switched to {0}").
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key), args);

    // The key -> localized-string map for the current language, for injecting into the WebView
    // pages. Optionally filtered to a key prefix.
    public static IReadOnlyDictionary<string, string> Map(string? prefix = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Entries)
        {
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[key] = Pick(value);
            }
        }

        return result;
    }

    private static string Pick(LocalizedString entry) => _language == AppLanguage.Ko ? entry.Ko : entry.En;
}
