namespace CodexDesktopUsageSwitcher.Windows.Domain;

// Identity of a transcript file, used as the cache key and folded into the
// freshness-revision hash. Transcripts are append-only JSONL, so every new event
// grows Size — (mtime, size) therefore changes on any real content change. The
// same-tick/same-size in-place-rewrite miss is an accepted residual; it cannot
// occur for append-only writers.
//
// MtimeTicksUtc MUST come from FileInfo.LastWriteTimeUtc compared against
// DateTimeOffset.UtcNow, never local time, so it lines up with the calculator's
// UTC event timestamps.
internal readonly record struct FileFingerprint(string FullPath, long MtimeTicksUtc, long Size);
