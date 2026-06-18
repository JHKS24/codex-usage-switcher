namespace CodexUsageSwitcher.Windows.Infrastructure;

internal interface ISwitcherClient
{
    Task<SwitcherCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
