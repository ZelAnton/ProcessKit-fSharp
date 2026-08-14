function Get-JjNonInteractiveEditorConfig {
    [CmdletBinding()]
    param()

    return '["pwsh", "-NoProfile", "-Command", "[Console]::Error.WriteLine(''Error: jj editor opened in non-interactive mode. Use -m flag to provide description inline.''); exit 1;"]'
}

function Get-JjNonInteractiveEditorError {
    [CmdletBinding()]
    param()

    return 'Error: jj editor opened in non-interactive mode. Use -m flag to provide description inline.'
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

    return $probeExitCode -eq 1 `
        -and ($probeOutput -join [Environment]::NewLine).Contains(
            (Get-JjNonInteractiveEditorError),
            [StringComparison]::Ordinal
        )
}
