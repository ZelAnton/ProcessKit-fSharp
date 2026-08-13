#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Merges the mutation shard reports and compares the mutation score with the committed baseline.

.DESCRIPTION
    This is the gate used by the `summary` job in .github/workflows/mutation.yml. scripts/mutate.ps1
    writes facts (one report per shard); this turns them into one verdict.

    Score. Following the standard mutation-testing definition:

        score = (killed + timeout) / (killed + timeout + survived)

    A timed-out mutant counts as detected: mutating a loop condition genuinely turns a bounded loop
    unbounded, and a suite that hangs on it has noticed. Errored mutants — the mutated assembly failed
    to load, or the run died before executing a single test — are excluded from BOTH sides: nothing
    was measured about them, so folding them in either direction would be a fabricated number. They
    are reported separately instead, so an engine regression that quietly errors everything is visible
    rather than hidden in a denominator.

    "No data" is never a score. This gate is written against a failure mode this repository has
    already lived through: coverage was collected EMPTY on every matrix leg for months, because
    `ContinuousIntegrationBuild=true` plus SourceLink deterministic paths silently disabled
    instrumentation, and a summary that read the missing percentage as a number would have reported
    0.00 % — a catastrophic regression that never happened. Every "nothing was measured" shape here is
    therefore its own SKIP state with its own explanation, never a zero:

      * a shard whose unmutated baseline run was red or executed no tests;
      * a scope that produced no mutants at all (a renamed type empties it silently);
      * a run in which no mutant was killed at all, over a sample large enough that this cannot be
        real — the signature of a harness that mutated an assembly the test host never loaded;
      * a partial run (any shard stopped on its time budget), which cannot be compared with a
        baseline recorded from a complete one;
      * a catalog that no longer matches the population pinned in the baseline
        (`expectedCatalogMutants`). This is the one degradation that is not an EMPTY measurement but
        a smaller one: part of the scope quietly stopping to match still yields well formed reports
        and a plausible score, over a different program than the baseline describes.

    Division is guarded by the denominator, never by trusting the report to be well formed.

    Advisory vs ratchet. This gate can fail — but it only ever fails THIS workflow, which runs on a
    schedule and on demand, never on pull_request or push. So a mutation regression is loud and
    reviewable without ever blocking an ordinary CI run. With `"minimumScore": null` the gate records
    instead of gating, which is how the baseline is meant to be armed: from a complete CI matrix run,
    not from a local one.

.PARAMETER ReportRoot
    Directory the per-shard artifacts were downloaded into; searched recursively for
    mutation-report.json files.

.PARAMETER BaselinePath
    The committed scope + ratchet file. Defaults to mutation-baseline.json in the repository root.

.PARAMETER StepSummaryPath
    Markdown file the verdict table is appended to. Defaults to $env:GITHUB_STEP_SUMMARY.

.NOTES
    Exit code contract (mirrors scripts/check-coverage-ratchet.ps1 on purpose):
      0 - the baseline is honoured, or the check was skipped on purpose (see the skip states above).
      1 - real regression: the mutation score is below minimumScore minus toleranceScorePoints.
      2 - the check could not run: missing or malformed baseline, or no shard report at all. A
          configuration fault, not a statement about mutants.

.EXAMPLE
    pwsh ./scripts/check-mutation-report.ps1 -ReportRoot artifacts/mutation
#>
[CmdletBinding()]
param(
    [string] $ReportRoot = 'artifacts/mutation',
    [string] $BaselinePath,
    [string] $StepSummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repoRoot 'mutation-baseline.json'
}

if ([string]::IsNullOrWhiteSpace($StepSummaryPath)) {
    $StepSummaryPath = $env:GITHUB_STEP_SUMMARY
}

# Comparisons run on doubles that came from an integer ratio, so a value mathematically equal to the
# threshold can land a fraction of an ULP below it. This slack is far smaller than one mutant.
$comparisonEpsilon = 1e-6

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

function Add-StepSummary {
    param([string[]] $Lines)

    Write-Host ($Lines -join [Environment]::NewLine)

    if ([string]::IsNullOrWhiteSpace($StepSummaryPath)) {
        return
    }

    Add-Content -LiteralPath $StepSummaryPath -Value ($Lines -join [Environment]::NewLine)
}

