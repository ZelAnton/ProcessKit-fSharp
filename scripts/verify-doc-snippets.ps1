#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Compiles every F# and C# code sample in the guide book against the current library.

.DESCRIPTION
    The guides in docs/*.md are the main channel through which people meet this
    library, but nothing kept their samples honest: the public-API snapshot test and
    ApiCompat guard the binary surface, while the prose was free to rot silently as
    the API evolved. This script closes that gap the way `mdbook test` does for
    Rust: it extracts every ```fsharp / ```csharp fenced block, wraps each one into
    its own compilation unit, and compiles the lot against the freshly built
    ProcessKit assemblies (docs/snippets/DocSnippets.slnx).

    Compile-only. Nothing extracted here is executed: a sample that spawns `git` or
    kills a process tree is checked for "does this still typecheck against the
    current API", not for what it would do. Behaviour is the test suite's job.

    HOW A BLOCK IS WRAPPED
    Each block becomes one generated file under docs/snippets/<harness>/Generated/:

      * `open` / `using` directives are hoisted to the top of the file, after a
        prelude of the imports the guides say they assume (`open ProcessKit`,
        `open System`, ...) plus DocSnippets.Fixtures.
      * type declarations (F# `type`, C# `class`/`record`/`struct`/`interface`/
        `enum`) are hoisted to module scope (F#) or to the enclosing static class
        (C#), because neither language allows a type declaration inside a method or
        a computation expression.
      * everything else is a statement. C# statements go into an `async Task` method
        so `await` and `using var` work. F# statements stay at module level, unless
        the block uses computation-expression syntax (`let!`, `match!`, `do!`,
        `use!`, `use`, `return!`), in which case they are wrapped in `task { }`.

    Fixtures (docs/snippets/*/Fixtures.*) supply the values the surrounding prose
    introduces but the block itself does not bind - `cmd`, `proc`, `group`,
    `logger`, `services`, ... They are compile-time stand-ins (`Unchecked.defaultof`
    / `default!`) with real ProcessKit types, so a renamed or re-typed API still
    breaks the build.

    MARKERS
    A block that is deliberately not a compilable unit opts out with an HTML comment
    on the line IMMEDIATELY above its opening fence (the equivalent of rustdoc's
    `ignore` / `no_run`). Nothing may sit between the marker and the fence - not
    even a blank line. Two directives exist:

        <!-- docsnippet:ignore reason: pseudocode, not a compilable unit -->
        <!-- docsnippet:imports Microsoft.FSharp.Core, System.Text -->

    `ignore` needs a reason - the point is to record WHY a sample cannot be checked,
    so the exemption list stays reviewable. `imports` adds `open`s (F#) / `using`s
    (C#) that the sample omits for readability. Several markers may be stacked on
    consecutive lines above the fence. An unknown directive, or an `ignore` without
    a reason, fails this script rather than being silently skipped.

    Compiler diagnostics are translated back to the markdown they came from, so a
    failure reads `docs/commands.md:47: error FS0039: ...` rather than pointing at a
    generated file nobody wrote.

.PARAMETER Path
    Markdown files to scan. Defaults to docs/*.md (the published guide book;
    docs/internals/** and docs/planning/** are internal notes and are not scanned).

.PARAMETER Configuration
    Build configuration for the harness and the libraries it references. Release by
    default, matching CI.

.PARAMETER SkipBuild
    Extract and generate the harness sources but do not build them. For debugging
    the extraction itself.

.EXAMPLE
    pwsh ./scripts/verify-doc-snippets.ps1

.EXAMPLE
    pwsh ./scripts/verify-doc-snippets.ps1 -Path docs/streaming.md -Configuration Debug
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$snippetsRoot = Join-Path $repoRoot 'docs/snippets'
$harnesses = @{
    fsharp = [pscustomobject]@{
        Directory = Join-Path $snippetsRoot 'DocSnippets.FSharp'
        Extension = '.fs'
    }
    csharp = [pscustomobject]@{
        Directory = Join-Path $snippetsRoot 'DocSnippets.CSharp'
        Extension = '.cs'
    }
}

# Imports the guides tell the reader to assume ("Samples assume `open ProcessKit`
# and `open System`"), plus the fixtures module. Kept in one place so a sample never
# has to repeat boilerplate the prose already established.
#
# The line: this prelude carries the language basics and THIS library's own
# namespaces. Anything else - NUnit, System.Diagnostics, OpenTelemetry - is
# declared per block with `<!-- docsnippet:imports ... -->`, so a sample never
# quietly compiles on an import a reader would not have guessed.
$fsharpPrelude = @(
    'open System'
    'open System.Collections.Generic'
    'open System.Threading'
    'open System.Threading.Tasks'
    'open Microsoft.Extensions.DependencyInjection'
    'open Microsoft.Extensions.Logging'
    'open ProcessKit'
    'open ProcessKit.Testing'
    'open ProcessKit.Extensions.DependencyInjection'
    'open ProcessKit.Extensions.Hosting'
    'open DocSnippets.Fixtures'
)
$csharpPrelude = @(
    'using System;'
    'using System.Collections.Generic;'
    'using System.Linq;'
    'using System.Threading;'
    'using System.Threading.Tasks;'
    # Consuming an F#-authored library from C# means naming FSharpResult/FSharpOption,
    # which the guides use freely - it is part of this library's C# surface, not
    # per-sample boilerplate.
    'using Microsoft.FSharp.Core;'
    'using Microsoft.Extensions.DependencyInjection;'
    'using Microsoft.Extensions.Logging;'
    'using ProcessKit;'
    'using ProcessKit.Testing;'
    'using ProcessKit.Extensions.DependencyInjection;'
    'using ProcessKit.Extensions.Hosting;'
    'using static DocSnippets.Fixtures;'
)

function ConvertTo-RepoRelative {
    param([string] $FullPath)

    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $FullPath)
    return $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function ConvertTo-Identifier {
    param([string] $FileName)

    # docs/timeouts-and-cancellation.md -> TimeoutsAndCancellation
    $parts = [System.IO.Path]::GetFileNameWithoutExtension($FileName) -split '[^A-Za-z0-9]+' |
        Where-Object { $_ -ne '' }
    return -join ($parts | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) })
}

# ---------------------------------------------------------------------------
# Markdown extraction
# ---------------------------------------------------------------------------

function Read-SnippetBlock {
    param([string] $MarkdownPath)

    $relative = ConvertTo-RepoRelative $MarkdownPath
    $identifier = ConvertTo-Identifier $MarkdownPath
    $lines = [System.IO.File]::ReadAllLines($MarkdownPath)
    $blocks = [System.Collections.Generic.List[object]]::new()
    $directives = [System.Collections.Generic.List[string]]::new()

    $index = 0
    while ($index -lt $lines.Length) {
        $line = $lines[$index]

        if ($line -match '^\s*<!--\s*docsnippet:\s*(?<body>.*?)\s*-->\s*$') {
            $directives.Add($Matches['body'])
            $index++
            continue
        }

        if ($line -match '^```(?<lang>[A-Za-z0-9_+#-]+)\s*$') {
            $language = $Matches['lang'].ToLowerInvariant()
            $bodyStart = $index + 1
            $end = $bodyStart
            while ($end -lt $lines.Length -and $lines[$end] -notmatch '^\s*```\s*$') {
                $end++
            }

            if ($harnesses.ContainsKey($language)) {
                $body = [System.Collections.Generic.List[object]]::new()
                for ($i = $bodyStart; $i -lt $end; $i++) {
                    $body.Add([pscustomobject]@{ Text = $lines[$i]; MdLine = $i + 1 })
                }

                $blocks.Add([pscustomobject]@{
                        MarkdownPath = $relative
                        StartLine    = $bodyStart + 1
                        Language     = $language
                        Body         = $body
                        Directives   = @($directives)
                        Name         = ('{0}_L{1:d4}' -f $identifier, ($bodyStart + 1))
                    })
            }
            elseif ($directives.Count -gt 0) {
                throw "$relative`:$($index + 1): docsnippet marker on a ``$language`` block, which this check does not compile."
            }

            $directives.Clear()
            $index = $end + 1
            continue
        }

        # Markers bind to the fence directly below them; anything else in between
        # (including a blank line) breaks the association, so it cannot silently
        # attach to some later block.
        $directives.Clear()
        $index++
    }

    return $blocks
}

function Resolve-Directive {
    param([object] $Block)

    $ignoreReason = $null
    $imports = [System.Collections.Generic.List[string]]::new()

    foreach ($directive in $Block.Directives) {
        if ($directive -match '^ignore\b\s*(?<rest>.*)$') {
            $rest = $Matches['rest'].Trim()
            if ($rest -notmatch '^reason:\s*(?<reason>\S.*)$') {
                throw "$($Block.MarkdownPath):$($Block.StartLine): 'docsnippet:ignore' needs a reason - write '<!-- docsnippet:ignore reason: ... -->'."
            }
            $ignoreReason = $Matches['reason'].Trim()
        }
        elseif ($directive -match '^imports\s+(?<list>\S.*)$') {
            foreach ($namespace in ($Matches['list'] -split ',')) {
                $trimmed = $namespace.Trim()
                if ($trimmed -ne '') {
                    $imports.Add($trimmed)
                }
            }
        }
        else {
            throw "$($Block.MarkdownPath):$($Block.StartLine): unknown docsnippet directive '$directive' (known: ignore, imports)."
        }
    }

    return [pscustomobject]@{
        IgnoreReason = $ignoreReason
        Imports      = $imports
    }
}

# ---------------------------------------------------------------------------
# Splitting a block into imports / declarations / statements
# ---------------------------------------------------------------------------

function Split-FSharpBody {
    param([object] $Block)

    $groups = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.List[object]]::new()
    $current = $null

    function New-Group {
        param([string] $Kind)
        return [pscustomobject]@{ Kind = $Kind; Lines = [System.Collections.Generic.List[object]]::new() }
    }

    foreach ($line in $Block.Body) {
        $text = $line.Text

        if ($text.Trim() -eq '') {
            if ($null -ne $current) {
                $current.Lines.Add($line)
            }
            continue
        }

        if ($text -match '^\s') {
            if ($null -eq $current) {
                # The block opens with an indented line: a continuation fragment
                # lifted out of a larger sample. Treat it as a statement and let the
                # compiler have its say.
                $current = New-Group 'Statement'
                $groups.Add($current)
            }
            $current.Lines.Add($line)
            continue
        }

        # Attributes and full-line comments belong to whatever construct follows.
        if ($text -match '^(//|\[<)') {
            $pending.Add($line)
            continue
        }

        $kind =
        if ($text -match '^open\s') { 'Import' }
        elseif ($text -match '^(type|module|namespace|exception)\s') { 'Declaration' }
        elseif ($text -match '^and\s' -and $groups.Count -gt 0 -and $groups[$groups.Count - 1].Kind -eq 'Declaration') { 'DeclarationContinuation' }
        else { 'Statement' }

        if ($kind -eq 'DeclarationContinuation') {
            $current = $groups[$groups.Count - 1]
        }
        else {
            $current = New-Group $kind
            $groups.Add($current)
        }

        foreach ($buffered in $pending) {
            $current.Lines.Add($buffered)
        }
        $pending.Clear()
        $current.Lines.Add($line)
    }

    return $groups
}

function Test-CSharpUsingDirective {
    param([string] $Text)

    return $Text -match '^using\s+(static\s+)?[A-Za-z_@][\w.]*(\s*=\s*[\w.<>,\[\]\s]+)?\s*;\s*$'
}

function Measure-CSharpDepth {
    <#
        Brace depth after this line, ignoring braces inside strings, chars and
        comments. Only used to find where a top-level construct ends, so the
        pathological cases (an unbalanced brace inside an interpolated string) would
        surface as a compile error, never as a silent skip.
    #>
    param([string] $Text, [ref] $InBlockComment, [int] $Depth)

    $depth = $Depth
    $i = 0
    while ($i -lt $Text.Length) {
        $c = $Text[$i]

        if ($InBlockComment.Value) {
            if ($c -eq '*' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/') {
                $InBlockComment.Value = $false
                $i += 2
                continue
            }
            $i++
            continue
        }

        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/') {
            break
        }
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*') {
            $InBlockComment.Value = $true
            $i += 2
            continue
        }
        if ($c -eq '@' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '"') {
            $i += 2
            while ($i -lt $Text.Length) {
                if ($Text[$i] -eq '"') {
                    if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '"') { $i += 2; continue }
                    $i++
                    break
                }
                $i++
            }
            continue
        }
        if ($c -eq '"') {
            $i++
            while ($i -lt $Text.Length) {
                if ($Text[$i] -eq '\') { $i += 2; continue }
                if ($Text[$i] -eq '"') { $i++; break }
                $i++
            }
            continue
        }
        if ($c -eq "'") {
            $i++
            while ($i -lt $Text.Length) {
                if ($Text[$i] -eq '\') { $i += 2; continue }
                if ($Text[$i] -eq "'") { $i++; break }
                $i++
            }
            continue
        }
        if ($c -eq '{') { $depth++ }
        elseif ($c -eq '}') { $depth-- }
        $i++
    }

    return $depth
}

function Split-CSharpBody {
    param([object] $Block)

    $groups = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.List[object]]::new()
    $declarationPattern = '^\s*((public|internal|private|protected|sealed|abstract|static|partial|file|readonly|ref|unsafe)\s+)*(class|record|struct|interface|enum)\b'

    $depth = 0
    $inBlockComment = $false
    $current = $null
    $declarationDepth = -1

    foreach ($line in $Block.Body) {
        $text = $line.Text
        $startDepth = $depth
        $depth = Measure-CSharpDepth -Text $text -InBlockComment ([ref] $inBlockComment) -Depth $depth

        if ($text.Trim() -eq '') {
            if ($null -ne $current) { $current.Lines.Add($line) }
            continue
        }

        if ($startDepth -eq 0 -and $declarationDepth -lt 0) {
            # Attributes on their own line attach to the construct below them.
            if ($text -match '^\s*\[[^\]]*\]\s*$') {
                $pending.Add($line)
                continue
            }

            $kind =
            if (Test-CSharpUsingDirective $text) { 'Import' }
            elseif ($text -match $declarationPattern) { 'Declaration' }
            else { 'Statement' }

            $current = [pscustomobject]@{ Kind = $kind; Lines = [System.Collections.Generic.List[object]]::new() }
            $groups.Add($current)
            foreach ($buffered in $pending) { $current.Lines.Add($buffered) }
            $pending.Clear()
            $current.Lines.Add($line)

            if ($kind -eq 'Declaration' -and -not ($depth -eq 0 -and $text.TrimEnd().EndsWith(';'))) {
                # A braced type: keep collecting until the braces close again.
                $declarationDepth = 0
            }
            continue
        }

        if ($null -eq $current) {
            $current = [pscustomobject]@{ Kind = 'Statement'; Lines = [System.Collections.Generic.List[object]]::new() }
            $groups.Add($current)
        }
        $current.Lines.Add($line)

        # The declaration ends where its braces close again - or, for a positional
        # record written across several lines, where its parameter list ends in `;`.
        if ($declarationDepth -ge 0 -and $depth -le 0 -and $text.TrimEnd() -match '[};]$') {
            $declarationDepth = -1
        }
    }

    return $groups
}

# ---------------------------------------------------------------------------
# Emitting a generated compilation unit
# ---------------------------------------------------------------------------

class SnippetWriter {
    [System.Collections.Generic.List[string]] $Lines
    [System.Collections.Generic.List[int]] $Map

    SnippetWriter() {
        $this.Lines = [System.Collections.Generic.List[string]]::new()
        $this.Map = [System.Collections.Generic.List[int]]::new()
    }

    [void] Add([string] $text) {
        $this.Lines.Add($text)
        $this.Map.Add(0)
    }

    [void] AddSource([string] $text, [int] $mdLine) {
        $this.Lines.Add($text)
        $this.Map.Add($mdLine)
    }
}

function Write-FSharpSnippet {
    param([object] $Block, [object] $Options)

    $groups = Split-FSharpBody $Block
    $statements = @($groups | Where-Object { $_.Kind -eq 'Statement' })

    # A sample written in computation-expression syntax (`let!`, `match!`, `use`, ...)
    # only compiles inside one. Most guides open their own `task { }`, in which case
    # wrapping again would add noise and - worse - a CE that ends on a `let` binding
    # does not compile. So wrap only when a CE keyword appears OUTSIDE any `task { }`
    # the sample opened itself: track the indentation of the open computation
    # expressions, the same way the offside rule scopes them.
    $needsTask = $false
    $openIndents = [System.Collections.Generic.List[int]]::new()
    foreach ($group in $statements) {
        foreach ($line in $group.Lines) {
            $text = $line.Text
            if ($text.Trim() -eq '') { continue }
            $indent = $text.Length - $text.TrimStart().Length

            while ($openIndents.Count -gt 0 -and $indent -le $openIndents[$openIndents.Count - 1]) {
                $openIndents.RemoveAt($openIndents.Count - 1)
            }

            $usesCeSyntax =
            ($text -match '(^|\s)(let!|use!|do!|match!|and!|return!)') -or
            ($text -match '^\s*use\s+[\w(_]')
            if ($usesCeSyntax -and $openIndents.Count -eq 0) {
                $needsTask = $true
            }

            if ($text -match '(^|[\s=>|(])(task|backgroundTask|async|asyncSeq|taskSeq)\s*\{\s*$') {
                $openIndents.Add($indent)
            }
        }
    }

    $writer = [SnippetWriter]::new()
    $writer.Add("module DocSnippets.Generated.$($Block.Name)")
    $writer.Add('')
    $writer.Add("// Generated from $($Block.MarkdownPath):$($Block.StartLine) by scripts/verify-doc-snippets.ps1.")
    $writer.Add('// Edit the markdown, not this file.')
    $writer.Add('')

    $seenImports = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($import in $fsharpPrelude) {
        [void] $seenImports.Add($import)
        $writer.Add($import)
    }
    foreach ($namespace in $Options.Imports) {
        $import = "open $namespace"
        if ($seenImports.Add($import)) { $writer.Add($import) }
    }
    foreach ($group in ($groups | Where-Object { $_.Kind -eq 'Import' })) {
        foreach ($line in $group.Lines) {
            if ($line.Text.Trim() -eq '') { continue }
            if ($seenImports.Add($line.Text.Trim())) {
                $writer.AddSource($line.Text, $line.MdLine)
            }
        }
    }
    $writer.Add('')

    foreach ($group in ($groups | Where-Object { $_.Kind -eq 'Declaration' })) {
        foreach ($line in $group.Lines) {
            $writer.AddSource($line.Text, $line.MdLine)
        }
        $writer.Add('')
    }

    if ($statements.Count -gt 0) {
        if ($needsTask) {
            $writer.Add('let private snippet () =')
            $writer.Add('    task {')
            foreach ($group in $statements) {
                foreach ($line in $group.Lines) {
                    $text = if ($line.Text.Trim() -eq '') { '' } else { '        ' + $line.Text }
                    $writer.AddSource($text, $line.MdLine)
                }
            }
            # A computation expression cannot end on a binding; close the block with
            # unit so a sample whose last construct is `let! x = ...` still compiles.
            $last = $statements[$statements.Count - 1]
            $firstText = ($last.Lines |
                    Where-Object { $_.Text.Trim() -ne '' -and $_.Text -notmatch '^\s*//' } |
                    Select-Object -First 1).Text
            if ($firstText -match '^\s*(let|use|and)') {
                $writer.Add('        ()')
            }
            $writer.Add('    }')
        }
        else {
            foreach ($group in $statements) {
                foreach ($line in $group.Lines) {
                    $writer.AddSource($line.Text, $line.MdLine)
                }
                $writer.Add('')
            }
        }
    }

    return $writer
}

