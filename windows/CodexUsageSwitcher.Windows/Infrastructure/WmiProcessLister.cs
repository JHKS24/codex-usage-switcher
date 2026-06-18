using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Lists running processes with their command lines via WMI (Win32_Process), the native equivalent
// of the PowerShell CIM dump the Python switcher shelled out to. Any failure to query returns null
// so the caller refuses to switch rather than acting on an empty list.
[SupportedOSPlatform("windows")]
internal sealed class WmiProcessLister : IProcessLister
{
    public IReadOnlyList<CodexProcessRow>? List()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process");
            using var results = searcher.Get();
            var rows = new List<CodexProcessRow>();
            foreach (var item in results)
            {
                using var process = (ManagementObject)item;
                if (!TryReadPid(process["ProcessId"], out var pid))
                {
                    continue;
                }

                var name = process["Name"] as string ?? "";
                var command = process["CommandLine"] as string ?? name;
                rows.Add(new CodexProcessRow(pid, name, command));
            }

            return rows;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadPid(object? value, out int pid)
    {
        pid = 0;
        if (value is null)
        {
            return false;
        }

        try
        {
            pid = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
