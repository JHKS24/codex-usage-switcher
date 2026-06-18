using System.Globalization;
using System.Runtime.CompilerServices;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.Tests;

// Several tests assert on Korean UI strings and ko-KR-formatted reset times. The Localizer picks its
// language from the machine's UI culture, so on an English CI runner those assertions would otherwise
// fail. Pin the culture and the Localizer language once, before any test runs, so the suite is
// deterministic on every machine.
internal static class TestCultureInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var korean = new CultureInfo("ko-KR");
        CultureInfo.DefaultThreadCurrentCulture = korean;
        CultureInfo.DefaultThreadCurrentUICulture = korean;
        CultureInfo.CurrentCulture = korean;
        CultureInfo.CurrentUICulture = korean;
        Localizer.SetLanguage(AppLanguage.Ko);
    }
}
