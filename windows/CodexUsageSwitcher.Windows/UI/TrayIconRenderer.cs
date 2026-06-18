using System.Runtime.InteropServices;

namespace CodexUsageSwitcher.Windows.UI;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(string valueLabel, string metricLabel, int? remainingPercent)
    {
        const int size = 16;
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.Transparent);

        var label = string.IsNullOrWhiteSpace(valueLabel) ? "--" : valueLabel.Trim();
        using var font = new Font("Segoe UI", label.Length > 2 ? 6.1f : 8.4f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(TextColor(metricLabel));
        using var shadowBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var textRect = new RectangleF(-1, 1, 18, 13);
        var shadowRect = new RectangleF(0, 2, 18, 13);
        graphics.DrawString(label, font, shadowBrush, shadowRect, format);
        graphics.DrawString(label, font, textBrush, textRect, format);

        if (remainingPercent is <= 20)
        {
            using var warnBrush = new SolidBrush(Color.FromArgb(255, 255, 184, 77));
            graphics.FillRectangle(warnBrush, 1, 14, 14, 2);
        }
        else
        {
            using var accentBrush = new SolidBrush(AccentColor(metricLabel));
            graphics.FillRectangle(accentBrush, 1, 14, 14, 1);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static string PercentLabel(int? value)
    {
        return value is null ? "--" : Math.Clamp(value.Value, 0, 100).ToString();
    }

    private static Color TextColor(string metricLabel)
    {
        if (metricLabel.EndsWith("W", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(255, 255, 214, 73);
        }

        return metricLabel.StartsWith("L", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 198, 161, 255)
            : Color.FromArgb(255, 232, 250, 255);
    }

    private static Color AccentColor(string metricLabel)
    {
        return metricLabel.StartsWith("S", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 87, 220, 180)
            : metricLabel.StartsWith("L", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(255, 168, 132, 255)
                : Color.FromArgb(255, 66, 205, 232);
    }
}
