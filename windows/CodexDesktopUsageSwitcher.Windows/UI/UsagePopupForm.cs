using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using CodexDesktopUsageSwitcher.Windows.UI.Controls;
using System.Runtime.InteropServices;

namespace CodexDesktopUsageSwitcher.Windows.UI;

internal sealed class UsagePopupForm : Form, IUsagePopup
{
    private const int WmSetRedraw = 0x000B;
    private const int LogicalWidth = 420;
    private readonly TableLayoutPanel _root = new();
    private readonly Label _title = Label("", 12.5F, FontStyle.Bold, Theme.Primary);
    private readonly Label _subTitle = Label("", 8.5F, FontStyle.Regular, Theme.Secondary);
    private readonly Label _codexName = Label("", 13F, FontStyle.Bold, Theme.Primary);
    private readonly Label _codexDetail = Label("", 8.5F, FontStyle.Regular, Theme.Secondary);
    private readonly MetricTile _codexFive = new(Localizer.L("usage.fiveHour.short"));
    private readonly MetricTile _codexWeek = new(Localizer.L("usage.weekly.short"));
    private readonly Label _claudeState = Label("", 9.5F, FontStyle.Bold, Theme.Primary);
    private readonly Label _claudeDetail = Label("", 8.5F, FontStyle.Regular, Theme.Secondary);
    private readonly Label _claudeValues = Label("", 9F, FontStyle.Bold, Theme.Warning);
    private readonly Button _claudeLogin = LinkButton(Localizer.L("common.login"));
    private readonly FlowLayoutPanel _profileRows = FlowHost();
    private readonly Panel _profileScroll = new();
    private readonly Button _switch = PrimaryButton(Localizer.L("popup.switchProfile"));
    private readonly Label _status = Label("", 8.5F, FontStyle.Regular, Theme.Secondary);
    private readonly Dictionary<string, ProfileRow> _profileControls = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedProfile;
    private string? _activeProfile;
    private bool _busy;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? ClaudeUsageLoginRequested;
    public event Func<string, Task>? SwitchProfileRequested;

    public Form Window => this;

    public UsagePopupForm()
    {
        Text = Localizer.L("popup.windowTitle");
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Body;
        Font = Theme.Font(9F);
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();
        BuildLayout();
    }

    public void Render(SwitcherSnapshot? snapshot, bool busy, string? error = null)
    {
        var holdRedraw = IsHandleCreated && Visible;
        if (holdRedraw)
        {
            SetRedraw(false);
        }

        SuspendLayout();
        _root.SuspendLayout();
        try
        {
            _busy = busy;
            _activeProfile = CurrentProfileName(snapshot);
            var usage = ActiveUsage(snapshot, _activeProfile);
            RenderHeader(snapshot, busy);
            RenderCodex(snapshot, _activeProfile, usage);
            RenderClaude(snapshot, busy);
            RenderProfiles(snapshot, _activeProfile);
            RenderStatus(busy, error);
            _switch.Enabled = !_busy && !string.IsNullOrWhiteSpace(_selectedProfile) && _selectedProfile != _activeProfile;
            ResizeToContent();
        }
        finally
        {
            _root.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
            if (holdRedraw)
            {
                SetRedraw(true);
                Invalidate(invalidateChildren: true);
                Update();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Theme.ApplyRoundedRegion(this, Scale(12));
    }

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.AutoSize = true;
        _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _root.ColumnCount = 1;
        _root.Padding = new Padding(Scale(16));
        _root.BackColor = Theme.Body;
        Controls.Add(_root);

        AddRow(Header());
        AddRow(CodexCard());
        AddRow(ClaudeCard());
        AddRow(ProfilesCard());
        AddRow(Footer());
        AddRow(_status);
    }

    private Control Header()
    {
        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(108)));

