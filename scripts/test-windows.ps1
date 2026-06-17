$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Project = Join-Path $Root "windows\CodexDesktopUsageSwitcher.Windows\CodexDesktopUsageSwitcher.Windows.csproj"
$Switcher = Join-Path $Root "switcher\codex-desktop-switch"
$TempHome = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-switcher-test-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $TempHome | Out-Null

# Build with the real profile (so the NuGet cache is reused) BEFORE redirecting
# HOME; only the python smoke calls need the isolated home.
dotnet build $Project -c Release

$oldHome = $env:HOME
$oldUserProfile = $env:USERPROFILE
$oldCodeXPath = $env:CODEX_DESKTOP_SWITCH_PATH

try {
    $env:HOME = $TempHome
    $env:USERPROFILE = $TempHome
    $env:CODEX_DESKTOP_SWITCH_PATH = $Switcher

    $doctor = python $Switcher doctor --json | ConvertFrom-Json
    if (-not $doctor.ok) { throw "doctor failed" }
    if ($doctor.platform -notlike "win32*") { throw "expected Windows platform, got $($doctor.platform)" }

    $list = python $Switcher list --json | ConvertFrom-Json
    if (-not $list.ok) { throw "list failed" }
    if ($list.profiles.Count -ne 0) { throw "fresh home should have no profiles" }

    $dryRun = python $Switcher use demo --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "use demo --json exited $LASTEXITCODE" }
    if (-not $dryRun.ok) { throw "use demo --json reported not ok" }
    if ($dryRun.command -ne "use") { throw "use demo --json returned wrong command payload" }
    if (-not $dryRun.dry_run) { throw "use demo --json should stay dry-run" }

    $snapshot = python $Switcher snapshot --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "snapshot --json exited $LASTEXITCODE" }
    if (-not $snapshot.ok) { throw "snapshot failed" }
    if ($null -eq $snapshot.current) { throw "snapshot missing current section" }
    if ($null -eq $snapshot.claude_usage) { throw "snapshot missing claude_usage section" }
    if ($snapshot.claude_usage.authenticated) { throw "fresh home should not be Claude-authenticated" }

    Write-Host "Windows smoke tests passed"
}
finally {
    $env:HOME = $oldHome
    $env:USERPROFILE = $oldUserProfile
    $env:CODEX_DESKTOP_SWITCH_PATH = $oldCodeXPath
    Remove-Item -Recurse -Force -Path $TempHome -ErrorAction SilentlyContinue
}
