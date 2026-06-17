#!/usr/bin/env pwsh
# Builds both Windows projects with warnings treated as errors, then runs the unit tests.
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$App = Join-Path $Root "windows\CodexDesktopUsageSwitcher.Windows\CodexDesktopUsageSwitcher.Windows.csproj"
$Tests = Join-Path $Root "windows\CodexDesktopUsageSwitcher.Windows.Tests\CodexDesktopUsageSwitcher.Windows.Tests.csproj"

foreach ($project in @($App, $Tests)) {
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Project not found: $project"
    }
}

# 1. Build both projects with warnings as errors.
foreach ($project in @($App, $Tests)) {
    Write-Host "=== Building $([System.IO.Path]::GetFileNameWithoutExtension($project)) ($Configuration) ==="
    dotnet build $project -c $Configuration -warnaserror --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $project (exit $LASTEXITCODE)." }
}

# 2. Run the test suite.
Write-Host "=== Running tests ($Configuration) ==="
dotnet test $Tests -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Build (warnings-as-errors) and tests passed."
