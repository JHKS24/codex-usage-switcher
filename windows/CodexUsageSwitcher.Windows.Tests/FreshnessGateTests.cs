using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Domain;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class FreshnessGateTests
{
    private static FileFingerprint F(string path, long mtime, long size) => new(path, mtime, size);

    [Fact]
    public void Revision_changes_when_a_file_grows()
        => Assert.NotEqual(
            FreshnessGate.Revision(new[] { F(@"C:\a.jsonl", 1, 100) }),
            FreshnessGate.Revision(new[] { F(@"C:\a.jsonl", 1, 200) }));

    [Fact]
    public void Revision_changes_when_a_file_is_added_or_removed()
        => Assert.NotEqual(
            FreshnessGate.Revision(new[] { F(@"C:\a.jsonl", 1, 100) }),
            FreshnessGate.Revision(new[] { F(@"C:\a.jsonl", 1, 100), F(@"C:\b.jsonl", 2, 50) }));

    [Fact]
    public void Revision_is_independent_of_enumeration_order()
        => Assert.Equal(
            FreshnessGate.Revision(new[] { F(@"C:\a.jsonl", 1, 100), F(@"C:\b.jsonl", 2, 50) }),
            FreshnessGate.Revision(new[] { F(@"C:\b.jsonl", 2, 50), F(@"C:\a.jsonl", 1, 100) }));

    [Fact]
    public void TryGet_hits_on_matching_revision_and_misses_otherwise()
    {
        var gate = new FreshnessGate();
        var insights = Empty();
        gate.Put(HistoryProvider.Claude, "rev1", insights);

        Assert.True(gate.TryGet(HistoryProvider.Claude, "rev1", out var hit));
        Assert.Same(insights, hit);
        Assert.False(gate.TryGet(HistoryProvider.Claude, "rev2", out _)); // revision moved on
        Assert.False(gate.TryGet(HistoryProvider.Codex, "rev1", out _));  // per-provider
    }

    private static UsageInsights Empty() => new(
        Array.Empty<DailyUsagePoint>(),
        Array.Empty<HourlyUsagePoint>(),
        Array.Empty<IReadOnlyList<long>>(),
        28,
        Array.Empty<ModelTurnStats>());
}
