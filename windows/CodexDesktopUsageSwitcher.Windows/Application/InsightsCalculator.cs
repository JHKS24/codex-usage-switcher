using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Application;

// Faithful C# port of the reference extension's insights.ts: turns flat per-call
// events into the compressed series the dashboard draws (14-day model-stacked daily,
// 24h hourly, weekday×hour heatmap over 28 days, per-turn token stats over 7 days).
// Day/hour boundaries use the supplied time zone (local clock), like the original.
internal static class InsightsCalculator
{
    public const int DailyDays = 14;
    public const int HeatmapDays = 28;
    private const long DayMs = 24L * 60 * 60 * 1000;
    private const long HourMs = 60L * 60 * 1000;

    public static UsageInsights Compute(IEnumerable<InsightEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var nowLocal = TimeZoneInfo.ConvertTime(now, zone);
        var todayStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var dailyStartMs = todayStart.ToUnixTimeMilliseconds() - ((DailyDays - 1) * DayMs);
        var heatmapStartMs = nowMs - (HeatmapDays * DayMs);
        var hourAnchor = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0, nowLocal.Offset);
        var hourlyStartMs = hourAnchor.ToUnixTimeMilliseconds() - (23 * HourMs);
        var sevenDaysAgo = nowMs - (7 * DayMs);

        var dailyTotals = new long[DailyDays];
        var dailyCost = new double[DailyDays];
        var dailyByModel = new Dictionary<string, long>[DailyDays];
        for (var i = 0; i < DailyDays; i++)
        {
            dailyByModel[i] = new Dictionary<string, long>();
        }

        var hourly = new long[24];
        var heatmap = new long[7][];
        for (var i = 0; i < 7; i++)
        {
            heatmap[i] = new long[24];
        }

        var turns = new Dictionary<string, TurnAgg>();

        foreach (var e in entries)
        {
            if (e.Ts <= 0 || e.Ts > nowMs + HourMs)
            {
                continue; // unknown / future timestamps are excluded
            }

            var model = string.IsNullOrEmpty(e.Model) ? "unknown" : e.Model;

            if (e.Ts >= dailyStartMs)
            {
                var idx = (int)((e.Ts - dailyStartMs) / DayMs);
                if (idx >= 0 && idx < DailyDays)
                {
                    dailyTotals[idx] += e.TotalTokens;
                    dailyCost[idx] += e.CostUsd ?? 0;
                    dailyByModel[idx][model] = dailyByModel[idx].GetValueOrDefault(model) + e.TotalTokens;
                }
            }

            if (e.Ts >= hourlyStartMs)
            {
                var idx = (int)((e.Ts - hourlyStartMs) / HourMs);
                if (idx >= 0 && idx < 24)
                {
                    hourly[idx] += e.TotalTokens;
                }
            }

            if (e.Ts >= heatmapStartMs)
            {
                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(e.Ts), zone);
                heatmap[((int)local.DayOfWeek + 6) % 7][local.Hour] += e.TotalTokens;
            }

            if (!string.IsNullOrEmpty(e.TurnKey) && e.Ts >= sevenDaysAgo)
            {
                if (!turns.TryGetValue(e.TurnKey, out var turn))
                {
                    turn = new TurnAgg();
                    turns[e.TurnKey] = turn;
                }

                turn.ByModel[model] = turn.ByModel.GetValueOrDefault(model) + e.TotalTokens;
                turn.Total += e.TotalTokens;
                turn.Output += e.OutputTokens;
                if (e.CostUsd is { } cost)
                {
                    turn.Cost += cost;
                    turn.HasCost = true;
                }

                turn.Calls += 1;
            }
        }

        var daily = new List<DailyUsagePoint>(DailyDays);
        for (var i = 0; i < DailyDays; i++)
        {
            daily.Add(new DailyUsagePoint(dailyStartMs + (i * DayMs), dailyTotals[i], dailyCost[i], dailyByModel[i]));
        }

        var hourlyPoints = new List<HourlyUsagePoint>(24);
        for (var i = 0; i < 24; i++)
        {
            hourlyPoints.Add(new HourlyUsagePoint(hourlyStartMs + (i * HourMs), hourly[i]));
        }

        var heatmapOut = heatmap.Select(row => (IReadOnlyList<long>)row).ToArray();
        return new UsageInsights(daily, hourlyPoints, heatmapOut, HeatmapDays, BuildModelTurns(turns));
    }

    private static IReadOnlyList<ModelTurnStats> BuildModelTurns(Dictionary<string, TurnAgg> turns)
    {
        var perModel = new Dictionary<string, ModelAgg>();
        foreach (var turn in turns.Values)
        {
            if (turn.Total <= 0)
            {
                continue;
            }

            var dominant = "unknown";
            long best = -1;
            foreach (var (model, tokens) in turn.ByModel)
            {
                if (tokens > best)
                {
                    best = tokens;
                    dominant = model;
                }
            }

            if (!perModel.TryGetValue(dominant, out var agg))
            {
                agg = new ModelAgg();
                perModel[dominant] = agg;
            }

            agg.Totals.Add(turn.Total);
            agg.Output += turn.Output;
            agg.Cost += turn.Cost;
            agg.HasCost = agg.HasCost || turn.HasCost;
            agg.Calls += turn.Calls;
        }

        return perModel
            .Select(kv =>
            {
                var sorted = kv.Value.Totals.OrderBy(v => v).ToList();
                var n = sorted.Count;
                var sum = sorted.Sum();
                return new ModelTurnStats(
                    kv.Key,
                    n,
                    kv.Value.Calls,
                    // Round-half-up (AwayFromZero) to match the reference's Math.round;
                    // the C# default is banker's rounding and diverges by 1 token on .5.
                    (long)Math.Round((double)sum / n, MidpointRounding.AwayFromZero),
                    Percentile(sorted, 0.5),
                    Percentile(sorted, 0.9),
                    (long)Math.Round((double)kv.Value.Output / n, MidpointRounding.AwayFromZero),
                    kv.Value.HasCost ? kv.Value.Cost / n : null);
            })
            .OrderByDescending(stats => stats.Turns)
            .ToList();
    }

    private static long Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var idx = Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1));
        return sorted[idx];
    }

    private sealed class TurnAgg
    {
        public Dictionary<string, long> ByModel { get; } = new();
        public long Total { get; set; }
        public long Output { get; set; }
        public double Cost { get; set; }
        public bool HasCost { get; set; }
        public int Calls { get; set; }
    }

    private sealed class ModelAgg
    {
        public List<long> Totals { get; } = new();
        public long Output { get; set; }
        public double Cost { get; set; }
        public bool HasCost { get; set; }
        public int Calls { get; set; }
    }
}