function Get-Field {
    param($Object, [string] $Name)

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $null
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Format-Percent {
    param([double] $Value)

    return $Value.ToString('0.00', [cultureinfo]::InvariantCulture)
}

# The actionable half of the report: which limits the suite executed but did not pin, with the source
# location to go and look at. Printed for every outcome that produced a score - a passing run's
# survivors are exactly the list somebody should be picking the next boundary test from.
function Write-SurvivorList {
    if ($survivors.Count -eq 0) {
        Add-StepSummary @('', 'No surviving mutants: every mutant in scope was detected.')
        return
    }

    $shown = 40
    $lines = @(
        '',
        "#### Surviving mutants ($($survivors.Count))",
        '',
        '| Where | Operator | Mutation |',
        '| --- | --- | --- |'
    )

    foreach ($mutant in ($survivors | Select-Object -First $shown)) {
        $file = [string] (Get-Field $mutant 'sourceFile')
        $line = [string] (Get-Field $mutant 'sourceLine')
        $method = [string] (Get-Field $mutant 'method')

        # A build with no readable PDB reports no source location. That is expected (and explicitly
        # tolerated), so fall back to the method signature rather than printing an empty cell.
        $where = if ([string]::IsNullOrWhiteSpace($file)) { $method } else { "$file`:$line" }

        $lines += "| ``$where`` | $(Get-Field $mutant 'kind') | ``$(Get-Field $mutant 'description')`` |"
    }

    if ($survivors.Count -gt $shown) {
        $lines += ''
        $lines += "...and $($survivors.Count - $shown) more; the full list is in each shard's mutation-report.json artifact."
    }

    Add-StepSummary $lines
}

# --- Read and validate the committed baseline ---------------------------------------------------

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    Write-Annotation error "Mutation baseline file not found: $BaselinePath"
    exit 2
}

try {
    $baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
}
catch {
    # A hand-edited baseline is the intended way this file changes, so malformed JSON is a realistic
    # input: report it as the configuration fault it is (exit 2) rather than letting an unhandled
    # error masquerade as a mutation regression (exit 1).
    Write-Annotation error "Mutation baseline file is not valid JSON ($BaselinePath): $($_.Exception.Message)"
    exit 2
}

if ($null -eq $baseline.PSObject.Properties['minimumScore']) {
    Write-Annotation error "Mutation baseline is missing the required 'minimumScore' field: $BaselinePath"
    exit 2
}

$baselineScoreField = Get-Field $baseline 'minimumScore'
$toleranceField = Get-Field $baseline 'toleranceScorePoints'
$minimumMutantsField = Get-Field $baseline 'minimumMutantsForVerdict'

$tolerance = 0.0

if ($null -eq $toleranceField -or -not [double]::TryParse(
        [string] $toleranceField, [Globalization.NumberStyles]::Float,
        [cultureinfo]::InvariantCulture, [ref] $tolerance) -or $tolerance -lt 0) {
    Write-Annotation error "Mutation baseline needs a non negative number in 'toleranceScorePoints': $BaselinePath"
    exit 2
}

$minimumMutants = 0

if ($null -eq $minimumMutantsField -or -not [int]::TryParse(
        [string] $minimumMutantsField, [ref] $minimumMutants) -or $minimumMutants -lt 1) {
    Write-Annotation error "Mutation baseline needs a positive integer in 'minimumMutantsForVerdict': $BaselinePath"
    exit 2
}

$baselineScore = 0.0
$baselineIsArmed = $null -ne $baselineScoreField

if ($baselineIsArmed) {
    if (-not [double]::TryParse(
            [string] $baselineScoreField, [Globalization.NumberStyles]::Float,
            [cultureinfo]::InvariantCulture, [ref] $baselineScore) -or
        $baselineScore -lt 0 -or $baselineScore -gt 100) {
        Write-Annotation error "Mutation baseline 'minimumScore' must be null or a percentage between 0 and 100: $BaselinePath"
        exit 2
    }
}

# The population the score was computed over, pinned next to the score itself.
#
# Every other guard in this file triggers on an EMPTY measurement. The case none of them sees is a
# PARTIAL one: if some of `scope.includeTypes` stops matching — a type or module renamed during an
# ordinary refactor, which mutate.ps1 calls out as "a renamed type silently empties the scope" — the
# catalog just gets smaller. Every shard still reports `status: ok`, every counter is still positive,
# and the arithmetic is still well formed; it simply describes a different, smaller program than the
# baseline does. Without a pinned population, `catalogTotal` is a number nothing compares against, and
# an armed ratchet would keep passing (or fail for a reason that has nothing to do with the change)
# over a set of mutants nobody noticed shrinking.
$expectedCatalogField = Get-Field $baseline 'expectedCatalogMutants'
$catalogToleranceField = Get-Field $baseline 'catalogTolerancePercent'

