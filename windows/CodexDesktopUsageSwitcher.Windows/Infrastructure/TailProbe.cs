using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Cutoff safety probe. The mtime cutoff trusts a file's mtime as a lower bound
// on its newest event — which a copy/restore/cloud-sync/git-checkout breaks (old mtime, recent
// content). So before skipping a never-cached old-mtime file, we read only its tail and find
// the newest event timestamp; the caller skips only if THAT is past the data window. mtime
// stays a cheap pre-filter; this is the authoritative check. Reads at most TailBytes — long
// active sessions have recent mtime and are never probed, so the hot path pays nothing.
internal static class TailProbe
{
    private const int TailBytes = 64 * 1024;

    // Newest event timestamp (Unix ms) found in the file's tail, or null if none parse
    // (the caller treats null conservatively — include the file rather than risk data loss).
    public static long? NewestTimestampMs(FileFingerprint file)
    {
        try
        {
            using var fs = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.SequentialScan);
            var from = Math.Max(0, fs.Length - TailBytes);
            if (from > 0)
            {
                fs.Seek(from, SeekOrigin.Begin);
            }

            using var reader = new JsonlLineReader(fs, from);
            long? newest = null;
            var dropLeading = from > 0; // a mid-file seek lands inside a line; drop that fragment

            while (reader.TryReadLine(out var line))
            {
                if (dropLeading)
                {
                    dropLeading = false;
                    continue;
                }

                var ts = TimestampOf(line);
                if (ts > 0 && (newest is null || ts > newest))
                {
                    newest = ts;
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // unreadable -> unknown -> caller includes the file
        }
    }

    private static long TimestampOf(ReadOnlyMemory<byte> line)
    {
        var span = line.Span;
        var start = JsonlLineReader.FirstNonWhitespace(span);
        if (start < 0 || span[start] != (byte)'{')
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(line.Slice(start));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                ? IsoTime.ToUnixMs(t.GetString())
                : 0;
        }
        catch (JsonException)
        {
            return 0; // a malformed tail line just doesn't contribute a timestamp
        }
    }
}
