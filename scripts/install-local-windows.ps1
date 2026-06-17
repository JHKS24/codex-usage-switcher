param(
    [string]$InstallDir = "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher",
    [string]$BinDir = "$HOME\bin",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("SelfContained")) {
    $SelfContained = $true
}

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

& (Join-Path $PSScriptRoot "build-windows.ps1") -Configuration Release -Runtime win-x64 -SelfContained:$SelfContained

$running = Get-Process -Name "CodexDesktopUsageSwitcher.Windows" -ErrorAction SilentlyContinue
$wasRunning = [bool]$running
if ($running) {
    $running | Stop-Process -Force
    # Stop-Process returns before teardown completes; wait on the handle so the
    # copy below cannot race a still-mapped exe and leave a mixed-version install.
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

$PublishDir = Join-Path $Root "build\windows\win-x64"
$resolvedInstallParent = Resolve-Path (Split-Path -Parent $InstallDir)
if (-not $resolvedInstallParent.Path.StartsWith($env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallDir must stay under LOCALAPPDATA unless you edit the script intentionally: $InstallDir"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force
$attempt = 0
while ($true) {
    $attempt++
    try {
        Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force
        break
    } catch {
        if ($attempt -ge 3) { throw }
        Start-Sleep -Seconds 1
    }
}

New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
$cliCmd = Join-Path $BinDir "codex-desktop-switch.cmd"
# InstallDir is guaranteed under LOCALAPPDATA above; the %LOCALAPPDATA% form keeps
# the ASCII-encoded .cmd correct for non-ASCII usernames.
$cliTarget = "%LOCALAPPDATA%" + $InstallDir.Substring($env:LOCALAPPDATA.Length) + "\codex-desktop-switch.py"
$cmd = @"
@echo off
set "PYTHON_CMD="
rem Probe by execution: the WindowsApps python.exe stub passes `where` but cannot run.
python -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" >nul 2>nul && set "PYTHON_CMD=python"
if not defined PYTHON_CMD py -3 -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" >nul 2>nul && set "PYTHON_CMD=py -3"
if not defined PYTHON_CMD (
  echo error: Python 3 was not found. Install Python 3 or put python on PATH. 1>&2
  exit /b 3
)
%PYTHON_CMD% "$cliTarget" %*
"@
Set-Content -LiteralPath $cliCmd -Value $cmd -Encoding ASCII

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenu "Codex Desktop Usage Switcher.lnk"
$exePath = Join-Path $InstallDir "CodexDesktopUsageSwitcher.Windows.exe"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

if ($wasRunning) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
}

Write-Host "Installed: $InstallDir"
Write-Host "Start menu: $shortcutPath"
Write-Host "CLI shortcut: $cliCmd"
Write-Host "Run: $exePath"
