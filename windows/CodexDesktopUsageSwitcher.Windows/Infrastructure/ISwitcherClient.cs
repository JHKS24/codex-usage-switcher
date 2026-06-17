namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal interface ISwitcherClient
{
    Task<SwitcherCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
