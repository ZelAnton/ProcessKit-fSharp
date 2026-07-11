# Testing your code

[‹ docs index](README.md)

Code that shells out is miserable to test — unless the subprocess sits behind a
seam. In ProcessKit that seam is one small interface, `IProcessRunner`. It is
*both* the dependency-injection point (production code depends on the interface,
not on a concrete spawner) and the test seam (a test hands the same code a
subprocess-free double). The default real implementation is `JobRunner` — each
run lands in a fresh kill-on-dispose group; everything in this guide swaps it for
a double so your tests never touch the operating system.

> The subprocess-free doubles ship in a **separate `ProcessKit.Testing` NuGet package**, kept out
> of the runtime `ProcessKit` package so its on-disk/JSON record-replay surface never enters your
> production dependency graph. Add a `ProcessKit.Testing` package reference to your test project;
> the types stay in the `ProcessKit.Testing` namespace.

**F#**

```fsharp
// One interface, three primitives — each takes a CancellationToken:
type IProcessRunner =
    abstract CaptureStringAsync: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<ProcessResult<string>, ProcessError>>
    abstract CaptureBytesAsync: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<ProcessResult<byte[]>, ProcessError>>
    abstract SpawnAsync: Command * System.Threading.CancellationToken -> System.Threading.Tasks.Task<Result<RunningProcess, ProcessError>>
```

**C#**

```csharp
// One interface, three primitives — each takes a CancellationToken:
public interface IProcessRunner
{
    Task<FSharpResult<ProcessResult<string>, ProcessError>> CaptureStringAsync(Command command, CancellationToken cancellationToken);
    Task<FSharpResult<ProcessResult<byte[]>, ProcessError>> CaptureBytesAsync(Command command, CancellationToken cancellationToken);
    Task<FSharpResult<RunningProcess, ProcessError>> SpawnAsync(Command command, CancellationToken cancellationToken);
}
```

Unlike some interfaces, none of the three is defaulted: a hand-rolled
`IProcessRunner` implements all three (the doubles that ship with ProcessKit do
this for you). `CaptureStringAsync` and `CaptureBytesAsync` are the bulk primitives;
`SpawnAsync` returns a live handle for streaming and probes. These are deliberately
*named apart* from the consuming verbs (`OutputStringAsync`/`RunAsync`/`StartAsync`/…):
the verbs layer on top of the primitives — applying the command's `Retry` policy and
the success/parse semantics — so you implement only the three primitives and get the
whole verb vocabulary for free. (Calling a verb routes through these primitives; for a
single raw capture with no retry, call `CaptureStringAsync` directly.)

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
| `Runner.run` | trimmed `string`, success required | `CaptureStringAsync` |
| `Runner.runUnit` | `unit`, success required | `CaptureStringAsync` |
| `Runner.outputString` | `ProcessResult<string>` (exit code is data) | `CaptureStringAsync` |
| `Runner.outputBytes` | `ProcessResult<byte[]>` | `CaptureBytesAsync` |
| `Runner.exitCode` | `int` | `CaptureStringAsync` |
| `Runner.probe` | `bool` (exit 0 → `true`, 1 → `false`) | `CaptureStringAsync` |
| `Runner.parse runner ct parser command` | `'T`, success required | `CaptureStringAsync` |
| `Runner.tryParse runner ct parser command` | `'T` (parser may fail) | `CaptureStringAsync` |
| `Runner.firstLine runner ct predicate command` | `string option` | `SpawnAsync` |
| `Runner.start` | `RunningProcess` | `SpawnAsync` |

