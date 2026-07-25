# Documentation-snippet harness

Every ` ```fsharp ` and ` ```csharp ` block in `docs/*.md` is compiled against the
library on every run of the Docs workflow. This directory is where they are compiled.

The guides are the main channel through which people meet ProcessKit, but until this
harness existed nothing kept their samples honest: `ApiSurfaceTests` and ApiCompat
guard the *binary* surface, while the prose was free to rot silently as the API moved
(F# has no `mdbook test` / rustdoc equivalent). Now a renamed member, a changed
signature, or a sample that never compiled in the first place fails the
`Verify documentation snippets compile` job in
[`.github/workflows/docs.yml`](../../.github/workflows/docs.yml).

Run it yourself — before pushing a docs edit, or after changing public API:

```pwsh
pwsh ./scripts/verify-doc-snippets.ps1                      # everything
pwsh ./scripts/verify-doc-snippets.ps1 -Path docs/streaming.md
pwsh ./scripts/verify-doc-snippets.ps1 -SkipBuild           # just regenerate, to inspect the units
```

Failures are reported at the markdown line they came from:

```text
docs/commands.md:47: error FS0039: The value or constructor 'RunAsyncc' is not defined.
      match! cmd.RunAsyncc() with
```

## What is checked, and what is not

**Compile-only.** Nothing here is executed — the harness assemblies are built and
thrown away. A sample that spawns `git` is checked for "does this still typecheck
against the current API", never for what it would do. Behaviour is the test suite's
job (`tests/ProcessKit.Tests`, `tests/ProcessKit.CSharp.Tests`).

**Warnings are not errors here**, unlike everywhere else in this repository. Samples
are written for a reader: they legitimately ignore results, bind values they never
use, and match only the interesting cases, so warnings-as-errors would be a wall of
false failures. Deprecation warnings are the exception and stay fatal (`FS0044`,
`CS0612/0618/0619`) — a guide still using an API we deprecated is exactly the drift
this check exists to catch.

## How a block becomes a compilation unit

`scripts/verify-doc-snippets.ps1` writes one file per block into
`DocSnippets.FSharp/Generated/` or `DocSnippets.CSharp/Generated/` (git-ignored,
rewritten from scratch on every run), named after the source: a block whose first
line is `docs/commands.md:36` becomes `Commands_L0036.fs`.

- **Imports** (`open` / `using`) are hoisted to the top of the unit, after a prelude
  of what the guides say they assume (`open ProcessKit`, `open System`, …) plus this
  library's own namespaces and the fixtures below. The prelude carries the language
  basics and *this* library only; anything else — NUnit, `System.Diagnostics`,
  OpenTelemetry — is declared per block with a marker, so a sample never quietly
  compiles on an import a reader would not have guessed.
- **Type declarations** are hoisted to module scope (F#) or to the enclosing static
  class (C#), because neither language allows a type declaration inside a method or
  a computation expression.
- **Everything else is a statement.** C# statements go into an `async Task` method so
  `await` and `using var` work. F# statements stay at module level, unless the block
  uses computation-expression syntax (`let!`, `match!`, `use`, …) outside a
  `task { }` it opened itself — then the harness supplies the `task { }`.

**Fixtures** (`DocSnippets.FSharp/Fixtures.fs`, `DocSnippets.CSharp/Fixtures.cs`)
stand in for the values the prose introduces around a sample — `cmd`, `proc`,
`group`, `logger`, `services` — plus the placeholders the guides use for the reader's
own code (`Widget`, `handle`, `deploy`, …). They are never evaluated
(`Unchecked.defaultof` / `default!`) but they carry the real ProcessKit types, so a
renamed or re-typed API still breaks the build. A sample that needs a new placeholder
fails with a clear "not defined" error at its markdown line: add the fixture, or mark
the block.

**One block, one compilation unit.** Blocks do not see each other, so a sample that
continues one defined earlier on the page has to be marked (see below) — the
alternative, copying that definition into the fixtures, would mean the check asserts
against its own copy of the thing it is verifying.

## Marking a block

A block that is deliberately not a compilable unit opts out with an HTML comment on
the line **immediately above** its opening fence — the equivalent of rustdoc's
`ignore`. Nothing may sit between the marker and the fence, not even a blank line.
Markers may be stacked on consecutive lines. Readers never see them; they are HTML
comments.

```markdown
<!-- docsnippet:ignore reason: CliWrap's own API, shown for contrast -->
<!-- docsnippet:imports NUnit.Framework, System.Text -->
```

| Directive | Effect |
|---|---|
| `ignore reason: <why>` | Skip the block. The reason is required, so the exemption list stays reviewable — the script fails on an `ignore` without one. |
| `imports <ns>[, <ns>…]` | Add `open`s (F#) / `using`s (C#) the sample omits for readability. |

An unknown directive fails the script rather than being silently ignored.

The current exemptions are printed on every run. They fall into three groups: another
library's API shown for contrast (CliWrap in `comparison.md`, OpenTelemetry in
`observability.md`), and samples that continue a definition from an earlier block on
the same page (`testing.md`).

## The projects

Two harness projects, one per language, in a mini-solution
(`DocSnippets.slnx`) that is deliberately **not** part of `ProcessKit.slnx`: this is
generated build-time scaffolding and must never join the library's build graph,
`dotnet test`, packing or ApiCompat. Its `BuildDependency` entries build the four
libraries first, which is what this repository's assembly-`Reference` +
`AssemblySearchPaths` convention needs (the same shape `samples/Samples.slnx` uses).
Both projects pin `net10.0` alone: the public surface is TFM-uniform, and one
configuration is enough to answer the question this harness asks.
