#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks this machine can build and test an F# (.NET) project before you
    initialize the template.

.DESCRIPTION
    Verifies the .NET SDK is installed and new enough (the major band pinned in
    global.json). Prints "Environment ready" and exits 0 on success; if a required
    tool is missing it prints per-OS install commands and exits 1 — install what it
    names, then re-run. (Fantomas is a local tool restored by `dotnet tool restore`,
    not a separate environment prerequisite, so it is not checked here.)

    Run it first, before scripts/init.ps1:

        pwsh ./scripts/check-env.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$problems = @()

Write-Host "==> Checking environment for F# (.NET) development" -ForegroundColor Cyan

# Required .NET major version — read from global.json when present, else default.
$requiredMajor = 10
$globalJson = Join-Path (Join-Path $PSScriptRoot '..') 'global.json'
if (Test-Path $globalJson) {
    try {
        $v = (Get-Content -Raw $globalJson | ConvertFrom-Json).sdk.version
        if ($v -match '^(\d+)\.') { $requiredMajor = [int]$Matches[1] }
    } catch {
        # global.json unreadable/edited - fall back to the default major above.
    }
}

# Required: the .NET SDK (it bundles the F# compiler and `dotnet test`).
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $problems += "the .NET SDK ('dotnet' is not on PATH)"
} else {
    $haveMajor = $false
    foreach ($line in (& dotnet --list-sdks)) {
        if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge $requiredMajor) { $haveMajor = $true }
    }
    if ($haveMajor) {
        Write-Host "    .NET SDK $requiredMajor+ found" -ForegroundColor DarkGray
    } else {
        $problems += "a .NET $requiredMajor SDK (dotnet found, but no installed SDK >= $requiredMajor)"
    }
}

# Soft: git drives the init defaults (author/email) and the VCS workflow.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "    note: git is not on PATH — init falls back to placeholder author/email." -ForegroundColor Yellow
}

if ($problems.Count -eq 0) {
    Write-Host ""
    Write-Host "Environment ready. Next: pwsh ./scripts/init.ps1 -ProjectName ..." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Environment NOT ready. Missing:" -ForegroundColor Red
foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
Write-Host ""
Write-Host "Install the .NET $requiredMajor SDK, then re-run this check:" -ForegroundColor Yellow
Write-Host "  Windows : winget install Microsoft.DotNet.SDK.$requiredMajor"
Write-Host "  macOS   : brew install --cask dotnet-sdk"
Write-Host "  Linux   : see https://learn.microsoft.com/dotnet/core/install/linux"
exit 1
