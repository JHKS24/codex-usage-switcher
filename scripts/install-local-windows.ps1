#!/usr/bin/env pwsh
# Builds the single self-contained Windows exe from source and installs it locally for the
# current user: copies it to %LOCALAPPDATA%\CodexDesktopUsageSwitcher, refreshes the Start Menu
# shortcut, and offers to launch the tray app. Intended for developers building from a clone.
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher",
    # Kept as a harmless no-op alias: the build is always single-file self-contained.
    [switch]$SelfContained,
    # Skip the "launch now?" prompt and do not start the app afterwards.
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Refuse to install outside LOCALAPPDATA so a stray override cannot wipe an unrelated folder.
$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$localAppData = $env:LOCALAPPDATA
if (-not $resolvedInstallDir.StartsWith($localAppData, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallDir must stay under LOCALAPPDATA unless you edit the script intentionally: $InstallDir"
}

# 1. Build the single self-contained exe via the repo-root build entry point.
Write-Host "=== Building single self-contained exe ==="
& (Join-Path $Root "build-windows.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-windows.ps1 failed (exit $LASTEXITCODE)." }

$BuildExe = Join-Path $Root "build\win-x64\CodexDesktopUsageSwitcher.Windows.exe"
if (-not (Test-Path -LiteralPath $BuildExe)) {
    throw "Build finished but $BuildExe is missing; the publish may have failed."
}

# 2. Stop a running instance and wait for it to fully exit. Stop-Process returns before
#    teardown completes; without the wait the copy below can race a still-mapped exe.
$running = Get-Process -Name "CodexDesktopUsageSwitcher.Windows" -ErrorAction SilentlyContinue
$wasRunning = [bool]$running
if ($running) {
    Write-Host "Stopping running instance..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 3. Copy the single exe into the install dir (with retry, in case a handle lingers).
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$exePath = Join-Path $InstallDir "CodexDesktopUsageSwitcher.Windows.exe"
$attempt = 0
while ($true) {
    $attempt++
    try {
        Copy-Item -LiteralPath $BuildExe -Destination $exePath -Force
        break
    } catch {
        if ($attempt -ge 3) { throw }
        Start-Sleep -Seconds 1
    }
}

# 4. Create / refresh the Start Menu shortcut.
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenu "Codex Desktop Usage Switcher.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

Write-Host ""
Write-Host "Installed:  $exePath"
Write-Host "Start menu: $shortcutPath"

# 5. Launch. If it was already running, restart it; otherwise offer to launch.
$shouldLaunch = $false
if (-not $NoLaunch) {
    if ($wasRunning) {
        $shouldLaunch = $true
    } else {
        $answer = Read-Host "Launch Codex Desktop Usage Switcher now? [Y/n]"
        if ($answer -eq "" -or $answer -match "^[Yy]") { $shouldLaunch = $true }
    }
}

if ($shouldLaunch) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
    Write-Host "Launched. The tray icon should appear shortly."
} else {
    Write-Host "Run it any time from: $exePath"
}
