using System.Text;
using CodexUsageSwitcher.Windows.Application;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class UsageHistoryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _claudeDir;
    private readonly string _codexHome;
    private readonly string _cacheRoot;

    public UsageHistoryServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "usage-history-test-" + Guid.NewGuid().ToString("N"));
        _claudeDir = Path.Combine(root, "claude");
        _codexHome = Path.Combine(root, "codex");
        _cacheRoot = Path.Combine(root, "cache"); // isolate the disk cache per test run
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_claudeDir);
        if (parent is not null && Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void LoadInsights_with_missing_dirs_returns_empty_series_without_throwing()
    {
        using var service = new UsageHistoryService(_claudeDir, _codexHome, _cacheRoot);

        var claude = service.LoadInsights(HistoryProvider.Claude, Now);
        var codex = service.LoadInsights(HistoryProvider.Codex, Now);

        // The calculator always emits a fixed-width, all-zero series for no data.
        Assert.All(new[] { claude, codex }, insights =>
        {
            Assert.Equal(InsightsCalculator.DailyDays, insights.Daily.Count);
            Assert.All(insights.Daily, d => Assert.Equal(0, d.TotalTokens));
            Assert.Empty(insights.ModelTurns);
        });
    }

    [Fact]
    public void LoadInsights_runs_claude_entries_through_the_calculator()
    {
        var projects = Path.Combine(_claudeDir, "projects", "demo");
        Directory.CreateDirectory(projects);
        var file = Path.Combine(projects, "s.jsonl");
        var line = "{" +
            "\"type\":\"assistant\",\"timestamp\":\"2026-06-16T11:00:00.000Z\",\"requestId\":\"r1\"," +
            "\"message\":{\"id\":\"m1\",\"model\":\"claude-opus-4\",\"usage\":{" +
            "\"input_tokens\":100,\"output_tokens\":40,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";
        File.WriteAllText(file, line + '\n', Encoding.UTF8);

        using var service = new UsageHistoryService(_claudeDir, _codexHome, _cacheRoot);
        var insights = service.LoadInsights(HistoryProvider.Claude, Now);

        // Today is the last daily bucket; the single 140-token event lands there.
        Assert.Equal(140, insights.Daily[^1].TotalTokens);
        Assert.Equal(140, insights.Daily[^1].ByModel["claude-opus-4"]);
    }

    [Fact]
    public void LoadInsights_returns_the_cached_instance_when_nothing_changed()
    {
        var projects = Path.Combine(_claudeDir, "projects", "demo");
        Directory.CreateDirectory(projects);
        var line = "{" +
            "\"type\":\"assistant\",\"timestamp\":\"2026-06-16T11:00:00.000Z\",\"requestId\":\"r1\"," +
            "\"message\":{\"id\":\"m1\",\"model\":\"claude-opus-4\",\"usage\":{" +
            "\"input_tokens\":100,\"output_tokens\":40,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";
        File.WriteAllText(Path.Combine(projects, "s.jsonl"), line + '\n', Encoding.UTF8);

        using var service = new UsageHistoryService(_claudeDir, _codexHome, _cacheRoot);
        var first = service.LoadInsights(HistoryProvider.Claude, Now);
        var second = service.LoadInsights(HistoryProvider.Claude, Now);

        // The freshness gate returns the same computed instance — no re-parse, no re-compute.
        Assert.Same(first, second);
    }
}
