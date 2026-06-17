using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class InsightsAggregatorTests
{
    private static FileCacheRecord Record(string path, params CachedUsageEntry[] entries) =>
        new(1, new FileFingerprint(path, 1, 1), 0, entries, 0, null);

    [Fact]
    public void Project_dedups_shared_keys_across_files_first_wins()
    {
        // Same key in two files (resume/fork). Records are in path order; the first wins.
        var r1 = Record(@"C:\a.jsonl", new CachedUsageEntry("k", 1000, "m1", 10, 5, null, null));
        var r2 = Record(@"C:\b.jsonl", new CachedUsageEntry("k", 2000, "m2", 99, 50, null, null));

        var entry = Assert.Single(InsightsAggregator.Project(new[] { r1, r2 }));
        Assert.Equal("m1", entry.Model);
        Assert.Equal(10, entry.TotalTokens);
    }

    [Fact]
    public void Project_never_dedups_empty_key_entries()
    {
        var record = Record(
            @"C:\a.jsonl",
            new CachedUsageEntry("", 1, "m", 10, 5, null, null),
            new CachedUsageEntry("", 2, "m", 10, 5, null, null));

        Assert.Equal(2, InsightsAggregator.Project(new[] { record }).Count);
    }

    [Fact]
    public void Project_preserves_first_occurrence_order()
    {
        var record = Record(
            @"C:\a.jsonl",
            new CachedUsageEntry("k1", 1, "m1", 1, 1, null, null),
            new CachedUsageEntry("k2", 2, "m2", 2, 2, null, null));

        var entries = InsightsAggregator.Project(new[] { record });
        Assert.Equal("m1", entries[0].Model);
        Assert.Equal("m2", entries[1].Model);
    }
}
