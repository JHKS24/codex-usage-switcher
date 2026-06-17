namespace CodexDesktopUsageSwitcher.Windows.Domain;

// One flattened usage event (≈ one API call). TurnKey groups events of the same
// turn (user input → final response); null TurnKey is excluded from turn stats but
// still counted in the daily/hourly/heatmap series. Ported from the reference
// extension's InsightEntry so the dashboard charts can be computed locally.
// readonly record struct (not class): on the hot path this list holds ~84k entries;
// a value type eliminates that many Gen0 allocations. Use InsightEntry? where null is
// meaningful — never default(InsightEntry) as a sentinel (the Ts<=0 guard filters any
// stray zero entry). Do NOT introduce LINQ materializations over the entry list (RR3).
internal readonly record struct InsightEntry(
    long Ts,
    string Model,
    long TotalTokens,
    long OutputTokens,
    double? CostUsd,
    string? TurnKey,
    long InputTokens = 0,
    long CacheReadTokens = 0,
    long Cache5mTokens = 0,
    long Cache1hTokens = 0,
    long ReasoningOutputTokens = 0)
{
    public long CacheCreationTokens => Cache5mTokens + Cache1hTokens;
}

internal sealed record DailyUsagePoint(
    long DayStartMs,
    long TotalTokens,
    double CostUsd,
    IReadOnlyDictionary<string, long> ByModel);

internal sealed record HourlyUsagePoint(long HourStartMs, long TotalTokens);

internal sealed record ModelTurnStats(
    string Model,
    int Turns,
    int Calls,
    long AvgTokensPerTurn,
    long MedianTokensPerTurn,
    long P90TokensPerTurn,
    long AvgOutputPerTurn,
    double? AvgCostPerTurn);

internal sealed record UsageInsights(
    IReadOnlyList<DailyUsagePoint> Daily,
    IReadOnlyList<HourlyUsagePoint> Hourly,
    IReadOnlyList<IReadOnlyList<long>> Heatmap,
    int HeatmapDays,
    IReadOnlyList<ModelTurnStats> ModelTurns);
