using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.Application;

// Per-provider parsing strategy: enumerate files, parse a whole file, or parse the appended
// delta from a prior record. This is the one place that adapts each reader's typed parse
// result (+ its resume state) into the provider-agnostic FileCacheRecord, so FileParseCache
// stays provider-neutral (guardrail D3 — readers are the SSOT, this just maps their output).
internal sealed record ProviderParser(
    Func<string, IReadOnlyList<FileFingerprint>> Enumerate,
    Func<FileFingerprint, FileCacheRecord> ParseWhole,
    Func<FileFingerprint, FileCacheRecord, FileCacheRecord> ParseDelta);

internal static class ProviderParsers
{
    public static ProviderParser Claude() => new(
        ClaudeHistoryReader.EnumerateFiles,
        file => ToRecord(file, ClaudeHistoryReader.ParseFile(file)),
        (file, prior) => ToRecord(
            file,
            ClaudeHistoryReader.ParseFileRange(file, prior.ParsedBytes, new ClaudeParseState(prior.ClaudeTurnSeq)),
            prior.Entries));

    public static ProviderParser Codex() => new(
        CodexHistoryReader.EnumerateFiles,
        file => ToRecord(file, CodexHistoryReader.ParseFile(file)),
        (file, prior) => ToRecord(
            file,
            CodexHistoryReader.ParseFileRange(file, prior.ParsedBytes, FromDto(prior.CodexState)),
            prior.Entries));

    private static FileCacheRecord ToRecord(FileFingerprint file, ClaudeParseResult result, IReadOnlyList<CachedUsageEntry>? prior = null) =>
        new(FileCacheStore.CurrentSchemaVersion, file, result.ParsedBytes, Merge(prior, result.Entries), result.State.TurnSeq, CodexState: null);

    private static FileCacheRecord ToRecord(FileFingerprint file, CodexParseResult result, IReadOnlyList<CachedUsageEntry>? prior = null) =>
        new(FileCacheStore.CurrentSchemaVersion, file, result.ParsedBytes, Merge(prior, result.Entries), ClaudeTurnSeq: 0, ToDto(result.State));

    // Append delta: keep the prior file's entries and tack on the newly-parsed tail. Order is
    // preserved (prior then new), which is also the chronological order in append-only JSONL.
    private static IReadOnlyList<CachedUsageEntry> Merge(IReadOnlyList<CachedUsageEntry>? prior, IReadOnlyList<CachedUsageEntry> fresh)
    {
        if (prior is null || prior.Count == 0)
        {
            return fresh;
        }

        if (fresh.Count == 0)
        {
            return prior;
        }

        var merged = new List<CachedUsageEntry>(prior.Count + fresh.Count);
        merged.AddRange(prior);
        merged.AddRange(fresh);
        return merged;
    }

    private static CodexStateDto ToDto(CodexParseState state) =>
        new(state.CurrentTurnId, new Dictionary<string, string>(state.TurnModels, StringComparer.Ordinal), state.LatestModel);

    private static CodexParseState FromDto(CodexStateDto? dto) =>
        dto is null
            ? CodexParseState.Empty
            : new CodexParseState(dto.CurrentTurnId, new Dictionary<string, string>(dto.TurnModels, StringComparer.Ordinal), dto.LatestModel);
}
