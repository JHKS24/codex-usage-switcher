using System.Drawing.Drawing2D;

namespace CodexUsageSwitcher.Windows.UI.Controls;

internal static class Theme
{
    public static readonly Color Body = Color.FromArgb(27, 32, 39);
    public static readonly Color Card = Color.FromArgb(35, 43, 53);
    public static readonly Color Hover = Color.FromArgb(43, 52, 64);
    public static readonly Color Selected = Color.FromArgb(36, 63, 73);
    public static readonly Color Border = Color.FromArgb(46, 55, 66);
    public static readonly Color Primary = Color.FromArgb(242, 245, 249);
    public static readonly Color Secondary = Color.FromArgb(155, 166, 180);
    public static readonly Color Accent = Color.FromArgb(70, 186, 201);
    public static readonly Color Good = Color.FromArgb(88, 201, 143);
    public static readonly Color Warning = Color.FromArgb(255, 183, 77);
    public static readonly Color Danger = Color.FromArgb(242, 120, 92);

    private static readonly Dictionary<(float Size, FontStyle Style), Font> FontCache = new();

    // Returned fonts are shared, process-lifetime instances — callers must not dispose
    // them. Paint paths (MetricTile) used to allocate two native fonts per frame.
    public static Font Font(float size, FontStyle style = FontStyle.Regular)
    {
        lock (FontCache)
        {
            if (!FontCache.TryGetValue((size, style), out var font))
            {
                font = new Font("Segoe UI Variable Text", size, style, GraphicsUnit.Point);
                FontCache[(size, style)] = font;
            }

            return font;
        }
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void ApplyRoundedRegion(Control control, int radius)
    {
        var bounds = new Rectangle(Point.Empty, control.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var path = RoundedRectangle(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), radius);
        control.Region?.Dispose();
        control.Region = new Region(path);
    }
}
