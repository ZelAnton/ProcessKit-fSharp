#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the locally available pre-push verification gates and prints one summary.

.PARAMETER SkipLinux
    Skip the Docker/Rancher Desktop Linux test run.

.PARAMETER SkipSnippets
    Skip compilation of documentation snippets.

.PARAMETER SkipLinks
    Skip the offline lychee link check.

.PARAMETER LibFuzzer
    Optional path to libfuzzer-dotnet. When supplied, both fuzz smoke targets run.
#>
[CmdletBinding()]
param(
    [switch] $SkipLinux,
    [switch] $SkipSnippets,
    [switch] $SkipLinks,
    [string] $LibFuzzer,
    [ValidateRange(1, 300)]
    [int] $FuzzDurationSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [string] $Name,
        [ValidateSet('PASS', 'FAIL', 'SKIP')]
        [string] $Status,
        [string] $Detail = ''
    )

    $results.Add([pscustomobject]@{ Stage = $Name; Status = $Status; Detail = $Detail })
}

function Invoke-External {
    param(
        [string] $Program,
        [string[]] $Arguments
    )

    & $Program @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Program failed with exit code $LASTEXITCODE"
    }
}

function Invoke-Stage {
    param(
        [string] $Name,
        [scriptblock] $Body
    )

    Write-Host "`n==> $Name" -ForegroundColor Cyan

    try {
        & $Body
        Add-Result $Name 'PASS'
    }
    catch {
        Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
        Add-Result $Name 'FAIL' $_.Exception.Message
    }
}

function Skip-Stage {
    param([string] $Name, [string] $Reason)

    Write-Host "`n==> $Name (skipped: $Reason)" -ForegroundColor DarkYellow
    Add-Result $Name 'SKIP' $Reason
}

Push-Location $repoRoot
try {
    Invoke-Stage 'Restore local tools' {
        Invoke-External 'dotnet' @('tool', 'restore')
    }

    Invoke-Stage 'Fantomas formatting' {
        Invoke-External 'dotnet' @('fantomas', '--check', 'src', 'tests', 'samples')
    }

    if (Get-Command typos -ErrorAction SilentlyContinue) {
        Invoke-Stage 'Spelling' {
            Invoke-External 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-spelling.ps1'))
        }
    }
    else {
        Skip-Stage 'Spelling' 'typos is not installed'
    }

    Invoke-Stage 'Solution build' {
        Invoke-External 'dotnet' @('build', 'ProcessKit.slnx', '--configuration', 'Release')
    }

    Invoke-Stage 'Sample build' {
        Invoke-External 'dotnet' @('build', 'samples/Samples.slnx', '--configuration', 'Release')
    }

    Invoke-Stage 'Test suite' {
        Invoke-External 'dotnet' @(
            'test', 'ProcessKit.slnx',
            '--no-build', '--configuration', 'Release',
            '--filter', 'Category!=Stress&Category!=Interleaving'
        )
    }

    if ($SkipSnippets) {
        Skip-Stage 'Documentation snippets' '-SkipSnippets'
    }
    else {
        Invoke-Stage 'Documentation snippets' {
            Invoke-External 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-doc-snippets.ps1'))
        }
    }

    $pinnedMdBook = Join-Path $repoRoot '.work/tools/mdbook-0.4.40/mdbook.exe'
    $mdBook = $null

    if ($env:PROCESSKIT_MDBOOK) {
        $mdBook = $env:PROCESSKIT_MDBOOK
    }
    elseif (Test-Path -LiteralPath $pinnedMdBook) {
        $mdBook = $pinnedMdBook
    }
    elseif (Get-Command mdbook -ErrorAction SilentlyContinue) {
        $version = (& mdbook --version 2>$null) -join ' '

        if ($version -match '\b0\.4\.40\b') {
            $mdBook = 'mdbook'
        }
    }

    $python =
        if (Get-Command python3 -ErrorAction SilentlyContinue) { 'python3' }
        elseif (Get-Command python -ErrorAction SilentlyContinue) { 'python' }
        else { $null }

    if (-not $mdBook) {
        Skip-Stage 'Rendered sidebar' 'mdBook 0.4.40 is unavailable; set PROCESSKIT_MDBOOK to the pinned binary'
    }
    elseif (-not $python) {
        Skip-Stage 'Rendered sidebar' 'Python is not installed'
    }
    else {
        Invoke-Stage 'Rendered sidebar' {
            Invoke-External $mdBook @('build')
            Invoke-External $python @('scripts/check-sidebar-nav.py', 'book/index.html')
        }
    }

    if ($SkipLinks) {
        Skip-Stage 'Offline links' '-SkipLinks'
    }
    elseif (-not (Get-Command lychee -ErrorAction SilentlyContinue)) {
        Skip-Stage 'Offline links' 'lychee is not installed'
    }
    else {
        Invoke-Stage 'Offline links' {
            Invoke-External 'lychee' @(
                '--offline', '--no-progress',
                './docs/**/*.md', './*.md', './samples/**/README.md'
            )
        }
    }

    if (-not $LibFuzzer) {
        Skip-Stage 'Fuzz smoke' 'pass -LibFuzzer to enable both fuzz targets'
    }
    else {
        Invoke-Stage 'Fuzz smoke' {
            Invoke-External 'pwsh' @(
                '-NoProfile', '-File', (Join-Path $PSScriptRoot 'fuzz.ps1'),
                '-Target', 'Pump',
                '-LibFuzzer', $LibFuzzer,
                '-DurationSeconds', $FuzzDurationSeconds,
                '-SkipBuild'
            )

            Invoke-External 'pwsh' @(
                '-NoProfile', '-File', (Join-Path $PSScriptRoot 'fuzz.ps1'),
                '-Target', 'Cassette',
                '-LibFuzzer', $LibFuzzer,
                '-DurationSeconds', $FuzzDurationSeconds,
                '-SkipBuild'
            )
        }
    }

    if ($SkipLinux) {
        Skip-Stage 'Linux tests' '-SkipLinux'
    }
    elseif (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Skip-Stage 'Linux tests' 'docker is not installed'
    }
    else {
        Invoke-Stage 'Linux tests' {
            Invoke-External 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-linux.ps1'))
        }
    }
}
finally {
    Pop-Location
}

Write-Host "`nVerification summary" -ForegroundColor Cyan
$results | Format-Table Stage, Status, Detail -AutoSize

$failed = @($results | Where-Object Status -eq 'FAIL')

if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) verification stage(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All executed verification stages passed.' -ForegroundColor Green
exit 0
