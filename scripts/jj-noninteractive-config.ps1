$script:JjNonInteractiveEditorError = 'Error: jj editor opened in non-interactive mode. Use -m flag to provide description inline.'

function Get-JjNonInteractiveEditorConfig {
    [CmdletBinding()]
    param()

    return '["pwsh", "-NoProfile", "-Command", "[Console]::Error.WriteLine(''{0}''); exit 1;"]' -f $script:JjNonInteractiveEditorError
}

function Get-JjNonInteractiveEditorError {
    [CmdletBinding()]
    param()

    return $script:JjNonInteractiveEditorError
}

function Test-JjNonInteractiveEditorConfig {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string] $EditorValue
    )

    return -not [string]::IsNullOrWhiteSpace($EditorValue) `
        -and $EditorValue.Contains('"-Command"', [StringComparison]::Ordinal) `
        -and $EditorValue.Contains((Get-JjNonInteractiveEditorError), [StringComparison]::Ordinal)
}

function Test-JjNonInteractiveEditorBehavior {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $JjPath,

        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $probeOutput = @('' | & $JjPath --repository $RepositoryRoot --ignore-working-copy describe 2>&1)
    $probeExitCode = $LASTEXITCODE
    $probeText = $probeOutput -join [Environment]::NewLine

    if ($probeExitCode -ne 0 -and $probeText.Contains((Get-JjNonInteractiveEditorError), [StringComparison]::Ordinal)) {
        return 'Guarded'
    }

    if ($probeExitCode -ne 0 -and $probeText.Contains('immutable', [StringComparison]::OrdinalIgnoreCase)) {
        return 'Inconclusive'
    }

    return 'Failed'
}
