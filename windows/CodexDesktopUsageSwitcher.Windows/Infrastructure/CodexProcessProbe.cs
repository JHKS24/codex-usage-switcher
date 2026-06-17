using System.ComponentModel;
using System.Diagnostics;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal readonly record struct CodexProcessRow(int Pid, string Name, string CommandLine);

// Source of the running-process table. Returns null when the list could not be obtained at all —
// the caller must treat "cannot inspect" as distinct from "none running" and refuse to switch.
internal interface IProcessLister
{
    IReadOnlyList<CodexProcessRow>? List();
}

internal readonly record struct CodexStopResult(
    bool Inspected,
    IReadOnlyList<string> Terminated,
    IReadOnlyList<string> Killed,
    IReadOnlyList<string> Remaining);

// Decides whether a process row belongs to Codex Desktop / app-server, mirroring the original
// tool's name + command-line markers (and its ignore list, so the crashpad helper and this app
// itself are never matched). Pure and fully unit-tested.
internal static class CodexProcessMatcher
{
    private static readonly string[] ProcessNames = ["Codex.exe", "app-server.exe"];
    private static readonly string[] Markers = ["codex app-server", "Codex Desktop", "\\Codex\\Codex.exe", "/Codex/Codex.exe"];
    private static readonly string[] Ignored =
        ["browser_crashpad_handler", "SkyComputerUseClient turn-ended", "SkyComputerUseService", "codex-desktop-switch"];

    public static bool IsCodexProcess(string name, string commandLine)
    {
        if (Ignored.Any(marker => commandLine.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        if (ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // app-server.exe is a generic name; only count it when the command line names codex.
            return !name.Equals("app-server.exe", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("codex", StringComparison.OrdinalIgnoreCase);
        }

        return Markers.Any(marker => commandLine.Contains(marker, StringComparison.Ordinal));
    }
}

// Finds and (best-effort) stops Codex processes. Detection is testable via IProcessLister; the
// actual termination uses native Process APIs (graceful window close, then force-kill the tree).
internal sealed class CodexProcessProbe
{
    private readonly IProcessLister _lister;
    private readonly int _selfPid;

    public CodexProcessProbe(IProcessLister lister, int selfPid)
    {
        _lister = lister;
        _selfPid = selfPid;
    }

    // null => could not inspect; otherwise the matching rows, excluding this process.
    public IReadOnlyList<CodexProcessRow>? FindRunning()
    {
        var rows = _lister.List();
        return rows?
            .Where(row => row.Pid != _selfPid && CodexProcessMatcher.IsCodexProcess(row.Name, row.CommandLine))
            .ToArray();
    }

    public async Task<CodexStopResult> StopAsync(double graceSeconds, CancellationToken cancellationToken)
    {
        var entries = FindRunning();
        if (entries is null)
        {
            return new CodexStopResult(false, [], [], []);
        }

        if (entries.Count == 0)
        {
            return new CodexStopResult(true, [], [], []);
        }

        foreach (var entry in entries)
        {
            TryGraceful(entry.Pid);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, graceSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var still = FindRunning();
            if (still is null || still.Count == 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }

        var survivors = FindRunning() ?? [];
        var killed = survivors.Where(entry => TryKillTree(entry.Pid)).Select(Describe).ToArray();
        var remaining = (FindRunning() ?? []).Select(Describe).ToArray();
        return new CodexStopResult(true, entries.Select(Describe).ToArray(), killed, remaining);
    }

    private static string Describe(CodexProcessRow row) =>
        $"{row.Pid} {(string.IsNullOrEmpty(row.CommandLine) ? row.Name : row.CommandLine)}";

    private static void TryGraceful(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.CloseMainWindow(); // windowless children are handled by the force-kill pass
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Process already gone, or has no message loop; nothing graceful to do.
        }
    }

    private static bool TryKillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already exited between detection and kill
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false; // could not kill (access denied, etc.)
        }
    }
}
