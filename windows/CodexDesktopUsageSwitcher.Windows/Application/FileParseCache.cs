using System.Collections.Concurrent;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;

namespace CodexDesktopUsageSwitcher.Windows.Application;

// Counters for one Refresh, for diagnostics and tests (observability).
internal sealed class RefreshStats
{
    public int Reused { get; set; }        // exact fingerprint hit (in-proc or disk)
    public int Parsed { get; set; }        // full parse (new / truncated / rewritten)
    public int DeltaParsed { get; set; }   // append-only delta parse
    public int SkippedByCutoff { get; set; } // never-cached file older than the window
    public int Removed { get; set; }       // source file deleted since last refresh
}

// One provider's incremental file cache: an in-proc map (survives within a process) over a
// per-file disk cache (survives restarts), keyed by source path. Refresh brings the cache in
// line with the live files and returns the in-scope records in deterministic path order so
// the aggregator's cross-file de-dup is reproducible.
//
// Per file: an exact (mtime+size) match is reused for free; a grown file is parsed as an
// append delta from where we last stopped; a shrunk/rewritten file is fully re-parsed; a
// never-cached file older than the mtime cutoff is tail-probed and skipped only if its newest
// event is past the data window; a removed file is pruned. Refresh is serialized
// per provider by the caller, but the map is concurrent as belt-and-suspenders.
internal sealed class FileParseCache
{
    // The widest calculator window is the 28-day heatmap; the mtime cutoff adds a 2-day margin
    // so sub-day mtime/clock skew can never push an in-window file past the boundary.
    private const int MtimeCutoffDays = 30;
    private const int EventWindowDays = 28;

    private readonly ProviderParser _parser;
    private readonly FileCacheStore _disk;
    private readonly ConcurrentDictionary<string, FileCacheRecord> _mem = new(StringComparer.OrdinalIgnoreCase);

    public FileParseCache(ProviderParser parser, FileCacheStore disk)
    {
        _parser = parser;
        _disk = disk;
    }

    // Live files for the provider, used by the freshness gate before deciding to refresh.
    public IReadOnlyList<FileFingerprint> Enumerate(string baseDir) => _parser.Enumerate(baseDir);

    public IReadOnlyList<FileCacheRecord> Refresh(string baseDir, DateTimeOffset now, RefreshStats stats)
        => Refresh(Enumerate(baseDir), now, stats);

    // Refresh against an already-enumerated file list so the caller (freshness gate) and the
    // parse share a single point-in-time enumeration (no TOCTOU between the revision and the data).
    public IReadOnlyList<FileCacheRecord> Refresh(IReadOnlyList<FileFingerprint> files, DateTimeOffset now, RefreshStats stats)
    {
        var mtimeCutoffTicks = now.UtcDateTime.AddDays(-MtimeCutoffDays).Ticks;
        var eventCutoffMs = now.AddDays(-EventWindowDays).ToUnixTimeMilliseconds();

        var ordered = files
            .OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<FileCacheRecord>(ordered.Count);
        foreach (var file in ordered)
        {
            live.Add(file.FullPath);
            var record = Resolve(file, mtimeCutoffTicks, eventCutoffMs, stats);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        PruneRemoved(live, stats);
        return records;
    }

    private FileCacheRecord? Resolve(FileFingerprint file, long mtimeCutoffTicks, long eventCutoffMs, RefreshStats stats)
    {
        if (TryGetFresh(file, out var fresh))
        {
            stats.Reused++;
            return fresh;
        }

        var prior = TryGetPrior(file.FullPath);
        if (prior is null && file.MtimeTicksUtc < mtimeCutoffTicks && IsBeyondWindow(file, eventCutoffMs))
        {
            stats.SkippedByCutoff++;
            return null;
        }

        var record = Parse(file, prior, stats);
        _mem[file.FullPath] = record;
        _disk.Save(record);
        return record;
    }

    private bool TryGetFresh(FileFingerprint file, out FileCacheRecord record)
    {
        if (_mem.TryGetValue(file.FullPath, out var mem) && mem.Fingerprint == file)
        {
            record = mem;
            return true;
        }

        if (_disk.TryLoad(file.FullPath, out var disk) && disk.Fingerprint == file)
        {
            _mem[file.FullPath] = disk;
            record = disk;
            return true;
        }

        record = null!;
        return false;
    }

    private FileCacheRecord? TryGetPrior(string path)
    {
        if (_mem.TryGetValue(path, out var mem))
        {
            return mem;
        }

        return _disk.TryLoad(path, out var disk) ? disk : null;
    }

    private FileCacheRecord Parse(FileFingerprint file, FileCacheRecord? prior, RefreshStats stats)
    {
        // Append: append-only JSONL grew past the prior size -> parse only the new tail from
        // the last consumed '\n'. Anything else (new, shrank/truncated, same-size rewrite) is a
        // full parse, which also overwrites the stale disk record on Save.
        if (prior is not null && file.Size > prior.Fingerprint.Size && prior.ParsedBytes <= file.Size)
        {
            stats.DeltaParsed++;
            return _parser.ParseDelta(file, prior);
        }

        stats.Parsed++;
        return _parser.ParseWhole(file);
    }

    private static bool IsBeyondWindow(FileFingerprint file, long eventCutoffMs)
    {
        // Trust the tail's newest event, not mtime. Unknown tail -> include conservatively.
        return TailProbe.NewestTimestampMs(file) is long ts && ts < eventCutoffMs;
    }

    private void PruneRemoved(HashSet<string> live, RefreshStats stats)
    {
        foreach (var path in _mem.Keys.ToList())
        {
            if (!live.Contains(path))
            {
                _mem.TryRemove(path, out _);
                _disk.Evict(path);
                stats.Removed++;
            }
        }
    }
}