$expectedCatalog = 0
$catalogIsPinned = $null -ne $expectedCatalogField
$catalogTolerancePercent = 0.0

if ($catalogIsPinned) {
    if (-not [int]::TryParse([string] $expectedCatalogField, [ref] $expectedCatalog) -or $expectedCatalog -lt 1) {
        Write-Annotation error "Mutation baseline 'expectedCatalogMutants' must be null or a positive integer: $BaselinePath"
        exit 2
    }

    if ($null -eq $catalogToleranceField -or -not [double]::TryParse(
            [string] $catalogToleranceField, [Globalization.NumberStyles]::Float,
            [cultureinfo]::InvariantCulture, [ref] $catalogTolerancePercent) -or
        $catalogTolerancePercent -lt 0 -or $catalogTolerancePercent -gt 100) {
        Write-Annotation error ('Mutation baseline needs a percentage between 0 and 100 in ' +
            "'catalogTolerancePercent' alongside 'expectedCatalogMutants': $BaselinePath")
        exit 2
    }
}

# Arming the score without pinning the population it came from would re-open exactly the hole above,
# so it is a configuration fault rather than something to warn about once the gate is already live.
if ($baselineIsArmed -and -not $catalogIsPinned) {
    Write-Annotation error ("Mutation baseline arms 'minimumScore' without an 'expectedCatalogMutants': a score " +
        'only means something relative to the mutant set it was computed over. Record both, in the same ' +
        "change: $BaselinePath")
    exit 2
}

# --- Collect the shard reports -------------------------------------------------------------------

$reportFiles = @()

if (Test-Path -LiteralPath $ReportRoot -PathType Container) {
    $reportFiles = @(Get-ChildItem -LiteralPath $ReportRoot -Recurse -File -Filter 'mutation-report.json')
}

if ($reportFiles.Count -eq 0) {
    Write-Annotation error "No mutation-report.json found under $ReportRoot; the shard jobs produced nothing to merge."
    exit 2
}

$killed = 0
$survived = 0
$timedOut = 0
$errored = 0
$evaluated = 0
$catalogTotal = 0
$partialShards = @()
$degradedShards = @()
$survivors = @()

# How many shards the run INTENDED, taken from the reports that did arrive rather than from the
# baseline file: it is the count the driver actually ran with, so a local single-shard run
# (`-ShardCount 1`, the whole catalog in one report) is complete, while a four-way CI matrix that lost
# a leg is visibly not. A run where every shard died reports nothing at all and is caught above.
$expectedShards = 1

foreach ($file in $reportFiles) {
    try {
        $report = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
    }
    catch {
        Write-Annotation error "Shard report is not valid JSON ($($file.FullName)): $($_.Exception.Message)"
        exit 2
    }

    $shardIndex = [string] (Get-Field $report 'shardIndex')
    $status = [string] (Get-Field $report 'status')
    $expectedShards = [math]::Max($expectedShards, [int] (Get-Field $report 'shardCount'))

    if ($status -ne 'ok') {
        # The shard itself already decided it measured nothing, and said why. Carry that verdict up
        # rather than re-deriving it from counters that are all zero for a reason.
        $degradedShards += "shard ${shardIndex}: $status"
        continue
    }

    if ([bool] (Get-Field $report 'budgetExhausted')) {
        $partialShards += "shard $shardIndex"
    }

    $counts = Get-Field $report 'counts'
    $killed += [int] (Get-Field $counts 'killed')
    $survived += [int] (Get-Field $counts 'survived')
    $timedOut += [int] (Get-Field $counts 'timeout')
    $errored += [int] (Get-Field $counts 'errored')
    $evaluated += [int] (Get-Field $report 'evaluated')
    $catalogTotal = [math]::Max($catalogTotal, [int] (Get-Field $report 'catalogTotal'))

    foreach ($mutant in @(Get-Field $report 'mutants')) {
        if ((Get-Field $mutant 'status') -eq 'survived') {
            $survivors += $mutant
        }
    }
}

$detected = $killed + $timedOut
$decided = $detected + $survived

$facts = @(
    '',
    '### Mutation tier',
    '',
    '| Metric | Value |',
    '| --- | --- |',
    "| Shard reports merged | $($reportFiles.Count) of $expectedShards |",
    "| Mutants in catalog | $catalogTotal |"
)

