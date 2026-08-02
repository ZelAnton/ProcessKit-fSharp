[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Pump', 'Cassette', 'Framing', 'Ansi')]
    [string] $Target,

    [Parameter(Mandatory)]
    [string] $LibFuzzer,

    [ValidateRange(1, 86400)]
    [int] $DurationSeconds = 60,

    [ValidateRange(1, 65536)]
    [int] $MaxInputBytes = 65536,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fuzzerPath = (Resolve-Path -LiteralPath $LibFuzzer).Path
$projectPath = Join-Path $repoRoot 'tests/ProcessKit.Fuzz/ProcessKit.Fuzz.fsproj'
$targetName = $Target.ToLowerInvariant()
$seedPath = Join-Path $repoRoot "tests/ProcessKit.Fuzz/corpus/$targetName"
$findingPath = Join-Path $repoRoot "artifacts/fuzz/$targetName"
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("processkit-fuzz-" + [Guid]::NewGuid().ToString('N'))
$publishPath = Join-Path $scratchRoot 'publish'
$corpusPath = Join-Path $scratchRoot 'corpus'
$previousTarget = [Environment]::GetEnvironmentVariable('PROCESSKIT_FUZZ_TARGET', 'Process')

try {
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
    New-Item -ItemType Directory -Path $corpusPath -Force | Out-Null
    New-Item -ItemType Directory -Path $findingPath -Force | Out-Null
    Get-ChildItem -LiteralPath $seedPath -File | Copy-Item -Destination $corpusPath

    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE"
    }

    if (-not $SkipBuild) {
        & dotnet build (Join-Path $repoRoot 'ProcessKit.slnx') --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "solution build failed with exit code $LASTEXITCODE"
        }
    }

    & dotnet publish $projectPath --configuration Release --framework net10.0 --no-build --output $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "fuzz harness publish failed with exit code $LASTEXITCODE"
    }

    $instrumentationTargets = @('ProcessKit.dll')
    if ($Target -eq 'Cassette') {
        $instrumentationTargets += 'ProcessKit.Testing.dll'
    }

    foreach ($assemblyName in $instrumentationTargets) {
        $assemblyPath = Join-Path $publishPath $assemblyName
        & dotnet tool run sharpfuzz -- $assemblyPath
        if ($LASTEXITCODE -ne 0) {
            throw "SharpFuzz instrumentation failed for $assemblyName with exit code $LASTEXITCODE"
        }
    }

    [Environment]::SetEnvironmentVariable('PROCESSKIT_FUZZ_TARGET', $targetName, 'Process')
    $harnessPath = Join-Path $publishPath 'ProcessKit.Fuzz.dll'
    $artifactPrefix = [System.IO.Path]::GetFullPath($findingPath) + [System.IO.Path]::DirectorySeparatorChar

    & $fuzzerPath `
        "-max_total_time=$DurationSeconds" `
        '-timeout=10' `
        "-max_len=$MaxInputBytes" `
        "-artifact_prefix=$artifactPrefix" `
        '--target_path=dotnet' `
        "--target_arg=$harnessPath" `
        $corpusPath

    if ($LASTEXITCODE -ne 0) {
        throw "libFuzzer reported a failure for $Target with exit code $LASTEXITCODE"
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PROCESSKIT_FUZZ_TARGET', $previousTarget, 'Process')

    $resolvedScratch = [System.IO.Path]::GetFullPath($scratchRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

    if ($resolvedScratch.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedScratch).StartsWith('processkit-fuzz-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
