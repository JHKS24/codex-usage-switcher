using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;

namespace CodexDesktopUsageSwitcher.Windows.Application;

internal sealed class SwitcherService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(45);
    private static readonly TrayMetricDefinition[] TrayMetricDefinitions =
    [
        new("codex:5h", "codex", "Codex 5시간", "C5", Weekly: false),
        new("codex:week", "codex", "Codex 주간", "CW", Weekly: true),
        new("codexsub:5h", "codexsub", "CodexSub 5시간", "S5", Weekly: false),
        new("codexsub:week", "codexsub", "CodexSub 주간", "SW", Weekly: true),
        new("claude:5h", "claude", "Claude 5시간", "L5", Weekly: false),
        new("claude:week", "claude", "Claude 주간", "LW", Weekly: true),
    ];
    private readonly ISwitcherClient _client;
    private readonly IInteractiveCliLauncher _interactiveCliLauncher;
    private readonly ISettingsStore _settingsStore;

    public SwitcherService(
        ISwitcherClient client,
        IInteractiveCliLauncher interactiveCliLauncher,
        ISettingsStore settingsStore)
    {
        _client = client;
        _interactiveCliLauncher = interactiveCliLauncher;
        _settingsStore = settingsStore;
    }

    public async Task<SwitcherSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var trayMetricVisibilityTask = _settingsStore.LoadTrayMetricVisibilityAsync(cancellationToken);
        var (profiles, current, usageRows, claudeUsage) = await LoadBackendSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var trayMetricVisibility = await trayMetricVisibilityTask.ConfigureAwait(false);
        var providers = BuildProviderRows(current, usageRows, claudeUsage);
        var trayMetrics = BuildTrayMetricRows(profiles, current, usageRows, claudeUsage, trayMetricVisibility);

        return new SwitcherSnapshot(
            profiles,
            current,
            usageRows,
            claudeUsage,
            providers,
            trayMetrics,
            DateTimeOffset.Now);
    }

    private async Task<(IReadOnlyList<ProfileSummary> Profiles, CurrentState Current, IReadOnlyList<UsageRow> UsageRows, ClaudeUsage ClaudeUsage)>
        LoadBackendSnapshotAsync(CancellationToken cancellationToken)
    {
        // One combined CLI call boots the interpreter once instead of four times per
        // refresh. Older backend scripts without `snapshot` fall through to the
        // per-command path below.
        var result = await _client.RunAsync(["snapshot", "--json"], UsageTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            using var document = TryParseObject(result.Stdout);
            if (document is not null)
            {
                var root = document.RootElement;
                return (
                    ProfilesFromElement(root),
                    root.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object
                        ? CurrentFromElement(current)
                        : UnknownCurrent(),
                    UsageRowsFromElement(root),
                    root.TryGetProperty("claude_usage", out var claude) && claude.ValueKind == JsonValueKind.Object
                        ? ClaudeUsageFromElement(claude)
                        : ClaudeUsageUnavailable());
            }
        }

        var profilesTask = LoadProfilesAsync(cancellationToken);
        var currentTask = LoadCurrentAsync(cancellationToken);
        var usageTask = LoadUsageAsync(cancellationToken);
        var claudeTask = LoadClaudeUsageAsync(cancellationToken);
        return (
            await profilesTask.ConfigureAwait(false),
            await currentTask.ConfigureAwait(false),
            await usageTask.ConfigureAwait(false),
            await claudeTask.ConfigureAwait(false));
    }

    public Task SetTrayMetricVisibilityAsync(string metricKey, bool visible, CancellationToken cancellationToken)
    {
        return _settingsStore.SetTrayMetricVisibilityAsync(metricKey, visible, cancellationToken);
    }

    public async Task<CommandOutcome> SwitchProfileAsync(string profile, CancellationToken cancellationToken)
    {
        var stop = await _client.RunAsync(
            ["stop-codex", "--json", "--grace-seconds", "4"],
            SwitchTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!stop.Ok)
        {
            return OutcomeFromResult(stop, "Codex 종료 실패");
        }

        var use = await _client.RunAsync(
            ["use", profile, "--apply", "--json"],
            SwitchTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!use.Ok)
        {
            return OutcomeFromResult(use, "전환 실패");
        }

        var open = await _client.RunAsync(["open"], StatusTimeout, cancellationToken).ConfigureAwait(false);
        if (!open.Ok)
        {
            return new CommandOutcome(true, use.ExitCode, $"{profile} 전환 완료, Codex 자동 실행 실패", use.Stdout, open.Stderr);
        }

        return new CommandOutcome(true, 0, $"{profile} 전환 완료", use.Stdout, use.Stderr);
    }

    public async Task<CommandOutcome> StartClaudeLoginAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _interactiveCliLauncher.LaunchClaudeLogin();
    }

    public async Task<CommandOutcome> StartClaudeCodeLoginAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _interactiveCliLauncher.LaunchClaudeCodeLogin();
    }

    public async Task<CommandOutcome> StartCodexLoginAsync(string profile, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _interactiveCliLauncher.LaunchCodexLogin(profile);
    }

    public async Task<CommandOutcome> SaveCurrentProfileAsync(string profile, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _interactiveCliLauncher.LaunchSaveCurrentProfile(profile);
    }

    public async Task<CommandOutcome> DoctorAsync(CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(["doctor", "--json"], StatusTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return OutcomeFromResult(result, "doctor 실패");
        }

        using var document = TryParseObject(result.Stdout);
        if (document is null)
        {
            return OutcomeFromResult(result, "doctor 응답을 해석할 수 없습니다");
        }

        var root = document.RootElement;
        var lines = new List<string>
        {
            $"platform: {StringValue(root, "platform") ?? "unknown"}",
            $"tool: {StringValue(root, "tool") ?? "unknown"}",
            $"profiles_root: {StringValue(root, "profiles_root") ?? "unknown"}",
            $"profiles: {ArrayLength(root, "profiles")}",
            $"codex_cli: {StringValue(root, "codex_cli") ?? "missing"}",
            $"codex_app: {StringValue(root, "codex_app") ?? "missing"}",
            $"codex_running: {BoolValue(root, "codex_running")?.ToString() ?? "unknown"}",
            $"process_check: {StringValue(root, "process_check") ?? "unknown"}",
        };
        return new CommandOutcome(true, 0, string.Join(Environment.NewLine, lines), result.Stdout, result.Stderr);
    }

    private async Task<IReadOnlyList<ProfileSummary>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(["list", "--json"], StatusTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return [];
        }

        using var document = TryParseObject(result.Stdout);
        return document is null ? [] : ProfilesFromElement(document.RootElement);
    }

    private async Task<CurrentState> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(["current", "--json"], StatusTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return UnknownCurrent();
        }

        using var document = TryParseObject(result.Stdout);
        return document is null ? UnknownCurrent() : CurrentFromElement(document.RootElement);
    }

    private async Task<IReadOnlyList<UsageRow>> LoadUsageAsync(CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(["usage", "--json"], UsageTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return [];
        }

        using var document = TryParseObject(result.Stdout);
        return document is null ? [] : UsageRowsFromElement(document.RootElement);
    }

    private async Task<ClaudeUsage> LoadClaudeUsageAsync(CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(["claude-usage", "--json"], UsageTimeout, cancellationToken).ConfigureAwait(false);

        // claude-usage may exit nonzero or emit empty/non-JSON output; degrade locally
        // instead of throwing, otherwise a single bad provider breaks the whole snapshot.
        using var document = TryParseObject(result.Stdout);
        return document is null ? ClaudeUsageUnavailable() : ClaudeUsageFromElement(document.RootElement);
    }

    private static IReadOnlyList<ProfileSummary> ProfilesFromElement(JsonElement container)
    {
        if (!container.TryGetProperty("profiles", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rows.EnumerateArray()
            .Select(row => new ProfileSummary(
                StringValue(row, "profile") ?? "",
                BoolValue(row, "exists") ?? false))
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .ToArray();
    }

    private static CurrentState CurrentFromElement(JsonElement element)
    {
        return new CurrentState(
            StringValue(element, "active_label") ?? "unknown",
            StringValue(element, "matched_profile"),
            StringValue(element, "auth_match") ?? "unknown",
            BoolValue(element, "codex_running"));
    }

    private static IReadOnlyList<UsageRow> UsageRowsFromElement(JsonElement container)
    {
        if (!container.TryGetProperty("usage", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rows.EnumerateArray()
            .Select(row => new UsageRow(
                StringValue(row, "profile") ?? "",
                StringValue(row, "plan"),
                IntValue(row, "five_hour_left"),
                StringValue(row, "five_hour_reset"),
                IntValue(row, "weekly_left"),
                StringValue(row, "weekly_reset"),
                StringValue(row, "error")))
            .Where(row => !string.IsNullOrWhiteSpace(row.Profile))
            .ToArray();
    }

    private static ClaudeUsage ClaudeUsageFromElement(JsonElement element)
    {
        var authenticated = BoolValue(element, "authenticated") ?? false;
        var error = StringValue(element, "error");
        if (!authenticated)
        {
            var message = error switch
            {
                "rate_limited" => "잠시 후 재시도",
                "network" => "네트워크 오류 · 잠시 후 재시도",
                _ => "로그인 필요",
            };
            return new ClaudeUsage(false, null, null, null, null, message);
        }

        if (error is "network" or "usage_request_failed")
        {
            // Logged in, but the usage fetch itself failed transiently.
            return new ClaudeUsage(true, null, null, null, null, "네트워크 오류 · 잠시 후 재시도");
        }

        var five = RemainingPercentFromUtilization(NestedDouble(element, "five_hour", "utilization"));
        var fiveReset = NestedString(element, "five_hour", "resets_at");
        var week = RemainingPercentFromUtilization(NestedDouble(element, "seven_day", "utilization"));
        var weekReset = NestedString(element, "seven_day", "resets_at");
        var hasUsage = five is not null || week is not null;
        return new ClaudeUsage(true, five, fiveReset, week, weekReset, hasUsage ? null : "표시값 없음");
    }

    private static CurrentState UnknownCurrent()
    {
        return new CurrentState("unknown", null, "unknown", null);
    }

    private static ClaudeUsage ClaudeUsageUnavailable()
    {
        return new ClaudeUsage(false, null, null, null, null, "조회 실패");
    }

    private static IReadOnlyList<TrayMetricRow> BuildTrayMetricRows(
        IReadOnlyList<ProfileSummary> profiles,
        CurrentState current,
        IReadOnlyList<UsageRow> usageRows,
        ClaudeUsage claudeUsage,
        IReadOnlySet<string> visibleMetricKeys)
    {
        var active = ActiveCodexProfile(current);
        var codexUsage = usageRows.FirstOrDefault(row => row.Profile.Equals(active, StringComparison.OrdinalIgnoreCase));
        var codexSubUsage = FindCodexSubUsage(profiles, usageRows, active);

        return TrayMetricDefinitions
            .Select(metric => new TrayMetricRow(
                metric.Key,
                metric.ProviderId,
                metric.DisplayName,
                metric.ShortLabel,
                visibleMetricKeys.Contains(metric.Key),
                ResolveTrayMetric(metric, codexUsage, codexSubUsage, claudeUsage),
                ResolveTrayMetricDetail(metric, active, codexUsage, codexSubUsage, claudeUsage)))
            .ToArray();
    }

    private static int? ResolveTrayMetric(
        TrayMetricDefinition metric,
        UsageRow? codexUsage,
        UsageRow? codexSubUsage,
        ClaudeUsage claudeUsage)
    {
        return metric.ProviderId switch
        {
            "codex" => metric.Weekly ? codexUsage?.WeeklyLeft : codexUsage?.FiveHourLeft,
            "codexsub" => metric.Weekly ? codexSubUsage?.WeeklyLeft : codexSubUsage?.FiveHourLeft,
            "claude" => metric.Weekly ? claudeUsage.WeeklyLeft : claudeUsage.FiveHourLeft,
            _ => null,
        };
    }

    private static string ResolveTrayMetricDetail(
        TrayMetricDefinition metric,
        string active,
        UsageRow? codexUsage,
        UsageRow? codexSubUsage,
        ClaudeUsage claudeUsage)
    {
        return metric.ProviderId switch
        {
            "codex" when !string.IsNullOrWhiteSpace(codexUsage?.Error) => codexUsage.Error,
            "codex" => $"프로필 {active}",
            "codexsub" when codexSubUsage is null => "sub 프로필 없음",
            "codexsub" when !string.IsNullOrWhiteSpace(codexSubUsage.Error) => codexSubUsage.Error,
            "codexsub" => $"프로필 {codexSubUsage.Profile}",
            "claude" => claudeUsage.Authenticated ? claudeUsage.Message ?? "남은 사용량" : claudeUsage.Message ?? "로그인 필요",
            _ => "",
        };
    }

    private static UsageRow? FindCodexSubUsage(
        IReadOnlyList<ProfileSummary> profiles,
        IReadOnlyList<UsageRow> usageRows,
        string active)
    {
        string[] preferredNames = ["sub", "codexsub", "codex-sub", "codex_sub"];
        foreach (var name in preferredNames)
        {
            var match = usageRows.FirstOrDefault(row => row.Profile.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        var profileNames = profiles
            .Select(profile => profile.Name)
            .Where(name => !name.Equals(active, StringComparison.OrdinalIgnoreCase))
            .Where(name => name.Contains("sub", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return usageRows.FirstOrDefault(row => profileNames.Contains(row.Profile, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ProviderQuotaRow> BuildProviderRows(
        CurrentState current,
        IReadOnlyList<UsageRow> usageRows,
        ClaudeUsage claudeUsage)
    {
        var active = current.MatchedProfile ?? current.ActiveLabel;
        var codexUsage = usageRows.FirstOrDefault(row => row.Profile == active);
        var codexDetail = current.AuthMatch == "mismatch"
            ? "현재 계정이 저장 프로필과 불일치"
            : $"현재 프로필: {active}";
        if (!string.IsNullOrWhiteSpace(codexUsage?.Error))
        {
            codexDetail = codexUsage.Error;
        }

        return
        [
            new(
                "codex",
                "Codex",
                AuthState: codexUsage is not null && string.IsNullOrWhiteSpace(codexUsage.Error)
                    ? ProviderAuthState.LoggedIn
                    : ProviderAuthState.Unknown,
                CurrentText: $"5시간 {PercentOrDash(codexUsage?.FiveHourLeft)}",
                WeeklyText: $"주간 {PercentOrDash(codexUsage?.WeeklyLeft)}",
                PlanText: codexUsage?.Plan ?? "",
                Detail: codexDetail),
            new(
                "claude",
                "Claude 사용량",
                AuthState: claudeUsage.Authenticated ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut,
                CurrentText: claudeUsage.Authenticated ? $"5시간 {PercentOrDash(claudeUsage.FiveHourLeft)}" : "로그인 필요",
                WeeklyText: claudeUsage.Authenticated ? $"주간 {PercentOrDash(claudeUsage.WeeklyLeft)}" : "주간 -",
                PlanText: "OAuth",
                Detail: claudeUsage.Message ?? "Claude 남은 사용량 OAuth"),
        ];
    }

    private static CommandOutcome OutcomeFromResult(SwitcherCommandResult result, string fallback)
    {
        var message = fallback;
        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(result.Stdout);
                message = StringValue(document.RootElement, "error") ?? fallback;
            }
            catch (JsonException)
            {
                message = result.Stdout.Trim();
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            message = result.Stderr.Trim();
        }

        return new CommandOutcome(false, result.ExitCode, message, result.Stdout, result.Stderr);
    }

    // Single safe-parse boundary for CLI JSON: returns a document only when stdout is a
    // well-formed JSON object, otherwise null. Callers degrade instead of throwing.
    private static JsonDocument? TryParseObject(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringValue(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    private static bool? BoolValue(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static int? IntValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static double? NestedDouble(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!nested.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.TryGetDouble(out var parsed) ? parsed : null;
    }

    private static string? NestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? StringValue(nested, propertyName)
            : null;
    }

    private static int ArrayLength(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static int? RemainingPercentFromUtilization(double? value)
    {
        if (value is null)
        {
            return null;
        }
        return (int)Math.Round(Math.Clamp(100 - value.Value, 0, 100));
    }

    private static string PercentOrDash(int? value)
    {
        return value is null ? "-" : $"{value}%";
    }

    private static string ActiveCodexProfile(CurrentState current)
    {
        return current.MatchedProfile ?? current.ActiveLabel;
    }

    private sealed record TrayMetricDefinition(string Key, string ProviderId, string DisplayName, string ShortLabel, bool Weekly);
}
