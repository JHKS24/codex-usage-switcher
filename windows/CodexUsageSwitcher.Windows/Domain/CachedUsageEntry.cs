namespace CodexUsageSwitcher.Windows.Domain;

// Key-bearing usage entry stored in the per-file cache. Mirrors the reference
// extension's UsageEntry: it carries the dedup key so cross-file de-duplication
// runs exactly once, at aggregation, after which survivors project to the keyless
// InsightEntry the calculator consumes.
//
// Never cache the keyless InsightEntry directly: the same message can recur across
// files (resume / fork / branch), so totals would inflate without the key. An empty
// DedupKey means the source line had no id and must never be de-duplicated (it is a
// distinct event each time it appears).
internal readonly record struct CachedUsageEntry(
    string DedupKey,
    long Ts,
    string Model,
    long TotalTokens,
    long OutputTokens,
    double? CostUsd,
    string? TurnKey,
    // Token breakdown (trailing + defaulted so existing call sites/tests are unaffected).
    // Carried so the dashboard can compute per-window cost and cache metrics; TotalTokens
    // stays the reported total. Claude fills the cache split; Codex fills CacheRead/Reasoning.
    long InputTokens = 0,
    long CacheReadTokens = 0,
    long Cache5mTokens = 0,
    long Cache1hTokens = 0,
    long ReasoningOutputTokens = 0)
{
    public long CacheCreationTokens => Cache5mTokens + Cache1hTokens;

    // Projection happens only after global de-duplication; keep this the single
    // place that drops the key so the public shape stays the SSOT (InsightEntry).
    public InsightEntry ToInsight() => new(
        Ts, Model, TotalTokens, OutputTokens, CostUsd, TurnKey,
        InputTokens, CacheReadTokens, Cache5mTokens, Cache1hTokens, ReasoningOutputTokens);
}
