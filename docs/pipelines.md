# Pipelines

[Previous: Overview](./)

Build `a → b → c` **without a shell**. Each stage's stdout is wired straight into the
next stage's stdin by an in-process relay — there is no shell string anywhere, so there
are no quoting rules, no word splitting, and no injection surface. Every stage spawns
into one shared kill-on-dispose [process group](process-groups.md), so the whole chain
lives and dies as a unit: tear the chain down (a timeout, a cancellation, an early
return) and every stage goes with it.

The relay is a copy loop, not a kernel splice. When a consumer exits early it closes the
upstream read end, so the producer stops on a *broken pipe* — its next write fails once
the relay's downstream is gone. On POSIX the OS may deliver that as `SIGPIPE`; Windows
has no `SIGPIPE`, so there it surfaces as a failed write instead. See
[Unchecked stages](#unchecked-stages) for why that distinction matters.

The samples below run inside a `task { }` block and use `match!`; from C# the same
surface is `await`-able fluent methods.

- [Building a pipeline](#building-a-pipeline)
- [The verbs](#the-verbs)
- [Pipefail: the result and the ends](#pipefail-the-result-and-the-ends)
- [Fail-loud output overflow](#fail-loud-output-overflow)
- [Unchecked stages](#unchecked-stages)
- [Timeouts and cancellation](#timeouts-and-cancellation)
- [Re-running a pipeline](#re-running-a-pipeline)

## Building a pipeline

There are two equivalent ways to wire stages together; pick whichever reads better in
context. There is **no `|` operator** — F# reserves `|` for patterns and active
patterns — so the fluent `.Pipe` method and the `Pipeline` module are the only two ways
to build a chain.

The fluent way: `Command.Pipe(next)` turns a `Command` into a `Pipeline`, and chaining
`.Pipe(...)` again appends another stage. Finish with a verb.

**F#**

```fsharp
task {
    // git log --format=%an | sort | uniq -c
    let pipeline =
        (Command.create "git" |> Command.args [ "log"; "--format=%an" ])
            .Pipe(Command.create "sort")
            .Pipe(Command.create "uniq" |> Command.arg "-c")

    match! pipeline.RunAsync() with
    | Ok authors -> printfn $"{authors}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// git log --format=%an | sort | uniq -c
var pipeline = new Command("git").Args(["log", "--format=%an"])
    .Pipe(new Command("sort"))
    .Pipe(new Command("uniq").Arg("-c"));

Console.WriteLine(await pipeline.RunAsync() switch
{
    { IsOk: true, ResultValue: var authors } => authors,
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

The pipe-style module mirror builds the same value: `Pipeline.create first second` seeds
a two-stage chain, and `Pipeline.pipe next pipeline` appends a stage — so it threads
naturally through `|>`:

**F#**

```fsharp
task {
    let pipeline =
        Pipeline.create
            (Command.create "git" |> Command.args [ "log"; "--format=%an" ])
            (Command.create "sort")
        |> Pipeline.pipe (Command.create "uniq" |> Command.arg "-c")

    match! pipeline.OutputStringAsync() with
    | Ok result -> printfn $"{result.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var pipeline = new Command("git").Args(["log", "--format=%an"])
    .Pipe(new Command("sort"))
    .Pipe(new Command("uniq").Arg("-c"));

Console.WriteLine(await pipeline.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => result.Stdout,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

`Pipeline.timeout` and `Pipeline.cancelOn` round out the module mirror; they correspond
to the fluent `.Timeout` and `.CancelOn` covered under
[Timeouts and cancellation](#timeouts-and-cancellation). Building a pipeline spawns
nothing — a `Pipeline` is an immutable value, and each builder call returns a new one.
Nothing runs until you call a verb.

## The verbs

A `Pipeline` finishes with the same verb vocabulary as a `Command`; each one folds the
whole chain's outcome (see [pipefail](#pipefail-the-result-and-the-ends) below) and
returns `Task<Result<_, ProcessError>>`:

| Verb | On success you get | A failing stage is… |
|---|---|---|
| `RunAsync()` | trimmed final `string` | …raised as the first unclean checked stage's `ProcessError.Exit` |
| `RunUnitAsync()` | `unit` | …same success rule; the output is discarded |
| `OutputStringAsync()` | `ProcessResult<string>` | …folded into the result (code / stderr / program of the first unclean stage); never an `Error` on its own |
| `OutputBytesAsync()` | `ProcessResult<byte[]>` | …same, with the last stage's stdout captured raw — for binary pipes |
| `ExitCodeAsync()` | `int` | …its attributed code (a timed-out / signalled chain errors rather than inventing a number) |
| `ProbeAsync()` | `bool` | exit `0` → `true`, `1` → `false`, anything else → `Error` |
| `ParseAsync(f)` / `TryParseAsync(f)` | `'T` | …raised as that stage's `ProcessError.Exit`; `ParseAsync` requires success |

Every verb also accepts an optional `CancellationToken` — `pipeline.RunAsync(token)`,
`pipeline.OutputStringAsync(token)`, and so on — for a per-call token alongside the
chain-level [`CancelOn`](#timeouts-and-cancellation).

An `Error` from a capture verb such as `OutputStringAsync` means a stage couldn't be *started
or driven* at all — a spawn failure, a not-found program, broken plumbing — **never** a
mere non-zero exit. A non-zero exit is data in the `ProcessResult`.

There is deliberately no streaming verb and no `FirstLineAsync` on a `Pipeline`: a chain
consumes its last stage in full to fold the pipefail outcome, so there is no live handle
to stream from. To *capture* the first matching line of a finished chain, append a
`head -n 1` (POSIX) / `findstr` (Windows) stage and capture its stdout. If you instead
need a streaming readiness probe over a chain that must keep running — wait for a banner
line, then leave it alive — a `head` stage would tear it down; reach for a single
`Command` with `WaitForLineAsync` instead (see [streaming.md](streaming.md)).

## Pipefail: the result and the ends

The outcome follows shell **pipefail** (`set -o pipefail`):

- **stdout** is always the **last** stage's output — that is what the chain produced.
- **code**, **stderr**, and the reported **program** come from the **rightmost** stage that
  did not finish successfully — a code outside *its* accepted `OkCodes` (just `0` unless
  widened), a signal kill, or a timeout — or from the last stage when every stage succeeded.

So when an inner stage fails, the result's stdout is whatever the tail still printed,
while the diagnostics point at the culprit:

**F#**

```fsharp
task {
    let pipeline =
        (Command.create "cat" |> Command.arg "data.txt")
            .Pipe(Command.create "grep" |> Command.arg "ERROR") // suppose grep exits 2 (bad regex)
            .Pipe(Command.create "wc" |> Command.arg "-l")

    match! pipeline.OutputStringAsync() with
    | Ok result ->
        // Blame points at grep — the rightmost unclean stage — while Stdout is whatever wc managed.
        printfn $"code={result.Code} program={result.Program} success={result.IsSuccess}"
        // code=Some 2  program=grep  success=false
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var pipeline = new Command("cat").Arg("data.txt")
    .Pipe(new Command("grep").Arg("ERROR")) // suppose grep exits 2 (bad regex)
    .Pipe(new Command("wc").Arg("-l"));

Console.WriteLine(await pipeline.OutputStringAsync() switch
{
    // Blame points at grep — the rightmost unclean stage — while Stdout is whatever wc managed.
    { IsOk: true, ResultValue: var result } => $"code={result.Code} program={result.Program} success={result.IsSuccess}", // code=Some 2  program=grep  success=false
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

The success-requiring verbs turn that same pipefail outcome into a typed error attributed
to the blamed stage. `ProcessResult.ensureSuccess` does it explicitly, and `RunAsync` does it
for you:

**F#**

```fsharp
match! pipeline.RunAsync() with
| Ok out -> printfn $"{out}"
| Error(ProcessError.Exit(program, code, _, stderr)) ->
    eprintfn $"{program} exited {code}: {stderr}" // program = "grep", code = 2
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
Console.WriteLine(await pipeline.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => output,
    { IsOk: false, ErrorValue: ProcessError.Exit { Program: var p, Code: var c, Stderr: var s } } => $"{p} exited {c}: {s}", // program = "grep", code = 2
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

The two ends of the chain behave like a single `Command`:

- The **first** stage's configured [`Stdin`](commands.md) source is honored — feed the
  whole pipeline from a string, bytes, a file, or a stream. A `Stdin` source on any **later**
  stage is a configuration error — that stage's stdin is always rewired to the previous stage's
  stdout — so `.Pipe` rejects it with an `ArgumentException` naming the offending stage.
- A `KeepStdinOpen` on a stage has no effect inside a chain: a pipeline exposes no live stdin
  handle to write into, and the relay wires each later stage's stdin itself.
- Every stage's **stdout** is wired into a pipe — feeding the next stage's stdin, or captured
  at the end — so a `Stdout` mode of `Null` / `Inherit` set on a stage is overridden to keep the
  chain connected.
- Every stage's **stderr** is captured per-stage for pipefail diagnostics under that stage's own
  [`OutputBuffer`](commands.md#raw-byte-captures-obey-the-byte-cap-too) byte cap (`MaxBytes` +
  `Overflow`); only the last stage's stdout reaches you. A fail-loud (`OverflowMode.Error`) overflow
  is honoured on **any** stage's stderr, not only the final stdout — see
  [Fail-loud output overflow](#fail-loud-output-overflow) below.
- [`Command.MergeStderr`](commands.md#merging-stderr-into-stdout-21) (a shell `2>&1`) is allowed
  only on the **last** stage — its stdout is the pipeline's captured output, so merging there
  captures the final stage's combined stdout+stderr. On an earlier stage it is rejected
  (`ArgumentException`) the moment another stage is appended after it: the chain wires each stage's
  stdout into the next stage's stdin, so an OS-level merge on an intermediate stage would inject its
  stderr into the downstream stage's input data.
- A per-stage `Timeout`, `Retry`, or `CancelOn` is rejected when the stage is piped (see
  [Timeouts and cancellation](#timeouts-and-cancellation)); a per-stage `Logger` or `StreamBuffer`
  has no effect inside a chain — observe or bound an individual command by running it on its own.
- The **last** stage's [`OutputBuffer`](commands.md#raw-byte-captures-obey-the-byte-cap-too) byte
  cap (`MaxBytes` + `Overflow`) bounds the captured **stdout** — the same way a single command's
  `OutputBytesAsync` does (`Error` -> `OutputTooLarge`, `DropOldest`/`DropNewest` -> a tail/head with
  `Truncated` set). Its `MaxLines`, and every intermediate stage's **stdout** buffer policy, do not
  apply (an intermediate stdout is plumbing into the next stage, not a capture). Each stage's
  **stderr** cap, by contrast, applies on every stage — see
  [Fail-loud output overflow](#fail-loud-output-overflow).

**F#**

```fsharp
task {
    let uniqueCount =
        (Command.create "sort" |> Command.stdin (Stdin.FromString "b\na\nb\nc\n"))
            .Pipe(Command.create "uniq")
            .Pipe(Command.create "wc" |> Command.arg "-l")

    match! uniqueCount.RunAsync() with
    | Ok n -> printfn $"{n}" // "3"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var uniqueCount = new Command("sort").Stdin(Stdin.FromString("b\na\nb\nc\n"))
    .Pipe(new Command("uniq"))
    .Pipe(new Command("wc").Arg("-l"));

Console.WriteLine(await uniqueCount.RunAsync() switch
{
    { IsOk: true, ResultValue: var n }    => n, // "3"
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

## Fail-loud output overflow

Every captured stream in a chain obeys **its own stage's**
[`OutputBuffer`](commands.md#raw-byte-captures-obey-the-byte-cap-too) byte cap (`MaxBytes` +
`Overflow`) — not only the final stdout. Two kinds of capture are subject to a cap:

- the **last** stage's **stdout** — the pipeline's captured output, and
- **every** stage's **stderr** — drained per-stage for diagnostics, bounded so a chatty stage can
  never exhaust memory regardless of its position in the chain.

Under `OverflowMode.Error` a cap is **fail-loud**: once the stream exceeds it, the verb returns
`ProcessError.OutputTooLarge` — naming the offending stage's program and *its* configured caps —
exactly like a single command's byte capture, and consistently whether the overflow is on the final
stdout or on any stage's stderr (an intermediate stage's stderr fail-loud overflow is no longer
silently dropped). Under `DropOldest` / `DropNewest` the same overflow stays **lossy but
non-erroring** (a bounded tail/head with `Truncated` set), for stderr just as for stdout.

When more than one stream overflows at once — several stages, and/or a stage's stderr together with
the final stdout — one deterministic error is chosen by
**first-offending-stage-in-pipeline-order**: the **leftmost** stage in the chain wins (the earliest
point the chain overflowed), and within a single stage its captured **stdout** (only the last stage
has one) is preferred over its **stderr**. So an overflow on an earlier stage's stderr outranks the
final stdout's, while the pre-existing "only the final stdout overflowed" case is reported exactly as
before (same program, limits, and totals).

## Unchecked stages

Strict pipefail has one classic false positive: a consumer that legitimately stops
reading early. In `producer | head -1` the consumer exits `0` after one line and closes
the pipe; the producer then dies on a **broken pipe** — its next write fails once the
relay's downstream is gone (a failed write on Windows, possibly delivered as `SIGPIPE` on
POSIX). That is a perfectly normal death, but strict pipefail would blame the chain for
it. Mark the producer `Command.uncheckedInPipe` (fluent: `.UncheckedInPipe()`) and
pipefail skips it:

**F#**

```fsharp
task {
    // seq 1 1000000 | head -n 1 — the producer's broken-pipe death is expected.
    let first =
        (Command.create "seq" |> Command.args [ "1"; "1000000" ] |> Command.uncheckedInPipe)
            .Pipe(Command.create "head" |> Command.args [ "-n"; "1" ])

    match! first.RunAsync() with
    | Ok line -> printfn $"{line}" // "1"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// seq 1 1000000 | head -n 1 — the producer's broken-pipe death is expected.
var first = new Command("seq").Args(["1", "1000000"]).UncheckedInPipe()
    .Pipe(new Command("head").Args(["-n", "1"]));

Console.WriteLine(await first.RunAsync() switch
{
    { IsOk: true, ResultValue: var line } => line, // "1"
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

The rules:

- An unchecked stage's unclean exit — a non-zero code, or a broken-pipe death from a
  consumer that closed early (a failed write on Windows; `SIGPIPE` on POSIX where the OS
  delivers it) — is **skipped** when the chain decides what to report.
- A **checked** failure always trumps an unchecked one, regardless of position:
  `uncheckedInPipe` never shields another stage's real failure.
- A chain whose only failures are unchecked reports **success** — the last stage's stdout
  and code `0`.
- `uncheckedInPipe` forgives exit *status* only — never a whole-chain
  [`Pipeline.Timeout`](#timeouts-and-cancellation) — and it has no effect on a `Command`
  run outside a pipeline, where a single run's status is already plain data in its
  `ProcessResult`.

## Timeouts and cancellation

A pipeline is bounded as a **whole chain** — there is no per-stage timeout:

**F#**

```fsharp
task {
    let pipeline =
        (Command.create "producer")
            .Pipe(Command.create "consumer")
            .Timeout(TimeSpan.FromSeconds 30.0) // whole-CHAIN deadline

    match! pipeline.OutputStringAsync() with
    | Ok result -> printfn $"timedOut={result.IsTimedOut}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var pipeline = new Command("producer")
    .Pipe(new Command("consumer"))
    .Timeout(TimeSpan.FromSeconds(30)); // whole-CHAIN deadline

Console.WriteLine(await pipeline.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => $"timedOut={result.IsTimedOut}",
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

- **`Pipeline.Timeout`** (module mirror: `Pipeline.timeout`) bounds the **whole chain**:
  at the deadline the shared group is torn down and the run reports the timeout — on
  `OutputStringAsync` as `IsTimedOut`, on `RunAsync` as an `Error`. Unlike a single command's
  *captured* timeout, there is no salvaged partial stdout to read back.
- A per-stage **`Command.Timeout`** cannot bound one stage of a chain — a pipeline spawns its
  stages directly, so a stage's own deadline never fires. Setting one is a configuration error, so
  `.Pipe` rejects it with an `ArgumentException`. Bound the whole chain with `Pipeline.Timeout`, or
  run the stage as a standalone `Command` when it needs its own deadline.
- A per-stage **`Command.Retry`** is rejected the same way — retry is a verb-layer mechanism and
  pipeline stages are spawned directly, bypassing it. Retry the pipeline as a whole instead.

Cancellation has two forms:

- **`Pipeline.CancelOn(token)`** (module mirror: `Pipeline.cancelOn`) is the chain-level
  control: the token is applied to every stage, so firing it tears the whole chain down
  and the run resolves to `ProcessError.Cancelled`.
- Each verb's optional **`CancellationToken`** (`pipeline.RunAsync(token)`) ties a single call
  to a token without baking it into the pipeline.

A per-stage **`Command.CancelOn`** cannot cancel one stage of a chain — a pipeline spawns its
stages directly, so a stage's own token is a verb-layer mechanism the spawn bypasses and never
fires. Setting one is a configuration error, so `.Pipe` rejects it with an `ArgumentException`.
Cancel the whole chain with the chain-level `Pipeline.CancelOn` (or pass a token to the verb)
instead. For the full model — captured vs. raised deadlines, and how cancellation differs from a
timeout — see [timeouts-and-cancellation.md](timeouts-and-cancellation.md).

## Re-running a pipeline

A `Pipeline` is an immutable value: building it spawns nothing, and each verb call drives
the chain afresh, so you can hold one and run it more than once. The one caveat is
inherited from `Command` — when a chain runs repeatedly, feed the first stage from a
**reusable** stdin source (`Stdin.FromString` / `Stdin.FromBytes` / `Stdin.FromFile`)
rather than a stream you can only read once. See [commands.md](commands.md) for the full
set of stdin sources and their semantics.

---

Next: [Timeouts, retries & cancellation](timeouts-and-cancellation.md)
