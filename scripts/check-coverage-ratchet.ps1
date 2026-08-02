#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Compares the merged line coverage of a CI run with the committed coverage baseline.

.DESCRIPTION
    This is the coverage ratchet gate used by the `coverage-summary` job in
    .github/workflows/ci.yml. It reads the JsonSummary that ReportGenerator has already produced
    for the human readable summary instead of parsing Cobertura XML itself, so the gated number and
    the number published in the job summary come from one merge with one set of assembly filters
    and cannot drift apart.

    The verdict is deliberately fail closed only for a genuine regression. Merged coverage is a
    union over the whole test matrix, and platform specific code (the Windows Job Object backend,
    the Linux cgroup backend, the POSIX signal paths) is covered only by its own leg, so a run that
    lost a leg reports several points less than the same commit would with the full matrix.
    Comparing such a run against a baseline recorded from the full matrix would be a false failure,
    so an incomplete matrix reports SKIPPED with a warning instead.

    A run whose reports all arrived but carry no coverage data (the collector wrote well formed
    Cobertura files with an empty `packages` element, so the merge reports zero assemblies and no
    line coverage) is the same class of event: this run measured nothing. It also reports SKIPPED,
    because reading the absent percentage as 0.00 % would announce a total loss of coverage that
    never happened. Genuine zero coverage has a different shape (assemblies and coverable lines are
    present, the percentage is 0) and is still gated.

.PARAMETER CoverageRoot
    Directory the per leg coverage artifacts were downloaded into: one subdirectory per artifact,
    each holding one or more coverage.cobertura.xml files. Subdirectories are counted, not named,
    so adding or removing a matrix leg needs no change here.

.PARAMETER SummaryJson
    ReportGenerator JsonSummary file (Summary.json) written by the summary step.

.PARAMETER BaselinePath
    Committed baseline file. Defaults to coverage-baseline.json in the repository root.

.PARAMETER ExpectedLegs
    How many matrix legs this run should have delivered coverage for, as published by the test job
    (strategy.job-total). Empty or not a positive number falls back to the matrixLegs value
    recorded in the baseline file.

.PARAMETER StepSummaryPath
    Markdown file the verdict table is appended to. Defaults to $env:GITHUB_STEP_SUMMARY.

.NOTES
    Exit code contract:
      0 - the baseline is honoured, or the check was skipped on purpose (no coverage reports at
          all, reports that carry no coverage data, fewer matrix legs than expected, or a baseline
          that is deliberately unset).
      1 - real regression: merged line coverage is below lineCoverage minus toleranceLinePoints.
      2 - the check could not run: missing or malformed baseline file, or a missing summary file
          that ReportGenerator was supposed to write. This is a configuration fault, not a
          statement about coverage.

.EXAMPLE
    pwsh ./scripts/check-coverage-ratchet.ps1 -CoverageRoot coverage -SummaryJson coverage-report/Summary.json
#>
[CmdletBinding()]
param(
    [string] $CoverageRoot = 'coverage',
    [string] $SummaryJson = 'coverage-report/Summary.json',
    [string] $BaselinePath,
    [string] $ExpectedLegs = '',
    [string] $StepSummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repoRoot 'coverage-baseline.json'
}

if ([string]::IsNullOrWhiteSpace($StepSummaryPath)) {
    $StepSummaryPath = $env:GITHUB_STEP_SUMMARY
}

# Comparisons are done on doubles that came from an integer ratio, so a value that is mathematically
# equal to the threshold can still land a fraction of an ULP below it. This slack is far smaller
# than one covered line and only prevents that artefact.
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

function Format-Count {
    param([bool] $HasValue, [int] $Value)

    if ($HasValue) {
        return [string] $Value
    }

    return 'not reported'
}

# --- Discover what this run actually delivered ------------------------------------------------

$allReports = @()
$legsPresent = 0

