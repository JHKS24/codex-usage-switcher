namespace CodexUsageSwitcher.Windows.UI.Controls;

using System.ComponentModel;
using CodexUsageSwitcher.Windows.Infrastructure;

internal sealed class ProfileRow : Panel
{
    private readonly Label _dot = new();
    private readonly Label _name = new();
    private readonly Label _quota = new();
    private readonly Label _check = new();
    private readonly TableLayoutPanel _layout;
    private bool _selected;

    public ProfileRow(string profileName)
    {
        ProfileName = profileName;
        DoubleBuffered = true;
        Height = 42;
        Margin = new Padding(0, 0, 0, 6);
        Cursor = Cursors.Hand;
        BackColor = Theme.Card;

        _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(8, 4, 8, 4) };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        Controls.Add(_layout);

        Configure(_dot, Theme.Accent, ContentAlignment.MiddleCenter, "●");
        Configure(_name, Theme.Primary, ContentAlignment.MiddleLeft, profileName);
        Configure(_quota, Theme.Secondary, ContentAlignment.MiddleRight, "");
        Configure(_check, Theme.Accent, ContentAlignment.MiddleCenter, "");
        _name.Font = Theme.Font(9.5F, FontStyle.Bold);
        _layout.Controls.Add(_dot, 0, 0);
        _layout.Controls.Add(_name, 1, 0);
        _layout.Controls.Add(_quota, 2, 0);
        _layout.Controls.Add(_check, 3, 0);
        WireMouseEvents(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Rows are built per-render at runtime, after the form's one-time AutoScale
        // pass, so WinForms never auto-scales them (dpi-6). Apply DPI scaling once
        // DeviceDpi is known. No-op at 100%.
        Height = LogicalToDeviceUnits(42);
        Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(6));
        _layout.Padding = new Padding(
            LogicalToDeviceUnits(8), LogicalToDeviceUnits(4),
            LogicalToDeviceUnits(8), LogicalToDeviceUnits(4));
        _layout.ColumnStyles[0].Width = LogicalToDeviceUnits(16);
        _layout.ColumnStyles[3].Width = LogicalToDeviceUnits(22);
    }

    public string ProfileName { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateBackColor();
        }
    }

    public void Render(bool active, bool hasAuth, string quotaText, string state)
    {
        _dot.Text = active ? "●" : "";
        _check.Text = active ? "✓" : "";
        _quota.Text = hasAuth ? quotaText : Localizer.L("common.loginRequired");
        _quota.ForeColor = hasAuth ? Theme.Secondary : Theme.Warning;
        if (!string.IsNullOrWhiteSpace(state) && !active)
        {
            _quota.Text = state;
        }

        UpdateBackColor();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = Theme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 7);
        using var pen = new Pen(_selected ? Theme.Accent : Color.Transparent);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateBackColor()
    {
        BackColor = _selected ? Theme.Selected : Theme.Card;
    }

    private void WireMouseEvents(Control control)
    {
        foreach (Control child in control.Controls)
        {
            child.Click += (_, _) => OnClick(EventArgs.Empty);
            child.DoubleClick += (_, _) => OnDoubleClick(EventArgs.Empty);
            WireMouseEvents(child);
        }
    }

    private static void Configure(Label label, Color color, ContentAlignment align, string text)
    {
        label.Text = text;
        label.Dock = DockStyle.Fill;
        label.ForeColor = color;
        label.TextAlign = align;
        label.AutoEllipsis = true;
        label.Font = Theme.Font(9F);
    }
}
