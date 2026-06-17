namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal static class CliToolPaths
{
    public static string? ResolveClaudeCodePath()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "bin",
            "claude.exe");
        return File.Exists(local) ? local : ResolveFromPath("claude.exe");
    }

    internal static string? ResolveFromPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
