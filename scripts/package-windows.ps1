param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("SelfContained")) {
    $SelfContained = $true
}

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

& (Join-Path $PSScriptRoot "build-windows.ps1") -Configuration $Configuration -Runtime $Runtime -SelfContained:$SelfContained

$PublishDir = Join-Path $Root "build\windows\$Runtime"
$DistRoot = Join-Path $Root "dist"
$PackageName = "CodexDesktopUsageSwitcher-$Runtime"
$Stage = Join-Path $DistRoot $PackageName
$ZipPath = Join-Path $DistRoot "$PackageName.zip"

Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

Get-ChildItem -LiteralPath $PublishDir -Force | Where-Object {
    $_.Name -notlike "*.pdb" -and
    $_.Name -notlike "*.xml" -and
    $_.Name -ne "createdump.exe"
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $Stage -Recurse -Force
}

# One-click installer shipped inside the package: unzip anywhere, run install.cmd.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\install.ps1") -Destination $Stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\install.cmd") -Destination $Stage -Force

$readme = @'
Codex Desktop Usage Switcher for Windows

Install (recommended):
  1. Unzip this package anywhere.
  2. Double-click install.cmd.
     It checks Python 3 (offers winget install), copies the app to
     %LOCALAPPDATA%\CodexDesktopUsageSwitcher, adds Start Menu and
     auto-start shortcuts, and launches the tray app.
     Options: install.cmd -NoStartup -NoLaunch

Run without installing:
  CodexDesktopUsageSwitcher.Windows.exe

CLI:
  codex-desktop-switch.cmd list
  codex-desktop-switch.cmd login main
  codex-desktop-switch.cmd use main
  codex-desktop-switch.cmd use main --apply

Notes:
- `use main` is a dry run. `use main --apply` actually changes the active
  Codex Desktop login and refuses to run while Codex Desktop/app-server is
  still open. Prefer switching from the tray popup unless you intentionally
  quit Codex first.
- The tray app is tray-icon-only by default. Left-click or right-click the tray
  icon to open the usage popup with the active Codex profile and 5H / Week
  remaining quota. Refresh updates the open popup in place.
- Settings can toggle up to six taskbar notification-area number icons:
  Codex 5H / Week, CodexSub 5H / Week, and Claude 5H / Week. Claude values
  are normalized to remaining quota as 100 - current utilization.
- Settings can add Codex profiles, save the current Codex login, launch Claude
  usage login, launch Claude Code login, and run Doctor. Claude Code is exposed
  as a separate login action only.
- Requires Python 3 on PATH, or the Windows py launcher.
- Keep auth.json, credentials.json, .codex-switch, .codex, and profile folders out of this package.
- Set CODEX_CLI_PATH if codex is not on PATH.
- Set CODEX_DESKTOP_APP_PATH if Codex.exe is installed outside common locations.
'@
Set-Content -LiteralPath (Join-Path $Stage "README.windows.txt") -Value $readme -Encoding UTF8

# With $ErrorActionPreference=Stop, a missing rg or a Write-Error would abort
# before the intended diagnostics print; guard up front and report with Write-Host.
$escapedRoot = [regex]::Escape($Root.Path)
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

Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $ZipPath -Force

Write-Host "Packaged: $ZipPath"
Write-Host "Staging: $Stage"
