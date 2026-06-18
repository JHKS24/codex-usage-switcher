using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.Tests;

// The process boundary is the one edge this suite mocks (guardrails T2);
// everything inside SwitcherService runs for real.
internal sealed class FakeSwitcherClient : ISwitcherClient
{
    private readonly Dictionary<string, SwitcherCommandResult> _responses = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = [];

    public FakeSwitcherClient Respond(string command, int exitCode, string stdout, string stderr = "")
    {
        _responses[command] = new SwitcherCommandResult(exitCode, stdout, stderr);
        return this;
    }

    public Task<SwitcherCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var command = arguments[0];
        Commands.Add(command);
        return Task.FromResult(
            _responses.TryGetValue(command, out var result)
                ? result
                : new SwitcherCommandResult(2, "", $"unknown command: {command}"));
    }
}
