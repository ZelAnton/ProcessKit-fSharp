#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the mutation-testing tier over the critical modules named in mutation-baseline.json.

.DESCRIPTION
    Mutation testing asks a question coverage cannot: not "was this line executed" but "would the
    tests notice if it were wrong". For each mutant this script rewrites one CIL instruction in the
    built ProcessKit.dll, re-runs a small hermetic slice of the suite against the mutated assembly,
    and records whether the suite noticed.

    The engine is tests/ProcessKit.Mutation (Mono.Cecil), NOT an off-the-shelf mutation tester: no
    maintained .NET mutation tool can mutate F#. See CONTRIBUTING.md, "Mutation testing", for the
    evidence behind that choice and for the exclusion list.

    This script owns the loop, the schedule and the classification; the engine owns only "what can be
    mutated" and "produce that one mutant". It writes facts, never a verdict — the pass/fail decision
    lives in scripts/check-mutation-report.ps1, which merges the shards.

    Sharding and the time budget. The catalog is deterministically shuffled by the engine (seeded from
    the baseline file), then split by `index % ShardCount`. A shard evaluates its list until the time
    budget runs out and reports `budgetExhausted` when it stopped early, so a partial run is visibly
    partial rather than quietly reported as a complete one. Because the catalog is shuffled, a
    truncated shard is still a representative sample instead of "the alphabetically first types".

    Retry controller. A Killed or Survived verdict comes from a deterministic, hermetic slice, so it
    is final and is never retried. Errored is infrastructure-shaped (the engine could not write the
    mutant, the run died before executing a single test) and is retried up to -MaxRetries.

    Timeout is deliberately NOT retried by default, which is a measured decision rather than a
    default: mutating a loop condition genuinely turns a bounded loop unbounded, so a timeout is the
    expected, correct verdict for a real class of mutants and not a symptom of a busy runner. The
    derived budget is already an order of magnitude above the measured baseline, so retrying every
    timeout only re-pays that budget - the first full local run spent about 1000 of its 1300 seconds
    re-running six mutants that were always going to time out. Pass -RetryTimeouts when a runner is
    genuinely suspect; a retried timeout then gets double the budget.

.PARAMETER Configuration
    Build configuration to mutate and test. Release by default: it is what CI builds.

.PARAMETER Framework
    Target framework of the test run. Defaults to the baseline file's `framework`. Mutating one TFM is
    deliberate — the scoped modules are pure boundary logic with no TFM-specific paths, so a second
    leg would double the wall clock for identical verdicts.

.PARAMETER ShardIndex
    Zero-based index of this shard.

.PARAMETER ShardCount
    Total number of shards. Defaults to the baseline file's `shardCount`.

.PARAMETER TimeBudgetSeconds
    Wall-clock budget for the mutant loop (the build and the baseline run are not charged to it).

.PARAMETER MutantTimeoutSeconds
    Hard per-mutant timeout. 0 derives it from the measured baseline run (x4 plus slack), which keeps
    it proportionate on a fast laptop and on a loaded CI runner alike.

.PARAMETER MaxRetries
    How often an infrastructure-shaped outcome may be re-run. 0 disables retries.

.PARAMETER RetryTimeouts
    Also retry timed-out mutants, with double the budget on the retry. Off by default — see the
    retry-controller note above.

.PARAMETER BaselinePath
    The committed scope + ratchet file. Defaults to mutation-baseline.json in the repository root.

.PARAMETER OutputDirectory
    Where the report is written. Defaults to artifacts/mutation/shard-<ShardIndex> (git-ignored).

.PARAMETER SkipBuild
    Reuse the existing build output instead of building first.

.NOTES
    Exit code contract:
      0 - the shard ran and wrote a report. This includes every "nothing could be measured" state
          (no mutants in scope, no tests executed, a red baseline): those are recorded in the report's
          `status` for the gate to act on, and are NOT failures of this script.
      2 - the shard could not run at all: missing baseline file, failed build, missing assembly. A
          configuration fault, not a statement about mutants.

