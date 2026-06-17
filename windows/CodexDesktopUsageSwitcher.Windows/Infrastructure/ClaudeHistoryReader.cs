using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Faithful C# port of the reference extension's claudeService.ts / claudeApi.ts:
// reads <configDir>\projects\**\*.jsonl transcripts line-by-line and flattens each
// `message.usage` event into a CachedUsageEntry the dashboard's InsightsCalculator can
// consume (after de-duplication). Cost uses the same hardcoded per-MTok RATES table and
// substring bucketing the reference uses (claudeService.ts:23-103); TurnKey is
// `<filePath>#<turnSeq>` (claudeService.ts:544); TotalTokens is input+output+cacheRead+
// cacheCreate (claudeService.ts:511).
//
// Stateless parser: enumerate files (with fingerprints), parse whole or delta ranges into
// key-bearing entries; the global `message.id:requestId` de-dup (claudeService.ts:491-504)
// is applied by the caller at aggregation, not during parse. Streams via JsonlLineReader
// (a real session can exceed 500 MB).
internal static class ClaudeHistoryReader
{
    // Per-1M-token USD rates. Subscription users pay nothing; this is the
    // API-equivalent cost the dashboard surfaces (claudeService.ts:23-27).
    private static readonly ClaudeRate Opus = new(15, 75, 18.75, 30, 1.5);
    private static readonly ClaudeRate Sonnet = new(3, 15, 3.75, 6, 0.3);
    private static readonly ClaudeRate Haiku = new(1, 5, 1.25, 2, 0.1);

