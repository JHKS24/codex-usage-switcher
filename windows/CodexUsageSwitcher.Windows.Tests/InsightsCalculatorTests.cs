using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Domain;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class InsightsCalculatorTests
{
    // Fixed "now": 2026-06-16 12:00:00Z, computed in UTC for determinism.
    private static readonly DateTimeOffset Now = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    [Fact]
    public void Daily_buckets_today_and_past_days()
    {
        var entries = new[]
        {
            new InsightEntry(Ms(Now), "opus", 100, 40, 0.5, "t1"),
            new InsightEntry(Ms(Now.AddMinutes(-5)), "opus", 60, 20, 0.3, "t1"),
            new InsightEntry(Ms(Now.AddDays(-2)), "sonnet", 50, 10, 0.1, "t2"),
        };

        var result = InsightsCalculator.Compute(entries, Now, Utc);

        Assert.Equal(InsightsCalculator.DailyDays, result.Daily.Count);
        // Today is the last bucket (index 13).
        Assert.Equal(160, result.Daily[13].TotalTokens);
        Assert.Equal(160, result.Daily[13].ByModel["opus"]);
        // Two days ago is index 11.
        Assert.Equal(50, result.Daily[11].TotalTokens);
        Assert.Equal(0, result.Daily[0].TotalTokens);
    }

    [Fact]
    public void Heatmap_uses_local_weekday_and_hour()
    {
        // 2026-06-16 is a Tuesday -> Monday=0 mapping gives weekday index 1.
        var entries = new[] { new InsightEntry(Ms(Now), "opus", 200, 50, null, null) };

        var result = InsightsCalculator.Compute(entries, Now, Utc);

        Assert.Equal(7, result.Heatmap.Count);
        Assert.Equal(200, result.Heatmap[1][12]);
    }

    [Fact]
    public void ModelTurns_compute_avg_median_p90()
    {
        // Four turns of one model with totals 10/20/30/40 (each its own turnKey).
        var totals = new[] { 10, 20, 30, 40 };
        var entries = totals
            .Select((t, i) => new InsightEntry(Ms(Now.AddMinutes(-i)), "opus", t, t, null, $"turn{i}"))
            .ToArray();

        var stats = Assert.Single(InsightsCalculator.Compute(entries, Now, Utc).ModelTurns);

        Assert.Equal("opus", stats.Model);
        Assert.Equal(4, stats.Turns);
        Assert.Equal(25, stats.AvgTokensPerTurn);           // (10+20+30+40)/4
        Assert.Equal(20, stats.MedianTokensPerTurn);        // nearest-rank ceil(0.5*4)-1 = idx 1
        Assert.Equal(40, stats.P90TokensPerTurn);           // ceil(0.9*4)-1 = idx 3
    }

    [Fact]
    public void Future_and_nonpositive_timestamps_are_ignored()
    {
        var entries = new[]
        {
            new InsightEntry(Ms(Now.AddDays(2)), "opus", 999, 999, null, "future"),
            new InsightEntry(0, "opus", 999, 999, null, "zero"),
        };

        var result = InsightsCalculator.Compute(entries, Now, Utc);

        Assert.Empty(result.ModelTurns);
        Assert.All(result.Daily, d => Assert.Equal(0, d.TotalTokens));
    }
}
