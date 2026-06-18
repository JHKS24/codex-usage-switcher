#!/usr/bin/env pwsh
# Builds the single self-contained Windows exe and packages a redistributable zip:
# dist/CodexUsageSwitcher-win-x64/ holds the exe plus the end-user installer
# (install.cmd + install.ps1), and is zipped to dist/CodexUsageSwitcher-win-x64.zip.
[CmdletBinding()]
param(
    # Kept as a harmless no-op alias: the build is always single-file self-contained.
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Runtime = "win-x64"

# 1. Build the single self-contained exe via the repo-root build entry point.
Write-Host "=== Building single self-contained exe ==="
& (Join-Path $Root "build-windows.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-windows.ps1 failed (exit $LASTEXITCODE)." }

$BuildExe = Join-Path $Root "build\win-x64\CodexUsageSwitcher.Windows.exe"
if (-not (Test-Path -LiteralPath $BuildExe)) {
    throw "Build finished but $BuildExe is missing; the publish may have failed."
}

# 2. Stage the exe together with the end-user installer.
$DistRoot = Join-Path $Root "dist"
$PackageName = "CodexUsageSwitcher-$Runtime"
$Stage = Join-Path $DistRoot $PackageName
$ZipPath = Join-Path $DistRoot "$PackageName.zip"

Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

Copy-Item -LiteralPath $BuildExe -Destination $Stage -Force

# One-click installer shipped inside the package: unzip anywhere, run install.cmd.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\install.ps1") -Destination $Stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\install.cmd") -Destination $Stage -Force

$readme = @'
Codex Desktop Usage Switcher for Windows

Install (recommended):
  1. Unzip this package anywhere.
  2. Double-click install.cmd.
     It copies the app to %LOCALAPPDATA%\CodexUsageSwitcher, adds Start Menu
     and auto-start shortcuts, and launches the tray app.
     Options: install.cmd -NoStartup -NoLaunch

Run without installing:
  CodexUsageSwitcher.Windows.exe

Using the app:
- The app is tray-icon-only by default. Left-click the tray icon for the usage popup
  (Codex + Claude 5-hour and weekly remaining %, reset countdown, per-profile quota,
  and the profile switcher). Right-click for the Open / Quit menu.
- The dashboard window shows 14-day model-stacked token bars, a weekday-by-hour
  heatmap, per-turn token stats, live 5h/weekly limit donuts for both providers, and
  an estimated-cost summary.
- Settings can add Codex profiles, save the current Codex login into a profile, launch
  the in-app Claude usage OAuth login, launch Claude Code login, run Doctor, and switch
  the UI language (English / Korean).
- An account switch swaps ~/.codex/auth.json from a saved profile under
  ~/.codex-switch/profiles with timestamped backups, and refuses to run while Codex
  Desktop or its app-server is still open.

Notes:
- Self-contained: needs neither the .NET runtime nor any other tooling on the target
  machine. Requires Windows 10 1809+ and the Microsoft Edge WebView2 runtime
  (preinstalled on current Windows 10/11).
- For Codex profile login you also need Codex Desktop and the "codex" CLI; set
  CODEX_CLI_PATH if codex is not on PATH. For Claude Code login you need the "claude" CLI.
- Set CODEX_DESKTOP_APP_PATH if Codex.exe is installed outside common locations.
- Keep auth.json, credentials.json, .codex-switch, .codex, and profile folders out of this package.
'@
Set-Content -LiteralPath (Join-Path $Stage "README.windows.txt") -Value $readme -Encoding UTF8

# 3. Privacy/path scan: fail the package if a local user path leaked into the stage.
#    With $ErrorActionPreference=Stop a missing rg or a Write-Error would abort before the
#    diagnostics print, so guard up front and report with Write-Host.
$escapedRoot = [regex]::Escape($Root)
$escapedUser = [regex]::Escape($env:USERNAME)
$privacyPattern = "C:\\Users\\|$escapedUser|$escapedRoot"
$rg = Get-Command rg -ErrorAction SilentlyContinue
if ($rg) {
    $scanOutput = & $rg.Source -a -n $privacyPattern $Stage 2>$null
    if ($LASTEXITCODE -eq 0) {
        $scanOutput | Select-Object -First 40 | ForEach-Object { Write-Host "LEAK: $_" }
        throw "Privacy/path scan failed for package stage: $Stage"
    }
    if ($LASTEXITCODE -gt 1) {
        throw "Privacy/path scan could not run"
    }
} else {
    Write-Host "ripgrep not found; falling back to Select-String (slower)."
    $leaks = Get-ChildItem -LiteralPath $Stage -Recurse -File |
        Select-String -Pattern $privacyPattern -AllMatches -ErrorAction SilentlyContinue |
        Select-Object -First 40
    if ($leaks) {
        $leaks | ForEach-Object { Write-Host "LEAK: $($_.Path):$($_.LineNumber)" }
        throw "Privacy/path scan failed for package stage: $Stage"
    }
}

# 4. Zip the staged folder.
Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $ZipPath -Force

Write-Host ""
Write-Host "Packaged: $ZipPath"
Write-Host "Staging:  $Stage"
