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
opening a pull request. The `Stress` and `Interleaving` fixtures are marked
NUnit `Explicit`, so an ordinary `dotnet test ProcessKit.slnx` (and
`verify-all.ps1`) skips them. Opt in to either suite by selecting its category:

```sh
dotnet test ProcessKit.slnx --filter "Category=Stress"
dotnet test ProcessKit.slnx --filter "Category=Interleaving"
```

The weekly and manually dispatched CI jobs use those same category filters to
run the opt-in suites. Run a single ordinary test with:

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
- **The stable identifiers are locked too.** [`spec/identifiers.json`](spec/identifiers.json)
  is the machine readable dictionary of the wire names for `Mechanism`, `Signal`,
  `Outcome`, `ProcessError`, `LimitVerdict`, `SupervisionEventKind`, and
  `RlimitResource`. It is
  generated, never hand edited: `IdentifiersManifestTests` rebuilds it from the live
  cases and fails on any difference. Adding a case to one of the five unions is a
  compile error until it is named in `src/ProcessKit/StableIdentifiers.fs`; adding an
  `RlimitResource` is a compile error until it is named in its own wildcard free
  `Name` member in `src/ProcessKit/Limits.fs`, which is also the spelling
  `TryFromName` parses back; adding a
  `SupervisionEventKind` compiles (F# enum matches carry a wildcard arm) but fails that
  test until it is named in `SupervisionEventPayload.eventName`, next to the events
  that carry it. Name it in the one place the emitting code already reads — never in a
  second list — then run the tests and copy the generated `identifiers.received.json`
  (written next to the test assembly) over `spec/identifiers.json`. **New identifiers
  are appended; a shipped one is never renamed or reused** — readers in other languages
  pin these strings, so a rename breaks them even when the .NET API is untouched.
  **A new stable string vocabulary has to be registered by hand** in that generator's
  `dictionary` list: the compiler and the drift test between them guarantee that a type
  already in the dictionary stays complete, but neither can notice a seventh or eighth
  type that was never added, so adding one is part of introducing it.

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
reports arrived at all, the reports that arrived carry no coverage data (a collector that
instruments nothing still writes valid but empty Cobertura files, and merging those reports zero
assemblies and no percentage), fewer matrix legs delivered coverage than expected (merged coverage
is a union over the legs, so a missing leg lowers it for reasons that have nothing to do with your
change), or `lineCoverage` is `null`. A skipped run is a warning in the job summary, never a red
build. Coverage that is genuinely zero is not one of these cases: it still reports assemblies and
coverable lines, and is gated like any other number.

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

## Automation and non-interactive mode

On Windows, jj defaults to Notepad for commit-description editing, which can leave an unattended
run blocked on a GUI window. Before automated or otherwise non-interactive work, opt in to the
repository-wide guard:

```sh
pwsh ./scripts/setup-jj-noninteractive.ps1
```

The script asks jj to set `ui.editor` in the repository config with `jj config set --repo`, so the
guard applies to the main checkout and every linked workspace. Its inline command contains no
checkout path and remains valid when a workspace is removed. It does not change your user-level jj
configuration. To verify the guard directly, run `jj describe` without `-m`: it should fail with the
non-interactive-mode error. Commands that provide their message inline, such as
`jj describe -m "Description"`, work the same before and after setup. The environment checks report
a `ui.editor` note when the guard is absent. Revert the opt-in setting with
`jj config unset --repo ui.editor`.

## Mutation testing

Coverage answers "was this line executed". The mutation tier answers the harder question: **would the
tests notice if it were wrong?** It rewrites one instruction in the built `ProcessKit.dll` — a `<`
into a `<=`, a `+` into a `-`, a constant into the next one along — re-runs a small hermetic slice of
the suite, and records whether anything failed. A mutant nothing noticed is a limit the suite executes
but does not pin.

Run it locally — the whole catalog as a single shard, about twelve minutes for the 132 mutants in
scope today, inside the default fifteen minute budget:

```sh
pwsh ./scripts/mutate.ps1
```

That is the entire catalog because sharding is explicit: `-ShardCount` is `1` unless you pass it, so
what you measure locally is the same population the score is defined over. Taking a slice is a
deliberate act — `-ShardIndex 1 -ShardCount 4` runs a quarter of it, which is how the CI matrix runs
(and a report from one such shard is knowingly partial: the gate will say so rather than score it).
The other useful switches are `-TimeBudgetSeconds` to bound the loop, `-SkipBuild` to reuse the
current build output, and `-RetryTimeouts` when a machine is genuinely overloaded. Every parameter is
documented in the script's own help. The report lands in
`artifacts/mutation/shard-<n>/mutation-report.json`.

### Why an in-repo engine and not Stryker.NET

**Stryker.NET cannot mutate F#, and this was established by running it, not by reading its docs.**
Against this repository, `dotnet-stryker` 4.16.0 fails from both directions:

- pointed at the F# test project, it aborts during analysis with
  `System.FormatException: Commandline could not be parsed` — its Buildalyzer-based analysis only
  knows how to parse a `csc` command line, and an `.fsproj` hands it an `fsc` one;
- pointed at the C# test project instead (`tests/ProcessKit.CSharp.Tests`, which does analyse
  cleanly), it reports `Analyzing 0 projects` / `No project found` and exits — it cannot treat an
  `.fsproj` as a source project it could mutate at all.

