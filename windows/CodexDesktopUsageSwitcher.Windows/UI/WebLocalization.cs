using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// Bridges the Localizer string table into the WebView2 pages. The script is injected before each
// page loads (AddScriptToExecuteDocumentCreatedAsync) so static [data-i18n] labels can localize on
// first paint; the same map is also posted with each payload so a runtime language switch re-applies
// without a reload.
internal static class WebLocalization
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string DocumentCreatedScript()
    {
        var json = JsonSerializer.Serialize(Localizer.Map(), JsonOptions);
        var lang = JsonSerializer.Serialize(Localizer.LanguageCode);
        return $"window.__I18N__ = {json}; window.__LANG__ = {lang};";
    }

    public static IReadOnlyDictionary<string, string> Strings() => Localizer.Map();

    public static string LanguageCode => Localizer.LanguageCode;
}
