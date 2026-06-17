using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Application;

// Turns the per-file cache records into a single insights result. Records MUST already be in
// deterministic path order (FileParseCache guarantees this) so the cross-file de-dup is
// reproducible and matches a single full parse (verdict V3): one global first-wins HashSet
// over DedupKey (an empty key is never de-duplicated — it is a distinct event each time),
// then project survivors to the keyless InsightEntry and run the calculator.
internal static class InsightsAggregator
{
    public static UsageInsights Aggregate(IReadOnlyList<FileCacheRecord> records, DateTimeOffset now, TimeZoneInfo zone)
        => InsightsCalculator.Compute(Project(records), now, zone);

    public static IReadOnlyList<InsightEntry> Project(IReadOnlyList<FileCacheRecord> records)
    {
        var result = new List<InsightEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            foreach (var entry in record.Entries)
            {
                if (entry.DedupKey.Length > 0 && !seen.Add(entry.DedupKey))
                {
                    continue; // duplicate streamed/resumed/forked line — first occurrence wins
                }

                result.Add(entry.ToInsight());
            }
        }

        return result;
    }
}
