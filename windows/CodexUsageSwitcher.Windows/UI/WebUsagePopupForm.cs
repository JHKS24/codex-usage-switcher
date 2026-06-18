using System.Text.Json;
using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;
using CodexUsageSwitcher.Windows.UI.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CodexUsageSwitcher.Windows.UI;

// The redesigned popup: a borderless form hosting a WebView2 that renders the
// HTML/CSS dashboard (UI/Web/popup.html). The C# side maps the domain snapshot to a
// JSON payload (with computed reset countdowns) and posts it; the page posts back
// user actions. Selection is tracked in JS; a "switch" message carries the profile.
internal sealed class WebUsagePopupForm : Form, IUsagePopup
{
    private const int LogicalWidth = 400;
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string Html = LoadHtml();
    private readonly WebView2 _web = new();
    private bool _ready;
    private string? _pendingPayload;
    private SwitcherSnapshot? _lastSnapshot;
    private bool _lastBusy;
    private string? _lastError;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? ClaudeUsageLoginRequested;
    public event EventHandler? DashboardRequested;
    public event Func<string, Task>? SwitchProfileRequested;

    public Form Window => this;

    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public WebUsagePopupForm()
    {
        Text = Localizer.L("popup.windowTitle");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Body;
        ClientSize = new Size(LogicalToDeviceUnits(LogicalWidth), LogicalToDeviceUnits(420));
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Theme.Body;
        Controls.Add(_web);
        _ = InitializeAsync();
        Localizer.LanguageChanged += OnLanguageChanged;
    }

    public void Render(SwitcherSnapshot? snapshot, bool busy, string? error)
    {
        _lastSnapshot = snapshot;
        _lastBusy = busy;
        _lastError = error;
        var payload = JsonSerializer.Serialize(BuildPayload(snapshot, busy, error), JsonOptions);
        if (_ready && _web.CoreWebView2 is not null)
        {
            _web.CoreWebView2.PostWebMessageAsJson(payload);
        }
        else
        {
            _pendingPayload = payload;
        }
    }