# The pinned population belongs in the reading, not only in the verdict: it is what tells a reader
# whether the catalog above is the one the baseline is talking about.
if ($catalogIsPinned) {
    $facts += "| Catalog pinned at | $expectedCatalog +/- $(Format-Percent $catalogTolerancePercent) % |"
}

$facts += @(
    "| Mutants evaluated | $evaluated |",
    "| Killed | $killed |",
    "| Timed out (detected) | $timedOut |",
    "| Survived | $survived |",
    "| Errored (excluded) | $errored |"
)

# --- Degradation: a shard never reported at all ---------------------------------------------------

# The mutant population is split across shards, so a shard that died before writing its report takes
# a quarter of the catalog with it and leaves a score computed over a different, smaller population
# than the baseline was. Exactly the reasoning behind the coverage ratchet's "fewer matrix legs than
# expected" skip, and the same conclusion: one skipped check costs less than one false failure.
if ($reportFiles.Count -lt $expectedShards) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: only $($reportFiles.Count) of $expectedShards shard(s) reported. A shard that crashed before",
            'writing its report takes its slice of the catalog with it, so the remaining shards measure a smaller',
            'population than the baseline was recorded over. Look at the shard jobs, then re-run.'
        ))
    Write-Annotation warning "Mutation ratchet skipped: only $($reportFiles.Count) of $expectedShards shard(s) reported."
    exit 0
}

# --- Degradation: a shard could not measure anything ---------------------------------------------

if ($degradedShards.Count -gt 0) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: $($degradedShards.Count) shard(s) reported that they measured nothing " +
            "($($degradedShards -join '; ')).",
            'A shard reports this when its UNMUTATED baseline run was red, when the test filter executed no',
            'test, when the scope produced no mutants, or when it aborted part way through. None of those is a',
            'statement about the code, so the baseline is not enforced for this run. Fix the shard first, then',
            'read the score.'
        ))
    Write-Annotation warning ("Mutation ratchet skipped: $($degradedShards.Count) shard(s) measured nothing " +
        "($($degradedShards -join '; ')).")
    exit 0
}

# --- Degradation: nothing was decided ------------------------------------------------------------

# The guard that makes the division below safe, and the direct analogue of reading an absent coverage
# percentage as 0.00 %: with no decided mutant there is no score, and inventing one would report a
# total collapse that never happened.
if ($decided -le 0) {
    Add-StepSummary ($facts + @(
            '',
            'Skipped: no mutant reached a verdict, so this run produced no score to compare with the baseline.',
            "$errored mutant(s) errored (the mutated assembly did not load, or the run died before executing a",
            'test) and none was killed, timed out or survived. That is a broken harness, not a drop in mutation',
            'score. Look at the shard logs and at the engine step before reading anything into this run.'
        ))
    Write-Annotation warning "Mutation ratchet skipped: no mutant reached a verdict (errored: $errored)."
    exit 0
}

# --- Degradation: a plausible-looking run in which nothing was detected --------------------------

# The one shape that would otherwise pass as a real, terrible score. If the harness mutated an
# assembly the test host never actually loaded, every mutant survives and the arithmetic is a
# perfectly well formed 0.00 %. Over a sample this size that cannot be a genuine result — the scoped
# modules carry dedicated boundary tests — so it is treated as "nothing was measured", the same class
# of event as an empty coverage report.
if ($detected -eq 0 -and $evaluated -ge $minimumMutants) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: not one of $evaluated evaluated mutant(s) was detected. The scoped modules are covered by",
            'dedicated boundary tests, so a total absence of kills is not a plausible measurement — it is the',
            'signature of a run whose test host never loaded the mutated assembly. Reporting it as 0.00 % would',
            'announce a collapse that did not happen, so the baseline is not enforced. Check that the mutation',
            'step patched the assembly the test project actually resolves.'
        ))
    Write-Annotation warning "Mutation ratchet skipped: 0 of $evaluated evaluated mutant(s) were detected; the harness is suspect."
    exit 0
}

$score = 100.0 * $detected / $decided
$scoreText = Format-Percent $score
$facts += "| Mutation score | $scoreText % |"

# --- Degradation: the score was computed over a different population ------------------------------

