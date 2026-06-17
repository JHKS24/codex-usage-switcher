#!/usr/bin/env pwsh
# Builds the Windows app as a single, self-contained win-x64 executable (no .NET runtime or Python
# required on the target machine). Output: build/win-x64/CodexDesktopUsageSwitcher.Windows.exe
#
# Usage:  pwsh ./build-windows.ps1            # build the single-file exe
#         pwsh ./build-windows.ps1 -Test      # run the test suite first
[CmdletBinding()]
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'windows/CodexDesktopUsageSwitcher.Windows/CodexDesktopUsageSwitcher.Windows.csproj'
$tests = Join-Path $root 'windows/CodexDesktopUsageSwitcher.Windows.Tests/CodexDesktopUsageSwitcher.Windows.Tests.csproj'
$output = Join-Path $root 'build/win-x64'

if ($Test) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test $tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host 'Publishing single-file self-contained win-x64...' -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $output 'CodexDesktopUsageSwitcher.Windows.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Built $exe ($sizeMb MB)" -ForegroundColor Green
