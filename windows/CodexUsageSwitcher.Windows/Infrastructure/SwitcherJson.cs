using System.Text.Json.Nodes;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Builds the exact JSON the rest of the app parses (the contract the Python switcher used to emit).
// Centralizing the wire shape here keeps the dispatcher readable and lets the contract be tested
// directly. Token contents never appear in any of these payloads.
internal static class SwitcherJson
{
    public static string Serialize(JsonNode node) => node.ToJsonString();

    public static JsonObject Profiles(IReadOnlyList<ProfileEntry> profiles, string profilesRoot) => new()
    {
        ["ok"] = true,
        ["profiles_root"] = profilesRoot,
        ["profiles"] = ProfileArray(profiles),
    };

    public static JsonObject Current(CodexCurrentState state) => new()
    {
        ["ok"] = true,
        ["active_label"] = state.ActiveLabel,
        ["matched_profile"] = state.MatchedProfile,
        ["matched_profiles"] = StringArray(state.MatchedProfiles),
        ["identity_matched_profiles"] = StringArray(state.IdentityMatchedProfiles),
        ["match_method"] = state.MatchMethod,
        ["auth_match"] = state.AuthMatch,
        ["codex_running"] = Bool(state.CodexRunning),
        ["process_check"] = state.CodexRunning is null ? "unknown" : "ok",
    };

    public static JsonObject CurrentUnavailable() => new()
    {
        ["ok"] = false,
        ["active_label"] = "unknown",
        ["matched_profile"] = null,
        ["auth_match"] = "unknown",
        ["codex_running"] = null,
        ["process_check"] = "unknown",
    };

    public static JsonObject UsageRow(CodexUsageRow row) => new()
    {
        ["profile"] = row.Profile,
        ["account"] = row.Account,
        ["plan"] = row.Plan,
        ["five_hour_left"] = Num(row.FiveHourLeft),
        ["five_hour_reset"] = row.FiveHourResetIso,
        ["weekly_left"] = Num(row.WeeklyLeft),
        ["weekly_reset"] = row.WeeklyResetIso,
        ["error"] = row.Error,
    };

    public static JsonObject Usage(IReadOnlyList<CodexUsageRow> rows) => new()
    {
        ["ok"] = true,
        ["usage"] = UsageArray(rows),
    };

    public static JsonObject Claude(ClaudeUsageResult result)
    {
        var payload = new JsonObject
        {
            ["ok"] = true,
            ["source"] = "anthropic_oauth_usage",
            ["authenticated"] = result.Authenticated,
        };

        if (result.Error is not null)
        {
            payload["error"] = result.Error;
        }

        if (result.Authenticated && result.Error is null)
        {
            payload["five_hour"] = Bucket(result.FiveHourUtilization, result.FiveHourResetsAt);
            payload["seven_day"] = Bucket(result.SevenDayUtilization, result.SevenDayResetsAt);
        }

        return payload;
    }

    public static JsonObject ClaudeUnavailable() => new()
    {
        ["ok"] = false,
        ["source"] = "anthropic_oauth_usage",
        ["authenticated"] = false,
        ["error"] = "network",
    };

    public static JsonObject Snapshot(
        IReadOnlyList<ProfileEntry> profiles,
        string profilesRoot,
        JsonObject current,
        IReadOnlyList<CodexUsageRow> usage,
        JsonObject claude) => new()
    {
        ["ok"] = true,
        ["profiles_root"] = profilesRoot,
        ["profiles"] = ProfileArray(profiles),
        ["current"] = current,
        ["usage"] = UsageArray(usage),
        ["claude_usage"] = claude,
    };

    public static JsonObject Stop(CodexStopResult result) => new()
    {
        ["ok"] = result.Inspected && result.Remaining.Count == 0,
        ["terminated"] = StringArray(result.Terminated),
        ["killed"] = StringArray(result.Killed),
        ["remaining"] = StringArray(result.Remaining),
        ["process_check"] = result.Inspected ? "ok" : "unknown",
    };

    public static JsonObject Doctor(
        string profilesRoot,
        IReadOnlyList<ProfileEntry> profiles,
        string? codexCli,
        string? codexApp,
        bool? codexRunning) => new()
    {
        ["ok"] = true,
        ["platform"] = "win32",
        ["tool"] = "native (built-in)",
        ["profiles_root"] = profilesRoot,
        ["profiles"] = ProfileArray(profiles),
        ["codex_cli"] = codexCli,
        ["codex_app"] = codexApp,
        ["codex_running"] = Bool(codexRunning),
        ["process_check"] = codexRunning is null ? "unknown" : "ok",
    };

    public static JsonObject UseSuccess(string profile, string? backupId) => new()
    {
        ["ok"] = true,
        ["command"] = "use",
        ["profile"] = profile,
        ["switched"] = profile,
        ["backup_id"] = backupId,
    };

    public static JsonObject UseDryRun(string profile) => new()
    {
        ["ok"] = true,
        ["command"] = "use",
        ["profile"] = profile,
        ["dry_run"] = true,
        ["next"] = "quit Codex Desktop completely, then run again with --apply",
    };

    public static JsonObject Error(string message) => new()
    {
        ["ok"] = false,
        ["error"] = message,
    };

    private static JsonArray ProfileArray(IReadOnlyList<ProfileEntry> profiles)
    {
        var array = new JsonArray();
        foreach (var profile in profiles)
        {
            array.Add(new JsonObject { ["profile"] = profile.Name, ["exists"] = profile.Exists });
        }

        return array;
    }

    private static JsonArray UsageArray(IReadOnlyList<CodexUsageRow> rows)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            array.Add(UsageRow(row));
        }

        return array;
    }

    private static JsonObject Bucket(double? utilization, string? resetsAt) => new()
    {
        ["utilization"] = Num(utilization),
        ["resets_at"] = resetsAt,
    };

    private static JsonArray StringArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonNode? Num(int? value) => value is { } v ? JsonValue.Create(v) : null;

    private static JsonNode? Num(double? value) => value is { } v ? JsonValue.Create(v) : null;

    private static JsonNode? Bool(bool? value) => value is { } v ? JsonValue.Create(v) : null;
}
