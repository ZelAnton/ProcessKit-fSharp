#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

Push-Location $repoRoot
try {
    $output =
        & dotnet list ProcessKit.slnx package --vulnerable --include-transitive --format json 2>&1

    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "dotnet list package failed with exit code $LASTEXITCODE"
    }

    $report = ($output -join [Environment]::NewLine) | ConvertFrom-Json
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
