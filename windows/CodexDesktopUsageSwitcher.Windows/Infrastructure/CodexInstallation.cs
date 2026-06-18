using System.Diagnostics;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Locates the Codex Desktop app and the Codex CLI, and launches the app for the `open` command.
// Mirrors the original tool's resolution (env overrides, the standard install locations, PATH).
internal static class CodexInstallation
{
    public static string? ResolveAppPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_DESKTOP_APP_PATH")?.Trim();
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        foreach (var candidate in AppCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return WhichOnPath("Codex.exe");
    }

    public static string? ResolveCliPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH")?.Trim();
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        return WhichOnPath("codex.exe") ?? WhichOnPath("codex.cmd") ?? WhichOnPath("codex");
    }

    // Returns null on success, or an error message. Launches the app detached; never blocks.
    public static string? Open()
    {
        var app = ResolveAppPath();
        if (app is null)
        {
            return "Codex Desktop app not found; set CODEX_DESKTOP_APP_PATH to Codex.exe";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"failed to launch Codex Desktop: {PathRedaction.Scrub(ex.Message)}";
        }
    }

    private static IEnumerable<string> AppCandidates()
    {
        foreach (var envName in new[] { "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            yield return Path.Combine(root, "Programs", "Codex", "Codex.exe");
            yield return Path.Combine(root, "Codex", "Codex.exe");
        }
    }

    private static string? WhichOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
