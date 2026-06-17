using System.ComponentModel;
using System.Diagnostics;
using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;

namespace CodexDesktopUsageSwitcher.Windows.UI;

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
            return Fail("브라우저를 열지 못했습니다. 기본 브라우저 설정을 확인하세요.");
        }

        var raw = CodeInputDialog.Prompt(
            "Claude 로그인",
            "브라우저에서 승인한 뒤 표시되는 OAuth code만 복사해 붙여넣으세요.\ncommand·URL 전체·파일 내용은 붙여넣지 마세요.");
        if (raw is null)
        {
            return Fail("로그인이 취소되었습니다.");
        }

        var parsed = ClaudeOAuthClient.ParseInput(raw);
        if (parsed.Error is not null)
        {
            return Fail(parsed.Error);
        }

        if (parsed.State is not null && parsed.State != pkce.State)
        {
            return Fail("OAuth state가 일치하지 않습니다. 새 로그인으로 다시 시도하세요.");
        }

        return CompleteClaudeLogin(parsed.Code!, pkce);
    }

    private CommandOutcome CompleteClaudeLogin(string code, ClaudePkce pkce)
    {
        var result = _oauth.ExchangeAsync(code, pkce.State, pkce.Verifier, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Error is not null || result.Credentials is null)
        {
            return Fail(result.Error ?? "토큰 교환에 실패했습니다.");
        }

        try
        {
            _credentials.Save(result.Credentials.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"자격 증명 저장 실패: {ex.Message}");
        }

        return new CommandOutcome(true, 0, "Claude 사용량 로그인 완료. 팝업에서 새로고침하세요.");
    }

    public CommandOutcome LaunchClaudeCodeLogin()
    {
        var claude = CliToolPaths.ResolveClaudeCodePath();
        if (claude is null)
        {
            return Fail("Claude Code 실행 파일을 찾지 못했습니다. ~/.local/bin/claude.exe 또는 PATH를 확인하세요.");
        }

        return InteractiveTerminal.LaunchExe(
            claude, ["auth", "login"], null,
            "Claude Code Login",
            "이 창은 Claude Code의 `claude auth login`을 실행합니다.\n브라우저/터미널 안내에 따라 로그인하세요.",
            "Claude Code 로그인 터미널을 열었습니다.",
            "실패 시 `claude auth status`와 네트워크/브라우저 상태를 확인하세요.");
    }

    public CommandOutcome LaunchCodexLogin(string profile)
    {
        if (!ProfileName.IsValid(profile))
        {
            return Fail("잘못된 프로필 이름입니다.");
        }

        var codex = CodexInstallation.ResolveCliPath();
        if (codex is null)
        {
            return Fail("Codex CLI를 찾지 못했습니다. PATH에 codex를 두거나 CODEX_CLI_PATH를 설정하세요.");
        }

        var home = _paths.ProfileDir(profile);
        Directory.CreateDirectory(home);
        return InteractiveTerminal.LaunchExe(
            codex, ["login"], new Dictionary<string, string> { ["CODEX_HOME"] = home },
            $"Codex Login - {profile}",
            $"브라우저가 열리면 Codex 계정으로 로그인하세요. (프로필: {profile})",
            $"{profile} Codex 로그인 터미널을 열었습니다.",
            "실패 시 Codex CLI 경로와 브라우저 로그인 상태를 확인하세요.");
    }

    public CommandOutcome LaunchSaveCurrentProfile(string profile)
    {
        if (!ProfileName.IsValid(profile))
        {
            return Fail("잘못된 프로필 이름입니다.");
        }

        if (CodexConfigReader.FileSwitchingDisabled(CodexConfigReader.CredentialsStore(_paths.CodexHome)))
        {
            return Fail("cli_auth_credentials_store 설정으로 파일 저장이 비활성화되어 있습니다.");
        }

        var running = _processes.FindRunning();
        if (running is null)
        {
            return Fail("실행 중인 프로세스를 확인할 수 없어 저장을 거부합니다.");
        }

        if (running.Count > 0)
        {
            return Fail("Codex Desktop을 완전히 종료한 뒤 다시 시도하세요.");
        }

        var result = _store.SaveCurrent(profile, DateTimeOffset.Now);
        return result.Switched
            ? new CommandOutcome(true, 0, $"{profile} 프로필로 현재 로그인을 저장했습니다.")
            : Fail(result.Error ?? "저장에 실패했습니다.");
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
