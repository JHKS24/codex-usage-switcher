using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Application;

// The primary freshness gate (verdict V6): a revision hash over the live file fingerprints is
// the sole signal for "did anything change". A user-initiated open always enumerates and hashes
// (cheap, metadata-only); if the revision matches the last computed insights for that provider,
// we return them WITHOUT re-parsing. No time-based skip-TTL — that could show stale data after a
// real change. The residual same-tick/same-size in-place rewrite miss is accepted (RR1); for
// append-only JSONL every change moves the size and flips the hash.
internal sealed class FreshnessGate
{
    private readonly object _lock = new();
    private readonly Dictionary<HistoryProvider, (string Revision, UsageInsights Insights)> _byProvider = new();

    // Order-independent fold over (path, mtime, size) plus the file count, so append, add, and
    // remove all change the revision regardless of enumeration order.
    public static string Revision(IReadOnlyList<FileFingerprint> files)
    {
        ulong accumulated = 0;
        foreach (var file in files)
        {
            var h = 1469598103934665603UL; // FNV-1a 64-bit offset basis
            foreach (var ch in file.FullPath)
            {
                h = (h ^ char.ToLowerInvariant(ch)) * 1099511628211UL;
            }

            h = (h ^ (ulong)file.MtimeTicksUtc) * 1099511628211UL;
            h = (h ^ (ulong)file.Size) * 1099511628211UL;
            accumulated ^= h; // XOR keeps it order-independent; paths are unique so no cancellation
        }

        accumulated ^= (ulong)files.Count * 1099511628211UL;
        return accumulated.ToString("x16");
    }

    public bool TryGet(HistoryProvider provider, string revision, out UsageInsights insights)
    {
        lock (_lock)
        {
            if (_byProvider.TryGetValue(provider, out var entry) && entry.Revision == revision)
            {
                insights = entry.Insights;
                return true;
            }
        }

        insights = null!;
        return false;
    }

    public void Put(HistoryProvider provider, string revision, UsageInsights insights)
    {
        lock (_lock)
        {
            _byProvider[provider] = (revision, insights);
        }
    }
}