That is structural, not a configuration mistake: Stryker mutates C# **syntax trees** through Roslyn.
No maintained .NET alternative covers F# either — the IL-level mutation testers (Testura.Mutation,
NinjaTurtles, VisualMutator) are abandoned and .NET Framework-era, and Fettle is Roslyn/C# like
Stryker.

So the engine is [`tests/ProcessKit.Mutation`](tests/ProcessKit.Mutation), four F# files over
[Mono.Cecil](https://github.com/jbevain/cecil). Its operators are defined over **CIL**, which is
what makes the tier possible here at all: the compiled IL of an F# assembly can be rewritten like
any other. It is deliberately kept **out of `ProcessKit.slnx`** — the same treatment as
`docs/snippets/DocSnippets.slnx` — so the ordinary CI jobs never restore, build or pack it.

The engine has two stateless verbs, `list` (catalog the mutants) and `apply` (produce one mutated
assembly); `scripts/mutate.ps1` owns the loop, the schedule and the classification.

### How a mutant is judged

| Verdict | Meaning | Counts as |
|---|---|---|
| Killed | the slice failed, having executed at least one test | detected |
| Timeout | no verdict inside the per-mutant budget — a mutated loop condition really can be unbounded, and a suite that hangs on it has noticed | detected |
| Survived | the slice passed | **not** detected |
| Errored | the mutated assembly did not load, or the run exited non-zero without executing a single test | excluded from both sides |

`score = (killed + timeout) / (killed + timeout + survived)`. Errored mutants are excluded rather
than folded in either direction — nothing was measured about them — and are reported separately, so an
engine regression that quietly errors everything is visible instead of hidden in a denominator.

**Sharding and the budget.** The engine shuffles the catalog deterministically, seeded from the
committed baseline, and the driver splits it by `index % ShardCount`. The shuffle is what makes a
budget-truncated shard a representative sample rather than "the alphabetically first types", and the
seed is what keeps it reproducible. A shard that stops on its budget reports `budgetExhausted`, and
the gate refuses to compare a partial run with a baseline recorded from a complete one.

**Retry controller.** Killed and Survived come from a deterministic, hermetic slice, so they are final
and never retried. Errored is infrastructure-shaped and is retried. Timeout is **not** retried by
default: it is the correct verdict for a whole class of mutants, and retrying it only re-pays an
already generous budget — the first full local run spent about 1000 of its 1300 seconds re-running six
mutants that were always going to time out.

### What is out of scope, and why

The scope is the critical boundary core, listed in [`mutation-baseline.json`](mutation-baseline.json):
the retained-output buffers (`Pump.LineBuffer`, `Pump.RawBuffer`), the retry backoff math (`Backoff`,
`RetryDelayPolicy`), the resource-limit boundaries (`CgroupCpuMax`), the line-splitting rules
(`LineTerminator`, `LineTerminatorRules`) and the buffer policies. Everything else is excluded:

- **Compiler-generated closure and async state-machine types** (`excludeGeneratedClosureTypes`, F#
  names them with an `@`). Their logic is asynchronous orchestration, whose mutants are decided by
  timing and yield Timeout verdicts and flaky kills rather than statements about assertion strength.
  Their names also embed the source *line* they were generated at, so an individual exclusion entry
  would silently stop matching the moment the file above it grew — a structural rule cannot drift that
  way. Extending the tier to the async layer means flipping this flag and re-recording the baseline.
- **Compiler-generated methods** (`excludeCompilerGeneratedMethods`): the structural
  `Equals`/`GetHashCode`/`CompareTo` members, and — the reason this rule exists — the union case
  testers (`LineTerminator.IsLf`, …). F# *inlines* those at every F# call site, so their emitted bodies
  never run and their mutants are unkillable from F#. Measured, not assumed: eight such mutants
  survived a full run whose boundary test asserts the entire case/predicate matrix.
- **Static initialisers** (`.cctor`, by name): mutating module init breaks everything at once, which
  measures nothing about any individual assertion.
- **Equivalent mutants.** The mutants that survive today are overwhelmingly *equivalent* ones — a
  mutation whose behaviour is genuinely indistinguishable, so no test can kill it. They cluster in
  three shapes: saturation guards (`min total (int64 Int32.MaxValue)`, where `>=` and `>` differ only
  at exactly `Int32.MaxValue`), the `true` constant a short-circuiting `||` pushes (still truthy when
  bumped), and no-op boundary shifts in the raw-byte eviction loop (`available <= over` versus `<`,
  which copies the same bytes either way). They are why the score has a ceiling well below 100 % and
  why `minimumScore` is a ratchet rather than a target.

### The policy: a ratchet that never blocks ordinary CI

[`.github/workflows/mutation.yml`](.github/workflows/mutation.yml) runs **weekly and on demand only** —
never on `pull_request` or `push`. A mutation regression therefore fails *that* workflow, loudly and
reviewably, and cannot slow down or block an ordinary CI run. Each shard uploads its
`mutation-report.json`; the `summary` job merges them and appends a table of the surviving mutants,
with source file and line, to the job summary.

Like the coverage ratchet, the gate reports **skipped** rather than failing whenever it cannot compare
honestly. This is written against a failure mode this repository has already lived through — coverage
collected empty on every matrix leg for months, where reading the absent percentage as `0.00 %` would
have announced a collapse that never happened. So each "nothing was measured" shape is its own skip
state: a shard whose *unmutated* baseline run was red or executed no tests; a scope that produced no
mutants (a renamed type empties it silently); a shard that aborted part way through, or never reported
at all (each shard holds a slice of the catalog, so a lost one changes the population the score is
computed over); a partial run; a sample below `minimumMutantsForVerdict`; and — the one that would
otherwise pass as a real, terrible score — a run in which **no** mutant was detected at all over a
large enough sample, which is the signature of a harness whose test host never loaded the mutated
assembly. Division is guarded on the denominator, not on the report being well formed.

One skip state is not about an *empty* measurement but a **smaller** one, and it is the only defence
against the failure this tier would otherwise share with the coverage incident: a catalog that no
longer matches `expectedCatalogMutants`. Every guard above triggers when nothing at all was measured;
none of them notices when a *part* of `scope.includeTypes` stops matching — a type renamed during an
ordinary refactor drops out of the scope in silence, all shards still report `ok`, and the score is
computed, honestly and meaninglessly, over a program that is missing a module. So the baseline pins
the size of the population its score was recorded over, the gate compares the catalog it actually
measured against that pin (within `catalogTolerancePercent`), and a mismatch skips the comparison with
a message saying which way it moved. `scripts/mutate.ps1` prints the same warning as soon as it builds
the catalog, because the refactor that causes this happens locally, long before the weekly run. Arming
`minimumScore` without pinning `expectedCatalogMutants` is refused outright (exit 2): a score without
its population is not a threshold, it is a number.

The tier is immune to the mechanism behind that coverage incident, and this was checked rather than
asserted: with `ContinuousIntegrationBuild=true` and `DeterministicSourcePaths=true` genuinely active
(confirmed by querying the MSBuild properties, since the trigger is `GITHUB_ACTIONS=true`), the
catalog is **identical** — same 132 mutants, same ids — to the ordinary build's, because mutants are
addressed by IL metadata and never by source path. Source locations are decoration: they survive the
deterministic build (the engine normalizes SourceLink's `/_/…` prefix back to a repo-relative path),
and stripping the PDB entirely still yields the identical catalog with empty source fields.

### Moving the baseline

`minimumScore` ships as `null`, which makes the gate **record** the observed score instead of gating
on it. That is the supported way to bootstrap: arm it from a **complete** shard matrix run in CI, not
from a local one, because a partial or single-machine run measured something else.

1. Take `Mutation score` **and** `Mutants in catalog` from the **Mutation tier** table in the `summary`
   job of a run where no shard reported `budgetExhausted` or a skip state.
2. Set `minimumScore` and `expectedCatalogMutants` in `mutation-baseline.json` to those two numbers —
   both, always, because one without the other is a threshold with no population — and refresh
   `recordedOn` / `recordedFrom`.
3. Say in the pull request why it moved. Raising it after adding boundary tests locks the gain in;
   lowering it states on the record that this change trades assertion strength away on purpose.

**Any change to the size of the catalog invalidates the baseline**, not only a widening of
`scope.includeTypes`: narrowing it, renaming a scoped type, and changing the scoped code enough to add
or remove branches all move the population the score is computed over. Record both numbers again from
a run of the new scope, in the same change — `catalogTolerancePercent` (10 % today) exists to absorb
the ordinary edit, not to let a module go missing. Scope, population and score live in one file
precisely so that stays one reviewable diff.

#### Before this baseline can be armed

**Open as of 2026-08-13.** `minimumScore` must stay `null` — and this section must stay here — until
the tier has been shown to produce data *in CI*, verified from real artifacts rather than from a local
run. The reason is specific rather than procedural: every "nothing could be measured" state of this
tier exits 0 by design, so a green weekly workflow is not evidence that anything was measured, and the
mechanism this repository has already been bitten by (`ContinuousIntegrationBuild` plus deterministic
source paths silently emptying coverage instrumentation, for months, on every leg) was invisible
locally by construction. What was checked before this shipped is that the *catalog* is identical under
those settings and even with the PDB removed; what is still unchecked is that a shard on an
`ubuntu-latest` runner — different OS, different build, real timings — evaluates mutants at all.

Verification is a `gh run download` of the first `Mutation tier` run (`workflow_dispatch` on the
published commit), and it passes only when **every** shard artifact satisfies all of:

- `status == "ok"`;
- `catalogTotal > 0`, and equal to `expectedCatalogMutants` in this repository's baseline;
- `evaluated > 0`;
- `counts.killed > 0`;
- `budgetExhausted == false`;

and the `summary` job's step summary shows an actual `Mutation score` row rather than any skip state.
Anything less is a tier that is green without measuring, which is the incident this whole design is
written against. Only once that holds are `minimumScore` and `recordedFrom` filled in from that run —
see the steps above — and this section deleted.

The boundary tests written to kill specific survivors live in
[`tests/ProcessKit.Tests/MutationBoundaryTests.fs`](tests/ProcessKit.Tests/MutationBoundaryTests.fs),
each naming the mutant it kills. Loosening one of those assertions is a regression of this tier, not a
tidy-up. The gate itself is
[`scripts/check-mutation-report.ps1`](scripts/check-mutation-report.ps1); its exit codes match the
coverage ratchet's — 0 for honoured or skipped, 1 for a real regression, 2 for a broken baseline or a
missing report.

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