function Write-CSharpSnippet {
    param([object] $Block, [object] $Options)

    $groups = Split-CSharpBody $Block
    $writer = [SnippetWriter]::new()
    $writer.Add("// Generated from $($Block.MarkdownPath):$($Block.StartLine) by scripts/verify-doc-snippets.ps1.")
    $writer.Add('// Edit the markdown, not this file.')

    $seenImports = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($import in $csharpPrelude) {
        [void] $seenImports.Add($import)
        $writer.Add($import)
    }
    foreach ($namespace in $Options.Imports) {
        $import = "using $namespace;"
        if ($seenImports.Add($import)) { $writer.Add($import) }
    }
    foreach ($group in ($groups | Where-Object { $_.Kind -eq 'Import' })) {
        foreach ($line in $group.Lines) {
            if ($line.Text.Trim() -eq '') { continue }
            if ($seenImports.Add($line.Text.Trim())) {
                $writer.AddSource($line.Text, $line.MdLine)
            }
        }
    }

    $writer.Add('')
    $writer.Add('namespace DocSnippets.Generated;')
    $writer.Add('')
    $writer.Add("internal static class $($Block.Name)")
    $writer.Add('{')

    foreach ($group in ($groups | Where-Object { $_.Kind -eq 'Declaration' })) {
        foreach ($line in $group.Lines) {
            $text = if ($line.Text.Trim() -eq '') { '' } else { '    ' + $line.Text }
            $writer.AddSource($text, $line.MdLine)
        }
        $writer.Add('')
    }

    $writer.Add('    internal static async Task RunAsync()')
    $writer.Add('    {')
    foreach ($group in ($groups | Where-Object { $_.Kind -eq 'Statement' })) {
        foreach ($line in $group.Lines) {
            $text = if ($line.Text.Trim() -eq '') { '' } else { '        ' + $line.Text }
            $writer.AddSource($text, $line.MdLine)
        }
    }
    $writer.Add('    }')
    $writer.Add('}')

    return $writer
}

