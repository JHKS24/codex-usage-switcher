using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class SensitiveFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly SwitcherPaths _paths;
    private readonly SensitiveFileStore _store;
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 8, 30, 0, TimeSpan.Zero);

    public SensitiveFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sensitive-store-test-" + Guid.NewGuid().ToString("N"));
        var switchHome = Path.Combine(_root, ".codex-switch");
        _paths = new SwitcherPaths(
            CodexHome: Path.Combine(_root, ".codex"),
            SwitchHome: switchHome,
            ProfilesRoot: Path.Combine(switchHome, "profiles"),
            BackupRoot: Path.Combine(switchHome, "backups"),
            ActiveFile: Path.Combine(switchHome, "active"),
            ClaudeCredentials: Path.Combine(_root, "claude", "credentials.json"),
            ClaudePending: Path.Combine(switchHome, "pending.json"),
            ClaudeCooldown: Path.Combine(switchHome, "cooldown.json"));
        _store = new SensitiveFileStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void SeedProfile(string name, string content)
    {
        Directory.CreateDirectory(_paths.ProfileDir(name));
        File.WriteAllText(_paths.ProfileAuth(name), content);
    }

    private void SeedTarget(string content)
    {
        Directory.CreateDirectory(_paths.CodexHome);
        File.WriteAllText(_paths.TargetAuth, content);
    }

    [Fact]
    public void SwitchTo_backs_up_prior_then_installs_profile_and_marks_active()
    {
        SeedTarget("ORIGINAL");
        SeedProfile("work", "WORK");

        var result = _store.SwitchTo("work", Now);

        Assert.True(result.Switched);
        Assert.Null(result.Error);
        Assert.NotNull(result.BackupId);
        Assert.Equal("WORK", File.ReadAllText(_paths.TargetAuth));
        Assert.Equal("work", _store.ReadActive());
        var backedUp = File.ReadAllText(Path.Combine(_paths.BackupRoot, result.BackupId!, SwitcherPaths.AuthName));
        Assert.Equal("ORIGINAL", backedUp); // the file we overwrote is recoverable
    }

    [Fact]
    public void SwitchTo_with_no_prior_auth_installs_without_a_backup()
    {
        SeedProfile("edu", "EDU");

        var result = _store.SwitchTo("edu", Now);

        Assert.True(result.Switched);
        Assert.Null(result.BackupId);
        Assert.Equal("EDU", File.ReadAllText(_paths.TargetAuth));
    }

    [Fact]
    public void SwitchTo_rejects_invalid_name_and_missing_profile_without_touching_target()
    {
        SeedTarget("ORIGINAL");

        var bad = _store.SwitchTo("../escape", Now);
        Assert.False(bad.Switched);
        Assert.Equal("invalid profile name", bad.Error);

        var missing = _store.SwitchTo("ghost", Now);
        Assert.False(missing.Switched);
        Assert.Contains("no auth.json", missing.Error);

        Assert.Equal("ORIGINAL", File.ReadAllText(_paths.TargetAuth)); // untouched
    }

    [Fact]
    public void SwitchTo_leaves_no_temp_file_behind()
    {
        SeedTarget("ORIGINAL");
        SeedProfile("work", "WORK");

        _store.SwitchTo("work", Now);

        var leftovers = Directory.GetFiles(_paths.CodexHome).Where(f => f.Contains(".tmp")).ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void SwitchTo_failure_keeps_the_live_auth_intact()
    {
        SeedTarget("ORIGINAL");
        SeedProfile("work", "WORK");

        // Lock the live file so the switch cannot read/overwrite it, forcing the failure path.
        using (new FileStream(_paths.TargetAuth, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _store.SwitchTo("work", Now);
            Assert.False(result.Switched);
            Assert.Contains("switch failed", result.Error);
        }

        Assert.Equal("ORIGINAL", File.ReadAllText(_paths.TargetAuth)); // not corrupted
        Assert.Equal("unknown", _store.ReadActive()); // active never advanced
    }

    [Fact]
    public void CreateBackup_prunes_to_the_retention_limit()
    {
        for (var i = 0; i < 25; i++)
        {
            SeedTarget("v" + i);
            _store.CreateBackup(Now.AddSeconds(i));
        }

        var count = Directory.GetDirectories(_paths.BackupRoot).Length;
        Assert.Equal(20, count); // BackupKeep
    }
}
