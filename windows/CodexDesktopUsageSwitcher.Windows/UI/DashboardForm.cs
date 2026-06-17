using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using CodexDesktopUsageSwitcher.Windows.UI.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// A resizable WebView2 window that renders the insights dashboard (UI/Web/dashboard.html):
// daily model-stacked bars, a weekday×hour heatmap, per-turn token stats, and live limit
// donuts for both providers (Codex/Claude toggle in the page). Reading the transcripts is
// slow, so every load posts {type:"loading"} first, computes the insights on a background
// thread, then posts {type:"data"} and a {type:"limits"} message (the latter is cheap and
// reused by SetSnapshot on the tray's 10-min refresh). Mirrors WebUsagePopupForm's
// HTML-load / camelCase-JSON message plumbing.
internal sealed class DashboardForm : Form
{
    private const int LogicalWidth = 980;
    private const int LogicalHeight = 720;
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string Html = LoadHtml();
    private readonly UsageHistoryService _history;
    private readonly WebView2 _web = new();
    private CoreWebView2? _core;
    private SwitcherSnapshot? _snapshot;
    private bool _ready;
    private int _generation;
    private CancellationTokenSource? _refreshCts;
    private bool _initialLoadQueued;

    public DashboardForm(UsageHistoryService history)
    {
        _history = history;
        Text = Localizer.L("dashboard.title");
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Body;
        ShowInTaskbar = true;
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(480));
        ClientSize = new Size(LogicalToDeviceUnits(LogicalWidth), LogicalToDeviceUnits(LogicalHeight));
        TrySetIcon();
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Theme.Body;
        Controls.Add(_web);
        _ = InitializeAsync();
        Localizer.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        Text = Localizer.L("dashboard.title");
        Post(new { type = "i18n", language = Localizer.LanguageCode, strings = WebLocalization.Strings() });
        Post(BuildLimits(_snapshot)); // reset text is host-computed and localized
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The first compute is deferred until the page has loaded so the {loading:true}
        // and the result both reach a live WebView; if init hasn't finished yet, mark it
        // queued and let NavigationCompleted kick it off.
        if (_ready)
        {
            _ = RefreshAsync();
        }
        else
        {
            _initialLoadQueued = true;
        }
    }

    private void TrySetIcon()
    {
        // The owner form's icon is the app icon; reuse it when present so the dashboard
        // matches in the taskbar. A missing icon is cosmetic, never fatal.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
            if (File.Exists(path))
            {
                Icon = new Icon(path);
            }
        }
        catch (ArgumentException)
        {
            // A malformed icon file: leave the default window icon in place.
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async().ConfigureAwait(true);
            _core = _web.CoreWebView2;
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.IsZoomControlEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;
            _core.WebMessageReceived += OnWebMessage;
            _core.NavigationCompleted += OnNavigationCompleted;
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(WebLocalization.DocumentCreatedScript());
            _core.NavigateToString(Html);
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing/failed: log so init failure isn't a silent blank window
            // (this is a fire-and-forget task, so nothing else would observe the exception).
            CrashLog.Write(ex);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ready = true;
        if (_initialLoadQueued)
        {
            _initialLoadQueued = false;
            _ = RefreshAsync();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? action;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (action == "refresh")
        {
            _ = RefreshAsync(force: true); // manual refresh bypasses the freshness gate
        }
    }

    // A new refresh supersedes any in-flight one: it bumps the generation and cancels the
    // prior token. The slow load runs on a background thread (the service parallelizes the two
    // providers); the result is posted only if this is still the latest generation and the form
    // is alive, so a superseded or force-refresh-overtaken load can never paint stale data
    // Posts run on the UI thread (ConfigureAwait(true)). Never early-returns.
    private async Task RefreshAsync(bool force = false)
    {
        var generation = ++_generation;
        var prior = _refreshCts;
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        prior?.Cancel(); // signal the prior load; it disposes its own cts in its finally

        try
        {
            Post(new { type = "loading" });
            var now = DateTimeOffset.Now;
            var result = await _history.LoadAllAsync(now, force, cts.Token).ConfigureAwait(true);
            if (generation != _generation || IsDisposed)
            {
                return; // superseded by a newer refresh, or the form closed
            }

            Post(BuildState(now, result.Claude, result.Codex));
            Post(BuildLimits(_snapshot));
        }
        catch (OperationCanceledException)
        {
            // superseded before the load started — the newer refresh will paint
        }
        catch (Exception ex)
        {
            // fire-and-forget: route to the log boundary instead of vanishing as an unobserved
            // task exception (the freshness load otherwise degrades to empty, so this is rare).
            CrashLog.Write(ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
            }

            cts.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Localizer.LanguageChanged -= OnLanguageChanged;
            _refreshCts?.Cancel();
            if (_core is not null)
            {
                _core.WebMessageReceived -= OnWebMessage;
                _core.NavigationCompleted -= OnNavigationCompleted;
            }
        }

        base.Dispose(disposing);
    }

    // Refresh just the limit donuts/reset text from a new snapshot, without rereading
    // transcripts. Called when the dashboard opens and on the tray's 10-min refresh.
    public void SetSnapshot(SwitcherSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Post(BuildLimits(snapshot));
    }

    private void Post(object payload)
    {
        if (!_ready || _web.CoreWebView2 is null || IsDisposed)
        {
            return;
        }

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static object BuildState(DateTimeOffset now, UsageInsights claude, UsageInsights codex)
    {
        return new
        {
            type = "data",
            generatedAt = now.ToString("HH:mm:ss"),
            providers = new[]
            {
                new { key = "codex", title = "CODEX", cost = HasCost(codex), insights = codex },
                new { key = "claude", title = "CLAUDE", cost = HasCost(claude), insights = claude },
            },
        };
    }

    // Show estimated cost for a provider only when some events are actually priced; an entirely
    // unpriced provider (no model matched the price table) keeps the cost column hidden rather
    // than showing a misleading $0.
    private static bool HasCost(UsageInsights insights) => insights.Daily.Any(d => d.CostUsd > 0);

    // The limits message carries only the live 5h/weekly remaining-% and reset text per
    // provider. A null snapshot (no refresh yet) posts nulls so the donuts show "—".
    private static object BuildLimits(SwitcherSnapshot? snapshot)
    {
        var active = ActiveCodexProfile(snapshot);
        var codex = snapshot?.UsageRows.FirstOrDefault(row => row.Profile == active);
        var claude = snapshot?.ClaudeUsage;
        return new
        {
            type = "limits",
            providers = new[]
            {
                new
                {
                    key = "codex",
                    fiveHour = Donut(codex?.FiveHourLeft, codex?.FiveHourReset),
                    week = Donut(codex?.WeeklyLeft, codex?.WeeklyReset),
                },
                new
                {
                    key = "claude",
                    fiveHour = Donut(claude?.FiveHourLeft, claude?.FiveHourReset),
                    week = Donut(claude?.WeeklyLeft, claude?.WeeklyReset),
                },
            },
        };
    }

    private static object Donut(int? percent, string? reset)
    {
        return new { percent, reset = UsageFormatting.ResetText(reset) };
    }

    private static string? ActiveCodexProfile(SwitcherSnapshot? snapshot)
    {
        var current = snapshot?.Current;
        return current?.MatchedProfile ?? current?.ActiveLabel;
    }

    private static string LoadHtml()
    {
        var assembly = typeof(DashboardForm).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("dashboard.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("dashboard.html embedded resource not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("dashboard.html embedded resource stream is null.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
