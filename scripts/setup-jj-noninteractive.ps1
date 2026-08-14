#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configures this jj repository to fail instead of opening an interactive editor.

.DESCRIPTION
    Uses jj to set only the repository-wide ui.editor value shared by all
    workspaces. Commands that provide their text inline, such as
    `jj describe -m "..."`, are unaffected.
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
$expectedEditor = Get-JjNonInteractiveEditorConfig
$currentEditor = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config get ui.editor 2>$null)
$currentEditorValue = if ($LASTEXITCODE -eq 0) { ($currentEditor -join [Environment]::NewLine).Trim() } else { $null }

if ($currentEditorValue -ne $expectedEditor) {
    & $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config set --repo ui.editor $expectedEditor
    if ($LASTEXITCODE -ne 0) {
        throw "jj could not set the repository-wide ui.editor value."
    }
}

$resolvedEditor = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config get ui.editor 2>$null)
$resolvedEditorValue = if ($LASTEXITCODE -eq 0) { ($resolvedEditor -join [Environment]::NewLine).Trim() } else { $null }
if (-not (Test-JjNonInteractiveEditorConfig -EditorValue $resolvedEditorValue)) {
    throw "jj did not resolve ui.editor to the expected non-interactive command. Got '$resolvedEditorValue'."
}

$editorBehavior = Test-JjNonInteractiveEditorBehavior -JjPath $jjCommand.Source -RepositoryRoot $repositoryRoot
if ($editorBehavior -eq 'Inconclusive') {
    Write-Host 'The editor behavior probe was inconclusive because jj refused to describe the immutable commit before opening an editor.' -ForegroundColor Yellow
} elseif ($editorBehavior -ne 'Guarded') {
    throw "jj resolved ui.editor but did not reject an editor-driven description with the expected guidance."
}

if ($currentEditorValue -eq $expectedEditor) {
    Write-Host 'Already configured; no changes made.'
} else {
    $configPath = @(& $jjCommand.Source --repository $repositoryRoot --ignore-working-copy config path --repo 2>$null)
    $configDescription = if ($LASTEXITCODE -eq 0) { ($configPath -join [Environment]::NewLine).Trim() } else { 'the repository config' }
    Write-Host "Configured ui.editor for non-interactive mode in $configDescription"
}