if (Test-Path -LiteralPath $CoverageRoot -PathType Container) {
    $allReports = @(
        Get-ChildItem -LiteralPath $CoverageRoot -Recurse -File -Filter 'coverage.cobertura.xml'
    )

    foreach ($directory in @(Get-ChildItem -LiteralPath $CoverageRoot -Directory)) {
        $legReports = @(
            Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -Filter 'coverage.cobertura.xml'
        )

        if ($legReports.Count -gt 0) {
            $legsPresent++
        }
    }
}

# --- Read and validate the committed baseline --------------------------------------------------

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    Write-Annotation error "Coverage baseline file not found: $BaselinePath"
    exit 2
}

try {
    $baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
}
catch {
    # Malformed JSON in the committed baseline is a configuration fault: report it as such (exit 2)
    # rather than letting an unhandled error masquerade as a coverage regression (exit 1).
    Write-Annotation error "Coverage baseline file is not valid JSON ($BaselinePath): $($_.Exception.Message)"
    exit 2
}

$baselineCoverageField = Get-Field $baseline 'lineCoverage'
$toleranceField = Get-Field $baseline 'toleranceLinePoints'
$matrixLegsField = Get-Field $baseline 'matrixLegs'

if ($null -eq $baseline.PSObject.Properties['lineCoverage']) {
    Write-Annotation error "Coverage baseline is missing the required 'lineCoverage' field: $BaselinePath"
    exit 2
}

$tolerance = 0.0

if ($null -eq $toleranceField -or -not [double]::TryParse(
        [string] $toleranceField, [Globalization.NumberStyles]::Float,
        [cultureinfo]::InvariantCulture, [ref] $tolerance) -or $tolerance -lt 0) {
    Write-Annotation error "Coverage baseline needs a non negative number in 'toleranceLinePoints': $BaselinePath"
    exit 2
}

$expectedLegCount = 0

if ($null -eq $matrixLegsField -or -not [int]::TryParse(
        [string] $matrixLegsField, [ref] $expectedLegCount) -or $expectedLegCount -lt 1) {
    Write-Annotation error "Coverage baseline needs a positive integer in 'matrixLegs': $BaselinePath"
    exit 2
}

$baselineCoverage = 0.0
$baselineIsArmed = $null -ne $baselineCoverageField

if ($baselineIsArmed) {
    if (-not [double]::TryParse(
            [string] $baselineCoverageField, [Globalization.NumberStyles]::Float,
            [cultureinfo]::InvariantCulture, [ref] $baselineCoverage) -or
        $baselineCoverage -lt 0 -or $baselineCoverage -gt 100) {
        Write-Annotation error "Coverage baseline 'lineCoverage' must be null or a percentage between 0 and 100: $BaselinePath"
        exit 2
    }
}

# The value published by the test job wins when present: it describes the matrix as it is today,
# while the baseline's own matrixLegs describes the matrix the baseline was recorded from.
$parsedExpectedLegs = 0

if ([int]::TryParse($ExpectedLegs, [ref] $parsedExpectedLegs) -and $parsedExpectedLegs -gt 0) {
    $expectedLegCount = $parsedExpectedLegs
}

# --- Degradation: nothing to compare -----------------------------------------------------------

if ($allReports.Count -eq 0) {
    Add-StepSummary @(
        '',
        '### Coverage ratchet: skipped',
        '',
        'No Cobertura reports reached this job, so there is nothing to compare with the baseline.',
        'See the matrix test jobs for why coverage was not produced.'
    )
    Write-Annotation warning 'Coverage ratchet skipped: no coverage reports were produced.'
    exit 0
}

# --- Read the merged number ReportGenerator already computed ------------------------------------

if (-not (Test-Path -LiteralPath $SummaryJson -PathType Leaf)) {
    Write-Annotation error "ReportGenerator summary not found: $SummaryJson (expected next to the published Markdown summary)"
    exit 2
}

