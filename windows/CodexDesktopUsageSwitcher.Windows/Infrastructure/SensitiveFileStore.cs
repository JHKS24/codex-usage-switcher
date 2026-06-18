namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Outcome of a profile switch. BackupId is the timestamped backup folder created from the prior
// auth.json (null when there was none to back up).
internal readonly record struct SwitchResult(bool Switched, string? BackupId, string? Error);

// Credential-safe file operations for the switcher. Operates on FILES only — it copies/moves
// auth.json bytes but never parses or logs their contents. Writes go through a temp file + atomic
// replace so a crash mid-write can never leave a torn credential file, and every switch first
// backs up the file it is about to overwrite so it can roll back. Mirrors the original Python
// tool's backup/active layout (SwitcherPaths) for compatibility.
internal sealed class SensitiveFileStore
{
    // How many timestamped backups to retain; older ones are pruned best-effort.
    private const int BackupKeep = 20;

    private readonly SwitcherPaths _paths;

    public SensitiveFileStore(SwitcherPaths paths) => _paths = paths;

    public void EnsureDirs()
    {
        Directory.CreateDirectory(_paths.SwitchHome);
        Directory.CreateDirectory(_paths.ProfilesRoot);
        Directory.CreateDirectory(_paths.BackupRoot);
    }

    // Switch the active Codex auth.json to the named profile: back up the current one, atomically
    // copy the profile's auth.json into place, and record it as active. If any step throws, the
    // prior auth.json is restored from the backup so the live credential is never left broken.
    public SwitchResult SwitchTo(string profile, DateTimeOffset now)
    {
        if (!ProfileName.IsValid(profile))
        {
            return new SwitchResult(false, null, "invalid profile name");
        }

        var source = _paths.ProfileAuth(profile);
        if (!File.Exists(source))
        {
            return new SwitchResult(false, null, $"profile '{profile}' has no auth.json");
        }

        string? backupId = null;
        try
        {
            backupId = CreateBackup(now);
            CopyAtomic(source, _paths.TargetAuth);
            WriteActive(profile);
            return new SwitchResult(true, backupId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFromBackup(backupId, _paths.TargetAuth);
            return new SwitchResult(false, backupId, "switch failed: " + PathRedaction.Scrub(ex.Message));
        }
    }

    // Save the live auth.json into a profile (the inverse of SwitchTo): back up the profile's
    // existing auth.json, copy the current one over it atomically, and roll back on failure.
    public SwitchResult SaveCurrent(string profile, DateTimeOffset now)
    {
        if (!ProfileName.IsValid(profile))
        {
            return new SwitchResult(false, null, "invalid profile name");
        }

        var source = _paths.TargetAuth;
        if (!File.Exists(source))
        {
            return new SwitchResult(false, null, "current ~/.codex/auth.json not found");
        }

        var destination = _paths.ProfileAuth(profile);
        string? backupId = null;
        try
        {
            backupId = File.Exists(destination) ? BackupExistingFile(destination, now) : null;
            CopyAtomic(source, destination);
            return new SwitchResult(true, backupId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFromBackup(backupId, destination);
            return new SwitchResult(false, backupId, "save failed: " + PathRedaction.Scrub(ex.Message));
        }
    }

    // Atomic copy of src over dst: write to a unique temp in dst's directory, then replace. Never
    // strands a temp file holding credential bytes if the move fails.
    public void CopyAtomic(string src, string dst)
    {
        var dir = Path.GetDirectoryName(dst) ?? throw new ArgumentException("destination has no directory", nameof(dst));
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(dst)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    // Back up the current target auth.json into a fresh timestamped folder; returns its id, or
    // null when there is nothing to back up. Prunes to the newest BackupKeep.
    public string? CreateBackup(DateTimeOffset now) =>
        File.Exists(_paths.TargetAuth) ? BackupExistingFile(_paths.TargetAuth, now) : null;

    private string BackupExistingFile(string sourceFile, DateTimeOffset now)
    {
        EnsureDirs();
        var dir = ReserveBackupDir(now);
        File.Copy(sourceFile, Path.Combine(dir, SwitcherPaths.AuthName), overwrite: true);
        PruneBackups(keep: dir);
        return Path.GetFileName(dir);
    }

    public void WriteActive(string profile)
    {
        EnsureDirs();
        File.WriteAllText(_paths.ActiveFile, profile);
    }

    public string ReadActive() =>
        File.Exists(_paths.ActiveFile) ? File.ReadAllText(_paths.ActiveFile).Trim() : "unknown";

    private string ReserveBackupDir(DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(_paths.BackupRoot, stamp);
        var suffix = 2;
        while (Directory.Exists(dir))
        {
            dir = Path.Combine(_paths.BackupRoot, $"{stamp}-{suffix++}");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private void PruneBackups(string keep)
    {
        DirectoryInfo[] dirs;
        try
        {
            dirs = new DirectoryInfo(_paths.BackupRoot).GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // can't enumerate -> nothing to prune; the just-made backup still stands
        }

        var stale = dirs
            .Where(d => !string.Equals(d.FullName, keep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var dir in stale.Take(Math.Max(0, stale.Count - (BackupKeep - 1))))
        {
            TryDeleteDir(dir);
        }
    }

    private void RestoreFromBackup(string? backupId, string destination)
    {
        if (backupId is null)
        {
            return; // there was no prior file, so nothing to restore
        }

        var backupAuth = Path.Combine(_paths.BackupRoot, backupId, SwitcherPaths.AuthName);
        if (!File.Exists(backupAuth))
        {
            return;
        }

        try
        {
            CopyAtomic(backupAuth, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rollback itself failed: record it (path-only, no token contents) without masking the
            // original switch error already being returned to the caller.
            CrashLog.Write(new IOException("auth rollback failed", ex));
        }
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
            CrashLog.Write(ex); // best-effort cleanup of a temp file; not worth aborting for
        }
    }

    private static void TryDeleteDir(DirectoryInfo dir)
    {
        try
        {
            dir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex); // pruning an old backup is best-effort; a switch must not fail on it
        }
    }
}
