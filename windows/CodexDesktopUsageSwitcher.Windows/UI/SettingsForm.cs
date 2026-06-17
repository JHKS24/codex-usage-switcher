using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.UI.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CodexDesktopUsageSwitcher.Windows.UI;

// The redesigned settings window: a WebView2 hosting the HTML/CSS settings page
// (UI/Web/settings.html). The C# side maps the domain snapshot to a camelCase JSON
// payload and posts it; the page posts back user actions (login, save, toggle, etc.)
// that route to the SAME SwitcherService calls the WinForms version used. Mirrors
// WebUsagePopupForm/DashboardForm's HTML-load / NavigationCompleted-post plumbing.
// The public surface (ctor + SettingsChanged) is unchanged so the tray needs no edit.
internal sealed class SettingsForm : Form
{
    private const int LogicalWidth = 760;
    private const int LogicalHeight = 680;
    private static readonly Regex ProfileNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string Html = LoadHtml();
    private readonly SwitcherService _service;
    private readonly WebView2 _web = new();
    private bool _ready;
    private string? _pendingPayload;
    // Mirrors the WinForms _renderingTrayMetrics guard: while we re-render the page from
    // a fresh snapshot, any toggle message that races in is ignored so a programmatic
    // re-render can never re-fire SetTrayMetricVisibility.
    private bool _rendering;

    public event EventHandler? SettingsChanged;

    public SettingsForm(SwitcherService service)
    {
        _service = service;
        Text = "Codex Usage Switcher 설정";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Body;
        ShowInTaskbar = true;
        MinimumSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(520));
        ClientSize = new Size(LogicalToDeviceUnits(LogicalWidth), LogicalToDeviceUnits(LogicalHeight));
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Theme.Body;
        Controls.Add(_web);
        _ = InitializeAsync();
        Shown += async (_, _) => await RefreshAsync().ConfigureAwait(true);
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
        core.NavigateToString(Html);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // async void event handler: the single exception boundary for every user action.
        // Any failure becomes a status message rather than crashing the message pump.
        var message = ParseMessage(e.WebMessageAsJson);
        if (message is null)
        {
            return;
        }

