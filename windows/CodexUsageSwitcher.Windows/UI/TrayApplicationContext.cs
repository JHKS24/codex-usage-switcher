using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    // A tray click on an open popup first fires Deactivate (auto-hide); the following
    // MouseUp must not immediately reopen it. Ignore re-opens within this window.
    private const long ReopenGuardMilliseconds = 250;
    private static readonly TimeSpan StaleSnapshotAge = TimeSpan.FromMinutes(2);
    private readonly SwitcherService _service;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<string, NotifyIcon> _extraNotifyIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> _extraTrayIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly Control _dispatcher = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly UsageHistoryService _history = new();
    private IUsagePopup? _popup;
    private SettingsForm? _settingsForm;
    private DashboardForm? _dashboard;
    private SwitcherSnapshot? _snapshot;
    private Icon? _trayIcon;
    private string? _primaryMetricKey;
    private string? _lastError;
    private bool _busy;
    private bool _refreshQueued;
    private bool _queuedShowErrors;
    private bool _switching;
    private bool _popupOwnedModalOpen;
    private long _popupHiddenTick;
    private readonly CancellationTokenSource _warmupCts = new();

    public TrayApplicationContext(SwitcherService service)
    {
        _service = service;
        _trayIcon = TrayIconRenderer.Create("--", "C5", null);
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = Localizer.L("tray.tooltip.loading"),
            Visible = true,
        };
        // Left-click toggles the popup; right-click shows this menu. A separate
        // DoubleClick handler fired a second, conflicting toggle that opened-then-
        // closed the popup on the common double-click habit. Quit lives
        // here now instead of only inside the popup.
        BuildTrayMenu();
        Localizer.LanguageChanged += OnLanguageChanged;
        _notifyIcon.MouseUp += OnTrayMouseUp;
        _ = _dispatcher.Handle;

        _refreshTimer.Interval = 10 * 60 * 1000;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(showErrors: false).ConfigureAwait(true);
        _refreshTimer.Start();

        _ = RefreshAsync(showErrors: true);

        // Pre-warm the popup (and its WebView2) at startup so the first tray click
        // shows it instantly instead of waiting for the runtime to initialize.
        EnsurePopup();

        // Warm the usage-history cache in the background so the first dashboard open is hot.
        _ = WarmHistoryCacheAsync();
    }

    // Best-effort background warmup: lets startup settle, then parses + caches both providers
    // off the UI thread. Failures only mean a cold first open (the open path re-runs the load),
    // so they are handled here rather than crashing a fire-and-forget task.
    private async Task WarmHistoryCacheAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _warmupCts.Token).ConfigureAwait(false);
            await _history.WarmupAsync(DateTimeOffset.Now, _warmupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // app closing during warmup — expected
        }
        catch (Exception)
        {
            // warmup is a pure optimization; degrade to a cold first open, never crash
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Localizer.LanguageChanged -= OnLanguageChanged;
            _warmupCts.Cancel();
            _refreshTimer.Dispose();
            _trayMenu.Dispose();
            DisposeExtraTrayIcons();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _dispatcher.Dispose();
            _popup?.Dispose();
            _settingsForm?.Dispose();
            _dashboard?.Dispose();
            _trayIcon?.Dispose();
            _history.Dispose();
            _warmupCts.Dispose();
        }

        base.Dispose(disposing);
    }

    public void ShowPopupFromExternalActivation()
    {
        if (_dispatcher.IsDisposed)
        {
            return;
        }

        if (_dispatcher.InvokeRequired)
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(ShowPopupFromExternalActivation));
            }
            catch (InvalidOperationException)
            {
                // The app is shutting down; ignore late activation.
            }
            return;
        }

        ShowPopup();
    }

    private async Task RefreshAsync(bool showErrors)
    {
        if (_busy || _switching)
        {
            // Run again once the in-flight cycle ends instead of silently dropping the
            // request (settings toggles used to vanish when they raced a refresh).
            // Also yields to an in-progress profile switch: a refresh raised
            // during the switch's confirmation modal or apply must not interleave with
            // it on the shared busy/queue state — the switch drains this queue when done.
            _refreshQueued = true;
            _queuedShowErrors |= showErrors;
            return;
        }

        _busy = true;
        try
        {
            // Inside the try so a render exception can't leave _busy stuck true,
            // which would freeze every later refresh (guardrail I1).
            RenderOpenWindows();
            _snapshot = await _service.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            _lastError = null;
            UpdateTrayTitle();
        }
        catch (Exception ex)
        {
            var message = PathRedaction.Scrub(ex.Message);
            _lastError = message;
            if (showErrors)
            {
                ShowError(message, Localizer.L("app.name"));
            }
        }
        finally
        {
            _busy = false;
            RenderOpenWindows();
            if (_refreshQueued)
            {
                _refreshQueued = false;
                var queuedShowErrors = _queuedShowErrors;
                _queuedShowErrors = false;
                _ = RefreshAsync(queuedShowErrors);
            }
        }
    }

    private void RenderOpenWindows()
    {
        RenderOpenDashboard();

        if (_popup is null || _popup.Window.IsDisposed || !_popup.Window.Visible)
        {
            return;
        }

        _popup.Render(_snapshot, _busy, _lastError);

        // A render can grow the popup (e.g. profile rows arriving after the
        // open-time stale refresh); keep it inside the working area.
        var window = _popup.Window;
        var area = Screen.FromControl(window).WorkingArea;
        if (window.Bottom > area.Bottom - 8)
        {
            window.Top = Math.Max(area.Top + 8, area.Bottom - 8 - window.Height);
        }
    }

    private void RenderOpenDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed || !_dashboard.Visible)
        {
            return;
        }

        // Keep the limit donuts fresh on the 10-min refresh without forcing a transcript
        // reload — SetSnapshot only re-posts the cheap limits message.
        _dashboard.SetSnapshot(_snapshot);
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            TogglePopup();
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowTrayMenu();
        }
    }

    // Show the tray menu growing UPWARD from the cursor so it is never clipped behind the
    // taskbar (the OS-default downward placement left Open/Quit partly under the bar). A
    // foreground window is set first so the menu dismisses on an outside click — the classic
    // notify-icon context-menu requirement.
    private void ShowTrayMenu()
    {
        SetForegroundWindow(_dispatcher.Handle);
        _trayMenu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
    }

    private void TogglePopup()
    {
        if (_popup is not null && !_popup.Window.IsDisposed && _popup.Window.Visible)
        {
            _popup.Window.Hide();
            return;
        }

        if (Environment.TickCount64 - _popupHiddenTick < ReopenGuardMilliseconds)
        {
            // The same click that dismissed the popup via Deactivate; do not reopen it.
            return;
        }

        ShowPopup();
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_popupOwnedModalOpen)
        {
            // A dialog owned by the popup stole the focus; keep the popup up so the
            // busy indicator stays visible behind it. Unowned dialogs (e.g. startup
            // errors) must not pin the popup open.
            return;
        }

        _popupHiddenTick = Environment.TickCount64;
        _popup?.Window.Hide();
    }

    private void EnsurePopup()
    {
        if (_popup is not null && !_popup.Window.IsDisposed)
        {
            return;
        }

        _popup = CreatePopup();
        _popup.Window.Deactivate += OnPopupDeactivated;
        _popup.RefreshRequested += async (_, _) => await RefreshAsync(showErrors: true).ConfigureAwait(true);
        _popup.SettingsRequested += (_, _) => ShowSettings();
        _popup.QuitRequested += (_, _) => ExitThread();
        _popup.ClaudeUsageLoginRequested += async (_, _) => await StartClaudeLoginAsync().ConfigureAwait(true);
        _popup.SwitchProfileRequested += SwitchProfileAsync;
        if (_popup is WebUsagePopupForm web)
        {
            web.DashboardRequested += (_, _) => ShowDashboard();
        }
    }

    private void ShowPopup()
    {
        EnsurePopup();
        var window = _popup!.Window;
        _popup.Render(_snapshot, _busy, _lastError);
        PositionPopup();
        window.Show();
        window.BringToFront();
        // Borderless, no-taskbar popups don't take the foreground from a tray click on
        // their own, so a left-click left the popup behind other windows (you had to use
        // the context-menu "Open"). Activate + SetForegroundWindow brings it to front on a
        // single click, and — being the active form — Deactivate now fires and hides it
        // when focus moves away instead of leaving it stuck behind.
        window.Activate();
        ForceForeground(window);

        if (!_busy && (_snapshot is null || DateTimeOffset.Now - _snapshot.RefreshedAt > StaleSnapshotAge))
        {
            _ = RefreshAsync(showErrors: false);
        }
    }

    private static void ForceForeground(Form window)
    {
        if (!window.IsHandleCreated)
        {
            return;
        }

        var handle = window.Handle;
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();

        // The OS blocks a background process from stealing the foreground, so a plain
        // SetForegroundWindow leaves the popup behind other windows. Briefly attaching
        // the current foreground thread's input queue to ours lifts that block, so the
        // popup reliably comes to the front on a single tray click.
        var attached = foregroundThread != currentThread
            && AttachThreadInput(foregroundThread, currentThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private static IUsagePopup CreatePopup()
    {
        // WebView2 popup when its runtime is present; otherwise the classic WinForms
        // popup so the app still works on machines without the Evergreen runtime.
        return WebUsagePopupForm.IsRuntimeAvailable() ? new WebUsagePopupForm() : new UsagePopupForm();
    }

    private void ShowDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(_history);
        }

        // Seed/refresh the limit donuts from the latest snapshot before showing — cheap,
        // no transcript reload (the form's own load posts the insights).
        _dashboard.SetSnapshot(_snapshot);
        _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized)
        {
            _dashboard.WindowState = FormWindowState.Normal;
        }

        _dashboard.BringToFront();
        _dashboard.Activate();
    }

    private void PositionPopup()
    {
        if (_popup is null)
        {
            return;
        }

        var window = _popup.Window;
        var size = window.Size;

        // Anchor to the work-area corner nearest the click. The cursor sits on the tray
        // icon when the popup opens, so this is consistent across clicks and — by staying
        // inside the work area — never lets the popup hide behind the taskbar, whichever
        // edge the taskbar is docked to.
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X >= (area.Left + area.Right) / 2 ? area.Right - size.Width - 8 : area.Left + 8;
        var y = cursor.Y >= (area.Top + area.Bottom) / 2 ? area.Bottom - size.Height - 8 : area.Top + 8;
        window.Location = new Point(x, y);
    }

    private async Task SwitchProfileAsync(string profile)
    {
        // One re-entrancy gate guards both operations. _switching blocks a
        // second switch; _busy blocks a switch starting while a refresh is in flight
        // (and RefreshAsync reciprocally yields to _switching). This keeps the switch
        // and refresh from interleaving on the shared _busy/queue state.
        if (_switching || _busy)
        {
            return;
        }

        var confirmed = await RunSwitchAsync(profile).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        // The switch owned the busy/queue state until here; supersede anything that
        // queued during it (incl. refreshes that yielded to _switching) with one
        // authoritative post-switch refresh, now that the gate is released.
        _refreshQueued = false;
        _queuedShowErrors = false;
        await RefreshAsync(showErrors: true).ConfigureAwait(true);
    }

    // Runs the confirmation + apply under the _switching gate. Returns true when the
    // switch was actually applied (so the caller runs the post-switch refresh).
    private async Task<bool> RunSwitchAsync(string profile)
    {
        _switching = true;
        try
        {
            var answer = ShowOwnedMessage(
                Localizer.F("settings.switch.confirm.message", profile),
                Localizer.L("settings.switch.confirm.title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return false;
            }

            _busy = true;
            _notifyIcon.Text = TrimNotifyText(Localizer.F("tray.tooltip.switching", profile));
            RenderOpenWindows();
            try
            {
                var outcome = await _service.SwitchProfileAsync(profile, CancellationToken.None).ConfigureAwait(true);
                if (!outcome.Ok)
                {
                    ShowError(outcome.Message, Localizer.L("settings.switch.error.title"));
                }
            }
            finally
            {
                _busy = false;
                // Restore the tooltip from the last snapshot; without this a failed
                // post-switch refresh would leave the "switching" tooltip pinned in the tray.
                UpdateTrayTitle();
            }

            return true;
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task StartClaudeLoginAsync()
    {
        var outcome = await _service.StartClaudeLoginAsync(CancellationToken.None).ConfigureAwait(true);
        ShowOwnedMessage(
            outcome.Message,
            Localizer.L("login.claude.title"),
            MessageBoxButtons.OK,
            outcome.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1);
        await RefreshAsync(showErrors: false).ConfigureAwait(true);
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_service);
            _settingsForm.SettingsChanged += async (_, _) => await RefreshAsync(showErrors: false).ConfigureAwait(true);
        }

        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowError(string message, string title)
    {
        ShowOwnedMessage(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
    }

    private DialogResult ShowOwnedMessage(
        string message,
        string title,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        var owner = _popup is not null && !_popup.Window.IsDisposed && _popup.Window.Visible ? (IWin32Window)_popup.Window : null;
        _popupOwnedModalOpen = owner is not null;
        try
        {
            return owner is null
                ? MessageBox.Show(message, title, buttons, icon, defaultButton)
                : MessageBox.Show(owner, message, title, buttons, icon, defaultButton);
        }
        finally
        {
            _popupOwnedModalOpen = false;
        }
    }

    private void BuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add(Localizer.L("tray.menu.open"), null, (_, _) => ShowPopup());
        _trayMenu.Items.Add(Localizer.L("tray.menu.quit"), null, (_, _) => ExitThread());
    }

    // Rebuild the menu labels when the language changes; tray tooltips refresh via the snapshot.
    private void OnLanguageChanged() => BuildTrayMenu();

    private string CurrentProfileName()
    {
        var current = _snapshot?.Current;
        // "unknown" is a status sentinel (used for row lookups), not display text.
        return current?.MatchedProfile ?? current?.ActiveLabel ?? "unknown";
    }

    private void UpdateTrayTitle()
    {
        var displays = BuildTrayMetricDisplays();
        var primary = displays.FirstOrDefault() ?? FallbackDisplay();
        UpdatePrimaryIcon(primary);
        UpdateExtraIcons(displays.Skip(1).Take(5).ToArray());
    }

    private IReadOnlyList<TrayMetricDisplay> BuildTrayMetricDisplays()
    {
        var metrics = _snapshot?.TrayMetrics
            .Where(metric => metric.Visible)
            .Take(6)
            .Select(metric => new TrayMetricDisplay(
                metric.Key,
                metric.ShortLabel,
                TrayIconRenderer.PercentLabel(metric.RemainingPercent),
                Tooltip(metric),
                metric.RemainingPercent))
            .ToArray();

        return metrics is { Length: > 0 } ? metrics : [];
    }

    private TrayMetricDisplay FallbackDisplay()
    {
        var active = CurrentProfileName();
        var usage = _snapshot?.UsageRows.FirstOrDefault(row => row.Profile == active);
        var key = "fallback:codex";
        var tooltip = usage is null
            ? Localizer.L("app.name")
            : Localizer.F("tray.tooltip.fallback.usage", active, PercentOrDash(usage.FiveHourLeft), PercentOrDash(usage.WeeklyLeft));
        return new TrayMetricDisplay(key, "C5", TrayIconRenderer.PercentLabel(usage?.FiveHourLeft), tooltip, usage?.FiveHourLeft);
    }

    private void UpdatePrimaryIcon(TrayMetricDisplay display)
    {
        _primaryMetricKey = display.Key;
        _notifyIcon.Text = TrimNotifyText(display.Tooltip);
        var nextIcon = TrayIconRenderer.Create(display.ValueLabel, display.ShortLabel, display.RemainingPercent);
        var oldIcon = _trayIcon;
        _trayIcon = nextIcon;
        _notifyIcon.Icon = _trayIcon;
        oldIcon?.Dispose();
    }

    private void UpdateExtraIcons(IReadOnlyList<TrayMetricDisplay> displays)
    {
        var wanted = displays.Select(display => display.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _extraNotifyIcons.Keys.ToArray())
        {
            if (!wanted.Contains(key) || key.Equals(_primaryMetricKey, StringComparison.OrdinalIgnoreCase))
            {
                DisposeExtraTrayIcon(key);
            }
        }

        foreach (var display in displays)
        {
            if (display.Key.Equals(_primaryMetricKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_extraNotifyIcons.TryGetValue(display.Key, out var notifyIcon))
            {
                notifyIcon = CreateExtraNotifyIcon();
                _extraNotifyIcons[display.Key] = notifyIcon;
            }

            notifyIcon.Text = TrimNotifyText(display.Tooltip);
            var nextIcon = TrayIconRenderer.Create(display.ValueLabel, display.ShortLabel, display.RemainingPercent);
            if (_extraTrayIcons.Remove(display.Key, out var oldIcon))
            {
                oldIcon.Dispose();
            }

            _extraTrayIcons[display.Key] = nextIcon;
            notifyIcon.Icon = nextIcon;
            notifyIcon.Visible = true;
        }
    }

    private NotifyIcon CreateExtraNotifyIcon()
    {
        var notifyIcon = new NotifyIcon();
        notifyIcon.MouseUp += OnTrayMouseUp;
        return notifyIcon;
    }

    private void DisposeExtraTrayIcons()
    {
        foreach (var key in _extraNotifyIcons.Keys.ToArray())
        {
            DisposeExtraTrayIcon(key);
        }
    }

    private void DisposeExtraTrayIcon(string key)
    {
        if (_extraNotifyIcons.Remove(key, out var notifyIcon))
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }

        if (_extraTrayIcons.Remove(key, out var icon))
        {
            icon.Dispose();
        }
    }

    private static string Tooltip(TrayMetricRow metric)
    {
        return Localizer.F("tray.tooltip.metric", metric.DisplayName, PercentOrDash(metric.RemainingPercent), metric.Detail);
    }

    private static string TrimNotifyText(string text)
    {
        // NotifyIcon tooltips allow 127 chars on modern .NET; the old 63 cap cut the
        // trailing weekly value off long labels. Trim on a char boundary with an
        // ellipsis and never split a surrogate pair (i18n-7).
        const int max = 127;
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }

    private static string PercentOrDash(int? value)
    {
        return value is null ? Localizer.L("usage.percent.dash") : Localizer.F("usage.percent.value", value);
    }

    private sealed record TrayMetricDisplay(string Key, string ShortLabel, string ValueLabel, string Tooltip, int? RemainingPercent);
}
