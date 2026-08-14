#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configures this jj repository to fail instead of opening an interactive editor.

.DESCRIPTION
    Uses jj to set only the repository-local ui.editor value. Commands that
    provide their text inline, such as `jj describe -m "..."`, are unaffected.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'jj-noninteractive-config.ps1')

$jjCommand = Get-Command jj -ErrorAction SilentlyContinue
if ($null -eq $jjCommand) {
    throw "Could not configure jj because 'jj' is not on PATH."
}

$checkoutPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootOutput = @(& $jjCommand.Source --repository $checkoutPath --ignore-working-copy root 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Could not find the jj checkout containing '$PSScriptRoot': $($rootOutput -join [Environment]::NewLine)"
}

$repositoryRoot = ($rootOutput -join [Environment]::NewLine).Trim()
$editorScript = Join-Path $repositoryRoot 'scripts/jj-no-editor.ps1'
if (-not (Test-Path -LiteralPath $editorScript -PathType Leaf)) {
    throw "Could not find the non-interactive editor script at '$editorScript'."
}

$expectedEditor = Get-JjNonInteractiveEditorConfig -RepositoryRoot $repositoryRoot
$currentEditor = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config get ui.editor 2>$null)
$currentEditorValue = if ($LASTEXITCODE -eq 0) { ($currentEditor -join [Environment]::NewLine).Trim() } else { $null }

if ($currentEditorValue -ne $expectedEditor) {
    & $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config set --repo ui.editor $expectedEditor
    if ($LASTEXITCODE -ne 0) {
        throw "jj could not set the repository-local ui.editor value."
    }
}

$verifiedOutput = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config list --include-defaults ui.editor 2>&1)
$expectedLine = "ui.editor = $expectedEditor"
if ($LASTEXITCODE -ne 0 -or ($verifiedOutput -join [Environment]::NewLine).Trim() -ne $expectedLine) {
    throw "jj did not resolve ui.editor to the expected non-interactive command. Expected '$expectedLine'; got '$($verifiedOutput -join [Environment]::NewLine)'."
}

if ($currentEditorValue -eq $expectedEditor) {
    Write-Host 'Already configured; no changes made.'
} else {
    $configPath = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config path --repo 2>$null)
    $configDescription = if ($LASTEXITCODE -eq 0) { ($configPath -join [Environment]::NewLine).Trim() } else { 'the repo-local config' }
    Write-Host "Configured ui.editor for non-interactive mode in $configDescription"
}