.EXAMPLE
    pwsh ./scripts/mutate.ps1
    Runs the whole catalog locally (single shard) with the default 15 minute budget.

.EXAMPLE
    pwsh ./scripts/mutate.ps1 -ShardIndex 2 -ShardCount 4 -TimeBudgetSeconds 1200
    One shard of a four-way split, as the mutation workflow runs it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Framework,

    [ValidateRange(0, 63)]
    [int] $ShardIndex = 0,

    [ValidateRange(0, 64)]
    [int] $ShardCount = 0,

    [ValidateRange(1, 86400)]
    [int] $TimeBudgetSeconds = 900,

    [ValidateRange(0, 3600)]
    [int] $MutantTimeoutSeconds = 0,

    [ValidateRange(0, 5)]
    [int] $MaxRetries = 1,

    [switch] $RetryTimeouts,

    [string] $BaselinePath,

    [string] $OutputDirectory,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repoRoot 'mutation-baseline.json'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/mutation/shard-$ShardIndex"
}

function Get-Field {
    param($Object, [string] $Name)

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $null
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Write-Annotation {
    param(
        [ValidateSet('notice', 'warning', 'error')]
        [string] $Level,
        [string] $Message
    )

    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::${Level}::$Message"
    }
    else {
        Write-Host "$($Level.ToUpperInvariant()): $Message"
    }
}

function Invoke-Dotnet {
    param([string[]] $Arguments, [string] $FailureMessage)

    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        Write-Annotation error "$FailureMessage (exit code $LASTEXITCODE)"
        exit 2
    }
}

# Runs one `dotnet test` invocation under a hard wall-clock timeout.
#
# Start-Process writes the child's streams straight to files rather than into pipes this script would
# have to drain, so a mutant that floods stdout cannot deadlock the loop. On timeout the whole process
# TREE is killed: `dotnet test` starts a testhost (and a mutant may leave a child of its own behind),
# and killing only the launcher would leak a testhost holding a lock on the very assembly the next
# mutant has to overwrite.
function Invoke-TestRun {
    param(
        [string] $Project,
        [string] $Filter,
        [string] $ResultsDirectory,
        [string] $TrxName,
        [int] $TimeoutSeconds
    )

    # Every run gets its own directory, named after its trx. Reusing one directory would mean deleting
    # and re-writing the same files each time, and a test host killed on timeout can still be holding
    # them for a moment - the same handle race that the assembly copy has to retry around. Fresh names
    # avoid the race entirely rather than handling it, and the whole scratch tree is removed at the end.
    $runDirectory = Join-Path $ResultsDirectory ([System.IO.Path]::GetFileNameWithoutExtension($TrxName))
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

    $stdoutPath = Join-Path $runDirectory 'stdout.log'
    $stderrPath = Join-Path $runDirectory 'stderr.log'

    $arguments = @(
        'test', $Project,
        '--no-build', '--nologo',
        '--configuration', $Configuration,
        '--framework', $Framework,
        '--filter', $Filter,
        '--logger', "trx;LogFileName=$TrxName",
        '--results-directory', $runDirectory
    )

    $started = [Diagnostics.Stopwatch]::StartNew()

    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $exited = $process.WaitForExit($TimeoutSeconds * 1000)

    if (-not $exited) {
        try {
            $process.Kill($true)
            $process.WaitForExit(30000) | Out-Null
        }
        catch [System.InvalidOperationException] {
            # The process finished between the WaitForExit timeout and the Kill call. Nothing to
            # clean up, and the run is still classified as a timeout: it outlived its budget.
        }

        $started.Stop()

        return [pscustomobject]@{
            TimedOut = $true
            ExitCode = $null
            ExecutedTests = 0
            DurationSeconds = $started.Elapsed.TotalSeconds
        }
    }

    $started.Stop()

    # The authoritative "did anything actually run" signal. A mutated assembly that fails to load
    # produces a non-zero exit with ZERO executed tests, which must not be read as "the suite caught
    # the mutant" - it is the run failing, not an assertion firing.
    $executed = 0
    $trxPath = Join-Path $runDirectory $TrxName

    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        try {
            $trx = [xml](Get-Content -Raw -LiteralPath $trxPath)
            $counters = $trx.TestRun.ResultSummary.Counters

            if ($null -ne $counters) {
                $executed = [int] $counters.executed
            }
        }
        catch {
            # A truncated or unparsable trx (the run was killed mid-write, the schema moved) leaves
            # `executed` at 0, which routes the mutant to the Errored bucket - excluded from the score
            # and reported - instead of silently counting as a kill.
            $executed = 0
        }
    }

    return [pscustomobject]@{
        TimedOut = $false
        ExitCode = $process.ExitCode
        ExecutedTests = $executed
        DurationSeconds = $started.Elapsed.TotalSeconds
    }
}

