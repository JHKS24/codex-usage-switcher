namespace CodexDesktopUsageSwitcher.Windows.Domain;

// Cross-line parse state carried across an append resume so a delta parse produces the
// same entries as a full parse. Persisted in the cache (in-proc + disk) with the entries.

// Claude: only the running turn sequence survives across lines (TurnKey = file#turnSeq).
internal readonly record struct ClaudeParseState(long TurnSeq);

// Codex: the FULL turn->model map must survive (not just scalars) because a token_count
// after the resume offset can reference a turn_context defined in the already-parsed prefix
// CurrentTurnId / LatestModel are the other two pieces of live state.
internal sealed record CodexParseState(
    string? CurrentTurnId,
    IReadOnlyDictionary<string, string> TurnModels,
    string? LatestModel)
{
    public static CodexParseState Empty { get; } =
        new(null, new Dictionary<string, string>(StringComparer.Ordinal), null);
}

// Result of parsing a file or a delta range. ParsedBytes is the offset just past the last
// consumed '\n'; resume the next range from exactly there. Entries are key-bearing so
// de-duplication runs once at aggregation.
internal sealed record ClaudeParseResult(
    IReadOnlyList<CachedUsageEntry> Entries,
    long ParsedBytes,
    ClaudeParseState State);

internal sealed record CodexParseResult(
    IReadOnlyList<CachedUsageEntry> Entries,
    long ParsedBytes,
    CodexParseState State);
