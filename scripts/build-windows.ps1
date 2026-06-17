param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Project = Join-Path $Root "windows\CodexDesktopUsageSwitcher.Windows\CodexDesktopUsageSwitcher.Windows.csproj"
$PublishDir = Join-Path $Root "build\windows\$Runtime"

Remove-Item -LiteralPath $PublishDir -Recurse -Force -ErrorAction SilentlyContinue

$publishArgs = @(
    "publish",
    $Project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $PublishDir
)

if ($SelfContained) {
    $publishArgs += "--self-contained"
    $publishArgs += "true"
} else {
    $publishArgs += "--self-contained"
    $publishArgs += "false"
}

dotnet @publishArgs

$cmdPath = Join-Path $PublishDir "codex-desktop-switch.cmd"
$cmd = @'
@echo off
set SCRIPT_DIR=%~dp0
set "PYTHON_CMD="
rem Probe by execution: the WindowsApps python.exe stub passes `where` but cannot run.
python -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" >nul 2>nul && set "PYTHON_CMD=python"
if not defined PYTHON_CMD py -3 -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" >nul 2>nul && set "PYTHON_CMD=py -3"
if not defined PYTHON_CMD (
  echo error: Python 3 was not found. Install Python 3 or put python on PATH. 1>&2
  exit /b 3
)
%PYTHON_CMD% "%SCRIPT_DIR%codex-desktop-switch.py" %*
'@
Set-Content -LiteralPath $cmdPath -Value $cmd -Encoding ASCII

Write-Host "Built Windows tray app: $PublishDir"
Write-Host "Run: $PublishDir\CodexDesktopUsageSwitcher.Windows.exe"
Write-Host "CLI: $cmdPath"