# Swapping the assembly under test is the one operation in this loop that races the operating system.
# On Windows a test host that has just been killed (or has just exited) can still hold its handle on
# ProcessKit.dll for a short moment after the process is gone, and the copy then fails with a sharing
# violation. Observed, not hypothetical: an early full local run aborted at mutant 115 of 132 on
# exactly this, and — because the same copy is what restores the pristine assembly — left a MUTATED
# ProcessKit.dll behind in the working copy, which would silently poison every later
# `dotnet test --no-build` there.
#
# A bounded retry is the honest fix: the handle is released in milliseconds, so a few short waits turn
# a hard failure into a non-event, and a lock that genuinely persists still surfaces instead of being
# swallowed.
function Copy-AssemblyWithRetry {
    param([string] $Source, [string] $Destination, [int] $Attempts = 10)

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return $true
        }
        catch [System.IO.IOException] {
            # Sharing violation from a not-yet-released handle. Back off briefly and try again; on the
            # last attempt fall through and report the failure to the caller.
            if ($attempt -eq $Attempts) {
                Write-Annotation warning "Could not write $Destination after $Attempts attempts: $($_.Exception.Message)"
                return $false
            }

            Start-Sleep -Milliseconds (100 * $attempt)
        }
    }

    return $false
}

function Write-Report {
    param([hashtable] $Report)

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $reportPath = Join-Path $OutputDirectory 'mutation-report.json'
    $Report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Wrote $reportPath"
    return $reportPath
}

# --- Read the committed scope + ratchet file ----------------------------------------------------

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    Write-Annotation error "Mutation baseline file not found: $BaselinePath"
    exit 2
}

try {
    $baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
}
catch {
    Write-Annotation error "Mutation baseline file is not valid JSON ($BaselinePath): $($_.Exception.Message)"
    exit 2
}

if ([string]::IsNullOrWhiteSpace($Framework)) {
    $Framework = [string] (Get-Field $baseline 'framework')
}

if ($ShardCount -lt 1) {
    $configuredShards = Get-Field $baseline 'shardCount'
    $ShardCount = if ($null -eq $configuredShards) { 1 } else { [int] $configuredShards }
}

$testFilter = [string] (Get-Field $baseline 'testFilter')

if ([string]::IsNullOrWhiteSpace($Framework) -or [string]::IsNullOrWhiteSpace($testFilter)) {
    Write-Annotation error "Mutation baseline must set both 'framework' and 'testFilter': $BaselinePath"
    exit 2
}

if ($ShardIndex -ge $ShardCount) {
    Write-Annotation error "ShardIndex $ShardIndex is out of range for ShardCount $ShardCount"
    exit 2
}

$testProject = Join-Path $repoRoot 'tests/ProcessKit.Tests/ProcessKit.Tests.fsproj'
$engineProject = Join-Path $repoRoot 'tests/ProcessKit.Mutation/ProcessKit.Mutation.fsproj'
$testBinary = Join-Path $repoRoot "tests/ProcessKit.Tests/bin/$Configuration/$Framework"
$engineBinary = Join-Path $repoRoot "tests/ProcessKit.Mutation/bin/$Configuration/net10.0/ProcessKit.Mutation.dll"
$assemblyName = [string] (Get-Field (Get-Field $baseline 'scope') 'assembly')

