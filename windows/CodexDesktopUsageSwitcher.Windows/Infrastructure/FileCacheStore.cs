using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Per-file disk cache: one JSON file per source transcript, named by SHA-256 of its path,
// under a provider-specific directory. This is the boundary (guardrail I1) where cache I/O
// errors are handled: every failure degrades to "no cache" so the caller re-parses — the
// app never crashes because the cache is purely an optimization over the source JSONL.
//
//  - Writes are atomic (unique <hash>.<guid>.tmp then File.Move overwrite, verdict V5) so a
//    crash or a concurrent writer can never leave a torn cache file.
//  - A read that hits corruption deletes the bad file and misses, so the caller re-parses
//    from source rather than taking the mtime-skip path (verdict V1).
//  - A schema or fingerprint mismatch is a miss (the record will be overwritten on Save).
internal sealed class FileCacheStore
{
    public const int CurrentSchemaVersion = 2; // bumped: cached entries now carry the token breakdown

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _dir;

    public FileCacheStore(string cacheDir) => _dir = cacheDir;

    // Loads the stored record for a source path iff it exists, parses, and the schema matches.
    // The fingerprint is NOT compared here — the cache layer compares it to decide hit vs
    // append-delta vs full reparse, and even a stale record is useful as the delta base.
    // Corruption -> delete + miss.
    public bool TryLoad(string sourcePath, out FileCacheRecord record)
    {
        record = null!;
        var path = CachePath(sourcePath);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            FileCacheRecord? loaded;
            using (var stream = File.OpenRead(path))
            {
                loaded = JsonSerializer.Deserialize<FileCacheRecord>(stream, JsonOptions);
            }

            if (loaded is null ||
                loaded.SchemaVersion != CurrentSchemaVersion ||
                loaded.Entries is null ||
                loaded.CodexState is { TurnModels: null })
            {
                // missing / schema drift / structurally incomplete (valid JSON but null Entries
                // or null TurnModels would NRE downstream) -> miss; the caller reparses + Save
                // overwrites it.
                return false;
            }

            record = loaded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path); // corrupt cache file -> remove so the caller reparses cleanly
            return false;
        }
    }

    // Atomically writes the record. Best-effort: an I/O failure leaves the cache unchanged and
    // the next open re-parses; it never throws.
    public void Save(FileCacheRecord record)
    {
        var path = CachePath(record.Fingerprint.FullPath);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_dir);
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp); // leftover temp is harmless; the cache simply stays cold
        }
    }

    // Drops a file's cache entry (used on truncation / source removal).
    public void Evict(string sourcePath) => TryDelete(CachePath(sourcePath));

    private string CachePath(string sourcePath)
    {
        // Windows paths are case-insensitive: hash the lowercased path so the same file maps
        // to one cache entry regardless of casing.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())));
        return Path.Combine(_dir, $"{hash}.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort: a leftover cache/temp file is harmless and retried next cycle
        }
    }
}