        var titleStack = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, RowCount = 2 };
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.Controls.Add(_title, 0, 0);
        titleStack.Controls.Add(_subTitle, 0, 1);
        header.Controls.Add(titleStack, 0, 0);

        var buttons = new FlowLayoutPanel { Width = Scale(108), FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, WrapContents = false };
        var close = new GlyphButton("×", Localizer.L("common.close"));
        close.Click += (_, _) => Hide();
        close.ContextMenuStrip = QuitMenu();
        buttons.Controls.Add(close);
        buttons.Controls.Add(Glyph("⚙", Localizer.L("common.settings"), (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        buttons.Controls.Add(Glyph("↻", Localizer.L("common.refresh"), (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty)));
        header.Controls.Add(buttons, 1, 0);
        return header;
    }

    private Control CodexCard()
    {
        var card = Card();
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2 };
        card.Controls.Add(layout);

        var left = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, RowCount = 3 };
        left.Controls.Add(Caption(Localizer.L("usage.codex.caption")), 0, 0);
        left.Controls.Add(_codexName, 0, 1);
        left.Controls.Add(_codexDetail, 0, 2);
        layout.Controls.Add(left, 0, 0);

        var tiles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, Margin = new Padding(0, Scale(10), 0, 0) };
        tiles.Controls.Add(_codexFive);
        tiles.Controls.Add(_codexWeek);
        layout.Controls.Add(tiles, 0, 1);
        return card;
    }

    private Control ClaudeCard()
    {
        var card = Card();
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var left = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, RowCount = 3 };
        left.Controls.Add(Caption(Localizer.L("usage.claude.caption")), 0, 0);
        left.Controls.Add(_claudeState, 0, 1);
        left.Controls.Add(_claudeDetail, 0, 2);
        layout.Controls.Add(left, 0, 0);

        _claudeValues.TextAlign = ContentAlignment.MiddleRight;
        _claudeValues.MinimumSize = new Size(Scale(84), Scale(32));
        layout.Controls.Add(_claudeValues, 1, 0);
        _claudeLogin.Click += (_, _) => ClaudeUsageLoginRequested?.Invoke(this, EventArgs.Empty);
        layout.Controls.Add(_claudeLogin, 2, 0);
        return card;
    }

    private Control ProfilesCard()
    {
        var card = Card();
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, RowCount = 2 };
        layout.Controls.Add(Caption(Localizer.L("popup.profiles.caption")), 0, 0);
        _profileScroll.AutoScroll = true;
        _profileScroll.BackColor = Theme.Card;
        _profileScroll.Controls.Add(_profileRows);
        layout.Controls.Add(_profileScroll, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control Footer()
    {
        // Settings is reachable from the header gear; a second footer link was a
        // duplicate entry point. Quit moves to the tray
        // context menu in a later Phase 1 batch; kept here until then.
        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 2, 0, 6) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _switch.Click += async (_, _) => await SwitchSelectedProfileAsync().ConfigureAwait(true);
        footer.Controls.Add(_switch, 0, 0);
        footer.Controls.Add(LinkButton(Localizer.L("common.quit"), (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty)), 1, 0);
        return footer;
    }

    private void RenderHeader(SwitcherSnapshot? snapshot, bool busy)
    {
        _title.Text = _activeProfile == "unknown" ? Localizer.L("popup.header.title.codex") : Localizer.F("popup.header.title.codexProfile", _activeProfile);
        _subTitle.Text = snapshot is null ? Localizer.L("popup.header.loadingUsage") : busy ? Localizer.L("popup.header.refreshing") : Localizer.F("popup.header.refreshedAt", snapshot.RefreshedAt);
    }

    private void RenderStatus(bool busy, string? error)
    {
        if (busy)
        {
            _status.Text = Localizer.L("popup.status.refreshing");
            _status.ForeColor = Theme.Secondary;
            return;
        }

        // Background refresh failures used to be invisible; surface the last one here
        // instead of pretending the (stale) snapshot is fresh.
        _status.Text = error is null ? Localizer.L("popup.status.idle") : Localizer.F("error.refreshFailed", error);
        _status.ForeColor = error is null ? Theme.Secondary : Theme.Warning;
    }

    private void RenderCodex(SwitcherSnapshot? snapshot, string active, UsageRow? usage)
    {
        _codexName.Text = active;
        _codexFive.SetValue(PercentOrDash(usage?.FiveHourLeft), MetricColor(usage?.FiveHourLeft));
        _codexWeek.SetValue(PercentOrDash(usage?.WeeklyLeft), MetricColor(usage?.WeeklyLeft));
        _codexDetail.Text = CodexDetail(snapshot, usage);
    }

    private void RenderClaude(SwitcherSnapshot? snapshot, bool busy)
    {
        var usage = snapshot?.ClaudeUsage;
        var authenticated = usage?.Authenticated == true;
        _claudeState.Text = authenticated ? Localizer.L("usage.claude.connected") : Localizer.L("common.loginRequired");
        _claudeDetail.Text = authenticated ? usage?.Message ?? Localizer.L("usage.claude.remainingFallback") : usage?.Message ?? Localizer.L("usage.claude.loginNeeded");
        _claudeValues.Text = authenticated ? Localizer.F("usage.claude.values", PercentOrDash(usage?.FiveHourLeft), PercentOrDash(usage?.WeeklyLeft)) : "";
        _claudeLogin.Visible = !authenticated;
        _claudeLogin.Enabled = !busy;
    }

    private void RenderProfiles(SwitcherSnapshot? snapshot, string active)
    {
        _profileRows.SuspendLayout();
        DisposeChildren(_profileRows);
        _profileControls.Clear();
        var profiles = snapshot?.Profiles ?? [];
        foreach (var profile in profiles)
        {
            var usage = snapshot?.UsageRows.FirstOrDefault(row => row.Profile == profile.Name);
            var row = new ProfileRow(profile.Name) { Width = ContentWidth() };
            row.Render(profile.Name == active, profile.HasAuth, QuotaText(usage), ProfileState(profile, active, usage));
            row.Selected = profile.Name == _selectedProfile;
            row.Click += (_, _) => SelectProfile(profile.Name);
            row.DoubleClick += async (_, _) => await SwitchProfileAsync(profile.Name).ConfigureAwait(true);
            _profileRows.Controls.Add(row);
            _profileControls[profile.Name] = row;
        }

        if (profiles.Count == 0)
        {
            _profileRows.Controls.Add(EmptyLine(Localizer.L("popup.profiles.empty")));
        }

        // Drop a selection that no longer exists (profile deleted/renamed) so the
        // Switch button can't start a 45s switch to a missing profile.
        if (_selectedProfile is not null && !_profileControls.ContainsKey(_selectedProfile))
        {
            _selectedProfile = null;
        }

        _profileRows.ResumeLayout();

        // Reserve room for the vertical scrollbar when the list overflows the cap so
        // it can't cover the right-hand quota / check column (layout-2/scroll-1).
        var cap = Scale(200);
        var contentHeight = _profileRows.PreferredSize.Height + Scale(6);
        var rowWidth = contentHeight > cap
            ? ContentWidth() - SystemInformation.VerticalScrollBarWidth
            : ContentWidth();
        foreach (var control in _profileControls.Values)
        {
            control.Width = rowWidth;
        }

        _profileScroll.Size = new Size(ContentWidth(), Math.Min(cap, Math.Max(Scale(44), contentHeight)));
    }

    private void SelectProfile(string profile)
    {
        _selectedProfile = profile;
        foreach (var row in _profileControls.Values)
        {
            row.Selected = row.ProfileName == profile;
        }

        _switch.Enabled = !_busy && profile != _activeProfile;
    }

    private async Task SwitchSelectedProfileAsync()
    {
        if (!string.IsNullOrWhiteSpace(_selectedProfile))
        {
            await SwitchProfileAsync(_selectedProfile).ConfigureAwait(true);
        }
    }

    private async Task SwitchProfileAsync(string profile)
    {
        // Rows stay clickable during the 45s+ switch; without this guard a second
        // click runs the whole stop/use/open sequence concurrently with the first.
        if (_busy)
        {
            return;
        }

        if (SwitchProfileRequested is { } handler && profile != _activeProfile)
        {
            await handler(profile).ConfigureAwait(true);
        }
    }

    private void ResizeToContent()
    {
        var width = Scale(LogicalWidth);
        _root.Width = width;
        var preferred = _root.GetPreferredSize(new Size(width, 0));
        // Clamp to the popup's OWN screen, not the cursor's — on multi-monitor a
        // refresh after the cursor moved to a shorter display used to cap the height
        // there and silently clip the footer/Switch button (layout-1).
        var area = (IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position)).WorkingArea;
        ClientSize = new Size(width, Math.Min(preferred.Height, area.Height - Scale(24)));
    }

    private void SetRedraw(bool enabled)
    {
        SendMessage(Handle, WmSetRedraw, enabled ? 1 : 0, IntPtr.Zero);
    }

    // Controls.Clear() detaches but never disposes; rebuilding rows every render would
    // leak GDI handles and event wiring. Dispose the removed children explicitly.
    private static void DisposeChildren(Control host)
    {
        var existing = host.Controls.Cast<Control>().ToArray();
        host.Controls.Clear();
        foreach (var control in existing)
        {
            control.Dispose();
        }
    }

    private void AddRow(Control control)
    {
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(control, 0, _root.RowCount++);
    }

    private int ContentWidth()
    {
        return Scale(LogicalWidth) - _root.Padding.Horizontal - Scale(24);
    }

    private int Scale(int value)
    {
        return LogicalToDeviceUnits(value);
    }

    private ContextMenuStrip QuitMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Localizer.L("tray.quitApp"), null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));
        return menu;
    }

    private static RoundedPanel Card()
    {
        return new RoundedPanel { AutoSize = true, Dock = DockStyle.Top, FillColor = Theme.Card };
    }

    private static FlowLayoutPanel FlowHost()
    {
        return new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
    }

    private static GlyphButton Glyph(string glyph, string name, EventHandler handler)
    {
        var button = new GlyphButton(glyph, name);
        button.Click += handler;
        return button;
    }

    private static Label Caption(string text)
    {
        return Label(text, 8.5F, FontStyle.Bold, Theme.Secondary);
    }

    private static Label EmptyLine(string text)
    {
        return Label(text, 8.5F, FontStyle.Regular, Theme.Secondary);
    }

    private static Label Label(string text, float size, FontStyle style, Color color)
    {
        return new Label { Text = text, AutoSize = true, Font = Theme.Font(size, style), ForeColor = color, MaximumSize = new Size(380, 0) };
    }

    private static Button PrimaryButton(string text)
    {
        var button = new Button { Text = text, AutoSize = false, Size = new Size(138, 34), Cursor = Cursors.Hand };
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Theme.Accent;
        button.ForeColor = Color.FromArgb(7, 20, 24);
        button.Font = Theme.Font(9F, FontStyle.Bold);
        return button;
    }

    private static Button LinkButton(string text, EventHandler? handler = null)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Theme.Card;
        button.ForeColor = Theme.Primary;
        button.Font = Theme.Font(9F, FontStyle.Bold);
        if (handler is not null)
        {
            button.Click += handler;
        }

        return button;
    }

    private static string CurrentProfileName(SwitcherSnapshot? snapshot)
    {
        var current = snapshot?.Current;
        return current?.MatchedProfile ?? current?.ActiveLabel ?? "unknown";
    }

    private static UsageRow? ActiveUsage(SwitcherSnapshot? snapshot, string active)
    {
        return snapshot?.UsageRows.FirstOrDefault(row => row.Profile == active);
    }

    private static string CodexDetail(SwitcherSnapshot? snapshot, UsageRow? usage)
    {
        if (snapshot is null)
        {
            return Localizer.L("usage.codex.loading");
        }

        if (!string.IsNullOrWhiteSpace(usage?.Error))
        {
            return usage.Error;
        }

        var auth = snapshot.Current.AuthMatch == "mismatch" ? Localizer.L("usage.auth.mismatch") : Localizer.L("usage.auth.match");
        var running = snapshot.Current.CodexRunning is true ? Localizer.L("usage.codex.running") : Localizer.L("usage.codex.notRunning");
        var weeklyReset = UsageFormatting.ResetText(usage?.WeeklyReset);
        return string.IsNullOrEmpty(weeklyReset) ? Localizer.F("usage.codex.detail.authRun", auth, running) : Localizer.F("usage.codex.detail.authRunWeekly", auth, running, weeklyReset);
    }

    private static string ProfileState(ProfileSummary profile, string active, UsageRow? usage)
    {
        if (!string.IsNullOrWhiteSpace(usage?.Error))
        {
            return Localizer.L("common.loginRequired");
        }

        return profile.Name == active ? Localizer.L("usage.profile.inUse") : "";
    }

    private static string QuotaText(UsageRow? usage)
    {
        return string.IsNullOrWhiteSpace(usage?.Error) ? Localizer.F("usage.quota.values", PercentOrDash(usage?.FiveHourLeft), PercentOrDash(usage?.WeeklyLeft)) : Localizer.L("common.loginRequired");
    }

    private static Color MetricColor(int? value)
    {
        return value switch
        {
            null => Theme.Secondary,
            < 20 => Theme.Danger,
            < 50 => Theme.Warning,
            _ => Theme.Accent,
        };
    }

    private static string PercentOrDash(int? value)
    {
        return value is null ? Localizer.L("usage.percent.dash") : Localizer.F("usage.percent.value", value);
    }
}
