namespace CodexDesktopUsageSwitcher.Windows.Domain;

// Key-bearing usage entry stored in the per-file cache. Mirrors the reference
// extension's UsageEntry: it carries the dedup key so cross-file de-duplication
// runs exactly once, at aggregation, after which survivors project to the keyless
// InsightEntry the calculator consumes.
//
// Never cache the keyless InsightEntry directly: the same message can recur across
// files (resume / fork / branch), so totals would inflate without the key. An empty
// DedupKey means the source line had no id and must never be de-duplicated (it is a
// distinct event each time it appears). See verdict V3 in the hardened plan.
internal readonly record struct CachedUsageEntry(
    string DedupKey,
    long Ts,
    string Model,
    long TotalTokens,
    long OutputTokens,
    double? CostUsd,
    string? TurnKey)
{
    // Projection happens only after global de-duplication; keep this the single
    // place that drops the key so the public shape stays the SSOT (InsightEntry).
    public InsightEntry ToInsight() => new(Ts, Model, TotalTokens, OutputTokens, CostUsd, TurnKey);
}
