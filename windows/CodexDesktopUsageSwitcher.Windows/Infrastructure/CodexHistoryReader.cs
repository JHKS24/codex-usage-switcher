using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Faithful C# port of the reference extension's codexHistory.ts: reads
// <codexHome>\sessions\**\rollout-*.jsonl and state-accumulates per file
// (session_meta/turn_context/task_started/token_count) into CachedUsageEntry events.
// Per token_count it uses payload.info.last_token_usage as the per-event delta.
// Codex semantics (codexHistory.ts:101-109): reasoning ⊆ output and total =
// input + output, so the insights feed uses OUTPUT (not total) for OutputTokens to
// avoid double-counting reasoning; TotalTokens is the event's total_tokens.
// CostUsd is null for Codex (cost-not-shown policy). TurnKey is `<file>#<turnId>`,
// or null when the turn id is unknown (excluded from turn stats). Codex has no dedup,
// so every entry carries an empty dedup key. Streams via JsonlLineReader (a real
// rollout can exceed 500 MB); resume carries the full turn->model map (verdict V2). A final
// line without a trailing '\n' (a crashed/abandoned rollout) is withheld like every unterminated
// line and recovered on the next append — a deliberate, accepted divergence from the old
// full-parse reader, which flushed it (verified low: real rollouts are '\n'-terminated).
internal static class CodexHistoryReader
{
    // Resolves the Codex home like codexHistory.ts:164-166 (env then home).
    public static string ResolveCodexHome()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex");
    }

    public static string SessionsDir(string codexHome) => Path.Combine(codexHome, "sessions");

    // Enumerates unique rollout files with fingerprints via a metadata-carrying walk
    // (verdict V6). Same rollout under overlapping roots is counted once
    // (codexHistory.ts:60-72); Windows paths are case-insensitive. Missing dir -> empty.
    public static IReadOnlyList<FileFingerprint> EnumerateFiles(string codexHome)
    {
        var root = SessionsDir(codexHome);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var result = new List<FileFingerprint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
            {
                if (seen.Add(fi.FullName))
                {
                    result.Add(new FileFingerprint(fi.FullName, fi.LastWriteTimeUtc.Ticks, fi.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    // Parses a whole file from the start (fresh rollout state).
    public static CodexParseResult ParseFile(FileFingerprint file) => ParseFrom(file, 0, CodexParseState.Empty);

    // Parses only the bytes from startOffset onward, carrying the accumulated rollout state.
    public static CodexParseResult ParseFileRange(FileFingerprint file, long startOffset, CodexParseState state)
        => ParseFrom(file, startOffset, state);

    private static CodexParseResult ParseFrom(FileFingerprint file, long startOffset, CodexParseState state)
    {
        var rollout = RolloutState.From(state);
        var entries = new List<CachedUsageEntry>();
        var parsedBytes = startOffset;
        try
        {
            using var fs = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.SequentialScan);
            if (startOffset > 0)
            {
                fs.Seek(startOffset, SeekOrigin.Begin);
            }

            using var lr = new JsonlLineReader(fs, startOffset);
            try
            {
                while (lr.TryReadLine(out var line))
                {
                    HandleLine(file.FullPath, line, entries, rollout);
                }
            }
            finally
            {
                parsedBytes = lr.ParsedBytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep whatever parsed before the read failed; one bad/locked file is not fatal.
        }

        return new CodexParseResult(entries, parsedBytes, rollout.ToParseState());
    }

    // Back-compat: reads every rollout and returns flattened events (no dedup for Codex).
    public static IReadOnlyList<InsightEntry> Read(string codexHome)
    {
        var result = new List<InsightEntry>();
        foreach (var file in EnumerateFiles(codexHome))
        {
            foreach (var entry in ParseFile(file).Entries)
            {
                result.Add(entry.ToInsight());
            }
        }

        return result;
    }

    private static void HandleLine(string file, ReadOnlyMemory<byte> line, List<CachedUsageEntry> entries, RolloutState state)
    {
        var span = line.Span;
        var start = JsonlLineReader.FirstNonWhitespace(span);
        if (start < 0 || span[start] != (byte)'{')
        {
            return;
        }

        using var doc = TryParse(line.Slice(start));
        if (doc is null)
        {
            return; // skip malformed line
        }

        var obj = doc.RootElement;
        var ts = ParseTimestamp(obj);

        if (ApplyTopLevelEvent(obj, state))
        {
            return; // session_meta / turn_context handled
        }

        ApplyPayloadEvent(file, obj, ts, entries, state);
    }

    // session_meta and turn_context update model/turn state. Returns true when the
    // line was one of them (codexHistory.ts:282-298).
    private static bool ApplyTopLevelEvent(JsonElement obj, RolloutState state)
    {
        var type = GetString(obj, "type");
        if (type == "session_meta")
        {
            ApplySessionMeta(obj, state);
            return true;
        }

        if (type == "turn_context")
        {
            ApplyTurnContext(obj, state);
            return true;
        }

        return false;
    }

    private static void ApplySessionMeta(JsonElement obj, RolloutState state)
    {
        if (!obj.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var model = GetString(payload, "model") ?? GetString(payload, "settings", "model");
        if (!string.IsNullOrEmpty(model))
        {
            state.LatestModel = model;
        }
    }

    private static void ApplyTurnContext(JsonElement obj, RolloutState state)
    {
        if (!obj.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var turnId = GetString(payload, "turn_id");
        var model = GetString(payload, "model") ?? GetCollaborationModel(payload);
        if (string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(model))
        {
            return;
        }

        state.TurnModels[turnId] = model;
        state.LatestModel = model;
    }

    private static string? GetCollaborationModel(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("collaboration_mode", out var mode) && mode.ValueKind == JsonValueKind.Object &&
        mode.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
            ? GetString(settings, "model")
            : null;

    // payload.type == task_started updates the current turn; token_count emits an
    // event using last_token_usage (codexHistory.ts:300-320).
    private static void ApplyPayloadEvent(string file, JsonElement obj, long ts, List<CachedUsageEntry> entries, RolloutState state)
    {
        if (!obj.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var payloadType = GetString(payload, "type");
        if (payloadType == "task_started")
        {
            var turnId = GetString(payload, "turn_id");
            if (!string.IsNullOrEmpty(turnId))
            {
                state.CurrentTurnId = turnId;
            }

            return;
        }

        if (payloadType != "token_count")
        {
            return;
        }

        entries.Add(BuildEntry(file, payload, ts, state));
    }

    private static CachedUsageEntry BuildEntry(string file, JsonElement payload, long ts, RolloutState state)
    {
        var last = payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                   info.TryGetProperty("last_token_usage", out var raw)
            ? raw
            : default;
        var usage = NormalizeUsage(last);
        var model = ResolveModel(state);
        var turnKey = string.IsNullOrEmpty(state.CurrentTurnId) ? null : $"{file}#{state.CurrentTurnId}";
        return new CachedUsageEntry(
            DedupKey: "", ts, model, usage.Total, usage.Output, CostUsd: null, turnKey,
            InputTokens: usage.Input, CacheReadTokens: usage.CacheRead, ReasoningOutputTokens: usage.Reasoning);
    }

    private static string ResolveModel(RolloutState state)
    {
        if (!string.IsNullOrEmpty(state.CurrentTurnId) && state.TurnModels.TryGetValue(state.CurrentTurnId, out var turnModel))
        {
            return turnModel;
        }

        return string.IsNullOrEmpty(state.LatestModel) ? "unknown" : state.LatestModel;
    }

    // snake_case (with camelCase fallback) per codexHistory.ts:373-381.
    private static CodexUsage NormalizeUsage(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new CodexUsage(
            GetLong(raw, "input_tokens", "inputTokens"),
            GetLong(raw, "output_tokens", "outputTokens"),
            GetLong(raw, "total_tokens", "totalTokens"),
            GetLong(raw, "cached_input_tokens", "cachedInputTokens"),
            GetLong(raw, "reasoning_output_tokens", "reasoningOutputTokens"));
    }

    private static long ParseTimestamp(JsonElement obj) => IsoTime.ToUnixMs(GetString(obj, "timestamp"));

    private static JsonDocument? TryParse(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetString(JsonElement obj, string parent, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? GetString(child, name)
            : null;

    private static long GetLong(JsonElement obj, string snake, string camel)
    {
        if (TryReadLong(obj, snake, out var n))
        {
            return n;
        }

        return TryReadLong(obj, camel, out var m) ? m : 0;
    }

    // C2: tolerate float/exponent-encoded counts (e.g. 1234.0) the same way the
    // reference num() does; TryGetInt64 alone returns false for a JSON float.
    private static bool TryReadLong(JsonElement obj, string name, out long result)
    {
        result = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetInt64(out result))
        {
            return true;
        }

        if (value.TryGetDouble(out var d) && double.IsFinite(d))
        {
            result = (long)Math.Round(d);
            return true;
        }

        return false;
    }

    private readonly record struct CodexUsage(long Input, long Output, long Total, long CacheRead, long Reasoning);

    private sealed class RolloutState
    {
        public Dictionary<string, string> TurnModels { get; } = new(StringComparer.Ordinal);
        public string? LatestModel { get; set; }
        public string? CurrentTurnId { get; set; }

        public static RolloutState From(CodexParseState state)
        {
            var rollout = new RolloutState
            {
                LatestModel = state.LatestModel,
                CurrentTurnId = state.CurrentTurnId,
            };
            foreach (var kv in state.TurnModels)
            {
                rollout.TurnModels[kv.Key] = kv.Value;
            }

            return rollout;
        }

        public CodexParseState ToParseState() =>
            new(CurrentTurnId, new Dictionary<string, string>(TurnModels, StringComparer.Ordinal), LatestModel);
    }
}