if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    $assemblyName = 'ProcessKit.dll'
}

$assemblyUnderTest = Join-Path $testBinary $assemblyName

# --- Build ---------------------------------------------------------------------------------------

if (-not $SkipBuild) {
    Invoke-Dotnet @('build', (Join-Path $repoRoot 'ProcessKit.slnx'), '--configuration', $Configuration) `
        'Solution build failed'
    # The engine is deliberately outside ProcessKit.slnx (so the ordinary CI jobs never pay for it),
    # which means it has to be built on its own here.
    Invoke-Dotnet @('build', $engineProject, '--configuration', $Configuration) 'Mutation engine build failed'
}

foreach ($required in @($assemblyUnderTest, $engineBinary)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Write-Annotation error "Required build output is missing: $required (run without -SkipBuild)"
        exit 2
    }
}

$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("processkit-mutate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

$pristineAssembly = Join-Path $scratchRoot 'pristine.dll'
$mutantAssembly = Join-Path $scratchRoot 'mutant.dll'
$catalogPath = Join-Path $scratchRoot 'catalog.json'
$resultsDirectory = Join-Path $scratchRoot 'results'

$report = @{
    schema = 1
    status = 'ok'
    shardIndex = $ShardIndex
    shardCount = $ShardCount
    configuration = $Configuration
    framework = $Framework
    assembly = $assemblyName
    scopeFile = [System.IO.Path]::GetFileName($BaselinePath)
    testFilter = $testFilter
    catalogTotal = 0
    shardTotal = 0
    evaluated = 0
    budgetExhausted = $false
    baselineDurationSeconds = 0.0
    baselineExecutedTests = 0
    mutantTimeoutSeconds = 0
    counts = @{ killed = 0; survived = 0; timeout = 0; errored = 0 }
    mutants = @()
}

# Declared outside the try so the catch below can still publish whatever was measured before an
# unexpected failure, including one that happens before the loop is reached.
$evaluated = New-Object System.Collections.Generic.List[object]

try {
    Copy-Item -LiteralPath $assemblyUnderTest -Destination $pristineAssembly -Force

    # The PDB travels with the frozen copy so the catalog can still map each mutant to a source file
    # and line. That mapping is the whole value of the surviving-mutant artifact — without it a
    # survivor reads as an opaque IL offset. It is decoration, never a verdict input: a build with no
    # PDB produces the same catalog with empty source fields (see Catalog.normalizeSourcePath, which
    # also un-mangles the `/_/src/...` paths a deterministic CI build emits).
    $sourceSymbols = [System.IO.Path]::ChangeExtension($assemblyUnderTest, '.pdb')

    if (Test-Path -LiteralPath $sourceSymbols -PathType Leaf) {
        Copy-Item -LiteralPath $sourceSymbols -Destination ([System.IO.Path]::ChangeExtension($pristineAssembly, '.pdb')) -Force
    }

    # --- Baseline: the suite must be green BEFORE anything is mutated ---------------------------
    #
    # Without this, a suite that is already red would mark every mutant "killed" and report a perfect
    # score for a measurement that never happened.

    Write-Host "Baseline run (unmutated) with filter: $testFilter"
    $baselineRun = Invoke-TestRun -Project $testProject -Filter $testFilter `
        -ResultsDirectory $resultsDirectory -TrxName 'baseline.trx' -TimeoutSeconds 900

    $report.baselineDurationSeconds = [math]::Round($baselineRun.DurationSeconds, 2)
    $report.baselineExecutedTests = $baselineRun.ExecutedTests

    if ($baselineRun.TimedOut -or $baselineRun.ExitCode -ne 0) {
        $report.status = 'baseline-failed'
        Write-Annotation warning ('Mutation shard skipped: the unmutated baseline run did not pass ' +
            "(timed out: $($baselineRun.TimedOut), exit code: $($baselineRun.ExitCode)). Mutant verdicts " +
            'would be meaningless against a red suite.')
        Write-Report $report | Out-Null
        exit 0
    }

    # "No data" is its own state, never a score. A filter that matches nothing runs green and would
    # otherwise let every mutant survive, reporting a catastrophic 0 % that measured nothing at all.
    if ($baselineRun.ExecutedTests -le 0) {
        $report.status = 'no-tests'
        Write-Annotation warning ("Mutation shard skipped: the test filter executed 0 tests ($testFilter). " +
            'No mutant can be killed by a suite that does not run, so no score is reported.')
        Write-Report $report | Out-Null
        exit 0
    }

    Write-Host ("Baseline: $($baselineRun.ExecutedTests) test(s) in " +
        "$([math]::Round($baselineRun.DurationSeconds, 2))s")

    if ($MutantTimeoutSeconds -lt 1) {
        # Proportionate rather than absolute: x4 the measured baseline catches a mutant that turned a
        # bounded loop unbounded, while the floor keeps a very fast slice from timing out on a runner
        # that simply hiccuped.
        $MutantTimeoutSeconds = [int][math]::Max(60, [math]::Ceiling($baselineRun.DurationSeconds * 4) + 15)
    }

    $report.mutantTimeoutSeconds = $MutantTimeoutSeconds
    Write-Host "Per-mutant timeout: $MutantTimeoutSeconds s (retries: $MaxRetries)"

    # --- Catalog --------------------------------------------------------------------------------

    & dotnet $engineBinary list --assembly $pristineAssembly --scope $BaselinePath --output $catalogPath

    if ($LASTEXITCODE -ne 0) {
        Write-Annotation error "Mutation engine failed to build the catalog (exit code $LASTEXITCODE)"
        exit 2
    }

    $catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
    $allMutants = @(Get-Field $catalog 'mutants')
    $report.catalogTotal = $allMutants.Count

    # The engine already shuffled the catalog with the committed seed, so a straight modulo split
    # gives each shard a representative slice and the assignment is reproducible.
    $shardMutants = @(
        for ($i = 0; $i -lt $allMutants.Count; $i++) {
            if (($i % $ShardCount) -eq $ShardIndex) { $allMutants[$i] }
        }
    )

    $report.shardTotal = $shardMutants.Count

    if ($shardMutants.Count -eq 0) {
        $report.status = 'no-mutants'
        Write-Annotation warning ("Mutation shard skipped: the scope produced $($allMutants.Count) mutant(s) " +
            "in total and none fell to shard $ShardIndex of $ShardCount. Check 'scope.includeTypes' in " +
            "$BaselinePath - a renamed type silently empties the scope.")
        Write-Report $report | Out-Null
        exit 0
    }

    Write-Host ("Shard ${ShardIndex}/${ShardCount}: $($shardMutants.Count) of $($allMutants.Count) " +
        "mutant(s), budget $TimeBudgetSeconds s")

    # --- The mutant loop -------------------------------------------------------------------------

    $budget = [Diagnostics.Stopwatch]::StartNew()
    $index = 0

    foreach ($mutant in $shardMutants) {
        $index++

        if ($budget.Elapsed.TotalSeconds -ge $TimeBudgetSeconds) {
            $report.budgetExhausted = $true
            Write-Annotation notice ("Time budget of $TimeBudgetSeconds s reached after $($evaluated.Count) " +
                "of $($shardMutants.Count) mutant(s); the rest of this shard was not evaluated.")
            break
        }

        $status = 'errored'
        $detail = ''
        $attempts = 0
        $elapsed = 0.0
        $timeout = $MutantTimeoutSeconds

        # Killed/Survived are decisive and never retried. Errored is infrastructure-shaped and gets
        # another chance; Timeout is a legitimate verdict for a mutant that unbounded a loop, so it is
        # only retried (with double the budget) when the caller says the runner is suspect.
        while ($attempts -le $MaxRetries) {
            $attempts++

            & dotnet $engineBinary apply --assembly $pristineAssembly --scope $BaselinePath `
                --id $mutant.id --output $mutantAssembly | Out-Null

            if ($LASTEXITCODE -ne 0) {
                $status = 'errored'
                $detail = "mutation engine could not apply this mutant (exit code $LASTEXITCODE)"
                continue
            }

            if (-not (Copy-AssemblyWithRetry -Source $mutantAssembly -Destination $assemblyUnderTest)) {
                $status = 'errored'
                $detail = 'the mutated assembly could not be written over the assembly under test'
                continue
            }

            $run = Invoke-TestRun -Project $testProject -Filter $testFilter `
                -ResultsDirectory $resultsDirectory -TrxName "mutant-$($mutant.id).trx" -TimeoutSeconds $timeout

            $elapsed = $run.DurationSeconds

            if ($run.TimedOut) {
                $status = 'timeout'
                $detail = "no verdict within $timeout s"

                if (-not $RetryTimeouts) {
                    break
                }

                $timeout = $timeout * 2
                continue
            }

            if ($run.ExitCode -eq 0) {
                $status = 'survived'
                $detail = ''
                break
            }

            if ($run.ExecutedTests -le 0) {
                # A non-zero exit with nothing executed is the run failing, not an assertion firing:
                # the mutated assembly did not load, or the host died before discovery. Counting it as
                # a kill would inflate the score with mutants no test ever saw.
                $status = 'errored'
                $detail = 'the test run exited non-zero without executing any test'
                continue
            }

            $status = 'killed'
            $detail = ''
            break
        }

        $report.counts[$status] = [int] $report.counts[$status] + 1

        $evaluated.Add([pscustomobject]@{
                id = $mutant.id
                status = $status
                detail = $detail
                attempts = $attempts
                durationSeconds = [math]::Round($elapsed, 2)
                type = $mutant.type
                method = $mutant.method
                kind = $mutant.kind
                description = $mutant.description
                sourceFile = $mutant.sourceFile
                sourceLine = $mutant.sourceLine
            })

        Write-Host ("[$index/$($shardMutants.Count)] $($mutant.id) $($mutant.kind) " +
            "$($mutant.description) -> $status")
    }

    $report.evaluated = $evaluated.Count
    $report.mutants = $evaluated.ToArray()
}
catch {
    # An unexpected failure mid-loop must not throw away what the shard already measured: the report is
    # the only artifact, and a shard that dies silently is indistinguishable from one that never ran.
    # Mark it aborted (the gate treats any non-`ok` status as "this shard measured nothing", so a
    # partial count can never be mistaken for a real score) and let the report be written below.
    $report.status = 'aborted'
    $report.evaluated = $evaluated.Count
    $report.mutants = $evaluated.ToArray()
    Write-Annotation warning "Mutation shard aborted after $($evaluated.Count) mutant(s): $($_.Exception.Message)"
}
finally {
    # The pristine assembly goes back even if the loop threw: leaving a mutated ProcessKit.dll in the
    # test output would silently poison every later `dotnet test --no-build` in this working copy.
    # Retried, because this is the same racing copy as the swap above — and failing it is the worst
    # outcome this script has, so it is reported as an error rather than left to be discovered later.
    if (Test-Path -LiteralPath $pristineAssembly -PathType Leaf) {
        if (Copy-AssemblyWithRetry -Source $pristineAssembly -Destination $assemblyUnderTest) {
            Write-Host "Restored the unmutated $assemblyName"
        }
        else {
            Write-Annotation error ("Could NOT restore the unmutated $assemblyName to $assemblyUnderTest - " +
                'this working copy still holds a MUTATED assembly. Rebuild before running any test there.')
        }
    }

    $resolvedScratch = [System.IO.Path]::GetFullPath($scratchRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

    if ($resolvedScratch.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedScratch).StartsWith('processkit-mutate-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Report $report | Out-Null

Write-Host ("Shard ${ShardIndex}: killed $($report.counts.killed), survived $($report.counts.survived), " +
    "timeout $($report.counts.timeout), errored $($report.counts.errored)")

exit 0
