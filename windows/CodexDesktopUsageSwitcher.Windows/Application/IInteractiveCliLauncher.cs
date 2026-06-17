using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Application;

internal interface IInteractiveCliLauncher
{
    CommandOutcome LaunchClaudeLogin();
    CommandOutcome LaunchClaudeCodeLogin();
    CommandOutcome LaunchCodexLogin(string profile);
    CommandOutcome LaunchSaveCurrentProfile(string profile);
}