# ---------------------------------------------------------------------------
# Diagnostics: map a compiler message back to the markdown it came from
# ---------------------------------------------------------------------------

function Resolve-Diagnostic {
    param([string] $Line, [hashtable] $Index)

    if ($Line -notmatch '^\s*(?<file>[^(\r\n]+)\((?<line>\d+),(?<col>\d+)(-\d+,\d+)?\):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(\s*\[[^\]]*\])?\s*$') {
        return $null
    }

    $severity = $Matches['severity']
    if ($severity -ne 'error') { return $null }

    $file = $Matches['file'].Trim()
    $generatedLine = [int] $Matches['line']
    $entry = $Index[[System.IO.Path]::GetFileName($file)]

    if ($null -eq $entry) {
        return [pscustomobject]@{
            Location = "$file($generatedLine,$($Matches['col']))"
            Code     = $Matches['code']
            Message  = $Matches['message']
            Source   = $null
        }
    }

    $mdLine = 0
    if ($generatedLine -ge 1 -and $generatedLine -le $entry.Map.Count) {
        $mdLine = $entry.Map[$generatedLine - 1]
    }
    if ($mdLine -eq 0) { $mdLine = $entry.StartLine }

    $sourceText = $null
    if ($generatedLine -ge 1 -and $generatedLine -le $entry.Lines.Count) {
        $sourceText = $entry.Lines[$generatedLine - 1].Trim()
    }

    return [pscustomobject]@{
        Location = "$($entry.MarkdownPath):$mdLine"
        Code     = $Matches['code']
        Message  = $Matches['message']
        Source   = $sourceText
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if (-not $Path -or $Path.Count -eq 0) {
    $Path = @('docs/*.md')
}

$markdownFiles = [System.Collections.Generic.List[string]]::new()
foreach ($pattern in $Path) {
    $resolved = Resolve-Path -Path (Join-Path $repoRoot $pattern) -ErrorAction SilentlyContinue
    if (-not $resolved) {
        $resolved = Resolve-Path -Path $pattern -ErrorAction SilentlyContinue
    }
    if (-not $resolved) {
        throw "No markdown file matches '$pattern'."
    }
    foreach ($item in $resolved) {
        # SUMMARY.md is mdBook's table of contents - no prose, no samples.
        if ([System.IO.Path]::GetFileName($item.Path) -eq 'SUMMARY.md') { continue }
        $markdownFiles.Add($item.Path)
    }
}

Write-Host "==> Extracting snippets from $($markdownFiles.Count) markdown file(s)" -ForegroundColor Cyan

$generated = @{}
foreach ($language in $harnesses.Keys) {
    $generated[$language] = [System.Collections.Generic.List[object]]::new()
}
$ignored = [System.Collections.Generic.List[object]]::new()

foreach ($file in ($markdownFiles | Sort-Object)) {
    foreach ($block in (Read-SnippetBlock $file)) {
        $options = Resolve-Directive $block
        if ($options.IgnoreReason) {
            $ignored.Add([pscustomobject]@{
                    Location = "$($block.MarkdownPath):$($block.StartLine)"
                    Language = $block.Language
                    Reason   = $options.IgnoreReason
                })
            continue
        }

        $writer =
        if ($block.Language -eq 'fsharp') { Write-FSharpSnippet -Block $block -Options $options }
        else { Write-CSharpSnippet -Block $block -Options $options }

        $generated[$block.Language].Add([pscustomobject]@{
                Block  = $block
                Writer = $writer
            })
    }
}

foreach ($language in $harnesses.Keys) {
    $harness = $harnesses[$language]
    $outputDirectory = Join-Path $harness.Directory 'Generated'
    if (Test-Path $outputDirectory) {
        Remove-Item -Recurse -Force $outputDirectory
    }
    [void] (New-Item -ItemType Directory -Force -Path $outputDirectory)

    foreach ($item in $generated[$language]) {
        $fileName = $item.Block.Name + $harness.Extension
        $content = ($item.Writer.Lines -join "`n") + "`n"
        [System.IO.File]::WriteAllText(
            (Join-Path $outputDirectory $fileName),
            $content,
            [System.Text.UTF8Encoding]::new($false))
    }

    Write-Host ("    {0,-7} {1,3} block(s) -> {2}" -f $language, $generated[$language].Count, (ConvertTo-RepoRelative $outputDirectory))
}

if ($ignored.Count -gt 0) {
    Write-Host "    ignored by marker:" -ForegroundColor DarkGray
    foreach ($entry in $ignored) {
        Write-Host ("      {0} ({1}): {2}" -f $entry.Location, $entry.Language, $entry.Reason) -ForegroundColor DarkGray
    }
}

if ($SkipBuild) {
    Write-Host "Generation complete; skipping the build as requested." -ForegroundColor Yellow
    exit 0
}

# Index generated file name -> block, for translating diagnostics back to markdown.
$diagnosticIndex = @{}
foreach ($language in $harnesses.Keys) {
    foreach ($item in $generated[$language]) {
        $diagnosticIndex[$item.Block.Name + $harnesses[$language].Extension] = [pscustomobject]@{
            MarkdownPath = $item.Block.MarkdownPath
            StartLine    = $item.Block.StartLine
            Map          = $item.Writer.Map
            Lines        = $item.Writer.Lines
        }
    }
}

$solution = Join-Path $snippetsRoot 'DocSnippets.slnx'
Write-Host "==> Building $(ConvertTo-RepoRelative $solution) ($Configuration)" -ForegroundColor Cyan

Push-Location $repoRoot
try {
    # A failing build is the expected, handled outcome here - this script turns it into
    # markdown-located diagnostics below. Without this, a host where
    # $PSNativeCommandUseErrorActionPreference is on would throw on dotnet's non-zero
    # exit under `$ErrorActionPreference = 'Stop'` and lose the whole report.
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & dotnet build $solution --configuration $Configuration --nologo 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

$diagnostics = [System.Collections.Generic.List[object]]::new()
$seenDiagnostics = [System.Collections.Generic.HashSet[string]]::new()
foreach ($line in $output) {
    $diagnostic = Resolve-Diagnostic -Line $line -Index $diagnosticIndex
    if ($null -eq $diagnostic) { continue }
    $key = "$($diagnostic.Location)|$($diagnostic.Code)|$($diagnostic.Message)"
    if ($seenDiagnostics.Add($key)) {
        $diagnostics.Add($diagnostic)
    }
}

if ($exitCode -eq 0) {
    $total = ($generated.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
    Write-Host "Documentation snippets compile: $total block(s) checked, $($ignored.Count) ignored by marker." -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host 'Documentation snippets failed to compile:' -ForegroundColor Red
if ($diagnostics.Count -eq 0) {
    # The build broke somewhere the mapping does not reach (restore, a harness
    # project file, the referenced libraries) - show the raw output rather than
    # reporting a green-looking failure.
    $output | ForEach-Object { Write-Host "  $_" }
}
else {
    foreach ($diagnostic in $diagnostics) {
        Write-Host ("  {0}: error {1}: {2}" -f $diagnostic.Location, $diagnostic.Code, $diagnostic.Message) -ForegroundColor Red
        if ($diagnostic.Source) {
            Write-Host ("      {0}" -f $diagnostic.Source) -ForegroundColor DarkGray
        }
    }
    Write-Host ''
    Write-Host "$($diagnostics.Count) snippet error(s). Fix the sample in the markdown, or mark the block with" -ForegroundColor Red
    Write-Host "'<!-- docsnippet:ignore reason: ... -->' if it is deliberately not a compilable unit." -ForegroundColor Red
}

exit 1