try {
    $summaryDocument = Get-Content -Raw -LiteralPath $SummaryJson | ConvertFrom-Json
}
catch {
    # Same reasoning as for the baseline: a summary this job produced itself but cannot read back
    # is a broken tool chain, not a coverage regression.
    Write-Annotation error "ReportGenerator summary is not valid JSON ($SummaryJson): $($_.Exception.Message)"
    exit 2
}

$summary = Get-Field $summaryDocument 'summary'
$assemblies = Get-Field $summary 'assemblies'
$coveredLines = Get-Field $summary 'coveredlines'
$coverableLines = Get-Field $summary 'coverablelines'
$reportedLineCoverage = Get-Field $summary 'linecoverage'

$observed = 0.0
$assemblyCount = 0
$coveredLineCount = 0
$coverableLineCount = 0

$hasAssemblyCount =
    $null -ne $assemblies -and [int]::TryParse([string] $assemblies, [ref] $assemblyCount)
$hasCoveredLineCount =
    $null -ne $coveredLines -and [int]::TryParse([string] $coveredLines, [ref] $coveredLineCount)
$hasCoverableLineCount =
    $null -ne $coverableLines -and [int]::TryParse([string] $coverableLines, [ref] $coverableLineCount)

# The `-gt 0` part is what makes the division below safe; keep it in this flag.
$hasLineCounts = $hasCoveredLineCount -and $hasCoverableLineCount -and $coverableLineCount -gt 0

# --- Degradation: the reports merged, but hold no coverage data ---------------------------------

# A coverage collector that instruments nothing still writes a valid Cobertura file, just an empty
# one (`<packages />`). Every leg then delivers its artifact, the leg count looks complete, and the
# merge of those files reports zero assemblies, zero coverable lines and no line coverage at all.
# This says the run measured nothing, exactly like a run that delivered no reports, and it is not a
# statement about the code: reading the absent percentage as 0.00 % would fail the gate with a total
# loss of coverage that never happened, and calling it a broken configuration would turn a red test
# matrix into a second red job with a misleading reason.
#
# The signal is deliberately "no data was produced", not "the percentage is low": zero assemblies,
# or no percentage at all next to a zero coverable line count. A run that genuinely covers nothing
# reports its assemblies and its coverable lines and a `linecoverage` of 0, misses none of the three,
# and is gated below like any other number.
$mergeHasNoCoverageData =
    ($hasAssemblyCount -and $assemblyCount -eq 0) -or
    ($hasCoverableLineCount -and $coverableLineCount -eq 0 -and $null -eq $reportedLineCoverage)

if ($mergeHasNoCoverageData) {
    $assemblyText = Format-Count $hasAssemblyCount $assemblyCount
    $coverableText = Format-Count $hasCoverableLineCount $coverableLineCount

    Add-StepSummary @(
        '',
        '### Coverage ratchet: skipped',
        '',
        '| Metric | Value |',
        '| --- | --- |',
        "| Matrix legs with coverage | $legsPresent of $expectedLegCount |",
        "| Cobertura reports merged | $($allReports.Count) |",
        "| Assemblies in merged report | $assemblyText |",
        "| Coverable lines | $coverableText |",
        '',
        "Skipped: the $($allReports.Count) merged Cobertura report(s) carry no coverage data at all (no",
        'assemblies, no coverable lines, no line coverage), so this run produced no number to compare with',
        'the baseline. Reports that parse but are empty mean the collection step measured nothing: a',
        'coverlet.collector run that instruments no module writes exactly these files. That is not a drop in',
        'coverage, and reading the absent percentage as 0.00 % would report a collapse that never happened,',
        'so the baseline is not enforced for this run. Look at the Test step of the matrix legs, and at the',
        'coverage artifacts they uploaded, to find out why nothing was instrumented.'
    )
    Write-Annotation warning ("Coverage ratchet skipped: the $($allReports.Count) merged coverage report(s) carry no " +
        "coverage data (assemblies: $assemblyText, coverable lines: $coverableText); coverage was not collected.")
    exit 0
}

