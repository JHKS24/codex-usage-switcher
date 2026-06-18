namespace CodexUsageSwitcher.Windows.Infrastructure;

internal sealed record SwitcherCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}
