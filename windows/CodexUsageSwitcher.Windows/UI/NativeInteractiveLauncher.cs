using System.ComponentModel;
using System.Diagnostics;
using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.UI;

// Python-free interactive logins. Claude usage login runs the PKCE flow in-process (browser +
// paste dialog + token exchange); Codex/Claude-Code logins run their own CLIs in a terminal; saving
// the current profile is a direct, guarded file copy. No token contents are shown or logged.
internal sealed class NativeInteractiveLauncher : IInteractiveCliLauncher
{
    private readonly SwitcherPaths _paths;
    private readonly ClaudeOAuthClient _oauth;
    private readonly ClaudeCredentialStore _credentials;
    private readonly SensitiveFileStore _store;
    private readonly CodexProcessProbe _processes;

    public NativeInteractiveLauncher(
        SwitcherPaths paths,
        ClaudeOAuthClient oauth,
        ClaudeCredentialStore credentials,
        SensitiveFileStore store,
        CodexProcessProbe processes)
    {
        _paths = paths;
        _oauth = oauth;
        _credentials = credentials;
        _store = store;
        _processes = processes;
    }

    public static NativeInteractiveLauncher CreateDefault()
    {
        var paths = SwitcherPaths.CreateDefault();
        return new NativeInteractiveLauncher(
            paths,
            new ClaudeOAuthClient(new HttpJsonClient()),
            new ClaudeCredentialStore(paths.ClaudeCredentials),
            new SensitiveFileStore(paths),
            new CodexProcessProbe(new WmiProcessLister(), Environment.ProcessId));
    }

    public CommandOutcome LaunchClaudeLogin()
    {
        var pkce = ClaudeOAuthClient.GeneratePkce();
        if (!OpenBrowser(pkce.AuthorizeUrl))
        {
            return Fail(Localizer.L("login.error.browserOpenFailed"));
        }

        var raw = CodeInputDialog.Prompt(
            Localizer.L("login.claude.dialogTitle"),
            Localizer.L("login.claude.dialogBody"));
        if (raw is null)
        {
            return Fail(Localizer.L("login.error.cancelled"));
        }

        var parsed = ClaudeOAuthClient.ParseInput(raw);
        if (parsed.Error is not null)
        {
            return Fail(parsed.Error);
        }

        if (parsed.State is not null && parsed.State != pkce.State)
        {
            return Fail(Localizer.L("login.error.oauthStateMismatch"));
        }

        return CompleteClaudeLogin(parsed.Code!, pkce);
    }

    private CommandOutcome CompleteClaudeLogin(string code, ClaudePkce pkce)
    {
        var result = _oauth.ExchangeAsync(code, pkce.State, pkce.Verifier, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Error is not null || result.Credentials is null)
        {
            return Fail(result.Error ?? Localizer.L("login.error.tokenExchangeFailed"));
        }

        try
        {
            _credentials.Save(result.Credentials.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(Localizer.F("login.error.credentialSaveFailed", PathRedaction.Scrub(ex.Message)));
        }

        return new CommandOutcome(true, 0, Localizer.L("login.claude.success"));
    }

    public CommandOutcome LaunchClaudeCodeLogin()
    {
        var claude = CliToolPaths.ResolveClaudeCodePath();
        if (claude is null)
        {
            return Fail(Localizer.L("login.error.claudeCodeNotFound"));
        }

        return InteractiveTerminal.LaunchExe(
            claude, ["auth", "login"], null,
            Localizer.L("login.claudeCode.terminalTitle"),
            Localizer.L("login.claudeCode.terminalInstructions"),
            Localizer.L("login.claudeCode.terminalOpened"),
            Localizer.L("login.claudeCode.terminalFailureHint"));
    }

    public CommandOutcome LaunchCodexLogin(string profile)
    {
        if (!ProfileName.IsValid(profile))
        {
            return Fail(Localizer.L("login.error.invalidProfileName"));
        }

        var codex = CodexInstallation.ResolveCliPath();
        if (codex is null)
        {
            return Fail(Localizer.L("login.error.codexCliNotFound"));
        }

        var home = _paths.ProfileDir(profile);
        Directory.CreateDirectory(home);
        return InteractiveTerminal.LaunchExe(
            codex, ["login"], new Dictionary<string, string> { ["CODEX_HOME"] = home },
            Localizer.F("login.codex.terminalTitle", profile),
            Localizer.F("login.codex.terminalInstructions", profile),
            Localizer.F("login.codex.terminalOpened", profile),
            Localizer.L("login.codex.terminalFailureHint"));
    }

    public CommandOutcome LaunchSaveCurrentProfile(string profile)
    {
        if (!ProfileName.IsValid(profile))
        {
            return Fail(Localizer.L("login.error.invalidProfileName"));
        }

        if (CodexConfigReader.FileSwitchingDisabled(CodexConfigReader.CredentialsStore(_paths.CodexHome)))
        {
            return Fail(Localizer.L("settings.error.fileSwitchingDisabled"));
        }

        var running = _processes.FindRunning();
        if (running is null)
        {
            return Fail(Localizer.L("settings.error.cannotVerifyRunningProcesses"));
        }

        if (running.Count > 0)
        {
            return Fail(Localizer.L("settings.error.closeCodexDesktopFirst"));
        }

        var result = _store.SaveCurrent(profile, DateTimeOffset.Now);
        return result.Switched
            ? new CommandOutcome(true, 0, Localizer.F("settings.saveProfile.success", profile))
            : Fail(result.Error ?? Localizer.L("settings.error.saveFailed"));
    }

    private static bool OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static CommandOutcome Fail(string message) => new(false, -1, message);
}