if ($hasLineCounts) {
    # Preferred: the exact ratio behind the percentage. ReportGenerator rounds `linecoverage` to one
    # decimal, which is coarse enough to hide (or invent) a fraction of a point near the threshold.
    $observed = 100.0 * $coveredLineCount / $coverableLineCount
}
elseif ($null -ne $reportedLineCoverage -and [double]::TryParse(
        [string] $reportedLineCoverage, [Globalization.NumberStyles]::Float,
        [cultureinfo]::InvariantCulture, [ref] $observed)) {
    Write-Annotation notice 'Falling back to the rounded linecoverage field: the summary carried no line counts.'
}
else {
    # Not the empty merge handled above (that one reports its zeros): the summary object does not
    # carry the fields this script reads at all, so the schema is not the one it was written for.
    Write-Annotation error "ReportGenerator summary carries neither line counts nor a linecoverage value, so its schema is not the expected JsonSummary: $SummaryJson"
    exit 2
}

$observedText = Format-Percent $observed
$legText = "$legsPresent of $expectedLegCount"
$facts = @(
    '',
    '### Coverage ratchet',
    '',
    '| Metric | Value |',
    '| --- | --- |',
    "| Merged line coverage | $observedText % |",
    "| Matrix legs with coverage | $legText |",
    "| Cobertura reports merged | $($allReports.Count) |"
)

if ($hasLineCounts) {
    $facts += "| Covered / coverable lines | $coveredLineCount / $coverableLineCount |"
}

# --- Degradation: the matrix delivered less than it should have ---------------------------------

if ($legsPresent -lt $expectedLegCount) {
    Add-StepSummary ($facts + @(
            '',
            "Skipped: only $legsPresent of $expectedLegCount matrix legs delivered coverage. Merged coverage is a",
            'union over the matrix, so a missing leg lowers it for reasons that have nothing to do with this',
            'change. The baseline is not enforced for such a run.'
        ))
    Write-Annotation warning "Coverage ratchet skipped: $legsPresent of $expectedLegCount matrix legs delivered coverage."
    exit 0
}

# --- Degradation: the baseline is deliberately unset --------------------------------------------

if (-not $baselineIsArmed) {
    Add-StepSummary ($facts + @(
            '',
            'Skipped: the baseline is unset (`"lineCoverage": null`), so the ratchet records instead of gating.',
            ('Set the `lineCoverage` field of the baseline file to {0} to arm the gate at this level.' -f $observedText)
        ))
    Write-Annotation warning "Coverage ratchet is not armed: set lineCoverage in $BaselinePath (observed $observedText %)."
    exit 0
}

# --- The gate -----------------------------------------------------------------------------------

$minimumAccepted = $baselineCoverage - $tolerance
$facts += "| Baseline | $(Format-Percent $baselineCoverage) % |"
$facts += "| Tolerance | $(Format-Percent $tolerance) points |"
$facts += "| Minimum accepted | $(Format-Percent $minimumAccepted) % |"

if ($observed + $comparisonEpsilon -lt $minimumAccepted) {
    $shortfall = Format-Percent ($minimumAccepted - $observed)
    Add-StepSummary ($facts + @(
            '',
            "Failed: merged line coverage is $shortfall points below the accepted minimum.",
            'Either cover the new code, or move the baseline on purpose in this same change and say why',
            '(see the coverage baseline section of CONTRIBUTING.md).'
        ))
    Write-Annotation error "Coverage ratchet failed: $observedText % is below the accepted minimum of $(Format-Percent $minimumAccepted) %."
    exit 1
}

$headroom = Format-Percent ($observed - $minimumAccepted)
Add-StepSummary ($facts + @(
        '',
        "Passed: merged line coverage is $headroom points above the accepted minimum."
    ))

# Only worth mentioning once the gain is larger than the tolerance band; below that it is noise.
if ($observed - $baselineCoverage -gt $tolerance) {
    Write-Annotation notice "Coverage is $(Format-Percent ($observed - $baselineCoverage)) points above the baseline; consider raising it to $observedText."
}

exit 0
