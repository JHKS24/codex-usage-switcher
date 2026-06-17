using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodexDesktopUsageSwitcher.Windows.Domain;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Opens a visible PowerShell window that runs an external CLI (codex / claude) so the user can
// complete an interactive browser login and see the result. Replaces the Python-routed terminal
// launcher; runs the target executable directly with no interpreter in between.
internal static class InteractiveTerminal
{
    public static CommandOutcome LaunchExe(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        string title,
        string instructions,
        string successMessage,
        string failureHint)
    {
        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"codex-switcher-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(
                scriptPath,
                BuildScript(executable, args, environment, title, instructions, failureHint),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", scriptPath },
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            return new CommandOutcome(true, 0, successMessage);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new CommandOutcome(false, -1, $"터미널 실행 실패: {ex.Message}");
        }
    }

    private static string BuildScript(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        string title,
        string instructions,
        string failureHint)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"$Host.UI.RawUI.WindowTitle = {PsString(title)}");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        builder.AppendLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                builder.AppendLine($"$env:{pair.Key} = {PsString(pair.Value)}");
            }
        }

        builder.AppendLine($"Write-Host {PsString(instructions)}");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine($"& {PsString(executable)} @({PsArray(args)})");
        builder.AppendLine("$loginStatus = $LASTEXITCODE");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine("if ($loginStatus -eq 0) {");
        builder.AppendLine("    Write-Host \"완료되었습니다. 트레이 팝업에서 새로고침을 눌러 값을 갱신하세요.\"");
        builder.AppendLine("} else {");
        builder.AppendLine($"    Write-Host {PsString(failureHint)}");
        builder.AppendLine("}");
        builder.AppendLine("Write-Host \"\"");
        builder.AppendLine("Read-Host \"Enter를 누르면 창을 닫습니다\"");
        builder.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("exit $loginStatus");
        return builder.ToString();
    }

    private static string PsArray(IEnumerable<string> values) => string.Join(", ", values.Select(PsString));

    private static string PsString(string value) => "'" + value.Replace("'", "''") + "'";
}
