#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

Push-Location $repoRoot
try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($argument in @('list', 'ProcessKit.slnx', 'package', '--vulnerable', '--include-transitive', '--format', 'json')) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)

    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout }
            if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr }
            throw "dotnet list package failed with exit code $($process.ExitCode)"
        }
    }
    finally {
        $process.Dispose()
    }

    $report = $stdout | ConvertFrom-Json
    $findings = [System.Collections.Generic.List[object]]::new()

    foreach ($project in @($report.projects)) {
        $frameworks =
            if ($project.PSObject.Properties['frameworks']) { @($project.frameworks) }
            else { @() }

        foreach ($framework in $frameworks) {
            foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
                $packages =
                    if ($framework.PSObject.Properties[$kind]) { @($framework.$kind) }
                    else { @() }

                foreach ($package in $packages) {
                    $advisories =
                        if ($package.PSObject.Properties['vulnerabilities']) { @($package.vulnerabilities) }
                        else { @() }

                    foreach ($advisory in $advisories) {
                        $findings.Add(
                            [pscustomobject]@{
                                Project = $project.path
                                Framework = $framework.framework
                                Package = $package.id
                                Version = $package.resolvedVersion
                                Severity = $advisory.severity
                                Advisory = $advisory.advisoryUrl
                            }
                        )
                    }
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        $findings | Format-Table Project, Framework, Package, Version, Severity, Advisory -AutoSize
        throw "$($findings.Count) vulnerable dependency advisory finding(s) detected"
    }

    Write-Host 'No vulnerable direct or transitive dependencies found.' -ForegroundColor Green
}
finally {
    Pop-Location
}
