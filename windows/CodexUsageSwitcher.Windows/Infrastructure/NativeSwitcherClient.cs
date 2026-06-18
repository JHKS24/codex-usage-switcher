using System.Globalization;
using System.Text.Json.Nodes;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Native, Python-free implementation of ISwitcherClient. Maps the same command-line verbs the app
// already issues (list/current/usage/claude-usage/snapshot/stop-codex/use/open/doctor) onto the
// native collaborators and emits the exact JSON contract via SwitcherJson, so SwitcherService and
// the UI are unchanged. Token contents are never read here, only passed through the typed clients.
internal sealed class NativeSwitcherClient : ISwitcherClient
{
    private const int ExitMissing = 12;
    private const int ExitCodexRunning = 10;
    private const int ExitProcessUnknown = 13;
    private const int ExitOther = 15;

    private readonly SwitcherPaths _paths;
    private readonly ProfileEnumerator _profiles;
    private readonly CurrentStateBuilder _currentState;
    private readonly SensitiveFileStore _store;
    private readonly CodexProcessProbe _processes;
    private readonly CodexUsageClient _codexUsage;
    private readonly ClaudeUsageClient _claudeUsage;

    public NativeSwitcherClient(
        SwitcherPaths paths,
        ProfileEnumerator profiles,
        CurrentStateBuilder currentState,
        SensitiveFileStore store,
        CodexProcessProbe processes,
        CodexUsageClient codexUsage,
        ClaudeUsageClient claudeUsage)
    {
        _paths = paths;
        _profiles = profiles;
        _currentState = currentState;
        _store = store;
        _processes = processes;
        _codexUsage = codexUsage;
        _claudeUsage = claudeUsage;
    }

    public static NativeSwitcherClient CreateDefault()
    {
        var paths = SwitcherPaths.CreateDefault();
        var profiles = new ProfileEnumerator(paths);
        var http = new HttpJsonClient();
        return new NativeSwitcherClient(
            paths,
            profiles,
            new CurrentStateBuilder(paths, profiles),
            new SensitiveFileStore(paths),
            new CodexProcessProbe(new WmiProcessLister(), Environment.ProcessId),
            new CodexUsageClient(http),
            new ClaudeUsageClient(new ClaudeCredentialStore(paths.ClaudeCredentials), http));
    }

