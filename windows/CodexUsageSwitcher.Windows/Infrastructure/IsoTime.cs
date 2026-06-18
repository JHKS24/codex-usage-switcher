using System.Globalization;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Single source of truth (guardrail D3) for turning a transcript's ISO-8601 "timestamp"
// string into a Unix-millis UTC value, shared by both history readers and the cutoff
// tail-probe. Always normalizes to UTC (AssumeUniversal + AdjustToUniversal) so it lines up
// with the calculator's UTC event windows. Returns 0 for missing/unparseable input.
internal static class IsoTime
{
    public static long ToUnixMs(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0;
    }
}
