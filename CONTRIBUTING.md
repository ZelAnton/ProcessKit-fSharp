# Contributing to ProcessKit

Thanks for your interest in improving **ProcessKit**.

## Prerequisites

- .NET 10 SDK (the exact band is pinned in [`global.json`](global.json)).
- The .NET 8 runtime as well: the projects multi-target `net8.0;net10.0`, so the
  full `dotnet test` runs both legs. Run `dotnet test --framework net10.0` to test
  a single target if you don't have the net8 runtime installed.
- Local tools restored once per clone (`dotnet tool restore`) — this installs
  [Fantomas](https://fsprojects.github.io/fantomas/), the F# formatter.
- Optional: PowerShell 7+ and Docker/Rancher Desktop to run the Linux test
  helper (`scripts/test-linux.ps1`).

## Build and test

```sh
dotnet tool restore
dotnet build ProcessKit.slnx
dotnet test  ProcessKit.slnx
```

The build treats **warnings as errors**, so a clean local build is required
before opening a pull request. Run a single test with:

```sh
dotnet test ProcessKit.slnx --filter "FullyQualifiedName~TestMethodName"
```

## Conventions

- **Formatting** is governed by [Fantomas](https://fsprojects.github.io/fantomas/),
  this repo's style authority (the F# compiler does not enforce `.editorconfig`
  style the way Roslyn does for C#). F# source is indented with **spaces, not
  tabs** — the compiler rejects tabs. Check before pushing:
  ```sh
  dotnet fantomas --check src tests
  ```
  CI fails on unformatted F#. Do not reformat code you are not changing.
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

The browsable [API reference](https://zelanton.github.io/ProcessKit-fSharp/) is generated from the
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
site is built and published by [`.github/workflows/docs.yml`](.github/workflows/docs.yml) on every
published GitHub Release, with `root` pointed at the actual GitHub Pages URL.

## Changelog

Every user-visible change ships its [`CHANGELOG.md`](CHANGELOG.md) entry in the
same change set, under `## [Unreleased]`. Write the bullet for a consumer of the
library, not the implementer. Pure internal refactors are exempt.

## Pull requests

- Keep changes focused; unrelated cleanups belong in their own PR.
- Ensure CI (YAML lint, Fantomas formatting, and build/test on Linux, Windows,
  and macOS) passes.
- Fill in the pull-request checklist.
