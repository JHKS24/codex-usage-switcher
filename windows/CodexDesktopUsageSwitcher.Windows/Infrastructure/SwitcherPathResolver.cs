using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal static class SwitcherPathResolver
{
    private static readonly Lazy<string> PythonPath = new(ResolvePythonPathCore);

    public static string ResolveSwitcherPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_DESKTOP_SWITCH_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        // Prefer the bundled .py over the .cmd shim: the shim re-runs cmd.exe plus
        // `where python` on every call (3-4 extra processes) and re-parses arguments
        // with batch rules, mangling % ^ & ! in values. The .cmd stays for human CLI use.
        var bundledScript = Path.Combine(AppContext.BaseDirectory, "codex-desktop-switch.py");
        if (File.Exists(bundledScript))
        {
            return bundledScript;
        }

        var bundledCommand = Path.Combine(AppContext.BaseDirectory, "codex-desktop-switch.cmd");
        if (File.Exists(bundledCommand))
        {
            return bundledCommand;
        }

        throw new FileNotFoundException(
            "codex-desktop-switch was not found. Set CODEX_DESKTOP_SWITCH_PATH or rebuild the Windows app.",
            bundledScript);
    }

    public static string ResolvePythonPath()
    {
        return PythonPath.Value;
    }

    private static string ResolvePythonPathCore()
    {
        var overridePath = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (RunsPython(overridePath))
            {
                return overridePath;
            }

            throw new InvalidOperationException("PYTHON does not point to a runnable Python 3 interpreter.");
        }

        foreach (var candidate in new[] { CliToolPaths.ResolveFromPath("python.exe") })
        {
            if (candidate is not null && RunsPython(candidate))
            {
                return candidate;
            }
        }

        var pyLauncherPython = ResolvePyLauncherPython3(CliToolPaths.ResolveFromPath("py.exe"));
        if (pyLauncherPython is not null)
        {
            return pyLauncherPython;
        }

        if (RunsPython("python"))
        {
            return "python";
        }

        throw new FileNotFoundException("Python 3 was not found. Install Python 3 or put python on PATH.");
    }

    private static string? ResolvePyLauncherPython3(string? pyLauncher)
    {
        if (pyLauncher is null)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = pyLauncher,
                ArgumentList = { "-3", "-c", "import sys; print(sys.executable)" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The probe failed either way.
                }

                return null;
            }

            using var stdout = process.StandardOutput;
            using var stderr = process.StandardError;
            var pythonPath = stdout.ReadToEnd().Trim();
            _ = stderr.ReadToEnd();
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(pythonPath) && RunsPython(pythonPath))
            {
                return pythonPath;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    // The WindowsApps python.exe is a 0-byte App Execution Alias that merely opens the
    // Microsoft Store when no real python is installed; File.Exists is true either way,
    // so only an execution probe tells a stub from a working interpreter.
    private static bool RunsPython(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The probe failed either way.
                }

                return false;
            }

            using var stdout = process.StandardOutput;
            using var stderr = process.StandardError;
            var version = stdout.ReadToEnd() + stderr.ReadToEnd();
            return process.ExitCode == 0 && Regex.IsMatch(version, @"\bPython 3\.");
        }
        catch
        {
            return false;
        }
    }
}
