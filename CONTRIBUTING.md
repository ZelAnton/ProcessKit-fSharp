# Contributing to ProcessKit

Thanks for your interest in improving **ProcessKit**.

## Prerequisites

- .NET 10 SDK (the exact band is pinned in [`global.json`](global.json)).
- The .NET 8 runtime as well: the projects multi-target `net8.0;net10.0`, so the
  full `dotnet test` runs both legs. Run `dotnet test --framework net10.0` to test
  a single target if you don't have the net8 runtime installed.
- Local tools restored once per clone (`dotnet tool restore`) — this installs
  [Fantomas](https://fsprojects.github.io/fantomas/), the F# formatter, and
  `fsharp-analyzers`, the Ionide.Analyzers CLI runner used by CI.
- Optional: PowerShell 7+ and Docker/Rancher Desktop to run the Linux test
  helper (`scripts/test-linux.ps1`).

## Build and test

```sh
pwsh ./scripts/verify-all.ps1 -SkipLinux
```

This one command restores local tools, checks formatting and spelling when its
CLI is installed, builds the solution and samples in Release, runs the F# and C#
test projects on both target frameworks, compiles documentation
snippets, verifies the CI-pinned rendered sidebar when mdBook 0.4.40 is available,
and checks offline links when lychee is installed. Omit `-SkipLinux` for the full
Docker/Rancher Desktop run; pass `-LibFuzzer /path/to/libfuzzer-dotnet` to include
both fuzz smoke targets. Every unavailable optional tool is reported as `SKIP`
in the final table rather than disappearing silently.

The build treats **warnings as errors**, so every executed stage must pass before
opening a pull request. Run a single test with:

```sh
dotnet test ProcessKit.slnx --filter "FullyQualifiedName~TestMethodName"
```

## Conventions

- **Formatting** is governed by [Fantomas](https://fsprojects.github.io/fantomas/),
  this repo's style authority (the F# compiler does not enforce `.editorconfig`
  style the way Roslyn does for C#). F# source is indented with **spaces, not
  tabs** — the compiler rejects tabs. The aggregate script checks it before building;
  to run that stage alone:
  ```sh
  dotnet fantomas --check src tests samples benchmarks docs/snippets/DocSnippets.FSharp/Fixtures.fs
  ```
  CI fails on unformatted F#. Do not reformat code you are not changing.
- **Static analysis** uses `fsharp-analyzers` with the Ionide.Analyzers rule
  package. Check the main library locally with:
  ```sh
  analyzers_path="$(find "${NUGET_PACKAGES:-$HOME/.nuget/packages}/ionide.analyzers" -type d -path '*/analyzers/dotnet/fs' | sort -V | tail -n 1)"
  dotnet fsharp-analyzers --project src/ProcessKit/ProcessKit.fsproj --analyzers-path "$analyzers_path"
  ```
  For a concise CI-style check, run:
  ```sh
  analyzers_path="$(find "${NUGET_PACKAGES:-$HOME/.nuget/packages}/ionide.analyzers" -type d -path '*/analyzers/dotnet/fs' | sort -V | tail -n 1)"
  dotnet fsharp-analyzers --project src/ProcessKit/ProcessKit.fsproj \
    --analyzers-path "$analyzers_path" \
    --exclude-analyzers PostfixGenericsAnalyzer StructDiscriminatedUnionAnalyzer \
    --treat-as-error IONIDE-001 IONIDE-003 IONIDE-005 IONIDE-006 IONIDE-008 IONIDE-011 \
    --output-format github
  ```
  Add `--report artifacts/analyzers.sarif --code-root .` when you want a SARIF
  report file for review.
- **Compile order matters.** F# resolves declarations top-to-bottom; the
  `<Compile Include="..." />` order in the `.fsproj` is the dependency order, not
  cosmetic. Insert a new file after everything it depends on.
- **Dependencies** use Central Package Management — declare versions only in
  [`Directory.Packages.props`](Directory.Packages.props); `PackageReference`
  items carry no `Version`.
- **Cross-project references** use `Reference` + `AssemblySearchPaths`, never
  `ProjectReference`. Build order comes from `BuildDependency` in the `.slnx`.
- Match the surrounding code's style for exception handling, comments, and
  architecture; keep the public API surface small and intentional.
- **The public API is locked.** `ApiSurfaceTests` snapshots the exported surface
  against `tests/ProcessKit.Tests/PublicApi.*.approved.txt`. If you change the
  public API on purpose, run the tests, review the generated `*.received.txt`
  (written next to the test assembly), and copy it over the matching
  `*.approved.txt`. An unreviewed API change fails the build.

## API reference site

The browsable [API reference](https://zelanton.github.io/ProcessKit-fSharp/api/) is generated from the
XML doc comments on `ProcessKit` and `ProcessKit.Extensions.DependencyInjection` with
[fsdocs](https://fsprojects.github.io/FSharp.Formatting/) (restored as a local tool alongside
Fantomas). `apidocs/index.md` is the site's hand-written landing page; everything else under
`reference/` is generated. Build it locally to check how a doc-comment edit renders:

```sh
dotnet tool restore
dotnet build ProcessKit.slnx --configuration Release
dotnet fsdocs build --input apidocs --output apidocs/output \
  --projects src/ProcessKit/ProcessKit.fsproj src/ProcessKit.Extensions.DependencyInjection/ProcessKit.Extensions.DependencyInjection.fsproj \
  --properties Configuration=Release \
  --sourcerepo https://github.com/ZelAnton/ProcessKit-fSharp/tree/main \
  --parameters root "/" fsdocs-collection-name "ProcessKit API Reference" \
               fsdocs-license-link "https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/LICENSE" \
               fsdocs-release-notes-link "https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/CHANGELOG.md" \
  --clean
```

Then open `apidocs/output/index.html` (or serve the folder with any static file server — `root "/"`
keeps the generated links relative for local browsing). `apidocs/output/` is git-ignored; the real
site is built and published by [`.github/workflows/docs.yml`](.github/workflows/docs.yml), which
deploys one combined GitHub Pages site: the mdBook **guides** book at the root and this API reference
under the `/api/` subpath (so `root` is pointed at `…/ProcessKit-fSharp/api/`). The reference is
rebuilt from the latest published GitHub Release; the guides are rebuilt on pushes to
`docs/**`/`theme/**`/`book.toml`.

## Changelog

Every user-visible change ships its [`CHANGELOG.md`](CHANGELOG.md) entry in the
same change set, under `## [Unreleased]`. Write the bullet for a consumer of the
library, not the implementer. Pure internal refactors are exempt.

## Coverage baseline

The `coverage-summary` job merges the Cobertura reports from every leg of the test matrix,
publishes the usual Markdown summary, and then compares the merged **line** coverage with
[`coverage-baseline.json`](coverage-baseline.json). The job fails when merged coverage sits more
than `toleranceLinePoints` below the recorded `lineCoverage`, so coverage cannot drift downwards one
pull request at a time. The full semantics, and why the baseline is a committed file rather than a
lookup of the last green run, are written next to the gate in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml).

The gate reports **skipped** instead of failing whenever it cannot compare honestly: no coverage
reports arrived at all, fewer matrix legs delivered coverage than expected (merged coverage is a
union over the legs, so a missing leg lowers it for reasons that have nothing to do with your
change), or `lineCoverage` is `null`. A skipped run is a warning in the job summary, never a red
build.

Moving the baseline is deliberate and reviewable, and belongs in the same pull request as the change
that moves coverage:

1. Take `Merged line coverage` from the **Coverage ratchet** table in the `coverage-summary` job
   summary of a run with the **full** matrix — a skipped run measured nothing.
2. Set `lineCoverage` in `coverage-baseline.json` to that number, and refresh `recordedOn` and
   `recordedFrom` so the next person can see where the number came from.
3. Say in the pull request why it moved. Raising it after adding tests locks the gain in; lowering
   it states on the record that this change trades coverage away on purpose.

Adding or removing an OS leg in the matrix changes which platform specific code the union covers, so
update `matrixLegs` and record `lineCoverage` again from a run of the new matrix. Setting
`lineCoverage` to `null` disarms the gate and makes it print the observed value instead — the
supported way to bootstrap a baseline, not a way to silence a regression.

The check itself is [`scripts/check-coverage-ratchet.ps1`](scripts/check-coverage-ratchet.ps1). It
reads the summary ReportGenerator already produced, so it needs a full set of matrix artifacts and
cannot say anything useful about a local single platform run; its exit codes are 0 for honoured or
skipped, 1 for a real regression, and 2 for a broken baseline or summary file.

## Link checking

[`.github/workflows/link-check.yml`](.github/workflows/link-check.yml) checks Markdown
links across `docs/**`, the root project Markdown, and the sample READMEs with
[lychee](https://github.com/lycheeverse/lychee). The `internal` job runs `--offline`
(local relative-path links only) on every pull request/push, so it is fast and fully
deterministic; the `external` job additionally checks external URLs (and
`apidocs/index.md`'s links to the site) on a weekly schedule (or `workflow_dispatch`), so
a flaky third-party site never fails a PR. Shared settings live in
[`lychee.toml`](lychee.toml); a supported ignore list for flaky/anti-bot domains lives in
[`.lycheeignore`](.lycheeignore). Check locally with the
[lychee CLI](https://github.com/lycheeverse/lychee#installation):

```sh
lychee --offline './docs/**/*.md' './*.md' './samples/**/README.md'
```

## Pull requests

- Keep changes focused; unrelated cleanups belong in their own PR.
- Ensure CI (YAML lint, Fantomas formatting, build/test on Linux, Windows, and
  macOS, the coverage baseline gate, and the internal Markdown link check) passes.
- Fill in the pull-request checklist.
