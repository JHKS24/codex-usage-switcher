using System.Drawing.Drawing2D;

namespace CodexDesktopUsageSwitcher.Windows.UI.Controls;

internal sealed class MetricTile : Panel
{
    private string _value = "-";
    private string _caption = "";
    private Color _accent = Theme.Accent;

    public MetricTile(string caption)
    {
        _caption = caption;
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Margin = new Padding(8, 0, 0, 0);
        Size = new Size(92, 60);
        MinimumSize = Size;
        MaximumSize = Size;
    }

    public void SetValue(string value, Color accent)
    {
        _value = string.IsNullOrWhiteSpace(value) ? "-" : value;
        _accent = accent;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRectangle(rect, 8);
        using var fill = new SolidBrush(Color.FromArgb(28, 35, 44));
        using var border = new Pen(Color.FromArgb(40, 49, 60));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        TextRenderer.DrawText(e.Graphics, _value, Theme.Font(19F, FontStyle.Bold),
            new Rectangle(0, 8, Width, 28), _accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, _caption, Theme.Font(8F),
            new Rectangle(0, 38, Width, 18), Theme.Secondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }
}