    // Resolves the .claude config dir like claudeApi.ts:54-64 (env then home).
    public static string ResolveConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude");
    }

    public static string ProjectsDir(string configDir) => Path.Combine(configDir, "projects");

    // Enumerates unique transcript files with fingerprints via a metadata-carrying walk
    // (FileInfo.LastWriteTimeUtc/Length in one pass). Windows paths are
    // case-insensitive, so duplicate paths under overlapping roots are dropped. Missing dir
    // or a locked subtree -> empty; never throws.
    public static IReadOnlyList<FileFingerprint> EnumerateFiles(string configDir)
    {
        var root = ProjectsDir(configDir);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var result = new List<FileFingerprint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
            {
                if (seen.Add(fi.FullName))
                {
                    result.Add(new FileFingerprint(fi.FullName, fi.LastWriteTimeUtc.Ticks, fi.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked/forbidden subtree means no data from here, not a crash.
        }

        return result;
    }

    // Parses a whole file from the start (turnSeq resets to 0, like the old per-file parse).
    public static ClaudeParseResult ParseFile(FileFingerprint file) => ParseFrom(file, 0, default);

    // Parses only the bytes from startOffset (just past a prior '\n') onward, carrying the
    // turn sequence so the delta produces the same entries as a full parse.
    public static ClaudeParseResult ParseFileRange(FileFingerprint file, long startOffset, ClaudeParseState state)
        => ParseFrom(file, startOffset, state);

    private static ClaudeParseResult ParseFrom(FileFingerprint file, long startOffset, ClaudeParseState state)
    {
        var entries = new List<CachedUsageEntry>();
        var turnSeq = state.TurnSeq;
        var parsedBytes = startOffset;
        try
        {
            // FileShare.ReadWrite so a live writer is not blocked; bufferSize 1 because
            // JsonlLineReader does its own pooled buffering.
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
                    turnSeq = HandleLine(file.FullPath, line, entries, turnSeq);
                }
            }
            finally
            {
                parsedBytes = lr.ParsedBytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep whatever parsed before the read failed (matches the reference's
            // "return what we have"); a single bad/locked file is not fatal.
        }

        return new ClaudeParseResult(entries, parsedBytes, new ClaudeParseState(turnSeq));
    }

    // Back-compat: reads every transcript and returns flattened, de-duplicated events in
    // enumeration order (the calculator treats empty as "no data"); never throws.
    public static IReadOnlyList<InsightEntry> Read(string configDir)
    {
        var result = new List<InsightEntry>();
        // Dedup by `message.id:requestId` across all files, exactly like the reference's
        // global `seen` set (claudeService.ts:491-504); first occurrence wins.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFiles(configDir))
        {
            foreach (var entry in ParseFile(file).Entries)
            {
                if (entry.DedupKey.Length > 0 && !seenKeys.Add(entry.DedupKey))
                {
                    continue; // duplicate streamed/resumed line
                }

                result.Add(entry.ToInsight());
            }
        }

        return result;
    }

    private static long HandleLine(string file, ReadOnlyMemory<byte> line, List<CachedUsageEntry> entries, long turnSeq)
    {
        var span = line.Span;
        var start = JsonlLineReader.FirstNonWhitespace(span);
        if (start < 0 || span[start] != (byte)'{')
        {
            return turnSeq;
        }

        using var doc = TryParse(line.Slice(start));
        if (doc is null)
        {
            return turnSeq; // skip malformed line
        }

        var obj = doc.RootElement;
        turnSeq = AdvanceTurn(obj, turnSeq);

        var entry = BuildEntry(file, obj, turnSeq);
        if (entry is not null)
        {
            entries.Add(entry.Value);
        }

        return turnSeq;
    }

    // Turn boundary = a real user input (not tool result / meta / sidechain prompt).
    // Every usage event after it belongs to that turn (claudeService.ts:689-698).
    private static long AdvanceTurn(JsonElement obj, long turnSeq)
    {
        if (GetString(obj, "type") != "user" || GetBool(obj, "isSidechain") || GetBool(obj, "isMeta"))
        {
            return turnSeq;
        }

        if (obj.TryGetProperty("toolUseResult", out var tur) && tur.ValueKind is not JsonValueKind.Null)
        {
            return turnSeq;
        }

        return HasRealUserText(obj) ? turnSeq + 1 : turnSeq;
    }

    private static bool HasRealUserText(JsonElement obj)
    {
        if (!obj.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object ||
            !msg.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim().Length > 0;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text")
            {
                return true;
            }
        }

        return false;
    }

    // Builds a key-bearing entry from a `message.usage` event. The dedup key is stored, not
    // applied here — the caller de-duplicates globally at aggregation.
    private static CachedUsageEntry? BuildEntry(string file, JsonElement obj, long turnSeq)
    {
        if (!obj.TryGetProperty("message", out var msg) ||
            msg.ValueKind != JsonValueKind.Object ||
            !msg.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // An empty string is "missing" — `??` only fires on null, so "" would pass
        // through as the model. Treat empty/null alike and fall back to "unknown".
        var rawModel = GetString(msg, "model");
        var model = string.IsNullOrEmpty(rawModel) ? "unknown" : rawModel;
        var ts = ParseTimestamp(obj);
        var tokens = ReadTokens(usage);
        var total = tokens.Input + tokens.Output + tokens.CacheRead + tokens.CacheCreate;
        var cost = EntryCost(model, tokens);
        var key = DedupKey(msg, obj);
        return new CachedUsageEntry(
            key, ts, model, total, tokens.Output, cost, $"{file}#{turnSeq}",
            tokens.Input, tokens.CacheRead, tokens.Cache5m, tokens.Cache1h);
    }

    // `message.id + ":" + requestId`; empty when neither exists (never deduped),
    // matching claudeService.ts:717.
    private static string DedupKey(JsonElement msg, JsonElement obj)
    {
        var msgId = GetString(msg, "id") ?? "";
        var reqId = GetString(obj, "requestId") ?? "";
        return msgId.Length == 0 && reqId.Length == 0 ? "" : $"{msgId}:{reqId}";
    }

    private static ClaudeTokens ReadTokens(JsonElement usage)
    {
        var c5 = GetLong(usage, "cache_creation", "ephemeral_5m_input_tokens");
        var c1 = GetLong(usage, "cache_creation", "ephemeral_1h_input_tokens");
        var cacheCreateField = GetLong(usage, "cache_creation_input_tokens");
        var cacheCreate = cacheCreateField != 0 ? cacheCreateField : c5 + c1;
        return new ClaudeTokens(
            GetLong(usage, "input_tokens"),
            GetLong(usage, "output_tokens"),
            GetLong(usage, "cache_read_input_tokens"),
            cacheCreate,
            c5,
            c1);
    }

    // cost = (input·in + output·out + cacheRead·cr)/1e6 + cacheWrite cost, where
    // cacheWrite uses split 5m/1h rates if present else the 5m rate on the lump sum
    // (claudeService.ts:87-103).
    private static double EntryCost(string model, ClaudeTokens t)
    {
        var r = RateFor(model);
        var cacheWriteCost = t.Cache5m + t.Cache1h > 0
            ? (t.Cache5m * r.CacheWrite5m + t.Cache1h * r.CacheWrite1h) / 1e6
            : t.CacheCreate * r.CacheWrite5m / 1e6;
        return (t.Input * r.Input + t.Output * r.Output + t.CacheRead * r.CacheRead) / 1e6 + cacheWriteCost;
    }

    private static ClaudeRate RateFor(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("opus", StringComparison.Ordinal))
        {
            return Opus;
        }

        if (m.Contains("haiku", StringComparison.Ordinal))
        {
            return Haiku;
        }

        return Sonnet; // sonnet and unknown models default here
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

    private static bool GetBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        // Tolerate float/exponent-encoded counts (e.g. 1234.0) the same way the
        // reference num() does; TryGetInt64 alone returns 0 for a JSON float.
        if (value.TryGetInt64(out var n))
        {
            return n;
        }

        return value.TryGetDouble(out var d) && double.IsFinite(d) ? (long)Math.Round(d) : 0;
    }

    private static long GetLong(JsonElement obj, string parent, string name) =>
        obj.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? GetLong(child, name)
            : 0;

    private readonly record struct ClaudeRate(
        double Input,
        double Output,
        double CacheWrite5m,
        double CacheWrite1h,
        double CacheRead);

    private readonly record struct ClaudeTokens(
        long Input,
        long Output,
        long CacheRead,
        long CacheCreate,
        long Cache5m,
        long Cache1h);
}