        try
        {
            await DispatchAsync(message.Value).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PostStatus($"작업 실패: {ex.Message}", isError: true);
        }
    }

    private async Task DispatchAsync(WebMessage message)
    {
        switch (message.Action)
        {
            // "ready" only signals the page loaded; the Shown handler drives the first
            // load and Post() queues the payload until NavigationCompleted, so there is
            // no separate refresh here (avoids a duplicate snapshot fetch on open).
            case "refresh": await RefreshAsync().ConfigureAwait(true); break;
            case "codexLogin": await LaunchCodexLoginAsync(message.Name).ConfigureAwait(true); break;
            case "saveCurrent": await SaveCurrentProfileAsync(message.Name).ConfigureAwait(true); break;
            case "openFolder": OpenProfilesFolder(); break;
            case "toggleMetric": await ToggleMetricAsync(message.Key, message.Visible).ConfigureAwait(true); break;
            case "claudeUsageLogin": await LaunchClaudeLoginAsync().ConfigureAwait(true); break;
            case "claudeCodeLogin": await LaunchClaudeCodeLoginAsync().ConfigureAwait(true); break;
            case "doctor": await ShowDoctorAsync().ConfigureAwait(true); break;
            default: break;
        }
    }

    private async Task RefreshAsync()
    {
        PostStatus("프로필과 사용량을 불러오는 중...");
        var snapshot = await _service.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
        RenderSnapshot(snapshot);
    }

    private void RenderSnapshot(SwitcherSnapshot snapshot)
    {
        _rendering = true;
        try
        {
            Post(new
            {
                type = "data",
                status = $"갱신 {snapshot.RefreshedAt:HH:mm:ss}",
                profiles = BuildProfiles(snapshot),
                metrics = BuildMetrics(snapshot),
                claude = BuildClaude(snapshot.ClaudeUsage),
            });
        }
        finally
        {
            _rendering = false;
        }
    }

    private static object[] BuildProfiles(SwitcherSnapshot snapshot)
    {
        var active = snapshot.Current.MatchedProfile ?? snapshot.Current.ActiveLabel;
        return snapshot.Profiles
            .Select(profile =>
            {
                var usage = snapshot.UsageRows.FirstOrDefault(row => row.Profile == profile.Name);
                var hasError = !string.IsNullOrWhiteSpace(usage?.Error);
                return (object)new
                {
                    name = profile.Name,
                    plan = usage?.Plan,
                    auth = profile.HasAuth && !hasError,
                    usage = UsageText(usage),
                    active = string.Equals(profile.Name, active, StringComparison.OrdinalIgnoreCase),
                };
            })
            .ToArray();
    }

    private static object[] BuildMetrics(SwitcherSnapshot snapshot)
    {
        return snapshot.TrayMetrics
            .Select(metric => (object)new
            {
                key = metric.Key,
                label = metric.DisplayName,
                remaining = metric.RemainingPercent,
                visible = metric.Visible,
            })
            .ToArray();
    }

    // The Claude /usage (OAuth) connection state for the login·diagnostics card. This is the
    // only Claude connection the app tracks (claude-usage --json); "Claude Code" CLI login is a
    // fire-and-forget action with no tracked state, so it has no chip.
    private static object BuildClaude(ClaudeUsage usage) => new
    {
        authenticated = usage.Authenticated,
        message = usage.Message,
        fiveHourLeft = usage.FiveHourLeft,
        weeklyLeft = usage.WeeklyLeft,
        fiveHourReset = UsageFormatting.ResetText(usage.FiveHourReset),
        weeklyReset = UsageFormatting.ResetText(usage.WeeklyReset),
    };

    private static string UsageText(UsageRow? usage)
    {
        if (usage is null || !string.IsNullOrWhiteSpace(usage.Error))
        {
            return string.Empty;
        }

        return $"5H {Pct(usage.FiveHourLeft)} · 주 {Pct(usage.WeeklyLeft)}";
    }

    private static string Pct(int? value)
    {
        return value is null ? "-" : $"{value}%";
    }

    private async Task LaunchCodexLoginAsync(string? name)
    {
        if (!TryGetProfileName(name, out var profile))
        {
            return;
        }

        var outcome = await _service.StartCodexLoginAsync(profile, CancellationToken.None).ConfigureAwait(true);
        await ReportActionAsync(outcome, "Codex 로그인").ConfigureAwait(true);
    }

    private async Task SaveCurrentProfileAsync(string? name)
    {
        if (!TryGetProfileName(name, out var profile))
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "현재 Codex 로그인을 이 프로필로 저장할까요?\n\n토큰 내용은 표시하지 않습니다. Codex Desktop이 실행 중이면 CLI가 거부할 수 있습니다.",
            "현재 계정 저장",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var outcome = await _service.SaveCurrentProfileAsync(profile, CancellationToken.None).ConfigureAwait(true);
        await ReportActionAsync(outcome, "현재 계정 저장").ConfigureAwait(true);
    }

    private async Task ToggleMetricAsync(string? key, bool visible)
    {
        // The reentrancy guard: a re-render replaces the DOM, so a stale toggle message
        // arriving mid-render must not persist a value the user did not set.
        if (_rendering || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            await _service.SetTrayMetricVisibilityAsync(key, visible, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A locked settings.json (sync clients, antivirus) must surface and re-render
            // so the page reverts to the authoritative state instead of the optimistic one.
            PostStatus($"설정 저장 실패: {ex.Message}", isError: true);
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        PostStatus($"작업표시줄 표시 {(visible ? "켜짐" : "꺼짐")}");
        await RefreshAsync().ConfigureAwait(true);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LaunchClaudeLoginAsync()
    {
        var outcome = await _service.StartClaudeLoginAsync(CancellationToken.None).ConfigureAwait(true);
        await ReportActionAsync(outcome, "Claude 사용량 로그인").ConfigureAwait(true);
    }

    private async Task LaunchClaudeCodeLoginAsync()
    {
        var outcome = await _service.StartClaudeCodeLoginAsync(CancellationToken.None).ConfigureAwait(true);
        await ReportActionAsync(outcome, "Claude Code 로그인").ConfigureAwait(true);
    }

    private async Task ShowDoctorAsync()
    {
        var outcome = await _service.DoctorAsync(CancellationToken.None).ConfigureAwait(true);
        MessageBox.Show(
            this,
            outcome.Message,
            "진단",
            MessageBoxButtons.OK,
            outcome.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    // Successful actions update the status line and re-load the snapshot (a fresh login or
    // save changes the profile list / auth state); failures get a MessageBox like before.
    private async Task ReportActionAsync(CommandOutcome outcome, string title)
    {
        if (!outcome.Ok)
        {
            PostStatus(outcome.Message, isError: true);
            MessageBox.Show(this, outcome.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        PostStatus(outcome.Message);
        await RefreshAsync().ConfigureAwait(true);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGetProfileName(string? name, out string profile)
    {
        profile = (name ?? string.Empty).Trim();
        if (ProfileNamePattern.IsMatch(profile))
        {
            return true;
        }

        PostStatus(
            "프로필 이름은 영문/숫자로 시작하고, 영문/숫자/밑줄/대시/점만 사용할 수 있습니다.",
            isError: true);
        return false;
    }

    private static void OpenProfilesFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = Path.Combine(home, ".codex-switch", "profiles");
        Directory.CreateDirectory(profiles);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = profiles,
            UseShellExecute = true,
        });
    }

    private void PostStatus(string text, bool isError = false)
    {
        Post(new { type = "status", text, error = isError });
    }

    private void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (_ready && _web.CoreWebView2 is not null && !IsDisposed)
        {
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        else
        {
            _pendingPayload = json;
        }
    }

    private static WebMessage? ParseMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new WebMessage(
                Action: root.TryGetProperty("action", out var a) ? a.GetString() : null,
                Name: root.TryGetProperty("name", out var n) ? n.GetString() : null,
                Key: root.TryGetProperty("key", out var k) ? k.GetString() : null,
                Visible: root.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string LoadHtml()
    {
        var assembly = typeof(SettingsForm).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("settings.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("settings.html embedded resource not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("settings.html embedded resource stream is null.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly record struct WebMessage(string? Action, string? Name, string? Key, bool Visible);
}
