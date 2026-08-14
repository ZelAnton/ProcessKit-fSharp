#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $TemporaryFile
)

$ErrorActionPreference = 'Stop'
[Console]::Error.WriteLine('Error: jj editor opened in non-interactive mode. Use -m flag to provide description inline.')

if (-not [string]::IsNullOrWhiteSpace($TemporaryFile) -and (Test-Path -LiteralPath $TemporaryFile -PathType Leaf)) {
    try {
        Remove-Item -LiteralPath $TemporaryFile -Force
    } catch {
        [Console]::Error.WriteLine("Warning: could not remove jj editor temporary file '$TemporaryFile': $($_.Exception.Message)")
    }
}

exit 1
