using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class FileCacheStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly FileCacheStore _store;

    public FileCacheStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cache-store-test-" + Guid.NewGuid().ToString("N"));
        _store = new FileCacheStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static FileCacheRecord Sample(FileFingerprint fp) => new(
        FileCacheStore.CurrentSchemaVersion,
        fp,
        ParsedBytes: 4096,
        new[]
        {
            new CachedUsageEntry("msg-a:req-1", 1_700_000_000_000, "claude-opus-4", 4_000_000, 1_000_000, 110.25, "f#0"),
            new CachedUsageEntry("", 1_700_000_100_000, "gpt-5.5", 1234, 56, null, null),
        },
        ClaudeTurnSeq: 7,
        CodexState: new CodexStateDto("turn-1", new Dictionary<string, string> { ["turn-1"] = "gpt-5.5" }, "gpt-5.5"));

    [Fact]
    public void Save_then_TryLoad_roundtrips_record_including_record_struct_entries()
    {
        var fp = new FileFingerprint(@"C:\sessions\Rollout-A.jsonl", MtimeTicksUtc: 111, Size: 222);
        _store.Save(Sample(fp));

        Assert.True(_store.TryLoad(fp.FullPath, out var rec));
        Assert.Equal(fp, rec.Fingerprint);
        Assert.Equal(4096, rec.ParsedBytes);
        Assert.Equal(7, rec.ClaudeTurnSeq);
        Assert.Equal(2, rec.Entries.Count);
        Assert.Equal("msg-a:req-1", rec.Entries[0].DedupKey);
        Assert.Equal(110.25, rec.Entries[0].CostUsd!.Value, 6);
        Assert.Equal("gpt-5.5", rec.Entries[1].Model);
        Assert.Null(rec.Entries[1].CostUsd);
        Assert.Null(rec.Entries[1].TurnKey);
        Assert.Equal("gpt-5.5", rec.CodexState!.TurnModels["turn-1"]);
    }

    [Fact]
    public void TryLoad_returns_stored_record_so_the_cache_layer_can_compare_fingerprints()
    {
        // The store does not enforce freshness; it returns the stored record (with its own
        // fingerprint) even after the source changed, so the cache layer can reuse it as the
        // append-delta base.
        var fp = new FileFingerprint(@"C:\sessions\s.jsonl", MtimeTicksUtc: 111, Size: 222);
        _store.Save(Sample(fp));

        Assert.True(_store.TryLoad(fp.FullPath, out var rec));
        Assert.Equal(111, rec.Fingerprint.MtimeTicksUtc);
        Assert.Equal(222, rec.Fingerprint.Size);
    }

    [Fact]
    public void TryLoad_deletes_and_misses_on_corruption()
    {
        var fp = new FileFingerprint(@"C:\sessions\s.jsonl", MtimeTicksUtc: 1, Size: 2);
        _store.Save(Sample(fp));
        var cacheFile = Directory.GetFiles(_dir, "*.json").Single();
        File.WriteAllText(cacheFile, "{ this is : not valid json");

        Assert.False(_store.TryLoad(fp.FullPath, out _));
        Assert.False(File.Exists(cacheFile)); // corrupt cache removed so the caller reparses
    }

    [Fact]
    public void TryLoad_misses_for_unknown_path()
        => Assert.False(_store.TryLoad(@"C:\nope.jsonl", out _));
}
