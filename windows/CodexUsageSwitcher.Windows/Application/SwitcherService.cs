using System.Text.Json;
using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.Application;

internal sealed class SwitcherService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(45);
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

    public Task<string?> LoadLanguageAsync(CancellationToken cancellationToken)
    {
        return _settingsStore.LoadLanguageAsync(cancellationToken);
    }

    public Task SetLanguageAsync(string language, CancellationToken cancellationToken)
    {
        return _settingsStore.SetLanguageAsync(language, cancellationToken);
    }

    public async Task<CommandOutcome> SwitchProfileAsync(string profile, CancellationToken cancellationToken)
    {
        var stop = await _client.RunAsync(
            ["stop-codex", "--json", "--grace-seconds", "4"],
            SwitchTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!stop.Ok)
        {
            return OutcomeFromResult(stop, Localizer.L("error.codexStopFailed"));
        }

        var use = await _client.RunAsync(
            ["use", profile, "--apply", "--json"],
            SwitchTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!use.Ok)
        {
            return OutcomeFromResult(use, Localizer.L("error.switchFailed"));
        }

        var open = await _client.RunAsync(["open"], StatusTimeout, cancellationToken).ConfigureAwait(false);
        if (!open.Ok)
        {
            return new CommandOutcome(true, use.ExitCode, Localizer.F("popup.switchDoneAutoLaunchFailed", profile), use.Stdout, open.Stderr);
        }

        return new CommandOutcome(true, 0, Localizer.F("popup.switchDone", profile), use.Stdout, use.Stderr);
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
            return OutcomeFromResult(result, Localizer.L("error.doctorFailed"));
        }

        using var document = TryParseObject(result.Stdout);
        if (document is null)
        {
            return OutcomeFromResult(result, Localizer.L("error.doctorUnparseable"));
        }

        var root = document.RootElement;
        var lines = new List<string>
        {
            $"platform: {StringValue(root, "platform") ?? Localizer.L("common.unknown")}",
            $"tool: {StringValue(root, "tool") ?? Localizer.L("common.unknown")}",
            $"profiles_root: {StringValue(root, "profiles_root") ?? Localizer.L("common.unknown")}",
            $"profiles: {ArrayLength(root, "profiles")}",
            $"codex_cli: {StringValue(root, "codex_cli") ?? Localizer.L("common.missing")}",
            $"codex_app: {StringValue(root, "codex_app") ?? Localizer.L("common.missing")}",
            $"codex_running: {BoolValue(root, "codex_running")?.ToString() ?? Localizer.L("common.unknown")}",
            $"process_check: {StringValue(root, "process_check") ?? Localizer.L("common.unknown")}",
        };
        return new CommandOutcome(true, 0, PathRedaction.Scrub(string.Join(Environment.NewLine, lines)), result.Stdout, result.Stderr);
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
            // active_label / auth_match are status sentinels compared in logic (e.g. != "unknown");
            // keep them as data, not localized display text.
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
                "rate_limited" => Localizer.L("usage.claude.rateLimited"),
                "network" => Localizer.L("usage.claude.networkError"),
                _ => Localizer.L("common.loginRequired"),
            };
            return new ClaudeUsage(false, null, null, null, null, message);
        }

        if (error is "network" or "usage_request_failed")
        {
            // Logged in, but the usage fetch itself failed transiently.
            return new ClaudeUsage(true, null, null, null, null, Localizer.L("usage.claude.networkError"));
        }

        var five = RemainingPercentFromUtilization(NestedDouble(element, "five_hour", "utilization"));
        var fiveReset = NestedString(element, "five_hour", "resets_at");
        var week = RemainingPercentFromUtilization(NestedDouble(element, "seven_day", "utilization"));
        var weekReset = NestedString(element, "seven_day", "resets_at");
        var hasUsage = five is not null || week is not null;
        return new ClaudeUsage(true, five, fiveReset, week, weekReset, hasUsage ? null : Localizer.L("usage.claude.noValues"));
    }

    private static CurrentState UnknownCurrent()
    {
        return new CurrentState("unknown", null, "unknown", null);
    }

    private static ClaudeUsage ClaudeUsageUnavailable()
    {
        return new ClaudeUsage(false, null, null, null, null, Localizer.L("usage.claude.fetchFailed"));
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
        var metricDefinitions = BuildTrayMetricDefinitions(profiles, visibleMetricKeys);

        return metricDefinitions
            .Select(metric => new TrayMetricRow(
                metric.Key,
                metric.ProviderId,
                metric.DisplayName,
                metric.ShortLabel,
                visibleMetricKeys.Contains(metric.Key),
                ResolveTrayMetric(metric, codexUsage, usageRows, claudeUsage),
                ResolveTrayMetricDetail(metric, active, codexUsage, usageRows, claudeUsage)))
            .ToArray();
    }

    private static IReadOnlyList<TrayMetricDefinition> BuildTrayMetricDefinitions(
        IReadOnlyList<ProfileSummary> profiles,
        IReadOnlySet<string> visibleMetricKeys)
    {
        var definitions = new List<TrayMetricDefinition>
        {
            new("codex:5h", "codex", Localizer.L("tray.metric.codex5h"), "C5", Weekly: false),
            new("codex:week", "codex", Localizer.L("tray.metric.codexWeek"), "CW", Weekly: true),
        };

        var profileNames = profiles
            .Select(profile => profile.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < profileNames.Length; index++)
        {
            var profile = profileNames[index];
            var keyPrefix = $"codexprofile:{profile}";
            var labelPrefix = $"P{Math.Min(index + 1, 9)}";
            definitions.Add(new(
                $"{keyPrefix}:5h",
                "codexprofile",
                Localizer.F("tray.metric.codexProfile5h", profile),
                $"{labelPrefix}5",
                Weekly: false,
                Profile: profile));
            definitions.Add(new(
                $"{keyPrefix}:week",
                "codexprofile",
                Localizer.F("tray.metric.codexProfileWeek", profile),
                $"{labelPrefix}W",
                Weekly: true,
                Profile: profile));
        }

        var legacySubProfile = FindLegacySubProfile(profileNames);
        if (legacySubProfile is not null &&
            (visibleMetricKeys.Contains("codexsub:5h") || visibleMetricKeys.Contains("codexsub:week")))
        {
            definitions.Add(new(
                "codexsub:5h",
                "codexprofile",
                Localizer.L("tray.metric.codexSub5h"),
                "S5",
                Weekly: false,
                Profile: legacySubProfile));
            definitions.Add(new(
                "codexsub:week",
                "codexprofile",
                Localizer.L("tray.metric.codexSubWeek"),
                "SW",
                Weekly: true,
                Profile: legacySubProfile));
        }

        definitions.Add(new("claude:5h", "claude", Localizer.L("tray.metric.claude5h"), "L5", Weekly: false));
        definitions.Add(new("claude:week", "claude", Localizer.L("tray.metric.claudeWeek"), "LW", Weekly: true));
        return definitions;
    }

    private static string? FindLegacySubProfile(IReadOnlyList<string> profileNames)
    {
        string[] preferredNames = ["sub", "codexsub", "codex-sub", "codex_sub"];
        foreach (var preferred in preferredNames)
        {
            var match = profileNames.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static int? ResolveTrayMetric(
        TrayMetricDefinition metric,
        UsageRow? codexUsage,
        IReadOnlyList<UsageRow> usageRows,
        ClaudeUsage claudeUsage)
    {
        return metric.ProviderId switch
        {
            "codex" => metric.Weekly ? codexUsage?.WeeklyLeft : codexUsage?.FiveHourLeft,
            "codexprofile" => metric.Weekly
                ? UsageForProfile(usageRows, metric.Profile)?.WeeklyLeft
                : UsageForProfile(usageRows, metric.Profile)?.FiveHourLeft,
            "claude" => metric.Weekly ? claudeUsage.WeeklyLeft : claudeUsage.FiveHourLeft,
            _ => null,
        };
    }

    private static string ResolveTrayMetricDetail(
        TrayMetricDefinition metric,
        string active,
        UsageRow? codexUsage,
        IReadOnlyList<UsageRow> usageRows,
        ClaudeUsage claudeUsage)
    {
        var profileUsage = UsageForProfile(usageRows, metric.Profile);
        return metric.ProviderId switch
        {
            "codex" when !string.IsNullOrWhiteSpace(codexUsage?.Error) => codexUsage.Error,
            "codex" => Localizer.F("tray.detail.codexProfile", active),
            "codexprofile" when profileUsage is null => Localizer.F("tray.detail.codexProfile", metric.Profile ?? Localizer.L("common.unknown")),
            "codexprofile" when !string.IsNullOrWhiteSpace(profileUsage.Error) => profileUsage.Error,
            "codexprofile" => Localizer.F("tray.detail.codexProfile", profileUsage.Profile),
            "claude" => claudeUsage.Authenticated ? claudeUsage.Message ?? Localizer.L("usage.remaining") : claudeUsage.Message ?? Localizer.L("common.loginRequired"),
            _ => "",
        };
    }

    private static UsageRow? UsageForProfile(IReadOnlyList<UsageRow> usageRows, string? profile) =>
        string.IsNullOrWhiteSpace(profile)
            ? null
            : usageRows.FirstOrDefault(row => row.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ProviderQuotaRow> BuildProviderRows(
        CurrentState current,
        IReadOnlyList<UsageRow> usageRows,
        ClaudeUsage claudeUsage)
    {
        var active = current.MatchedProfile ?? current.ActiveLabel;
        var codexUsage = usageRows.FirstOrDefault(row => row.Profile == active);
        var codexDetail = current.AuthMatch == "mismatch"
            ? Localizer.L("provider.codex.authMismatch")
            : Localizer.F("provider.codex.currentProfile", active);
        if (!string.IsNullOrWhiteSpace(codexUsage?.Error))
        {
            codexDetail = codexUsage.Error;
        }

        return
        [
            new(
                "codex",
                Localizer.L("provider.codex.name"),
                AuthState: codexUsage is not null && string.IsNullOrWhiteSpace(codexUsage.Error)
                    ? ProviderAuthState.LoggedIn
                    : ProviderAuthState.Unknown,
                CurrentText: Localizer.F("usage.fiveHour", PercentOrDash(codexUsage?.FiveHourLeft)),
                WeeklyText: Localizer.F("usage.weekly", PercentOrDash(codexUsage?.WeeklyLeft)),
                PlanText: codexUsage?.Plan ?? "",
                Detail: codexDetail),
            new(
                "claude",
                Localizer.L("provider.claude.name"),
                AuthState: claudeUsage.Authenticated ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut,
                CurrentText: claudeUsage.Authenticated ? Localizer.F("usage.fiveHour", PercentOrDash(claudeUsage.FiveHourLeft)) : Localizer.L("common.loginRequired"),
                WeeklyText: claudeUsage.Authenticated ? Localizer.F("usage.weekly", PercentOrDash(claudeUsage.WeeklyLeft)) : Localizer.L("usage.weeklyDash"),
                PlanText: "OAuth",
                Detail: claudeUsage.Message ?? Localizer.L("provider.claude.detailDefault")),
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

    private sealed record TrayMetricDefinition(
        string Key,
        string ProviderId,
        string DisplayName,
        string ShortLabel,
        bool Weekly,
        string? Profile = null);
}
