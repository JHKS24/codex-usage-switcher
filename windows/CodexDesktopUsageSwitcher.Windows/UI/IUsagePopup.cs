using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// Common surface the tray drives, implemented by both the WebView2 popup and the
// classic WinForms popup (the fallback when the WebView2 runtime is unavailable).
// Window exposes the underlying Form for positioning / show-hide / screen queries.
internal interface IUsagePopup : IDisposable
{
    event EventHandler? RefreshRequested;
    event EventHandler? SettingsRequested;
    event EventHandler? QuitRequested;
    event EventHandler? ClaudeUsageLoginRequested;
    event Func<string, Task>? SwitchProfileRequested;

    Form Window { get; }

    void Render(SwitcherSnapshot? snapshot, bool busy, string? error);
}
