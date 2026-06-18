namespace CodexUsageSwitcher.Windows.Domain;

internal sealed record ProfileSummary(string Name, bool HasAuth);

internal sealed record CurrentState(
    string ActiveLabel,
    string? MatchedProfile,
    string AuthMatch,
    bool? CodexRunning);

internal sealed record UsageRow(
    string Profile,
    string? Plan,
    int? FiveHourLeft,
    string? FiveHourReset,
    int? WeeklyLeft,
    string? WeeklyReset,
    string? Error);

internal sealed record ClaudeUsage(
    bool Authenticated,
    int? FiveHourLeft,
    string? FiveHourReset,
    int? WeeklyLeft,
    string? WeeklyReset,
    string? Message);

internal enum ProviderAuthState
{
    NotInstalled,
    Unknown,
    LoggedOut,
    LoggedIn,
    NotApplicable,
}

internal sealed record ProviderQuotaRow(
    string ProviderId,
    string DisplayName,
    ProviderAuthState AuthState,
    string CurrentText,
    string WeeklyText,
    string PlanText,
    string Detail);

internal sealed record TrayMetricRow(
    string Key,
    string ProviderId,
    string DisplayName,
    string ShortLabel,
    bool Visible,
    int? RemainingPercent,
    string Detail);

internal sealed record SwitcherSnapshot(
    IReadOnlyList<ProfileSummary> Profiles,
    CurrentState Current,
    IReadOnlyList<UsageRow> UsageRows,
    ClaudeUsage ClaudeUsage,
    IReadOnlyList<ProviderQuotaRow> Providers,
    IReadOnlyList<TrayMetricRow> TrayMetrics,
    DateTimeOffset RefreshedAt);

internal sealed record CommandOutcome(bool Ok, int ExitCode, string Message, string Stdout = "", string Stderr = "");
