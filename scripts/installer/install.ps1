param(
    [string]$InstallDir = "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher",
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

Write-Host "=== Codex Desktop Usage Switcher installer ==="
Write-Host "Install dir: $InstallDir"

# 1. Python is required by the switcher backend. Probe by EXECUTION, not lookup:
#    on fresh Windows the WindowsApps python.exe is a Store-redirect stub that
#    Get-Command happily resolves but that cannot run anything.
function Test-WorkingPython {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        & $python.Source -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" *> $null
        if ($LASTEXITCODE -eq 0) { return $true }
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        & $py.Source -3 -c "import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)" *> $null
        if ($LASTEXITCODE -eq 0) { return $true }
    }

    return $false
}

if (-not (Test-WorkingPython)) {
    Write-Host ""
    Write-Host "Python 3 was not found (or only the Microsoft Store stub is on PATH)."
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "Python 3 is required. Install it from https://www.python.org/downloads/ and run install.cmd again."
    }
    $answer = Read-Host "Install Python 3 now via winget? [Y/n]"
    if ($answer -ne "" -and $answer -notmatch "^[Yy]") {
        throw "Python 3 is required. Install it from https://www.python.org/downloads/ and run install.cmd again."
    }
    winget install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install Python (exit $LASTEXITCODE). Install it manually and run install.cmd again."
    }
    # The new PATH lives in the registry; pull it into this process so the app
    # we launch below can actually find python.
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
        [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not (Test-WorkingPython)) {
        throw "Python was installed but is still not runnable from PATH. Re-open install.cmd once and it should pass."
    }
}

# 2. Stop a running instance and wait for it to actually exit.
$running = Get-Process -Name "CodexDesktopUsageSwitcher.Windows" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running instance..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 3. Copy the package next to this script into the install dir (with retry,
#    in case a file handle lingers briefly after process exit).
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$installRoot = [System.IO.Path]::GetPathRoot($resolvedInstallDir)
if ($resolvedInstallDir.TrimEnd("\") -eq $installRoot.TrimEnd("\")) {
    throw "Refusing to install into a drive root: $InstallDir"
}
$source = $PSScriptRoot
$excluded = @("install.cmd", "install.ps1")
$attempt = 0
while ($true) {
    $attempt++
    try {
        Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $source -Force | Where-Object { $excluded -notcontains $_.Name } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Recurse -Force
        }
        break
    } catch {
        if ($attempt -ge 3) { throw }
        Start-Sleep -Seconds 1
    }
}

$exePath = Join-Path $InstallDir "CodexDesktopUsageSwitcher.Windows.exe"
if (-not (Test-Path $exePath)) {
    throw "Copy finished but $exePath is missing; the package may be incomplete."
}

# 4. Start Menu shortcut.
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenu "Codex Desktop Usage Switcher.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

# 5. Auto-start at login (tray app), unless opted out.
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

# 6. CLI helper in ~\bin (optional convenience; PATH not modified).
#    Use %LOCALAPPDATA% expansion when possible: the file is written as ASCII,
#    and a literal path with a non-ASCII username would be mangled to '?'.
$binDir = Join-Path $HOME "bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$installedCli = Join-Path $InstallDir "codex-desktop-switch.py"
$cliTarget = $installedCli
$encoding = "Default"
if ($InstallDir.StartsWith($env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)) {
    $cliTarget = "%LOCALAPPDATA%" + $InstallDir.Substring($env:LOCALAPPDATA.Length) + "\codex-desktop-switch.py"
    $encoding = "ASCII"
}
$cliCmd = Join-Path $binDir "codex-desktop-switch.cmd"
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
Set-Content -LiteralPath $cliCmd -Value $cmd -Encoding $encoding

# 7. Launch.
if (-not $NoLaunch) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
}

Write-Host ""
Write-Host "Installed:   $InstallDir"
Write-Host "Start menu:  $shortcutPath"
if (-not $NoStartup) { Write-Host "Auto-start:  $startupPath" }
Write-Host "CLI helper:  $cliCmd"
Write-Host "Done. The tray icon should appear shortly."
