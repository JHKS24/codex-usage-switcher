namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Filesystem layout for the native switcher, mirroring the original tool's paths so existing
// profiles/backups stay compatible. auth.json is switched between
// ~/.codex-switch/profiles/<name>/ and ~/.codex/. Nothing here reads token CONTENTS — only
// locations. CODEX_HOME overrides the Codex dir (env then ~/.codex).
internal sealed record SwitcherPaths(
    string CodexHome,
    string SwitchHome,
    string ProfilesRoot,
    string BackupRoot,
    string ActiveFile,
    string ClaudeCredentials,
    string ClaudePending,
    string ClaudeCooldown)
{
    public const string AuthName = "auth.json";

    public string TargetAuth => Path.Combine(CodexHome, AuthName);

    public string ProfileDir(string profile) => Path.Combine(ProfilesRoot, profile);

    public string ProfileAuth(string profile) => Path.Combine(ProfilesRoot, profile, AuthName);

    public static SwitcherPaths CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var envCodex = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
        var codexHome = string.IsNullOrEmpty(envCodex) ? Path.Combine(home, ".codex") : envCodex;
        var switchHome = Path.Combine(home, ".codex-switch");
        return new SwitcherPaths(
            CodexHome: codexHome,
            SwitchHome: switchHome,
            ProfilesRoot: Path.Combine(switchHome, "profiles"),
            BackupRoot: Path.Combine(switchHome, "backups"),
            ActiveFile: Path.Combine(switchHome, "active"),
            ClaudeCredentials: Path.Combine(home, ".config", "claude-usage-bar", "credentials.json"),
            ClaudePending: Path.Combine(switchHome, "claude_oauth_pending.json"),
            ClaudeCooldown: Path.Combine(switchHome, "claude_oauth_cooldown.json"));
    }
}