    public Task<SwitcherCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Offload to the thread pool: file IO, WMI, and the synchronous switch must never run on a
        // UI thread, matching the old process-based client's behavior.
        return Task.Run(() => RunCoreAsync(arguments, timeout, cancellationToken), CancellationToken.None);
    }

    private async Task<SwitcherCommandResult> RunCoreAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await DispatchAsync(arguments, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SwitcherCommandResult(-1, "", $"command timed out after {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ExitOther, $"unexpected failure: {PathRedaction.Scrub(ex.Message)}");
        }
    }

    private async Task<SwitcherCommandResult> DispatchAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var command = arguments.Count > 0 ? arguments[0] : "";
        return command switch
        {
            "list" => Ok(SwitcherJson.Profiles(_profiles.List(), _paths.ProfilesRoot)),
            "current" => Ok(SwitcherJson.Current(BuildCurrent())),
            "usage" => Ok(SwitcherJson.Usage(await FetchUsageAsync(ProfileArg(arguments), cancellationToken).ConfigureAwait(false))),
            "claude-usage" => Ok(SwitcherJson.Claude(await _claudeUsage.GetUsageAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))),
            "snapshot" => await SnapshotAsync(cancellationToken).ConfigureAwait(false),
            "stop-codex" => await StopAsync(arguments, cancellationToken).ConfigureAwait(false),
            "use" => UseProfile(arguments),
            "open" => OpenApp(),
            "doctor" => Ok(BuildDoctor()),
            _ => Fail(ExitOther, $"unknown command: {command}"),
        };
    }

    private async Task<SwitcherCommandResult> SnapshotAsync(CancellationToken cancellationToken)
    {
        var currentTask = Task.Run(BuildCurrent, cancellationToken);
        var usageTask = FetchUsageAsync(null, cancellationToken);
        var claudeTask = _claudeUsage.GetUsageAsync(DateTimeOffset.UtcNow, cancellationToken);

        var current = await SafeAsync(currentTask, SwitcherJson.Current, SwitcherJson.CurrentUnavailable()).ConfigureAwait(false);
        var usage = await SafeListAsync(usageTask, cancellationToken).ConfigureAwait(false);
        var claude = await SafeAsync(claudeTask, SwitcherJson.Claude, SwitcherJson.ClaudeUnavailable()).ConfigureAwait(false);
        return Ok(SwitcherJson.Snapshot(_profiles.List(), _paths.ProfilesRoot, current, usage, claude));
    }

    private async Task<SwitcherCommandResult> StopAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _processes.StopAsync(GraceSeconds(arguments), cancellationToken).ConfigureAwait(false);
        var exit = result.Inspected
            ? (result.Remaining.Count == 0 ? 0 : ExitCodexRunning)
            : ExitProcessUnknown;
        return new SwitcherCommandResult(exit, SwitcherJson.Serialize(SwitcherJson.Stop(result)), "");
    }

    private SwitcherCommandResult UseProfile(IReadOnlyList<string> arguments)
    {
        var profile = ProfileArg(arguments);
        if (profile is null || !ProfileName.IsValid(profile))
        {
            return Fail(ExitOther, "invalid profile name");
        }

        if (!arguments.Contains("--apply"))
        {
            return Ok(SwitcherJson.UseDryRun(profile));
        }

        if (CodexConfigReader.FileSwitchingDisabled(CodexConfigReader.CredentialsStore(_paths.CodexHome)))
        {
            return Fail(ExitOther, "file switching is disabled by cli_auth_credentials_store");
        }

        if (!File.Exists(_paths.ProfileAuth(profile)))
        {
            return Fail(ExitMissing, $"source auth.json not found for profile: {profile}");
        }

        var running = _processes.FindRunning();
        if (running is null)
        {
            return Fail(ExitProcessUnknown, "could not inspect running processes; refusing to switch safely");
        }

        if (running.Count > 0)
        {
            return Fail(ExitCodexRunning, "quit Codex Desktop and Codex app-server sessions, then retry");
        }

        var result = _store.SwitchTo(profile, DateTimeOffset.Now);
        return result.Switched
            ? Ok(SwitcherJson.UseSuccess(profile, result.BackupId))
            : Fail(ExitOther, result.Error ?? "switch failed");
    }

    private SwitcherCommandResult OpenApp()
    {
        var error = CodexInstallation.Open();
        return error is null
            ? Ok(new JsonObject { ["ok"] = true, ["opened"] = "Codex" })
            : Fail(ExitMissing, error);
    }

    private CodexCurrentState BuildCurrent()
    {
        var running = _processes.FindRunning();
        return _currentState.Build(running is null ? null : running.Count > 0);
    }

    private JsonObject BuildDoctor()
    {
        var running = _processes.FindRunning();
        return SwitcherJson.Doctor(
            _paths.ProfilesRoot,
            _profiles.List(),
            CodexInstallation.ResolveCliPath(),
            CodexInstallation.ResolveAppPath(),
            running is null ? null : running.Count > 0);
    }

    private async Task<IReadOnlyList<CodexUsageRow>> FetchUsageAsync(string? profile, CancellationToken cancellationToken)
    {
        var directories = profile is not null
            ? [_paths.ProfileDir(profile)]
            : _profiles.DirectoryPaths();

        var now = DateTimeOffset.UtcNow;
        var tasks = directories.Select(directory =>
        {
            var name = Path.GetFileName(directory);
            return _codexUsage.FetchAsync(name, CodexAuthReader.Read(directory), now, cancellationToken);
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<JsonObject> SafeAsync<T>(Task<T> task, Func<T, JsonObject> map, JsonObject fallback)
    {
        try
        {
            return map(await task.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Write(ex);
            return fallback;
        }
    }

    private static async Task<IReadOnlyList<CodexUsageRow>> SafeListAsync(Task<IReadOnlyList<CodexUsageRow>> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Write(ex);
            return [];
        }
    }

    private static double GraceSeconds(IReadOnlyList<string> arguments)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "--grace-seconds")
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && index + 1 < arguments.Count &&
            double.TryParse(arguments[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Max(0, value);
        }

        return 4.0;
    }

    private static string? ProfileArg(IReadOnlyList<string> arguments) =>
        arguments.Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));

    private static SwitcherCommandResult Ok(JsonObject payload) =>
        new(0, SwitcherJson.Serialize(payload), "");

    private static SwitcherCommandResult Fail(int exitCode, string message) =>
        new(exitCode, SwitcherJson.Serialize(SwitcherJson.Error(message)), "");
}
