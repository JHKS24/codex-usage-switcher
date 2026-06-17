using System.Diagnostics;
using System.Text;
using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal sealed class WindowsTerminalClaudeLoginLauncher : IInteractiveCliLauncher
{
    private readonly string _switcherPath;
    private readonly string _pythonPath;

    private WindowsTerminalClaudeLoginLauncher(string switcherPath, string pythonPath)
    {
        _switcherPath = switcherPath;
        _pythonPath = pythonPath;
    }

    public static WindowsTerminalClaudeLoginLauncher CreateDefault()
    {
        return new WindowsTerminalClaudeLoginLauncher(
            SwitcherPathResolver.ResolveSwitcherPath(),
            SwitcherPathResolver.ResolvePythonPath());
    }

    public CommandOutcome LaunchClaudeLogin()
    {
        var instructions = """
Claude 사용량 로그인

브라우저가 열리면 Claude에서 승인한 뒤 OAuth code만 복사해서 아래 prompt에 붙여넣으세요.
command, URL, token, credentials 파일 내용은 붙여넣지 마세요.
""";
        return LaunchInteractiveCommand(
            ["claude-login"],
            "Claude Login - Codex Desktop Usage Switcher",
            instructions,
            "Claude 로그인 터미널을 열었습니다. 브라우저 인증 후 터미널에 OAuth code만 붙여넣으세요.",
            "429 rate limit이면 같은 코드를 재시도하지 말고, claude-login-reset 후 새 로그인으로 다시 시도하세요.");
    }

    public CommandOutcome LaunchClaudeCodeLogin()
    {
        var claude = CliToolPaths.ResolveClaudeCodePath();
        if (claude is null)
        {
            return new CommandOutcome(false, -1, "Claude Code 실행 파일을 찾지 못했습니다. ~/.local/bin/claude.exe 또는 PATH를 확인하세요.");
        }

        var instructions = """
Claude Code 로그인

이 창은 Claude Code의 `claude auth login`을 실행합니다.
브라우저 또는 터미널 안내에 따라 Anthropic 계정 로그인을 완료하세요.
""";
        return LaunchExternalCommand(
            claude,
            ["auth", "login"],
            "Claude Code Login",
            instructions,
            "Claude Code 로그인 터미널을 열었습니다.",
            "Claude Code 로그인이 실패하면 `claude auth status`와 네트워크/브라우저 상태를 확인하세요.");
    }

    public CommandOutcome LaunchCodexLogin(string profile)
    {
        var instructions = $"""
Codex 프로필 로그인: {profile}

브라우저가 열리면 Codex 계정으로 로그인하세요.
로그인이 끝나면 이 창에서 완료 여부를 확인할 수 있습니다.
""";
        return LaunchInteractiveCommand(
            ["login", profile],
            $"Codex Login - {profile}",
            instructions,
            $"{profile} Codex 로그인 터미널을 열었습니다.",
            "로그인이 실패하면 Codex CLI 경로와 브라우저 로그인 상태를 확인하세요.");
    }

    public CommandOutcome LaunchSaveCurrentProfile(string profile)
    {
        var instructions = $"""
현재 Codex 로그인을 프로필로 저장: {profile}

이 작업은 현재 사용자 계정의 Codex auth 파일을 {profile} 프로필로 저장합니다.
토큰 내용은 출력하지 않습니다.
""";
        return LaunchInteractiveCommand(
            ["save", profile, "--apply"],
            $"Save Codex Profile - {profile}",
            instructions,
            $"{profile} 저장 터미널을 열었습니다.",
            "저장이 실패하면 Codex Desktop을 완전히 종료한 뒤 다시 시도하세요.");
    }

    private CommandOutcome LaunchExternalCommand(
        string executable,
        string[] args,
        string title,
        string instructions,
        string successMessage,
        string failureHint)
    {
        try
        {
            var scriptPath = WriteExternalCommandScript(executable, args, title, instructions, failureHint);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-NoExit",
                    "-File",
                    scriptPath,
                },
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });

            return new CommandOutcome(true, 0, successMessage);
        }
        catch (Exception ex)
        {
            return new CommandOutcome(false, -1, $"터미널 실행 실패: {ex.Message}");
        }
    }

    private CommandOutcome LaunchInteractiveCommand(
        string[] cliArgs,
        string title,
        string instructions,
        string successMessage,
        string failureHint)
    {
        try
        {
            var scriptPath = WriteCommandScript(cliArgs, title, instructions, failureHint);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-NoExit",
                    "-File",
                    scriptPath,
                },
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });

            return new CommandOutcome(
                true,
                0,
                successMessage);
        }
        catch (Exception ex)
        {
            return new CommandOutcome(false, -1, $"터미널 실행 실패: {ex.Message}");
        }
    }

    private string WriteCommandScript(string[] cliArgs, string title, string instructions, string failureHint)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"codex-switcher-{Guid.NewGuid():N}.ps1");

        var command = BuildPowerShellCommand(cliArgs);
        var script = BuildTerminalScript(title, instructions, command, failureHint);

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static string WriteExternalCommandScript(string executable, string[] args, string title, string instructions, string failureHint)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"codex-switcher-{Guid.NewGuid():N}.ps1");

        var command = $"& {PowerShellString(executable)} @({PowerShellArray(args)})";
        var script = BuildTerminalScript(title, instructions, command, failureHint);

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static string BuildTerminalScript(string title, string instructions, string command, string failureHint)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"$Host.UI.RawUI.WindowTitle = {PowerShellString(title)}");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        builder.AppendLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
        builder.AppendLine($"Write-Host {PowerShellString(instructions)}");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine(command);
        builder.AppendLine("$loginStatus = $LASTEXITCODE");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine("if ($loginStatus -eq 0) {");
        builder.AppendLine("    Write-Host \"완료되었습니다. 트레이 팝업에서 새로고침을 눌러 값을 갱신하세요.\"");
        builder.AppendLine("} else {");
        builder.AppendLine("    Write-Host \"실패했습니다. 위 오류를 확인하세요.\"");
        builder.AppendLine($"    Write-Host {PowerShellString(failureHint)}");
        builder.AppendLine("}");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine("Read-Host \"Enter를 누르면 창을 닫습니다\"");
        builder.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("exit $loginStatus");
        return builder.ToString();
    }

    private string BuildPowerShellCommand(string[] cliArgs)
    {
        var extension = Path.GetExtension(_switcherPath);
        var args = PowerShellArray(cliArgs);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return $"& {PowerShellString(_switcherPath)} @({args})";
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"& {PowerShellString(_switcherPath)} @({args})";
        }

        var allArgs = new[] { _switcherPath }.Concat(cliArgs).ToArray();
        return $"& {PowerShellString(_pythonPath)} @({PowerShellArray(allArgs)})";
    }

    private static string PowerShellArray(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(PowerShellString));
    }

    private static string PowerShellString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }
}
