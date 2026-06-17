using System.Diagnostics;
using System.Text;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal sealed class ProcessSwitcherClient : ISwitcherClient
{
    private readonly string _switcherPath;
    private readonly string _pythonPath;

    private ProcessSwitcherClient(string switcherPath, string pythonPath)
    {
        _switcherPath = switcherPath;
        _pythonPath = pythonPath;
    }

    public static ProcessSwitcherClient CreateDefault()
    {
        var switcherPath = SwitcherPathResolver.ResolveSwitcherPath();
        var pythonPath = SwitcherPathResolver.ResolvePythonPath();
        return new ProcessSwitcherClient(switcherPath, pythonPath);
    }

    public Task<SwitcherCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // CreateProcess (plus the interpreter boot behind it) is synchronous and slow;
        // run the whole exchange on the thread pool so callers on the UI thread never block.
        return Task.Run(() => RunCoreAsync(arguments, timeout, cancellationToken), CancellationToken.None);
    }

    private async Task<SwitcherCommandResult> RunCoreAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(arguments);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Process.Dispose does not close redirected streams once they are referenced;
            // dispose them explicitly or each call strands two pipe handles until GC.
            using var stdout = process.StandardOutput;
            using var stderr = process.StandardError;
            var stdoutTask = stdout.ReadToEndAsync(cancellationToken);
            var stderrTask = stderr.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            if (await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false) == timeoutTask)
            {
                TryKill(process);
                try
                {
                    // The kill breaks the pipes, letting the pending reads complete before
                    // disposal — but the kill is best-effort, so bound the wait too.
                    await Task.WhenAll(stdoutTask, stderrTask)
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Reads may fault when the killed child's pipes break; nothing to salvage.
                }

                return new SwitcherCommandResult(-1, "", $"command timed out after {timeout.TotalSeconds:0}s");
            }

            timeoutCts.Cancel();
            await waitTask.ConfigureAwait(false);
            return new SwitcherCommandResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SwitcherCommandResult(-1, "", ex.Message);
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(_switcherPath);
        var runsDirectly = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = runsDirectly ? _switcherPath : _pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // Pin the child's stdio to UTF-8 regardless of the machine's ANSI code page,
        // matching the UTF-8 decode above (legacy cp949 systems would mojibake otherwise).
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        if (!runsDirectly)
        {
            startInfo.ArgumentList.Add(_switcherPath);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort timeout cleanup; the caller gets a timeout result.
        }
    }
}
