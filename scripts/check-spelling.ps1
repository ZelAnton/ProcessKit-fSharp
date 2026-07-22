#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks repository spelling with typos.

.DESCRIPTION
    Runs the same checked-in typos.toml configuration used by CI. Install
    typos-cli separately; this helper performs no network access and returns
    typos' exit code so it can be used before pushing.

.EXAMPLE
    pwsh ./scripts/check-spelling.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if (-not (Get-Command typos -ErrorAction SilentlyContinue)) {
    Write-Host "typos is not on PATH. Install typos-cli, then re-run this check." -ForegroundColor Red
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = Join-Path $repoRoot 'typos.toml'

Write-Host "==> Checking spelling with typos" -ForegroundColor Cyan
Push-Location $repoRoot
try {
    & typos --config $config --force-exclude --format brief
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -eq 0) {
    Write-Host "Spelling check passed." -ForegroundColor Green
} else {
    Write-Host "Spelling findings remain; correct them before pushing." -ForegroundColor Red
}

exit $exitCode
