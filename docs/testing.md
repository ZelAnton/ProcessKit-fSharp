# Testing your code

[‹ docs index](README.md)

Code that shells out is miserable to test — unless the subprocess sits behind a
seam. In ProcessKit that seam is one small interface, `IProcessRunner`. It is
*both* the dependency-injection point (production code depends on the interface,
not on a concrete spawner) and the test seam (a test hands the same code a
subprocess-free double). The default real implementation is `JobRunner` — each
run lands in a fresh kill-on-dispose group; everything in this guide swaps it for
a double so your tests never touch the operating system.

```fsharp
open ProcessKit

// One interface, three methods — each takes a CancellationToken:
type IProcessRunner =
    abstract OutputString: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<ProcessResult<string>, ProcessError>>
    abstract OutputBytes: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<ProcessResult<byte[]>, ProcessError>>
    abstract Start: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<RunningProcess, ProcessError>>
```

Unlike some interfaces, none of the three is defaulted: a hand-rolled
`IProcessRunner` implements all three (the doubles that ship with ProcessKit do
this for you). `OutputString` and `OutputBytes` are the bulk verbs; `Start`
returns a live handle for streaming and probes.

- [The `IProcessRunner` seam](#the-iprocessrunner-seam)
- [Scripting replies](#scripting-replies)
- [Custom doubles and mocking frameworks](#custom-doubles-and-mocking-frameworks)
- [What the doubles don't cover](#what-the-doubles-dont-cover)
- [Record and replay](#record-and-replay)
- [CliClient](#cliclient)
- [Dependency injection](#dependency-injection)

## The `IProcessRunner` seam

Write production code against `IProcessRunner` and let the caller supply the
runner. In production that runner is a `JobRunner` (or a [`ProcessGroup`](process-groups.md),
which is *itself* an `IProcessRunner`, so every run lands in one shared
kill-on-dispose group); in a test it is a double.

You rarely call the interface's three methods directly — the `Runner` module
gives every runner the full verb vocabulary, each verb taking
`(runner, cancellationToken, command)`:

| Verb | Returns | Routes through |
|---|---|---|
| `Runner.run` | trimmed `string`, success required | `OutputString` |
| `Runner.runUnit` | `unit`, success required | `OutputString` |
| `Runner.outputString` | `ProcessResult<string>` (exit code is data) | `OutputString` |
| `Runner.outputBytes` | `ProcessResult<byte[]>` | `OutputBytes` |
| `Runner.exitCode` | `int` | `OutputString` |
| `Runner.probe` | `bool` (exit 0 → `true`, 1 → `false`) | `OutputString` |
| `Runner.parse runner ct parser command` | `'T`, success required | `OutputString` |
| `Runner.tryParse runner ct parser command` | `'T` (parser may fail) | `OutputString` |
| `Runner.firstLine runner ct predicate command` | `string option` | `Start` |
| `Runner.start` | `RunningProcess` | `Start` |

Everything in the first eight rows reaches a child only through `OutputString`
(or `OutputBytes`), so it runs hermetically against the subprocess-free doubles
below. The last two — `firstLine` and `start` — need a live handle and go through
`Start`; see [what the doubles don't cover](#what-the-doubles-dont-cover).

Production code, generic over the runner:

```fsharp
open ProcessKit
open System.Threading

/// HEAD's commit id, run through whatever runner the caller injects.
let head (runner: IProcessRunner) (ct: CancellationToken) =
    Runner.run runner ct (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ])
```

In production you pass `JobRunner()`; in a test you pass a double and no process
spawns. The retry policy ([`Command.retry`](timeouts-and-cancellation.md)) is
applied by the `Runner` verbs, so a double exercises your retry handling without
a subprocess too.

## Scripting replies

`ScriptedRunner` (in the `ProcessKit.Testing` namespace) is the work-horse
double: it returns canned `Reply`s for matched commands. It is immutable and
fluent — `On` / `When` add rules and `Fallback` sets a catch-all, each returning
a new runner:

```fsharp
open ProcessKit
open ProcessKit.Testing

let runner =
    (ScriptedRunner())
        // Match when every listed token appears among the command's program and
        // arguments (order-independent):
        .On([ "git"; "rev-parse"; "HEAD" ], Reply.Ok "abc123\n")
        // …or by any predicate over the whole Command:
        .When((fun cmd -> cmd.WorkingDirectory.IsSome), Reply.Fail(128, "fatal: not a git repository"))
        // …with an optional catch-all:
        .Fallback(Reply.Ok "")
```

The pieces:

- **`Reply.Ok stdout`** — exit 0 with that stdout. **`Reply.Fail(code, stderr)`**
  — a non-zero exit with that stderr. **`Reply.Exit code`** — an explicit exit
  code with empty stdout/stderr. **`Reply.Signalled (Some n)`** — terminated by
  signal `n` (`Reply.Signalled None` when the number is unavailable).
- **`.WithStdout text`** / **`.WithStderr text`** refine any reply — e.g.
  `Reply.Fail(1, "merge failed").WithStdout("CONFLICT in app.fs")` to model a
  tool that writes to both streams.
- **Rule order matters: first match wins.** `On([ "git"; "rev-parse"; "HEAD" ])`
  matches any command whose program plus arguments contain all three tokens —
  it is a subset test, not a positional prefix.
- **No matching rule and no `Fallback` throws** — a missing stub fails the test
  loudly rather than silently returning a default, so an unexpected invocation
  can't slip through.
- A scripted reply respects the command's [`OkCodes`](commands.md): the
  `ProcessResult` it produces carries the command's accepted-codes, so
  `IsSuccess` and the success-requiring verbs honour them.

A test (any framework — the doubles depend on none; this repo's fixtures are
NUnit):

```fsharp
open NUnit.Framework
open System.Threading
open ProcessKit
open ProcessKit.Testing

[<TestFixture>]
type GitTests() =

    [<Test>]
    member _.``head returns the trimmed sha``() =
        task {
            let runner =
                (ScriptedRunner())
                    .On([ "git"; "rev-parse"; "HEAD" ], Reply.Ok "abc123\n")

            match! head runner CancellationToken.None with
            | Ok sha -> Assert.That(sha, Is.EqualTo "abc123")
            | Error err -> Assert.Fail err.Message
        }
```

A `Reply.Fail` behaves differently depending on the verb that consumes it — the
same honest-result rule as a real run. Through `Runner.run` / `Runner.runUnit`
(success required) a non-zero exit becomes `Error(ProcessError.Exit …)`; through
`Runner.outputString` it stays `Ok` with `result.IsSuccess = false` and the code
in `result.Code`:

```fsharp
task {
    let runner = (ScriptedRunner()).Fallback(Reply.Fail(2, "boom"))
    let grep = Command.create "grep" |> Command.args [ "needle"; "file" ]

    // Success-requiring verb: the non-zero exit surfaces as an error.
    match! Runner.run runner CancellationToken.None grep with
    | Error(ProcessError.Exit(program, code, _, stderr)) -> () // program="grep", code=2, stderr="boom"
    | _ -> ()

    // Honest-result verb: the non-zero exit is data.
    match! Runner.outputString runner CancellationToken.None grep with
    | Ok result -> Assert.That(result.IsSuccess, Is.False)
    | Error err -> Assert.Fail err.Message
}
```

## Custom doubles and mocking frameworks

`IProcessRunner` is a plain interface, so **any** .NET mocking framework (Moq,
NSubstitute, FakeItEasy) can stand in for it — handy when the *interaction* is
what you want to assert (was `Start` called? with which command?) or when you
want to return specific `Error` outcomes. The error cases are easy: every
`ProcessError` case has a public constructor, so a double can return
`Error(ProcessError.NotFound("git", None))`, `Error(ProcessError.Io "...")`, and
so on directly.

Returning a **successful** `ProcessResult`, though, is the library's job: its
constructor is internal, so you can't fabricate one by hand. For canned
successes use `ScriptedRunner` (it builds the result for you), and have any other
double **delegate** its success path to an inner runner. A custom
`IProcessRunner` written as an object expression — implementing all three
methods — composes cleanly. This one injects a single transient failure before
delegating, so you can test that retry handling actually retries:

```fsharp
open ProcessKit
open System.Threading.Tasks

let failOnce (inner: IProcessRunner) : IProcessRunner =
    let mutable calls = 0

    { new IProcessRunner with
        member _.OutputString(command, ct) =
            task {
                calls <- calls + 1

                if calls = 1 then
                    return Error(ProcessError.Io "transient blip") // ProcessError.isTransient -> true
                else
                    return! inner.OutputString(command, ct)
            }

        member _.OutputBytes(command, ct) = inner.OutputBytes(command, ct)
        member _.Start(command, ct) = inner.Start(command, ct) }
```

Wrap a `ScriptedRunner` with it and drive a retrying verb to prove the retry
fires. If a double is bulk-only and you want `Start` to be a hard error, return
`Error(ProcessError.Unsupported "no streaming in this double")` from `Start` —
exactly what the shipped doubles do.

## What the doubles don't cover

The subprocess-free doubles serve the **bulk** verbs only. `ScriptedRunner` and
[`RecordReplayRunner`](#record-and-replay) both implement `OutputString` /
`OutputBytes` and return `Error(ProcessError.Unsupported …)` from `Start`.
Because `Runner.start` and `Runner.firstLine` (and the matching `Command`
verbs) route through `Start`, they are not served by these doubles — nor is the
live [`RunningProcess`](streaming.md) surface (`StdoutLines`, `WaitForLine`,
`WaitForPort`, `TakeStdin`, `Profile`) or a [`Pipeline`](pipelines.md). Test
those paths against a real (possibly trivial) child process, then keep the
scripted/cassette doubles for everything that flows through `OutputString`.

## Record and replay

`RecordReplayRunner` (also in `ProcessKit.Testing`) closes the loop: record real
runs to a JSON *cassette* once, then replay them deterministically — fast,
hermetic, no subprocess in CI.

```fsharp
open ProcessKit
open ProcessKit.Testing
open System.Threading

task {
    // Record once against the real tool (wraps a real runner), then save:
    let recorder = RecordReplayRunner.Record("fixtures/git.json", JobRunner())
    let! _ = Runner.run recorder CancellationToken.None (Command.create "git" |> Command.arg "--version")
    recorder.Save() |> ignore // Result<unit, ProcessError> — surfaces write errors

    // Replay everywhere else — no subprocess, identical results:
    match RecordReplayRunner.Replay "fixtures/git.json" with
    | Ok replay ->
        match! Runner.run replay CancellationToken.None (Command.create "git" |> Command.arg "--version") with
        | Ok version -> () // the recorded stdout, replayed
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

`Record(path, inner)` wraps `inner` and captures each completed call; `Save()`
writes the cassette (it is also flushed best-effort on dispose —
`RecordReplayRunner` is `IDisposable` — but `Save()` is the call that surfaces a
write error). `Replay(path)` returns a `Result<RecordReplayRunner, ProcessError>`
loaded from the file.

Semantics worth knowing before you commit a cassette:

| Aspect | Behaviour |
|---|---|
| Match key | program + args + cwd + a stdin **source digest** (plus whether stdin was present). In-memory bytes hash their content; a `Stdin.FromFile` source hashes its path |
| Environment | override **values never reach the file** — only the variable names are stored, so env secrets can't leak, and env is not part of the match key |
| Miss | an unmatched call is `ProcessError.CassetteMiss` (distinct from a missing program) — replay never spawns a surprise subprocess; a stale cassette fails loudly |
| Duplicates of one key | replay in capture order, then the **last entry repeats** — a recorded before/after sequence replays faithfully, while retry/probe loops keep getting a stable final answer |
| Bytes | `OutputBytes` replays by round-tripping the recorded stdout through UTF-8 |
| `Start` | unsupported — replay serves the bulk verbs only (see [above](#what-the-doubles-dont-cover)) |
| Err results | not recorded — only completed runs (a non-zero exit and a captured timeout *are* results and are recorded) |
| One-shot stdin | `Stdin.FromReader` / `FromLines` / `FromAsyncLines` can't be keyed without consuming them, so recording or replaying such a call errors |
| Format | a versioned JSON envelope — `{ "Version", "Entries" }`; a cassette whose format version this build doesn't understand is rejected on load, and a partial/crafted entry (omitted fields) is normalized so replay can't trip on a missing value |

Only env *values* are redacted. `program`, `args`, `stdout`, and `stderr` are
stored **verbatim** and can carry secrets (a `--password=…` flag, a token echoed
to output), so review a fixture before committing it. On Unix the file is written
**atomically and owner-only** (`0600` from creation — a temp file renamed into
place, so it is never briefly world-readable); on Windows it inherits the
containing directory's ACL, so keep secret-bearing fixtures out of world-readable
directories.

A neat trick: in tests, record against a `ScriptedRunner` instead of
`JobRunner()` — the whole record → save → replay round trip is then itself
hermetic.

## CliClient

`CliClient` is the foundation for a typed wrapper around an external tool
(`git`, `gh`, `kubectl`, …): it owns the program name, per-client defaults, and
the runner, so your wrapper contributes only the commands and the parsers — and
because the runner is injectable, the wrapper tests hermetically with a
`ScriptedRunner`.

Create one with `CliClient.create name` (or `CliClient(name)`) and configure
shared defaults, each returning a new client:

- `.DefaultTimeout(timeSpan)`
- `.DefaultEnv(key, value)` / `.DefaultEnvRemove(key)`
- `.DefaultCurrentDir(directory)`
- `.DefaultCancelOn(token)`
- `.WithRunner(runner)` — run every command through `runner` instead of the
  default `JobRunner` (this is the test seam)

`.Command(args)` and `.CommandIn(directory, args)` build a configured `Command`
without running it (defaults applied), and `.Run(args)` / `.OutputString(args)` /
`.OutputBytes(args)` build and run. For the other verbs, reach the configured
command plus the client's `.Runner`:

```fsharp
open ProcessKit
open System.Threading

/// A small typed git wrapper. The CliClient is supplied, so tests inject a double.
type Git(client: CliClient) =

    /// HEAD's commit id (trimmed stdout, success required).
    member _.Head(repo: string) =
        client.Run [ "-C"; repo; "rev-parse"; "HEAD" ]

    /// Is the work tree clean? The exit code *is* the answer, so probe it
    /// through the client's runner and a configured Command.
    member _.IsClean(repo: string) =
        Runner.probe client.Runner CancellationToken.None (client.CommandIn(repo, [ "diff"; "--quiet" ]))
```

Production wires the real runner and the per-client defaults:

```fsharp
open System

let git = Git((CliClient.create "git").DefaultTimeout(TimeSpan.FromSeconds 30.0))
```

…and the wrapper tests against a scripted runner, no subprocess:

```fsharp
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

[<TestFixture>]
type GitWrapperTests() =

    [<Test>]
    member _.``Head is trimmed``() =
        task {
            let scripted =
                (ScriptedRunner())
                    .On([ "git"; "rev-parse"; "HEAD" ], Reply.Ok "abc123\n")

            let git = Git((CliClient.create "git").WithRunner scripted)

            match! git.Head "/repo" with
            | Ok sha -> Assert.That(sha, Is.EqualTo "abc123")
            | Error err -> Assert.Fail err.Message
        }
```

…or against a [cassette](#record-and-replay) recorded from the real tool once.

## Dependency injection

The separate `ProcessKit.Extensions.DependencyInjection` package wires the seam
into `Microsoft.Extensions.DependencyInjection`. `AddProcessKit()` registers an
`IProcessRunner` in the container — logger-aware when the container already has
an `ILoggerFactory`, so runs emit ProcessKit's lifecycle events with no extra
wiring:

```fsharp
open Microsoft.Extensions.DependencyInjection
open ProcessKit
open ProcessKit.Extensions.DependencyInjection
open System.Threading

services.AddProcessKit() |> ignore

// Consumers depend on the interface — the same seam you test against:
type Deployer(runner: IProcessRunner) =
    member _.Deploy() =
        Runner.run runner CancellationToken.None (Command.create "deploy")
```

`AddProcessKit` registers via `TryAdd`, so a pre-existing `IProcessRunner` is
left intact: to substitute a double in an integration test, register your
`ScriptedRunner` (or `RecordReplayRunner`) **before** calling `AddProcessKit`,
and the real runner backs off. In a plain unit test you usually skip the
container entirely and construct `Deployer(scriptedRunner)` directly — the whole
point of depending on the interface.

---

Next: [Platform support](platform-support.md) ·
[Supervision](supervision.md) ·
[Running commands](commands.md)
