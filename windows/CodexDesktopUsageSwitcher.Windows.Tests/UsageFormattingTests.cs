using CodexDesktopUsageSwitcher.Windows.UI;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class UsageFormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Null_or_unparseable_returns_empty()
    {
        Assert.Equal("", UsageFormatting.ResetText(null, Now, Utc));
        Assert.Equal("", UsageFormatting.ResetText("", Now, Utc));
        Assert.Equal("", UsageFormatting.ResetText("not-a-date", Now, Utc));
    }

    [Fact]
    public void Past_reset_reports_already_reset()
    {
        Assert.Equal("초기화됨", UsageFormatting.ResetText("2026-06-16T11:00:00+00:00", Now, Utc));
    }

    [Fact]
    public void Hours_and_minutes_same_day()
    {
        Assert.Equal("2시간 14분 후 초기화 · 14:14", UsageFormatting.ResetText("2026-06-16T14:14:00+00:00", Now, Utc));
    }

    [Fact]
    public void Minutes_only()
    {
        Assert.Equal("30분 후 초기화 · 12:30", UsageFormatting.ResetText("2026-06-16T12:30:00+00:00", Now, Utc));
    }

    [Fact]
    public void Multi_day_includes_date()
    {
        Assert.Equal("3일 6시간 후 초기화 · 6/19 18:00", UsageFormatting.ResetText("2026-06-19T18:00:00+00:00", Now, Utc));
    }

    [Fact]
    public void Offset_is_normalized_to_the_zone()
    {
        // 23:14 at +09:00 is 14:14Z; displayed in UTC it must read 14:14.
        Assert.Equal("2시간 14분 후 초기화 · 14:14", UsageFormatting.ResetText("2026-06-16T23:14:00+09:00", Now, Utc));
    }
}
