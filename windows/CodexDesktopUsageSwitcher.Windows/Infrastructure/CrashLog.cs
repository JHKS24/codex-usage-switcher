namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Single sink (guardrail D3) for non-fatal/background error reporting, so failures that can't
// reach the WinForms ThreadException boundary (fire-and-forget async work) still leave a trace
// instead of vanishing as unobserved task exceptions. Writing the log must never itself crash.
internal static class CrashLog
{
    public static void Write(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CodexDesktopUsageSwitcher");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {PathRedaction.Scrub(exception.ToString())}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // logging is best-effort; never take the caller down because the log couldn't be written
        }
    }
}
