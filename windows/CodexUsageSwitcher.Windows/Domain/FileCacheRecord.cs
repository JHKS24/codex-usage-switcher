namespace CodexUsageSwitcher.Windows.Domain;

// One source file's cached parse result, persisted to disk and held in memory. SchemaVersion
// gates format drift across app versions (a mismatch is a miss -> reparse). Fingerprint
// must equal the live file's (mtime+size) or the record is stale. The provider-specific resume
// state is a union: ClaudeTurnSeq carries Claude's state, CodexState carries Codex's; the
// reader for the other provider simply ignores the unused field.
internal sealed record FileCacheRecord(
    int SchemaVersion,
    FileFingerprint Fingerprint,
    long ParsedBytes,
    IReadOnlyList<CachedUsageEntry> Entries,
    long ClaudeTurnSeq,
    CodexStateDto? CodexState);

// Serializable form of CodexParseState. The FULL turn->model map is persisted (not just
// scalars) so a cross-process resume can attribute a post-offset token_count to a turn
// defined in the already-parsed prefix.
internal sealed record CodexStateDto(
    string? CurrentTurnId,
    Dictionary<string, string> TurnModels,
    string? LatestModel);