# The score is printed first, deliberately: a contributor who widened the scope on purpose needs both
# the new catalog size and the new score to re-record the baseline, and one skipped run should hand
# them both rather than sending them round again.
if ($catalogIsPinned) {
    $allowedDrift = $expectedCatalog * $catalogTolerancePercent / 100.0
    $drift = [math]::Abs($catalogTotal - $expectedCatalog)

    if ($drift - $comparisonEpsilon -gt $allowedDrift) {
        $direction = if ($catalogTotal -lt $expectedCatalog) { 'shrank to' } else { 'grew to' }

        Add-StepSummary ($facts + @(
                '',
                "Skipped: the catalog $direction $catalogTotal mutant(s), against the $expectedCatalog this baseline",
                "pins (tolerance $(Format-Percent $catalogTolerancePercent) %). The score above was computed over a",
                'different population than the baseline was recorded over, so comparing them would measure the change',
                'in scope rather than any change in assertion strength.',
                'A shrink is the case to look at first, because nothing else here can see it: a type renamed during an',
                'ordinary refactor drops out of `scope.includeTypes` silently, every shard still reports `ok`, and the',
                'mutants that went missing are simply never counted. If the new population is intended, re-record',
                '`expectedCatalogMutants` and `minimumScore` together in the same change; if it is not, fix the scope.',
                'CONTRIBUTING.md, section "Mutation testing", has the procedure.'
            ))
        Write-Annotation warning ("Mutation ratchet skipped: the catalog holds $catalogTotal mutant(s) but the " +
            "baseline pins $expectedCatalog; the score was computed over a different population.")
        exit 0
    }
}

# --- Degradation: too small a sample to mean anything --------------------------------------------

if ($evaluated -lt $minimumMutants) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: only $evaluated mutant(s) were evaluated, below the $minimumMutants required for a verdict.",
            'A handful of mutants moves the percentage by tens of points, so gating on it would be noise.'
        ))
    Write-Annotation warning "Mutation ratchet skipped: $evaluated evaluated mutant(s) is below the minimum of $minimumMutants."
    exit 0
}

# --- Degradation: the run is partial --------------------------------------------------------------

if ($partialShards.Count -gt 0) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: $($partialShards -join ', ') stopped on the time budget, so this run evaluated $evaluated of",
            "$catalogTotal mutant(s). The baseline was recorded from a complete run; comparing a partial one",
            'against it would fail (or pass) for reasons that have nothing to do with the change. Raise the',
            'budget, or add a shard.'
        ))
    Write-Annotation warning "Mutation ratchet skipped: $($partialShards -join ', ') exhausted the time budget ($evaluated of $catalogTotal mutants)."
    exit 0
}

# --- Degradation: the baseline is deliberately unset ----------------------------------------------

if (-not $baselineIsArmed) {
    Add-StepSummary ($facts + @(
            '',
            'Skipped: the baseline is unset (`"minimumScore": null`), so the ratchet records instead of gating.',
            ('Set `minimumScore` in the baseline file to {0} to arm the gate at this level.' -f $scoreText),
            'CONTRIBUTING.md, section "Mutation testing", has the procedure.'
        ))
    Write-Annotation warning "Mutation ratchet is not armed: set minimumScore in $BaselinePath (observed $scoreText %)."
    Write-SurvivorList
    exit 0
}

# --- The gate --------------------------------------------------------------------------------------

$minimumAccepted = $baselineScore - $tolerance
$facts += "| Baseline | $(Format-Percent $baselineScore) % |"
$facts += "| Tolerance | $(Format-Percent $tolerance) points |"
$facts += "| Minimum accepted | $(Format-Percent $minimumAccepted) % |"

if ($score + $comparisonEpsilon -lt $minimumAccepted) {
    $shortfall = Format-Percent ($minimumAccepted - $score)
    Add-StepSummary ($facts + @(
            '',
            "Failed: the mutation score is $shortfall points below the accepted minimum.",
            'Either strengthen the assertions the surviving mutants got past (tests/ProcessKit.Tests/',
            'MutationBoundaryTests.fs is where those live), or move the baseline on purpose in this same change',
            'and say why. CONTRIBUTING.md, section "Mutation testing", has the procedure.'
        ))
    Write-Annotation error "Mutation ratchet failed: $scoreText % is below the accepted minimum of $(Format-Percent $minimumAccepted) %."
    Write-SurvivorList
    exit 1
}

$headroom = Format-Percent ($score - $minimumAccepted)
Add-StepSummary ($facts + @(
        '',
        "Passed: the mutation score is $headroom points above the accepted minimum."
    ))

if ($score - $baselineScore -gt $tolerance) {
    Write-Annotation notice "Mutation score is $(Format-Percent ($score - $baselineScore)) points above the baseline; consider raising it to $scoreText."
}

Write-SurvivorList

exit 0
