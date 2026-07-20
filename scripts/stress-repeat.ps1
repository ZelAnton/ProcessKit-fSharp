#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Repeats a filtered `dotnet test` run N times, to reproduce or verify a load-sensitive
    test flake locally.

.DESCRIPTION
    A CI-only flake (timing/handle-count assertion overrun under a loaded runner, or a
    resource-exhaustion test landing on the wrong fd operation) usually cannot be reproduced
    by a single local `dotnet test` run — the local machine is not under the same load. This
    script instead repeats a filtered test selection N times in a row and reports how many
    runs failed, which is what actually reproduces (before a fix) or disproves (after a fix)
    that class of flake on demand.

    Defaults to the three fixtures T-179 stabilized (`ReadinessTests`, `WindowsOverlappedPipeTests`,
    `StreamingTests`) at the FIXTURE level rather than exact test names: several of the individual
    test names contain characters (parentheses, `/`) the NUnit3TestAdapter filter-expression parser
    treats specially, and quoting them portably across pwsh/bash defeats the purpose of a quick,
    reliable repeat script. Narrow with -Filter for a tighter/faster loop once you know which single
    test you are chasing.

.PARAMETER Filter
    dotnet test --filter expression. Defaults to the three fixtures this task stabilized.

.PARAMETER Count
    How many times to repeat the filtered run. Default 30.

.PARAMETER Configuration
    MSBuild configuration. Debug or Release. Defaults to Debug.

.PARAMETER Framework
    TFM to test. Defaults to net10.0 — this is also the only local workaround for hosts that
    carry just the net10.0 shared runtime (K-015); CI itself covers both net8.0 and net10.0.

.EXAMPLE
    pwsh ./scripts/stress-repeat.ps1

.EXAMPLE
    pwsh ./scripts/stress-repeat.ps1 -Filter "FullyQualifiedName~StreamingTests" -Count 100

.EXAMPLE
    pwsh ./scripts/stress-repeat.ps1 -Configuration Release -Count 50
#>
[CmdletBinding()]
param(
    [string]$Filter = 'FullyQualifiedName~ReadinessTests|FullyQualifiedName~WindowsOverlappedPipeTests|FullyQualifiedName~StreamingTests',
    [int]$Count = 30,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Framework = 'net10.0'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Solution = Join-Path $RepoRoot 'ProcessKit.slnx'
# Target the F# test project directly (not the whole solution): the three fixtures this task
# stabilized are all F# (`tests/ProcessKit.Tests`), and filtering the solution as a whole prints a
# spurious "No test matches" line for `ProcessKit.CSharp.Tests` on every single repeat — noise this
# script's own pass/fail count should not have to wade through.
$TestProject = Join-Path $RepoRoot 'tests/ProcessKit.Tests/ProcessKit.Tests.fsproj'

Write-Host "==> Repeating dotnet test $Count time(s)" -ForegroundColor DarkGray
Write-Host "    Filter:        $Filter" -ForegroundColor DarkGray
Write-Host "    Configuration: $Configuration" -ForegroundColor DarkGray
Write-Host "    Framework:     $Framework" -ForegroundColor DarkGray

# Build the whole solution once up front (build ordering across projects comes from the .slnx, per
# AGENTS.md/CLAUDE.md); every repeat run below passes --no-build so the loop measures test
# execution/flakiness only, not rebuild time N times over.
& dotnet build $Solution -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Initial build failed; aborting before the repeat loop." -ForegroundColor Red
    exit $LASTEXITCODE
}

$failedRuns = 0

for ($i = 1; $i -le $Count; $i++) {
    Write-Host "--- run $i/$Count ---" -ForegroundColor DarkGray
    & dotnet test $TestProject --no-build -c $Configuration --framework $Framework --filter $Filter
    if ($LASTEXITCODE -ne 0) {
        $failedRuns++
        Write-Host "    run $i FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    }
}

$color = if ($failedRuns -eq 0) { 'Green' } else { 'Red' }
Write-Host "==> $failedRuns / $Count run(s) failed" -ForegroundColor $color

exit $(if ($failedRuns -eq 0) { 0 } else { 1 })
