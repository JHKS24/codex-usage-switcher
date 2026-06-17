using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace CodexDesktopUsageSwitcher.Windows.UI.Controls;

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 10;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Theme.Card;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color StrokeColor { get; set; } = Theme.Border;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Padding = new Padding(12);
        Margin = new Padding(0, 0, 0, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRectangle(rect, Radius);
        using var fill = new SolidBrush(FillColor);
        using var stroke = new Pen(StrokeColor);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(stroke, path);
    }
}
