using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Application;

// Turns the per-file cache records into a single insights result. Records MUST already be in
// deterministic path order (FileParseCache guarantees this) so the cross-file de-dup is
// reproducible and matches a single full parse: one global first-wins HashSet
// over DedupKey (an empty key is never de-duplicated — it is a distinct event each time),
// then project survivors to the keyless InsightEntry and run the calculator.
internal static class InsightsAggregator
{
    public static UsageInsights Aggregate(IReadOnlyList<FileCacheRecord> records, DateTimeOffset now, TimeZoneInfo zone, ModelPricing? pricing = null)
        => InsightsCalculator.Compute(Project(records, pricing), now, zone);

    public static IReadOnlyList<InsightEntry> Project(IReadOnlyList<FileCacheRecord> records, ModelPricing? pricing = null)
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

                var insight = entry.ToInsight();
                // Cost is computed from the live price table (not baked into the cache), so a
                // price refresh updates costs without re-parsing; an unpriced model yields null.
                result.Add(pricing is null ? insight : insight with { CostUsd = pricing.EstimateCost(insight) });
            }
        }

        return result;
    }
}
