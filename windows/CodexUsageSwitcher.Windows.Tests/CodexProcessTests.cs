using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class CodexProcessTests
{
    private sealed class FakeLister(IReadOnlyList<CodexProcessRow>? rows) : IProcessLister
    {
        public IReadOnlyList<CodexProcessRow>? List() => rows;
    }

    [Theory]
    [InlineData("Codex.exe", @"C:\Users\me\AppData\Local\Programs\Codex\Codex.exe", true)]
    [InlineData("app-server.exe", "app-server --serve codex session", true)]
    [InlineData("app-server.exe", "app-server --serve something-else", false)] // generic name, not codex
    [InlineData("node.exe", "node codex app-server runner", true)] // marker in command
    [InlineData("Codex.exe", "browser_crashpad_handler --for Codex.exe", false)] // ignored helper
    [InlineData("Codex.exe", @"codex-desktop-switch use work", false)] // never match this tool
    [InlineData("notepad.exe", "notepad readme.txt", false)]
    public void Matcher_classifies_codex_processes(string name, string command, bool expected) =>
        Assert.Equal(expected, CodexProcessMatcher.IsCodexProcess(name, command));

    [Fact]
    public void FindRunning_returns_null_when_inspection_fails()
    {
        var probe = new CodexProcessProbe(new FakeLister(null), selfPid: 1);
        Assert.Null(probe.FindRunning());
    }

    [Fact]
    public void FindRunning_filters_to_codex_and_excludes_self()
    {
        var rows = new List<CodexProcessRow>
        {
            new(100, "Codex.exe", @"C:\Programs\Codex\Codex.exe"),
            new(200, "notepad.exe", "notepad"),
            new(300, "Codex.exe", "self codex-desktop-switch"), // ignored marker
            new(42, "Codex.exe", @"C:\Programs\Codex\Codex.exe"), // self pid
        };
        var probe = new CodexProcessProbe(new FakeLister(rows), selfPid: 42);

        var running = probe.FindRunning();

        Assert.NotNull(running);
        Assert.Single(running!);
        Assert.Equal(100, running![0].Pid);
    }

    [Fact]
    public async Task StopAsync_reports_inspection_failure_and_empty_cleanly()
    {
        var cannotInspect = await new CodexProcessProbe(new FakeLister(null), 1).StopAsync(0, CancellationToken.None);
        Assert.False(cannotInspect.Inspected);

        var nothingRunning = await new CodexProcessProbe(new FakeLister([]), 1).StopAsync(0, CancellationToken.None);
        Assert.True(nothingRunning.Inspected);
        Assert.Empty(nothingRunning.Remaining);
    }
}