    // Live language switch: push the new string table (re-localizes static labels) and re-render the
    // last snapshot so the host-computed strings update too.
    private void OnLanguageChanged()
    {
        if (IsDisposed || _web.CoreWebView2 is null || !_ready)
        {
            return;
        }

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
            new { type = "i18n", language = Localizer.LanguageCode, strings = WebLocalization.Strings() }, JsonOptions));
        Render(_lastSnapshot, _lastBusy, _lastError);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Localizer.LanguageChanged -= OnLanguageChanged;
        }

        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Theme.ApplyRoundedRegion(this, LogicalToDeviceUnits(12));
    }

    private async Task InitializeAsync()
    {
        await _web.EnsureCoreWebView2Async().ConfigureAwait(true);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) =>
        {
            _ready = true;
            if (_pendingPayload is not null)
            {
                core.PostWebMessageAsJson(_pendingPayload);
                _pendingPayload = null;
            }
        };
        // Inject the localized string table before the page loads so static labels localize on the
        // first paint.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebLocalization.DocumentCreatedScript());
        core.NavigateToString(Html);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // async void event handler: exceptions land on the app-wide boundary. JSON
        // parsing is guarded so a malformed message can never take the popup down.
        string? action;
        string? profile;
        int? height;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            profile = root.TryGetProperty("profile", out var p) ? p.GetString() : null;
            height = root.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : null;
        }
        catch (JsonException)
        {
            return;
        }

        switch (action)
        {
            case "refresh": RefreshRequested?.Invoke(this, EventArgs.Empty); break;
            case "settings": SettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case "close": Hide(); break;
            case "quit": QuitRequested?.Invoke(this, EventArgs.Empty); break;
            case "dashboard": DashboardRequested?.Invoke(this, EventArgs.Empty); break;
            case "claudeLogin": ClaudeUsageLoginRequested?.Invoke(this, EventArgs.Empty); break;
            case "resize" when height is int css: ApplyContentHeight(css); break;
            case "switch" when !string.IsNullOrWhiteSpace(profile) && SwitchProfileRequested is { } handler:
                await handler(profile).ConfigureAwait(true);
                break;
            default: break;
        }
    }

    private void ApplyContentHeight(int cssHeight)
    {
        var device = (int)Math.Ceiling(cssHeight * DeviceDpi / 96.0);
        var area = (IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position)).WorkingArea;
        var height = Math.Min(device, area.Height - LogicalToDeviceUnits(24));
        if (height <= 0 || height == ClientSize.Height)
        {
            return;
        }

        ClientSize = new Size(ClientSize.Width, height);
        if (Bottom > area.Bottom - 8)
        {
            Top = Math.Max(area.Top + 8, area.Bottom - 8 - Height);
        }
    }

    private static object BuildPayload(SwitcherSnapshot? snapshot, bool busy, string? error)
    {
        var active = CurrentProfileName(snapshot);
        var codexUsage = snapshot?.UsageRows.FirstOrDefault(row => row.Profile == active);
        var claude = snapshot?.ClaudeUsage;
        return new
        {
            busy,
            error,
            freshText = FreshText(snapshot, busy),
            subText = SubText(snapshot),
            codex = new
            {
                profile = active,
                active = active != "unknown",
                fiveHour = Limit(codexUsage?.FiveHourLeft, codexUsage?.FiveHourReset),
                week = Limit(codexUsage?.WeeklyLeft, codexUsage?.WeeklyReset),
            },
            claude = new
            {
                connected = claude?.Authenticated == true,
                state = claude?.Authenticated == true ? Localizer.L("popup.claude.connected") : Localizer.L("common.loginRequired"),
                fiveHour = Limit(claude?.FiveHourLeft, claude?.FiveHourReset),
                week = Limit(claude?.WeeklyLeft, claude?.WeeklyReset),
                message = claude?.Message,
            },
            profiles = (snapshot?.Profiles ?? []).Select(profile => new
            {
                name = profile.Name,
                active = profile.Name == active,
                quota = QuotaText(profile, snapshot?.UsageRows.FirstOrDefault(row => row.Profile == profile.Name)),
            }).ToArray(),
            selected = (string?)null,
        };
    }

    private static object Limit(int? percent, string? reset)
    {
        return new { percent, level = Level(percent), reset = UsageFormatting.ResetText(reset) };
    }

    private static string Level(int? percent)
    {
        return percent switch
        {
            null => "none",
            < 20 => "low",
            < 50 => "warn",
            _ => "ok",
        };
    }

    private static string FreshText(SwitcherSnapshot? snapshot, bool busy)
    {
        if (busy)
        {
            return Localizer.L("popup.refresh.refreshing");
        }

        if (snapshot is null)
        {
            return Localizer.L("popup.refresh.loading");
        }

        var age = DateTimeOffset.Now - snapshot.RefreshedAt;
        if (age < TimeSpan.FromSeconds(60))
        {
            return Localizer.L("popup.refresh.justNow");
        }

        return age < TimeSpan.FromHours(1)
            ? Localizer.F("popup.refresh.minutesAgo", (int)age.TotalMinutes)
            : Localizer.F("popup.refresh.hoursAgo", (int)age.TotalHours);
    }

    private static string SubText(SwitcherSnapshot? snapshot)
    {
        return snapshot is null
            ? Localizer.L("popup.subtext.autoRefresh")
            : Localizer.F("popup.subtext.autoRefreshWithTime", snapshot.RefreshedAt.ToString("HH:mm:ss"));
    }

    private static string QuotaText(ProfileSummary profile, UsageRow? usage)
    {
        if (!profile.HasAuth || !string.IsNullOrWhiteSpace(usage?.Error))
        {
            return Localizer.L("common.loginRequired");
        }

        return Localizer.F("usage.quotaSummary", Pct(usage?.FiveHourLeft), Pct(usage?.WeeklyLeft));
    }

    private static string Pct(int? value)
    {
        return value is null ? "-" : $"{value}%";
    }

    private static string CurrentProfileName(SwitcherSnapshot? snapshot)
    {
        var current = snapshot?.Current;
        return current?.MatchedProfile ?? current?.ActiveLabel ?? "unknown";
    }

    private static string LoadHtml()
    {
        var assembly = typeof(WebUsagePopupForm).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("popup.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("popup.html embedded resource not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("popup.html embedded resource stream is null.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
