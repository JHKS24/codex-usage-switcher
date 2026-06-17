using System.Globalization;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// Date/time display for usage limits. Backend reset timestamps are ISO-8601 with an
// offset (Codex usage rows expose five_hour_reset/weekly_reset; Claude exposes
// five_hour.resets_at/seven_day.resets_at). This turns them into
// "N시간 M분 후 초기화 · 로컬시각" instead of a raw timestamp. The (now, zone) overload
// keeps the logic testable without depending on the machine clock or time zone.
internal static class UsageFormatting
{
    public static string ResetText(string? isoReset)
    {
        return ResetText(isoReset, DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    public static string ResetText(string? isoReset, DateTimeOffset now, TimeZoneInfo zone)
    {
        var reset = ParseReset(isoReset);
        if (reset is null)
        {
            return "";
        }

        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "초기화됨";
        }

        var resetLocal = TimeZoneInfo.ConvertTime(reset.Value, zone);
        var nowLocal = TimeZoneInfo.ConvertTime(now, zone);
        return $"{Humanize(remaining)} 후 초기화 · {Clock(resetLocal, nowLocal)}";
    }

    public static DateTimeOffset? ParseReset(string? isoReset)
    {
        return DateTimeOffset.TryParse(
            isoReset,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static string Humanize(TimeSpan span)
    {
        if (span.Days >= 1)
        {
            return $"{span.Days}일 {span.Hours}시간";
        }

        if (span.Hours >= 1)
        {
            return $"{span.Hours}시간 {span.Minutes}분";
        }

        return $"{Math.Max(1, span.Minutes)}분";
    }

    private static string Clock(DateTimeOffset resetLocal, DateTimeOffset nowLocal)
    {
        return resetLocal.Date == nowLocal.Date
            ? resetLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : resetLocal.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
    }
}
