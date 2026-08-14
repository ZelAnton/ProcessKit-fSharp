#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configures this jj checkout to fail instead of opening an interactive editor.

.DESCRIPTION
    Writes only the repository-local .jj/repo/config.toml. Commands that provide
    their text inline, such as `jj describe -m "..."`, are unaffected.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$searchDirectory = Get-Item -LiteralPath (Join-Path $PSScriptRoot '..')
$repositoryRoot = $null

while ($null -ne $searchDirectory) {
    if (Test-Path -LiteralPath (Join-Path $searchDirectory.FullName '.jj')) {
        $repositoryRoot = $searchDirectory.FullName
        break
    }

    $parentDirectory = $searchDirectory.Parent
    if ($null -eq $parentDirectory -or $parentDirectory.FullName -eq $searchDirectory.FullName) {
        break
    }

    $searchDirectory = $parentDirectory
}

if ($null -eq $repositoryRoot) {
    throw "Could not find a jj checkout (.jj) from '$PSScriptRoot' or its parent directories."
}

$configDirectory = Join-Path $repositoryRoot '.jj/repo'
$configPath = Join-Path $configDirectory 'config.toml'
$editorLine = 'editor = ["pwsh", "-NoProfile", "-Command", "Write-Host ''Error: jj editor opened in non-interactive mode. Use -m flag to provide description inline.''; exit 1"]'

if (Test-Path -LiteralPath $configPath) {
    $originalContent = [string](Get-Content -LiteralPath $configPath -Raw)
} else {
    $originalContent = ''
}

$normalizedContent = $originalContent -replace "`r`n", "`n" -replace "`r", "`n"
$lines = [System.Collections.Generic.List[string]]::new([regex]::Split($normalizedContent, "`n"))
$uiHeaderIndex = -1

for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^\s*\[ui\]\s*(?:#.*)?$') {
        $uiHeaderIndex = $index
        break
    }
}

$editorIndexes = [System.Collections.Generic.List[int]]::new()
$legacyEditorArgsIndexes = [System.Collections.Generic.List[int]]::new()

if ($uiHeaderIndex -ge 0) {
    for ($index = $uiHeaderIndex + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*\[[^]]+\]\s*(?:#.*)?$') {
            break
        }

        if ($lines[$index] -match '^\s*editor\s*=') {
            $editorIndexes.Add($index)
        } elseif ($lines[$index] -match '^\s*editor-args\s*=') {
            $legacyEditorArgsIndexes.Add($index)
        }
    }
}

$alreadyConfigured =
    $editorIndexes.Count -eq 1 -and
    $legacyEditorArgsIndexes.Count -eq 0 -and
    $lines[$editorIndexes[0]].Trim() -eq $editorLine

if ($alreadyConfigured) {
    Write-Host 'Already configured; no changes made.'
    exit 0
}

if ($uiHeaderIndex -lt 0) {
    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) {
        $lines.RemoveAt($lines.Count - 1)
    }

    if ($lines.Count -gt 0) {
        $lines.Add('')
    }

    $lines.Add('[ui]')
    $lines.Add($editorLine)
} else {
    if ($editorIndexes.Count -gt 0) {
        $lines[$editorIndexes[0]] = $editorLine
    }

    $indexesToRemove = [System.Collections.Generic.List[int]]::new()
    for ($index = 1; $index -lt $editorIndexes.Count; $index++) {
        $indexesToRemove.Add($editorIndexes[$index])
    }

    foreach ($index in $legacyEditorArgsIndexes) {
        $indexesToRemove.Add($index)
    }

    $indexesToRemove.Sort()
    $indexesToRemove.Reverse()
    foreach ($index in $indexesToRemove) {
        $lines.RemoveAt($index)
    }

    if ($editorIndexes.Count -eq 0) {
        $lines.Insert($uiHeaderIndex + 1, $editorLine)
    }
}

if (-not (Test-Path -LiteralPath $configDirectory)) {
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
}

$newContent = [string]::Join("`n", $lines).TrimEnd([char[]]"`r`n") + "`n"
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($configPath, $newContent, $utf8WithoutBom)

Write-Host 'Configured ui.editor for non-interactive mode in .jj/repo/config.toml'
