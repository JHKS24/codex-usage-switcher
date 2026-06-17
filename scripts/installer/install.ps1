#!/usr/bin/env pwsh
# End-user installer: run after unzipping a release. Copies the self-contained
# CodexDesktopUsageSwitcher.Windows.exe (sitting next to this script) into
# %LOCALAPPDATA%\CodexDesktopUsageSwitcher, creates a Start Menu shortcut (and an
# auto-start shortcut unless -NoStartup), and launches the tray app (unless -NoLaunch).
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher",
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

Write-Host "=== Codex Desktop Usage Switcher installer ==="
Write-Host "Install dir: $InstallDir"

# 0. The exe must be next to this script (i.e. the unzipped release folder).
$source = $PSScriptRoot
$sourceExe = Join-Path $source "CodexDesktopUsageSwitcher.Windows.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "CodexDesktopUsageSwitcher.Windows.exe was not found next to this installer. Unzip the full release and run install.cmd from inside that folder."
}

# 1. Stop a running instance and wait for it to actually exit before we overwrite it.
$running = Get-Process -Name "CodexDesktopUsageSwitcher.Windows" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running instance..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 2. Create the install dir, guarding against a drive root, then copy the exe in
#    (with retry, in case a file handle lingers briefly after process exit).
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$installRoot = [System.IO.Path]::GetPathRoot($resolvedInstallDir)
if ($resolvedInstallDir.TrimEnd("\") -eq $installRoot.TrimEnd("\")) {
    throw "Refusing to install into a drive root: $InstallDir"
}

$exePath = Join-Path $InstallDir "CodexDesktopUsageSwitcher.Windows.exe"
$attempt = 0
while ($true) {
    $attempt++
    try {
        Copy-Item -LiteralPath $sourceExe -Destination $exePath -Force
        break
    } catch {
        if ($attempt -ge 3) { throw }
        Start-Sleep -Seconds 1
    }
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Copy finished but $exePath is missing; the package may be incomplete."
}

# 3. Start Menu shortcut.
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenu "Codex Desktop Usage Switcher.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

# 4. Auto-start at login (tray app), unless opted out.
$startupPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\Codex Desktop Usage Switcher.lnk"
if ($NoStartup) {
    Remove-Item -LiteralPath $startupPath -Force -ErrorAction SilentlyContinue
} else {
    $startupShortcut = $shell.CreateShortcut($startupPath)
    $startupShortcut.TargetPath = $exePath
    $startupShortcut.WorkingDirectory = $InstallDir
    $startupShortcut.IconLocation = "$exePath,0"
    $startupShortcut.Save()
}

# 5. Launch.
if (-not $NoLaunch) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
}

Write-Host ""
Write-Host "Installed:   $exePath"
Write-Host "Start menu:  $shortcutPath"
if (-not $NoStartup) { Write-Host "Auto-start:  $startupPath" }
Write-Host "Done. The tray icon should appear shortly."
