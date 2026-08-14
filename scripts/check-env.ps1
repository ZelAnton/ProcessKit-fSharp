#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks this machine can build and test an F# (.NET) project before you
    initialize the template.

.DESCRIPTION
    Verifies the .NET SDK is installed and new enough (the major band pinned in
    global.json), and that the .NET 8 runtime required by the full multi-target
    test run is installed. Prints "Environment ready" and exits 0 on success; if
    a required tool is missing it prints per-OS install commands and exits 1 —
    install what it names, then re-run. (Fantomas is a local tool restored by
    `dotnet tool restore`, not a separate environment prerequisite, so it is not
    checked here.)

    Run it first, before scripts/init.ps1:

        pwsh ./scripts/check-env.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$problems = @()
. (Join-Path $PSScriptRoot 'jj-noninteractive-config.ps1')

Write-Host "==> Checking environment for F# (.NET) development" -ForegroundColor Cyan

# Required .NET major version — read from global.json when present, else default.
$requiredMajor = 10
$requiredRuntimeMajor = 8
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

    $haveRuntime = $false
    foreach ($line in (& dotnet --list-runtimes)) {
        if ($line -match '^Microsoft\.NETCore\.App\s+(\d+)\.' -and [int]$Matches[1] -eq $requiredRuntimeMajor) { $haveRuntime = $true }
    }
    if ($haveRuntime) {
        Write-Host "    .NET $requiredRuntimeMajor runtime found" -ForegroundColor DarkGray
    } else {
        $problems += "the .NET $requiredRuntimeMajor runtime (required for the full multi-target test run)"
    }
}

# Soft: git drives the init defaults (author/email) and the VCS workflow.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "    note: git is not on PATH — init falls back to placeholder author/email." -ForegroundColor Yellow
}

# Soft: an interactive jj editor can leave unattended runs waiting for input.
$jjCommand = Get-Command jj -ErrorAction SilentlyContinue
if ($null -ne $jjCommand) {
    $checkoutPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $repositoryRoot = $null
    $actualEditor = $null

    try {
        $rootOutput = @(& $jjCommand.Source --repository $checkoutPath --ignore-working-copy root 2>$null)
        if ($LASTEXITCODE -eq 0) {
            $repositoryRoot = ($rootOutput -join [Environment]::NewLine).Trim()
            $editorOutput = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config get ui.editor 2>$null)
            if ($LASTEXITCODE -eq 0) {
                $actualEditor = ($editorOutput -join [Environment]::NewLine).Trim()
            }
        }
    } catch {
        # Advisory only - an unreadable jj config must not make the environment check fail.
    }

    if ($null -ne $repositoryRoot) {
        if (-not (Test-JjNonInteractiveEditorConfig -EditorValue $actualEditor)) {
            $actualDescription = if ([string]::IsNullOrWhiteSpace($actualEditor)) { 'unavailable' } else { $actualEditor }
            Write-Host "    note: jj's ui.editor is not the repository's non-interactive command ($actualDescription). This can block automation. Run 'pwsh ./scripts/setup-jj-noninteractive.ps1' to configure non-interactive mode." -ForegroundColor Yellow
        }
    }
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
if ($problems -contains "the .NET $requiredRuntimeMajor runtime (required for the full multi-target test run)") {
    Write-Host ""
    Write-Host "Install the .NET $requiredRuntimeMajor runtime for the full test run:" -ForegroundColor Yellow
    Write-Host "  Windows : winget install Microsoft.DotNet.Runtime.$requiredRuntimeMajor"
    Write-Host "  macOS   : brew install dotnet"
    Write-Host "  Linux   : see https://learn.microsoft.com/dotnet/core/install/linux"
    Write-Host "Alternative: run only the .NET $requiredMajor test leg with:"
    Write-Host "  dotnet test --framework net$requiredMajor.0"
}
exit 1