Everything in the first eight rows reaches a child only through `CaptureStringAsync`
(or `CaptureBytesAsync`), so it runs hermetically against the subprocess-free doubles
below. The last two — `firstLine` and `start` — need a live handle and go through
`SpawnAsync`; both `ScriptedRunner` and `RecordReplayRunner` serve it (a `RecordReplayRunner`
reconstructs a live handle from the recording), so streaming and readiness code replays too —
see [what the doubles don't cover](#what-the-doubles-dont-cover).

Production code, generic over the runner:

**F#**

```fsharp
/// HEAD's commit id, run through whatever runner the caller injects.
let head (runner: IProcessRunner) (ct: CancellationToken) =
    Runner.run runner ct (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ])
```

**C#**

```csharp
/// HEAD's commit id, run through whatever runner the caller injects.
Task<FSharpResult<string, ProcessError>> Head(IProcessRunner runner, CancellationToken ct) =>
    runner.RunAsync(new Command("git").Args(["rev-parse", "HEAD"]), ct);
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

**F#**

```fsharp
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

**C#**

```csharp
var runner = new ScriptedRunner()
    // Match when every listed token appears among the command's program and
    // arguments (order-independent):
    .On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123\n"))
    // …or by any predicate over the whole Command:
    .When(cmd => cmd.WorkingDirectory is not null, Reply.Fail(128, "fatal: not a git repository"))
    // …with an optional catch-all:
    .Fallback(Reply.Ok(""));
```

The pieces:

- **`Reply.Ok stdout`** — exit 0 with that stdout. **`Reply.Fail(code, stderr)`**
  — a non-zero exit with that stderr. **`Reply.Exit code`** — an explicit exit
  code with empty stdout/stderr. **`Reply.Signalled n`** — terminated by
  signal `n` (**`Reply.Signalled ()`** when the number is unavailable).
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

**F#**

```fsharp
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

**C#**

```csharp
[TestFixture]
public class GitTests
{
    [Test]
    public async Task Head_returns_the_trimmed_sha()
    {
        var runner = new ScriptedRunner()
            .On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123\n"));

        switch (await Head(runner, CancellationToken.None))
        {
            case { IsOk: true, ResultValue: var sha }:  Assert.That(sha, Is.EqualTo("abc123")); break;
            case { IsOk: false, ErrorValue: var err }: Assert.Fail(err.Message); break;
        }
    }
}
```

A `Reply.Fail` behaves differently depending on the verb that consumes it — the
same honest-result rule as a real run. Through `Runner.run` / `Runner.runUnit`
(success required) a non-zero exit becomes `Error(ProcessError.Exit …)`; through
`Runner.outputString` it stays `Ok` with `result.IsSuccess = false` and the code
in `result.Code`:

**F#**

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

**C#**

```csharp
var runner = new ScriptedRunner().Fallback(Reply.Fail(2, "boom"));
var grep = new Command("grep").Args(["needle", "file"]);

// Success-requiring verb: the non-zero exit surfaces as an error.
if (await runner.RunAsync(grep) is { IsOk: false, ErrorValue: { IsExit: true } }) { } // program="grep", code=2, stderr="boom"

// Honest-result verb: the non-zero exit is data.
switch (await runner.OutputStringAsync(grep))
{
    case { IsOk: true, ResultValue: var output }: Assert.That(output.IsSuccess, Is.False); break;
    case { IsOk: false, ErrorValue: var err }:   Assert.Fail(err.Message); break;
}
```

## Custom doubles and mocking frameworks

`IProcessRunner` is a plain interface, so **any** .NET mocking framework (Moq,
NSubstitute, FakeItEasy) can stand in for it — handy when the *interaction* is
what you want to assert (was `StartAsync` called? with which command?) or when you
want to return specific `Error` outcomes. The error cases are easy: every
`ProcessError` case has a public constructor, so a double can return
`Error(ProcessError.NotFound("git", None))`, `Error(ProcessError.Io "...")`, and
so on directly.

Returning a `ProcessResult` is almost as easy. Its constructor is internal, but
the `ProcessResult` *test factories* build one directly:
`ProcessResult.Success(stdout)` for a clean exit, `ProcessResult.Failure(stdout, stderr, exitCode)`
for a non-zero exit, and `ProcessResult.Create(stdout, stderr, outcome, duration)` for full control
over the `Outcome` (e.g. `Outcome.TimedOut`). The captured-stdout type is inferred — C# writes
`ProcessResult.Success("out")`, F# writes `ProcessResult.Success "out"` — and the result behaves
like a real one (`IsSuccess`, `EnsureSuccess`, `Code`, …), so they double as fixtures for any code
that *consumes* a `ProcessResult`:

**F#**

```fsharp
let fixedSha: IProcessRunner =
    { new IProcessRunner with
        member _.CaptureStringAsync(_, _) = task { return Ok(ProcessResult.Success "abc123\n") }
        member _.CaptureBytesAsync(_, _) = task { return Ok(ProcessResult.Success [| 1uy; 2uy |]) }
        member _.SpawnAsync(_, _) =
            task { return Error(ProcessError.Unsupported "no streaming in this double") } }
```

**C#**

```csharp
public sealed class FixedSha : IProcessRunner
{
    public Task<FSharpResult<ProcessResult<string>, ProcessError>> CaptureStringAsync(Command command, CancellationToken ct) =>
        Task.FromResult(FSharpResult<ProcessResult<string>, ProcessError>.NewOk(ProcessResult.Success("abc123\n")));

    public Task<FSharpResult<ProcessResult<byte[]>, ProcessError>> CaptureBytesAsync(Command command, CancellationToken ct) =>
        Task.FromResult(FSharpResult<ProcessResult<byte[]>, ProcessError>.NewOk(ProcessResult.Success(new byte[] { 1, 2 })));

    public Task<FSharpResult<RunningProcess, ProcessError>> SpawnAsync(Command command, CancellationToken ct) =>
        Task.FromResult(FSharpResult<RunningProcess, ProcessError>.NewError(ProcessError.NewUnsupported("no streaming in this double")));
}
```

For canned successes wired through a matcher, `ScriptedRunner` is still the most
convenient seam (it builds the result for you). Doubles can also **delegate**
their success path to an inner runner. A custom `IProcessRunner` written as an
object expression — implementing all three methods — composes cleanly. This one
injects a single transient failure before delegating, so you can test that retry
handling actually retries:

**F#**

```fsharp
let failOnce (inner: IProcessRunner) : IProcessRunner =
    let mutable calls = 0

    { new IProcessRunner with
        member _.CaptureStringAsync(command, ct) =
            task {
                calls <- calls + 1

                if calls = 1 then
                    return Error(ProcessError.Io "transient blip") // ProcessError.isTransient -> true
                else
                    return! inner.CaptureStringAsync(command, ct)
            }

        member _.CaptureBytesAsync(command, ct) = inner.CaptureBytesAsync(command, ct)
        member _.SpawnAsync(command, ct) = inner.SpawnAsync(command, ct) }
```

**C#**

```csharp
public sealed class FailOnce(IProcessRunner inner) : IProcessRunner
{
    private int calls;

    public Task<FSharpResult<ProcessResult<string>, ProcessError>> CaptureStringAsync(Command command, CancellationToken ct) =>
        ++calls == 1
            ? Task.FromResult(
                FSharpResult<ProcessResult<string>, ProcessError>.NewError(ProcessError.NewIo("transient blip"))) // ProcessError.isTransient -> true
            : inner.CaptureStringAsync(command, ct);

    public Task<FSharpResult<ProcessResult<byte[]>, ProcessError>> CaptureBytesAsync(Command command, CancellationToken ct) =>
        inner.CaptureBytesAsync(command, ct);

    public Task<FSharpResult<RunningProcess, ProcessError>> SpawnAsync(Command command, CancellationToken ct) =>
        inner.SpawnAsync(command, ct);
}
```

Wrap a `ScriptedRunner` with it and drive a retrying verb to prove the retry
fires — because retry lives in the verb layer over the `CaptureStringAsync` primitive,
`failOnce`'s single transient error is retried away. If a double is bulk-only and you
want spawning to be a hard error, return
`Error(ProcessError.Unsupported "no streaming in this double")` from `SpawnAsync`.

## What the doubles don't cover

The subprocess-free doubles center on the **bulk** primitives. `ScriptedRunner` and
[`RecordReplayRunner`](#record-and-replay) both implement `CaptureStringAsync` and
`CaptureBytesAsync`, and both serve a [`FakeProcess`](#scripting-replies) from `SpawnAsync`
(so the parts of the live surface a fake can replay — `StdoutLinesAsync`, the readiness probes —
*are* testable through them). The one gap is **recording** a live stream: a
`RecordReplayRunner` in record mode returns `Error(ProcessError.Unsupported …)` from
`SpawnAsync`, because a live stream can't be captured without racing the consumer — record a
streaming call through a capture verb, then replay it as a stream. The full live
[`RunningProcess`](streaming.md) surface (`WaitForPortAsync`, `TakeStdin`, `ProfileAsync`, …)
and a [`Pipeline`](pipelines.md) are best tested against a real (possibly trivial) child
process; keep the scripted/cassette doubles for everything that flows through the capture
primitives.

## Record and replay

`RecordReplayRunner` (also in `ProcessKit.Testing`) closes the loop: record real
runs to a JSON *cassette* once, then replay them deterministically — fast,
hermetic, no subprocess in CI.

**F#**

```fsharp
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

**C#**

```csharp
// Record once against the real tool (wraps a real runner), then save:
var recorder = RecordReplayRunner.Record("fixtures/git.json", new JobRunner());
await recorder.RunAsync(new Command("git").Arg("--version"), CancellationToken.None);
recorder.Save(); // Result<unit, ProcessError> — surfaces write errors

// Replay everywhere else — no subprocess, identical results:
var replayResult = RecordReplayRunner.Replay("fixtures/git.json");
if (replayResult is { IsOk: true, ResultValue: var replay })
{
    // the recorded stdout, replayed
    if (await replay.RunAsync(new Command("git").Arg("--version"), CancellationToken.None) is { IsOk: false, ErrorValue: var err })
        Console.Error.WriteLine(err.Message);
}
else if (replayResult is { IsOk: false, ErrorValue: var loadErr })
    Console.Error.WriteLine(loadErr.Message);
```

`Record(path, inner)` wraps `inner` and captures each completed call; `Save()`
writes the cassette (it is also flushed best-effort on dispose —
`RecordReplayRunner` is `IDisposable` — but `Save()` is the call that surfaces a
write error). `Replay(path)` returns a `Result<RecordReplayRunner, ProcessError>`
loaded from the file.

Semantics worth knowing before you commit a cassette:

| Aspect | Behaviour |
|---|---|
| Match key | program + args + a stdin **source digest** (plus whether stdin was present). In-memory bytes hash their content; a `Stdin.FromFile` source hashes its path (opt into hashing its **contents** with `RecordReplayOptions.WithFileStdinContentHashing`). The working directory does **not** participate by default — a cassette recorded in one `cwd` still replays from another — opt in with `RecordReplayOptions.WithCwdMatching()` |
| Environment | now part of the match key through a redacting **fingerprint** of the effective environment — the `EnvClear` flag plus the net effect of the `Env`/`EnvRemove` overrides (removals and last-write-wins included; env-name case is insensitive on Windows, sensitive on POSIX), while repeated/no-op overrides with the same final effect still match. Override **values never reach the file** — only the variable names and a versioned SHA-256 fingerprint — so env secrets can't leak, yet a call with a different value, name, removal, or `EnvClear` no longer replays an unrelated recording |
| Miss | an unmatched call is `ProcessError.CassetteMiss` (distinct from a missing program) — replay never spawns a surprise subprocess; a stale cassette fails loudly |
| Duplicates of one key | replay in capture order, then the **last entry repeats** — a recorded before/after sequence replays faithfully, while retry/probe loops keep getting a stable final answer |
| Bytes | `CaptureBytesAsync` / `outputBytes` is supported: a **bytes recording** stores the exact stdout bytes (base64) and replays them byte-for-byte, including non-UTF-8 output. A **text** recording (or a pre-v2 cassette) replayed through the bytes verb is honestly `ProcessError.Unsupported` — it never hands back a lossy re-encode — so re-record that call through the bytes verb |
| `SpawnAsync` | **replay** reconstructs a live handle ([`FakeProcess`](#scripting-replies)) from the recording, so `StdoutLinesAsync` / readiness probes / exit replay too. **Record** mode can't capture a live stream (it would race the consumer) and returns `Unsupported` — record the call through a capture verb, then replay it as a stream |
| Fidelity | for the **capture** verbs, a recording's **truncation** flag and wall-clock **duration** survive replay, so `ProcessResult.Truncated` / `Duration` read true on replay (not a synthetic `false` / `0`). Streaming replay (`SpawnAsync`) reconstructs the recorded lines and outcome; its duration is measured live and truncation is not replayed |
| Err results | not recorded — only completed runs (a non-zero exit and a captured timeout *are* results and are recorded) |
| One-shot stdin | `Stdin.FromStream` / `FromLines` / `FromAsyncLines` can't be keyed without consuming them, so recording or replaying such a call errors |
| Format | a versioned JSON envelope — `{ "Version", "Entries" }` (current version **3**); a cassette **newer** than this build understands is rejected on load, while an older compatible one (a v1/v2 cassette) still loads (missing fields default — a pre-v3 entry with no env fingerprint keys as the default, un-customized environment). A partial/crafted entry (omitted fields) is normalized so replay can't trip on a missing value |

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

**Grow a cassette on miss (VCR "new episodes").** `RecordReplayRunner.Auto(path, inner)`
replays what the cassette already holds and, on a **miss**, delegates to `inner`, records the
result, and grows the file on `Save()`/dispose — so you build a cassette up incrementally
instead of curating every entry by hand. Existing entries still replay hermetically; only a
first-seen call reaches the real tool. A missing (or empty) file starts a fresh cassette. Use
strict `Replay(path)` in CI, where a miss should fail loudly. (Like record mode, `Auto` can't
capture a *streaming* miss — record such a call through a capture verb first.)

**Matching customization & redaction (`RecordReplayOptions`).** Pass an immutable, fluent
`RecordReplayOptions` to `Record` / `Replay` / `Auto` (use the **same** options on both sides,
since they change how invocations are keyed):

- `WithFileStdinContentHashing()` — key a `Stdin.FromFile` source by its **contents** (a SHA-256
  of the bytes) instead of its path, so a cassette matches on what was actually fed to the child
  (and matches a `Stdin.FromBytes` of the same bytes). Opt-in: the file must exist at record and
  replay time, and an unreadable file surfaces `ProcessError.Stdin`.
- `WithArgNormalizer(args -> args)` — normalize the argument list before matching, so a volatile
  argument (a temp directory, a nonce) no longer defeats the match — drop it, or rewrite it to a
  stable placeholder. The **raw** arguments are still stored verbatim for inspection.
- `WithRedaction(text -> text)` — scrub captured **text** before it is written, so a secret
  echoed to stdout/stderr never reaches disk. Applied at record time to a string capture's
  stdout/stderr and a bytes capture's stderr; a `byte[]` stdout capture is stored opaquely
  (base64) and is not passed through the redactor.
- `WithCwdMatching()` — restore the working directory (`Command.CurrentDir`) as part of the match
  key, so two otherwise-identical invocations that ran in different directories are treated as
  distinct recordings. `CassetteEntry.Cwd` always stores the working directory verbatim for
  inspection regardless of this setting; only its participation in *matching* is opt-in.

```csharp
var options = new RecordReplayOptions()
    .WithArgNormalizer(args => args.Where(a => !a.StartsWith("/tmp/")).ToArray())
    .WithRedaction(text => text.Replace(token, "[REDACTED]"));

// Auto (like Replay) returns a Result — it can fail to load an existing cassette.
if (RecordReplayRunner.Auto("fixtures/git.json", new JobRunner(), options) is { IsOk: true, ResultValue: var recorder })
{
    // recorder replays a hit, records a miss, and grows the cassette on Save()...
}
```

## CliClient

`CliClient` is the foundation for a typed wrapper around an external tool
(`git`, `gh`, `kubectl`, …): it owns the program name, per-client defaults, and
the runner, so your wrapper contributes only the commands and the parsers — and
because the runner is injectable, the wrapper tests hermetically with a
`ScriptedRunner`.

Create one with `CliClient.create name` (or `CliClient(name)`) and configure it,
each call returning a new client:

- `.WithDefaults(configure)` — apply shared defaults with the **full** `Command`
  builder, e.g. `client.WithDefaults(fun c -> c.CurrentDir(repo).Timeout(ts).Env("K", "V"))`
  (timeout, working directory, environment, encoding, ok-codes, retry, logger, …)
- `.WithRunner(runner)` — run every command through `runner` instead of the
  default `JobRunner` (this is the test seam)

`.Command(args)` builds a configured `Command` without running it (the template's
defaults applied), and `.RunAsync(args)` / `.OutputStringAsync(args)` / `.OutputBytesAsync(args)`
(plus `ExitCodeAsync`/`ProbeAsync`/`ParseAsync`/…) build and run through the client's runner.
`.EnsureAvailableAsync()` is a preflight check — "is the client's program installed?" — with no
spawn (see [Preflight: is a program installed?](commands.md#preflight-is-a-program-installed)); it
is always **local**, never delegated to `.WithRunner`'s runner, so a `ScriptedRunner` injected for
the wrapper's own tests has no bearing on it.

**F#**

```fsharp
/// A small typed git wrapper. The CliClient is supplied, so tests inject a double.
type Git(client: CliClient) =

    /// HEAD's commit id (trimmed stdout, success required).
    member _.Head(repo: string) =
        client.RunAsync [ "-C"; repo; "rev-parse"; "HEAD" ]

    /// Is the work tree clean? The exit code *is* the answer, so probe it.
    member _.IsClean(repo: string) =
        client.ProbeAsync [ "-C"; repo; "diff"; "--quiet" ]
```

**C#**

```csharp
/// A small typed git wrapper. The CliClient is supplied, so tests inject a double.
public class Git(CliClient client)
{
    /// HEAD's commit id (trimmed stdout, success required).
    public Task<FSharpResult<string, ProcessError>> Head(string repo) =>
        client.RunAsync(["-C", repo, "rev-parse", "HEAD"]);

    /// Is the work tree clean? The exit code *is* the answer, so probe it.
    public Task<FSharpResult<bool, ProcessError>> IsClean(string repo) =>
        client.ProbeAsync(["-C", repo, "diff", "--quiet"]);
}
```

Production wires the real runner and the per-client defaults:

**F#**

```fsharp
let git = Git((CliClient.create "git").WithDefaults(fun c -> c.Timeout(TimeSpan.FromSeconds 30.0)))
```

**C#**

```csharp
var git = new Git(new CliClient("git").WithDefaults(c => c.Timeout(TimeSpan.FromSeconds(30))));
```

…and the wrapper tests against a scripted runner, no subprocess:

**F#**

```fsharp
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

**C#**

```csharp
[TestFixture]
public class GitWrapperTests
{
    [Test]
    public async Task Head_is_trimmed()
    {
        var scripted = new ScriptedRunner()
            .On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123\n"));

        var git = new Git(new CliClient("git").WithRunner(scripted));

        switch (await git.Head("/repo"))
        {
            case { IsOk: true, ResultValue: var sha }:  Assert.That(sha, Is.EqualTo("abc123")); break;
            case { IsOk: false, ErrorValue: var err }: Assert.Fail(err.Message); break;
        }
    }
}
```

…or against a [cassette](#record-and-replay) recorded from the real tool once.

## Dependency injection

The separate `ProcessKit.Extensions.DependencyInjection` package wires the seam
into `Microsoft.Extensions.DependencyInjection`. `AddProcessKit()` registers an
`IProcessRunner` in the container — logger-aware when the container already has
an `ILoggerFactory`, so runs emit ProcessKit's lifecycle events with no extra
wiring. (See the [Dependency injection guide](dependency-injection.md) for
configured defaults, keyed per-tool clients, and a shared container-managed group.)

**F#**

```fsharp
services.AddProcessKit() |> ignore

// Consumers depend on the interface — the same seam you test against:
type Deployer(runner: IProcessRunner) =
    member _.Deploy() =
        Runner.run runner CancellationToken.None (Command.create "deploy")
```

**C#**

```csharp
services.AddProcessKit();

// Consumers depend on the interface — the same seam you test against:
public class Deployer(IProcessRunner runner)
{
    public Task<FSharpResult<string, ProcessError>> Deploy() =>
        runner.RunAsync(new Command("deploy"), CancellationToken.None);
}
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
