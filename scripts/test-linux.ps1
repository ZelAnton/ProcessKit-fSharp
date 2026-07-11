#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the test suite inside a Linux container.

.DESCRIPTION
    Cross-platform wrapper around `docker run` that builds and tests the
    solution against a Linux .NET SDK image. Intended for developers on
    Windows using Rancher Desktop (or Docker Desktop) to exercise the
    Linux/Unix code path without leaving their host.

    The host's bin/obj folders (populated by the Windows IDE build) are
    shadowed inside the container with anonymous volumes — Linux build
    artifacts live in those throwaway volumes and never touch the host
    working copy. A named volume caches NuGet packages between runs.

    This script is an optional convenience helper. Delete it (and
    docs/linux-testing.md) if your project does not need Linux testing.

.PARAMETER Image
    Container image. Defaults to mcr.microsoft.com/dotnet/sdk:10.0 (or, with
    -Alpine, mcr.microsoft.com/dotnet/sdk:10.0-alpine). Passing -Image
    overrides that default regardless of -Alpine.

.PARAMETER Configuration
    MSBuild configuration. Debug or Release. Defaults to Release.

.PARAMETER Filter
    Optional `dotnet test --filter` expression
    (e.g. "FullyQualifiedName~greet").

.PARAMETER Rebuild
    Run `dotnet clean` before the tests.

.PARAMETER Privileged
    Run the container with `--privileged --cgroupns=host` so the Linux cgroup v2
    `limits` backend can engage (it needs write access to the real cgroup
    hierarchy). Without this, the unprivileged container exercises the
    fail-fast / process-group fallback path only.

.PARAMETER Alpine
    Run against mcr.microsoft.com/dotnet/sdk:10.0-alpine (musl libc) instead of
    the default glibc-based image, to exercise the native layer's musl path
    (Alpine is the de facto standard base for containerized deployments).
    The Alpine image has no bash, so the in-container script runs under `sh`
    instead; it also lacks a real `setpriv` (BusyBox ships an incompatible
    applet of the same name — no --reuid/--regid/--clear-groups), so this
    switch installs the real util-linux package first. Does not change the
    default (glibc) behaviour when omitted.

.EXAMPLE
    pwsh ./scripts/test-linux.ps1

.EXAMPLE
    pwsh ./scripts/test-linux.ps1 -Filter "FullyQualifiedName~greet"

.EXAMPLE
    pwsh ./scripts/test-linux.ps1 -Privileged -Filter "FullyQualifiedName~LimitsTests"

.EXAMPLE
    pwsh ./scripts/test-linux.ps1 -Alpine
#>
[CmdletBinding()]
param(
    [string]$Image,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Filter,
    [switch]$Rebuild,
    [switch]$Privileged,
    [switch]$Alpine
)

$ErrorActionPreference = 'Stop'

if (-not $Image) {
    $Image = if ($Alpine) { 'mcr.microsoft.com/dotnet/sdk:10.0-alpine' } else { 'mcr.microsoft.com/dotnet/sdk:10.0' }
}

# Normalise to forward slashes so docker on Windows handles paths with mixed
# separators uniformly. The bind-mount source still needs to be quoted in case
# the user clones the repo into a path containing spaces.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -replace '\\', '/'
$NugetVolume = 'ProcessKit-nuget'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "docker CLI not found on PATH." -ForegroundColor Red
    Write-Host "Start Rancher Desktop (with the dockerd/moby engine) or install Docker Desktop, then re-open the shell." -ForegroundColor Yellow
    exit 1
}

& docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Cannot reach the Docker daemon. Is Rancher Desktop running?" -ForegroundColor Red
    exit 1
}

$bashLines = @('set -e')
if ($Alpine) {
    # BusyBox (Alpine's base) ships its own `setpriv` applet — same name, incompatible flags (no
    # --reuid/--regid/--clear-groups) — so the Uid/Gid privilege-drop path (Native.Posix.fs,
    # setprivCommand) needs the real util-linux package installed first, shadowing the applet.
    $bashLines += 'apk add --no-cache util-linux'
}
if ($Privileged) {
    # cgroup v2's "no internal processes" rule lets controllers be enabled (in subtree_control)
    # only for the real hierarchy root. Move this shell — and the dotnet processes it spawns — to
    # the real root so the `limits` cgroup backend can engage, then tell the tests to *require* the
    # cgroup path (not silently accept the fail-fast fallback).
    $bashLines += 'if echo $$ > /sys/fs/cgroup/cgroup.procs 2>/dev/null; then export PROCESSKIT_EXPECT_CGROUP=1; fi'
}
if ($Rebuild) {
    $bashLines += "dotnet clean -c $Configuration"
}
# The default sdk:10.0(-alpine) image carries only the net10 runtime, so build and test the
# net10.0 TFM only (the Linux-specific code path is runtime-version-independent). CI exercises
# both net8.0 and net10.0; this helper just exercises the Linux/Unix path quickly.
$bashLines += "dotnet build -c $Configuration -p:TargetFrameworks=net10.0"
$testCmd = "dotnet test --no-build -c $Configuration --framework net10.0 tests/ProcessKit.Tests/ProcessKit.Tests.fsproj"
if ($Filter) {
    $testCmd += " --filter `"$Filter`""
}
$bashLines += $testCmd
$bashScript = $bashLines -join "`n"

# Anonymous volumes shadow the host bin/obj folders inside the container so
# Windows IDE artifacts cannot leak into the Linux build, and the Linux build
# does not write back into the host tree. The library DLL still lands at the
# standard `src/ProcessKit/bin/...` path the test project expects via
# AssemblySearchPaths — just inside the anonymous volume.
$shadowedPaths = @(
    '/src/src/ProcessKit/bin',
    '/src/src/ProcessKit/obj',
    '/src/tests/ProcessKit.Tests/bin',
    '/src/tests/ProcessKit.Tests/obj'
)

$dockerArgs = @(
    'run', '--rm',
    '-v', "${RepoRoot}:/src",
    '-v', "${NugetVolume}:/root/.nuget/packages"
)
if ($Privileged) {
    # cgroup v2 limits need write access to the real cgroup hierarchy: --cgroupns=host
    # exposes it (a private cgroup namespace EBUSYs the subtree_control enable).
    $dockerArgs += @('--privileged', '--cgroupns=host')
}
foreach ($p in $shadowedPaths) {
    $dockerArgs += @('-v', $p)
}
# Alpine's base image has no bash (only BusyBox's `sh`); every line above is plain POSIX sh, so
# running under `sh` works for both images and avoids an extra `apk add bash` just for this script.
$shellBin = if ($Alpine) { 'sh' } else { 'bash' }
$dockerArgs += @(
    '-w', '/src',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '-e', 'DOTNET_NOLOGO=1',
    $Image,
    $shellBin, '-c', $bashScript
)

Write-Host "==> Running tests in $Image" -ForegroundColor DarkGray
Write-Host "    Repo:          $RepoRoot -> /src" -ForegroundColor DarkGray
Write-Host "    Configuration: $Configuration" -ForegroundColor DarkGray
if ($Filter)  { Write-Host "    Filter:        $Filter" -ForegroundColor DarkGray }
if ($Rebuild) { Write-Host "    Rebuild:       yes" -ForegroundColor DarkGray }
if ($Alpine)  { Write-Host "    Alpine:        yes (musl)" -ForegroundColor DarkGray }

& docker @dockerArgs
exit $LASTEXITCODE
