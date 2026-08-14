function Get-JjNonInteractiveEditorConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $editorScript = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'scripts/jj-no-editor.ps1'))
    $tomlPath = $editorScript.Replace('\', '/').Replace('"', '\"')
    return '["pwsh", "-NoProfile", "-File", "{0}"]' -f $tomlPath
}
