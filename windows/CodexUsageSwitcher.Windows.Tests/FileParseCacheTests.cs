using System.Globalization;
using System.Text;
using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class FileParseCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDir;
    private readonly string _projects;

    public FileParseCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "parse-cache-test-" + Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_root, "claude");
        _projects = Path.Combine(_configDir, "projects", "demo");
        Directory.CreateDirectory(_projects);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileParseCache NewCache(string? cacheDirName = null) =>
        new(ProviderParsers.Claude(), new FileCacheStore(Path.Combine(_root, cacheDirName ?? "cache")));

    [Fact]
    public void Unchanged_files_are_reused_in_proc_and_from_disk_on_a_fresh_cache()
    {
        var now = DateTimeOffset.UtcNow;
        WriteFile("a.jsonl", Usage("m1", Iso(now), 100, 40), Usage("m2", Iso(now), 200, 60));

        var cache = NewCache();
        var first = new RefreshStats();
        cache.Refresh(_configDir, now, first);
        Assert.Equal(1, first.Parsed);

        // Same process, unchanged file -> in-proc reuse, zero parsing.
        var second = new RefreshStats();
        cache.Refresh(_configDir, now, second);
        Assert.Equal(1, second.Reused);
        Assert.Equal(0, second.Parsed);

        // Fresh cache (simulated restart) over the same disk dir -> reused from disk.
        var afterRestart = new RefreshStats();
        NewCache().Refresh(_configDir, now, afterRestart);
        Assert.Equal(1, afterRestart.Reused);
        Assert.Equal(0, afterRestart.Parsed);
    }

    [Fact]
    public void Append_delta_produces_the_same_entries_as_a_full_parse()
    {
        var now = DateTimeOffset.UtcNow;
        var file = "session.jsonl";
        WriteFile(file, UserText(Iso(now)), Usage("m1", Iso(now), 100, 40));

        var cache = NewCache("delta-cache");
        cache.Refresh(_configDir, now, new RefreshStats());

        // Append a new turn + usage event (file already ends in '\n').
        AppendLines(file, UserText(Iso(now)), Usage("m2", Iso(now), 300, 90));
        var deltaStats = new RefreshStats();
        var deltaEntries = cache.Refresh(_configDir, now, deltaStats).Single().Entries;
        Assert.Equal(1, deltaStats.DeltaParsed);

        // A brand-new cache full-parsing the grown file must yield identical entries
        // (turn sequence carried across the append, byte-exact).
        var fullEntries = NewCache("full-cache").Refresh(_configDir, now, new RefreshStats()).Single().Entries;
        Assert.Equal(fullEntries, deltaEntries);
        Assert.Equal(2, deltaEntries.Count);
        Assert.NotEqual(deltaEntries[0].TurnKey, deltaEntries[1].TurnKey); // distinct turns
    }

    [Fact]
    public void Truncation_triggers_a_full_reparse()
    {
        var now = DateTimeOffset.UtcNow;
        var file = "t.jsonl";
        WriteFile(file, Usage("m1", Iso(now), 100, 40), Usage("m2", Iso(now), 200, 60));

        var cache = NewCache();
        cache.Refresh(_configDir, now, new RefreshStats());

        WriteFile(file, Usage("m3", Iso(now), 10, 5)); // shorter -> truncation/rewrite
        var stats = new RefreshStats();
        var entries = cache.Refresh(_configDir, now, stats).Single().Entries;

        Assert.Equal(1, stats.Parsed);
        Assert.Equal(0, stats.DeltaParsed);
        Assert.Single(entries);
        Assert.Equal(15, entries[0].TotalTokens);
    }

    [Fact]
    public void Old_mtime_file_is_skipped_only_when_its_newest_event_is_past_the_window()
    {
        var now = DateTimeOffset.UtcNow;

        // Restored-from-backup case: old mtime but RECENT content -> must be kept.
        WriteFile("recent.jsonl", Usage("r1", Iso(now.AddDays(-5)), 111, 11));
        SetOldMtime("recent.jsonl", now);

        // Genuinely old file: old mtime AND old content -> skipped.
        WriteFile("old.jsonl", Usage("o1", Iso(now.AddDays(-40)), 999, 99));
        SetOldMtime("old.jsonl", now);

        var stats = new RefreshStats();
        var records = NewCache().Refresh(_configDir, now, stats);

        Assert.Equal(1, stats.SkippedByCutoff);
        var entries = records.SelectMany(r => r.Entries).ToList();
        Assert.Single(entries);
        Assert.Equal(122, entries[0].TotalTokens); // the recent file (111+11), not the old one
    }

    [Fact]
    public void Removed_file_is_pruned_from_the_results()
    {
        var now = DateTimeOffset.UtcNow;
        WriteFile("keep.jsonl", Usage("k1", Iso(now), 10, 5));
        WriteFile("gone.jsonl", Usage("g1", Iso(now), 20, 10));

        var cache = NewCache();
        Assert.Equal(2, cache.Refresh(_configDir, now, new RefreshStats()).Count);

        File.Delete(Path.Combine(_projects, "gone.jsonl"));
        var stats = new RefreshStats();
        var records = cache.Refresh(_configDir, now, stats);

        Assert.Equal(1, stats.Removed);
        Assert.Single(records);
    }

    private void WriteFile(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_projects, name), string.Join('\n', lines) + '\n', Encoding.UTF8);

    private void AppendLines(string name, params string[] lines) =>
        File.AppendAllText(Path.Combine(_projects, name), string.Join('\n', lines) + '\n', Encoding.UTF8);

    private void SetOldMtime(string name, DateTimeOffset now) =>
        File.SetLastWriteTimeUtc(Path.Combine(_projects, name), now.AddDays(-40).UtcDateTime);

    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Usage(string id, string ts, long input, long output) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"requestId\":\"" + id + "\"," +
        "\"message\":{\"id\":\"" + id + "\",\"model\":\"claude-sonnet-4\",\"usage\":{" +
        "\"input_tokens\":" + input + ",\"output_tokens\":" + output +
        ",\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";

    private static string UserText(string ts) =>
        "{\"type\":\"user\",\"timestamp\":\"" + ts + "\",\"message\":{\"content\":\"hi\"}}";
}
