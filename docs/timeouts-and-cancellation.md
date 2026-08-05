# Timeouts, retries & cancellation

[Previous: Overview](./)

Three ways a run can end early, with three different philosophies:

- a **timeout** is *data* — the deadline was part of the run's contract, so its
  expiry is captured in the result, and only the success-checking verbs turn it
  into an error;
- a **retry** is a *policy* — the verbs replay the run while your classifier says
  the failure is worth another attempt;
- a **cancellation** is an *abandonment* — the caller changed its mind, so every
  path reports an error and there is no result worth inspecting.

The samples below run inside a `task { }` block and use `match!` / `let!`; from
C# the same surface is `await`-able fluent methods. Every builder method has a
pipe-friendly `Command.*` mirror (`Command.timeout`, `Command.retry`,
`Command.cancelOn`), shown alongside the fluent form.

- [Timeouts](#timeouts)
- [Graceful timeout](#graceful-timeout)
- [Idle timeout](#idle-timeout)
- [Captured vs raised: the decision table](#captured-vs-raised-the-decision-table)
- [Retries](#retries)
- [Cancellation](#cancellation)
- [Pipelines and clients](#pipelines-and-clients)
- [Precedence and interactions](#precedence-and-interactions)

## Timeouts

`Command.Timeout(duration)` (mirror: `Command.timeout`) kills the **whole process
tree** at the deadline — not just the direct child, so a wrapper script's
grandchildren die too. The run's `Outcome` becomes `Outcome.TimedOut`.

**F#**

```fsharp
task {
    // Captured: a non-zero exit / timeout is data on the capture verbs.
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 5.0)

    match! cmd.OutputStringAsync() with
    | Ok result when result.IsTimedOut ->
        // Code is None on a timeout; the partial output captured before the kill is kept.
        printfn $"timed out; partial stdout before the kill: {result.Stdout}"
    | Ok result -> printfn $"exited {result.Code}: {result.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// Captured: a non-zero exit / timeout is data on the capture verbs.
var cmd = new Command("slow-tool")
    .Timeout(TimeSpan.FromSeconds(5));

Console.WriteLine(await cmd.OutputStringAsync() switch
{
    // Code is None on a timeout; the partial output captured before the kill is kept.
    { IsOk: true, ResultValue: { IsTimedOut: true } result } => $"timed out; partial stdout before the kill: {result.Stdout}",
    { IsOk: true, ResultValue: var result }                  => $"exited {result.Code}: {result.Stdout}",
    { IsOk: false, ErrorValue: var err }                    => err.Message,
});
```

The same command finished with a success-checking verb raises the deadline as a
typed error instead:

**F#**

```fsharp
task {
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 5.0)

    match! cmd.RunAsync() with
    | Ok stdout -> printfn $"{stdout}"
    | Error(ProcessError.Timeout(program, timeout, _, _)) ->
        eprintfn $"{program} exceeded {timeout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("slow-tool")
    .Timeout(TimeSpan.FromSeconds(5));

Console.WriteLine(await cmd.RunAsync() switch
{
    { IsOk: true, ResultValue: var stdout } => stdout,
    { IsOk: false, ErrorValue: ProcessError.Timeout { Program: var p, Timeout: var t } } => $"{p} exceeded {t}",
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

`ProcessError.Timeout(program, timeout, stdout, stderr)` carries the partial
stdout/stderr captured before the kill — a hung tool's last words are still
available on the error, not discarded.

> **Two distinct deadline families — keep them apart.** `Command.Timeout` is the
> *run's own contract* (this guide): it kills the tree. The readiness probes'
> `within` parameter (`WaitForLineAsync` / `WaitForPortAsync` / `WaitForAsync`, see
> [streaming.md](streaming.md)) is a different deadline: it gives
> `ProcessError.NotReady` and **never kills the child** — the caller decides what
> happens next.

## Graceful timeout

By default the deadline **hard-kills** the tree at once. Add
`Command.TimeoutGrace(grace)` (mirror: `Command.timeoutGrace`) to give the tree a
chance to clean up: at the deadline it is sent the command's `StopSignal` (default
`Signal.Term`), allowed up to the grace window to exit, then hard-killed — the same
soft signal → wait → hard-kill tier as
[`ProcessGroup.ShutdownAsync`](process-groups.md). A signal-handling child that exits
ends the grace early.

**F#**

```fsharp
task {
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 30.0)
        |> Command.stopSignal Signal.Usr1
        |> Command.timeoutGrace (TimeSpan.FromSeconds 5.0) // SIGUSR1, wait up to 5s, then SIGKILL

    let! _ = cmd.OutputStringAsync()
    ()
}
```

**C#**

```csharp
var cmd = new Command("slow-tool")
    .Timeout(TimeSpan.FromSeconds(30))
    .StopSignal(Signal.Usr1)
    .TimeoutGrace(TimeSpan.FromSeconds(5)); // SIGUSR1, wait up to 5s, then SIGKILL

await cmd.OutputStringAsync();
```

`IsTimedOut` is `true` regardless of whether the child exited on the signal or was
hard-killed after the grace — the deadline is what fired. Windows refuses a non-default
`StopSignal` at spawn rather than silently pretending to deliver it; the default preserves the
existing best-effort WM_CLOSE phase followed by the Job Object hard-kill.

## Idle timeout

`Command.Timeout` bounds the **total** run length. The other common failure is a run
that is still alive but *stuck* — it has stopped producing output. `Command.IdleTimeout(duration)`
(mirror: `Command.idleTimeout`) catches exactly that: it kills the tree when **neither
stdout nor stderr** produces output for `duration`. Every chunk of output **resets** the
deadline, so a run that keeps streaming stays alive; one that goes quiet is killed.

**F#**

```fsharp
task {
    // Kill the build if it stops printing for 30s, however long it runs overall.
    let cmd =
        Command.create "long-build"
        |> Command.idleTimeout (TimeSpan.FromSeconds 30.0)

    match! cmd.OutputStringAsync() with
    | Ok result when result.IsTimedOut -> eprintfn "stalled — no output for 30s"
    | Ok result -> printfn $"exited {result.Code}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// Kill the build if it stops printing for 30s, however long it runs overall.
var cmd = new Command("long-build")
    .IdleTimeout(TimeSpan.FromSeconds(30));

Console.WriteLine(await cmd.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: { IsTimedOut: true } } => "stalled — no output for 30s",
    { IsOk: true, ResultValue: var result }           => $"exited {result.Code}",
    { IsOk: false, ErrorValue: var err }              => err.Message,
});
```

Key facts:

- **Same honest result as `Timeout`.** An idle kill surfaces as `Outcome.TimedOut` — so
  `IsTimedOut` on the capture verbs and `ProcessError.Timeout` on the success-checking
  verbs, exactly like the total timeout. At the API level the two are *not* distinguished
  (both mean "killed on a deadline"); logs tell them apart (the message names an idle kill
  and reports the idle window, under the same `ProcessTimedOut` event id).
- **Byte granularity, every verb.** Activity is any output read from the child, measured in
  bytes — so a single long line without a newline still counts as active, and it works
  uniformly for the buffered capture verbs, the streaming verbs, the raw `OutputBytesAsync`,
  and even the output-discarding `WaitAsync`/`ProfileAsync`. It is independent of
  `StdoutLineCount`/`StderrLineCount`, which stay pure line counters. This requires at least
  one output stream the parent can read: a piped stdout/stderr or a PTY's merged master.
  A command whose effective destinations are only `StdioMode.Null`, `StdioMode.Inherit`, or
  direct file redirects is rejected with `ArgumentException` at the builder boundary (in
  either chaining order), because those OS-level destinations cannot report activity back to
  the idle watchdog.
- **The idle clock starts when consumption begins** (the verb's exit wait), not at some
  earlier construction, so a handle you drive later is not killed for a gap before you
  started reading.
- **Independent of `Timeout`.** Set both — each fires on its own condition, whichever comes
  first, with a single kill and a single reported outcome (no double kill). `IdleTimeout`
  honours `TimeoutGrace` (configured soft signal → grace → hard kill) exactly as `Timeout` does.
- A negative `duration` is rejected (`ArgumentOutOfRangeException`); one larger than ~24.8
  days is treated as no idle deadline (as with `Timeout`).

Like `Command.Timeout`, a per-stage `Command.IdleTimeout` cannot bound one stage of a
pipeline — a pipeline captures only the last stage's output and does not monitor per-stage
activity — so `.Pipe` rejects it with an `ArgumentException` rather than silently ignoring
it (see [Pipelines and clients](#pipelines-and-clients)).

## Captured vs raised: the decision table

The same timeout lands differently depending on the verb you finish with. The
capture verbs treat the deadline as *data*; the success-checking verbs raise it.

| Verb | A timeout deadline becomes |
|---|---|
| `OutputStringAsync()` / `OutputBytesAsync()` | `Ok` result with `IsTimedOut = true`, `Code = None`, `Outcome = Outcome.TimedOut`, partial output kept |
| `RunAsync()` / `RunUnitAsync()` | `Error (ProcessError.Timeout(program, timeout, stdout, stderr))` — partial output attached |
| `ExitCodeAsync()` | `Error (ProcessError.Timeout …)` — it will not invent a sentinel code |
| `ProbeAsync()` | `Error (ProcessError.Timeout …)` |
| `ParseAsync(f)` / `TryParseAsync(f)` | `Error (ProcessError.Timeout …)` — both require success, so the deadline is raised |
| `StartAsync()` + streaming | the stream **ends** at the deadline (tree killed, pipes closed); a following `FinishAsync()` reports `Outcome.TimedOut` |
| `ProcessResult.ensureSuccess` on a captured result | `Error (ProcessError.Timeout …)` — the same conversion `RunAsync` does for you |
| `FirstLineAsync(p)` | the stream closes at the deadline; if no line matched first, you get `Ok None` (it is not a success-checking verb) |

Streaming makes the "captured" half concrete — the deadline bounds the stream,
and the outcome is readable afterwards:

**F#**

```fsharp
task {
    let cmd =
        Command.create "chatty-job"
        |> Command.timeout (TimeSpan.FromSeconds 10.0)

    match! cmd.StartAsync() with
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"> {e.Current}"
                | false -> go <- false // the stream ends when the deadline kills the tree
        finally
            e.DisposeAsync().AsTask().Wait()

        match! proc.FinishAsync() with
        | Ok finished when finished.Outcome.IsTimedOut -> eprintfn "killed at the deadline"
        | Ok _ -> ()
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("chatty-job")
    .Timeout(TimeSpan.FromSeconds(10));

await using var proc = (await cmd.StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"> {line}"); // the stream ends when the deadline kills the tree

var finished = await proc.FinishAsync();
if (finished is { IsOk: true, ResultValue: { Outcome.IsTimedOut: true } })
    Console.Error.WriteLine("killed at the deadline");
else if (finished is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

## Retries

`Command.Retry(maxAttempts, delay, predicate)` (mirror: `Command.retry`) replays a
failed run, sleeping `delay` between tries, retrying only while `predicate`
accepts the error. The predicate is a `Func<ProcessError, bool>` (from F#, a
plain `ProcessError -> bool` through the module mirror).

For a growing delay, use
`Command.RetryBackoff(maxAttempts, baseDelay, factor, maxDelay, jitter, predicate)`
(mirror: `Command.retryBackoff`). The unjittered first retry uses `baseDelay`;
later waits grow as `baseDelay × factor^n`; those values are capped at `maxDelay`
before optional uniform jitter multiplies them by a value in `[0.5, 1.5)`. This is the same
backoff vocabulary as `Supervisor`, applied to a finite one-operation retry loop.

`maxAttempts` is the **total** number of runs (the first run plus up to
`maxAttempts - 1` retries), so `Retry 3` runs the command at most three times, and
`0`/`1` both mean a single run — a command always runs at least once.

The classifier is part of the typed result boundary. If it throws, ProcessKit stops
the retry loop and returns `Error (ProcessError.RetryPredicate(program, original, detail))`:
`original` is the failed attempt's complete typed `ProcessError`, while `detail` is
the callback exception message. No raw callback exception escapes, and no additional
attempt is started. A `RetryPredicate` error is terminal and is not itself retried.

`delay` must be zero or positive; negative values, including
`Timeout.InfiniteTimeSpan`, are rejected when the command is built. Delays beyond
the maximum interval supported by the runtime timer (about 24.8 days) are clamped
to that interval when armed. `RetryBackoff` likewise rejects negative base/cap
delays, and its factor must be finite and at least `1.0`.

Retry delays use `TimeProvider.System` by default. Tests that need to advance retry time without
sleeping can attach a deterministic provider with `Command.TimeProvider(provider)` (or
`Command.timeProvider provider`); the same provider also drives readiness deadlines and a
`Supervisor` built for that command.

**F#**

```fsharp
task {
    let cmd =
        Command.create "curl"
        |> Command.args [ "-fsS"; "https://example.com/api" ]
        |> Command.timeout (TimeSpan.FromSeconds 10.0)
        |> Command.retry
            3
            (TimeSpan.FromMilliseconds 250.0)
            (fun err ->
                // transient (spawn/I/O), a timeout, or curl's "couldn't connect" (exit 7)
                ProcessError.isTransient err
                || err.IsTimeout
                || (match err with
                    | ProcessError.Exit(_, 7, _, _) -> true
                    | _ -> false))

    match! cmd.RunAsync() with
    | Ok body -> printfn $"{body}"
    | Error err -> eprintfn $"gave up: {err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("curl")
    .Args(["-fsS", "https://example.com/api"])
    .Timeout(TimeSpan.FromSeconds(10))
    .Retry(
        3,
        TimeSpan.FromMilliseconds(250),
        err =>
            // transient (spawn/I/O), a timeout, or curl's "couldn't connect" (exit 7)
            err.IsTransient
            || err.IsTimeout
            || err is ProcessError.Exit { Code: 7 });

Console.WriteLine(await cmd.RunAsync() switch
{
    { IsOk: true, ResultValue: var body } => body,
    { IsOk: false, ErrorValue: var err } => $"gave up: {err.Message}",
});
```

The two built-in classifiers are ready to drop in as predicates:

- `ProcessError.isTransient` (from C#, `err.IsTransient`) — `true` for `Spawn` and `Io` errors
  (spawn races, transient I/O blips) that may succeed on another try.
- `ProcessError.isNotFound` (from C#, the generated `err.IsNotFound` tester) — `true` for a
  program-not-found failure (usually a reason to install-then-retry rather than to blindly replay).

**F#**

```fsharp
let cmd =
    Command.create "flaky-tool"
    |> Command.retryBackoff
        5
        (TimeSpan.FromMilliseconds 200.0)
        2.0
        (TimeSpan.FromSeconds 10.0)
        true
        ProcessError.isTransient
```

**C#**

```csharp
var cmd = new Command("flaky-tool")
    .RetryBackoff(
        5,
        TimeSpan.FromMilliseconds(200),
        2.0,
        TimeSpan.FromSeconds(10),
        true,
        err => err.IsTransient);
```

**Where retry earns its keep.** Retry replays the run whenever a verb yields an
`Error` your predicate accepts. The success-checking verbs (`RunAsync` / `RunUnitAsync` /
`ExitCodeAsync` / `ProbeAsync` / `ParseAsync` / `TryParseAsync`) are where that matters: they turn a
non-zero exit into `ProcessError.Exit` and a timeout into `ProcessError.Timeout`,
so your classifier can act on the *outcome* of the run. The capture verbs
(`OutputStringAsync` / `OutputBytesAsync`) keep a non-zero exit **and** a timeout as data —
an `Ok` result — so a retry there can only ever fire on a genuine failure-to-run
(a transient spawn or I/O error), never on an exit code or a deadline.

Two ground rules:

- The classifier sees the typed `ProcessError` — match on the case, on an exit
  code, even on the captured stderr.
- A `ProcessError.Cancelled` is effectively terminal: the built-in classifiers
  reject it, and once the run's token is cancelled the retry loop stops re-trying
  regardless — another attempt could only fail the same way.

For "keep a *service* alive whenever it exits" rather than "replay this one
operation", reach for a [supervision.md](supervision.md) `Supervisor` — the same
backoff vocabulary, a different loop condition.

## Cancellation

Hand any verb a `System.Threading.CancellationToken`; cancelling the token kills
the run's tree and makes every consuming path report `ProcessError.Cancelled`.
Every verb takes an optional `CancellationToken` (`cmd.RunAsync(token)`,
`cmd.OutputStringAsync(token)`, …):

**F#**

```fsharp
task {
    use cts = new CancellationTokenSource()
    let job = (Command.create "long-export").RunAsync(cts.Token)

    // elsewhere — a shutdown signal, a sibling failure, a UI button:
    cts.Cancel()

    match! job with
    | Error(ProcessError.Cancelled program) -> printfn $"{program} cancelled"
    | _ -> ()
}
```

**C#**

```csharp
using var cts = new CancellationTokenSource();
var job = new Command("long-export").RunAsync(cts.Token);

// elsewhere — a shutdown signal, a sibling failure, a UI button:
cts.Cancel();

if (await job is { IsOk: false, ErrorValue: ProcessError.Cancelled { Program: var p } })
    Console.WriteLine($"{p} cancelled");
```

Or tie a token to a command for its whole lifetime with `Command.CancelOn(token)`
(mirror: `Command.cancelOn`) — it is **linked in addition to** any per-verb token,
so either source cancels the run:

**F#**

```fsharp
let cmd = Command.create "long-export" |> Command.cancelOn shutdownToken
let! _ = cmd.RunAsync() // also cancels if shutdownToken fires
```

**C#**

```csharp
var cmd = new Command("long-export").CancelOn(shutdownToken);
await cmd.RunAsync(); // also cancels if shutdownToken fires
```

The contract, path by path:

For a live `RunningProcess` in a CLI wrapper, `ForwardParentSignals(gracePeriod)` is the first-class
scope for Ctrl+C/Ctrl+Break on Windows and SIGINT/SIGTERM on POSIX. It calls `StopAsync` once,
auto-unsubscribes when the child exits, and leaves output consumption to the caller.

| Situation | Behavior |
|---|---|
| Cancel during `RunAsync` / `OutputStringAsync` / `OutputBytesAsync` / `ExitCodeAsync` / `ProbeAsync` / `ParseAsync` | tree killed → `Error (ProcessError.Cancelled program)` |
| Cancel on a live handle (`StdoutLinesAsync`/`FinishAsync` after `StartAsync`) | **not tracked** — the token is checked only before the spawn, and a live handle is caller-driven. Stop the handle yourself: `Kill`/dispose for an immediate hard kill, `StopAsync(gracePeriod)` for a graceful stop, or `ForwardParentSignals(gracePeriod)` for parent console/termination signals; ProcessKit does not surface `Cancelled` for you here |
| Token already cancelled **before** the run | short-circuits before spawning — no process is ever created |
| `FirstLineAsync` mid-run | surfaces `ProcessError.Cancelled` once the token fires (not `Ok None`) |
| Under `Retry` | terminal — the built-in classifiers reject `Cancelled` and the loop stops re-trying |
| Under a [supervision.md](supervision.md) `Supervisor` | terminal — supervision returns `Cancelled` instead of restarting into a still-cancelled token. This row is about a *token*; supervision has one further, token-free producer of `Cancelled` — a session `StopAsync` that lands before the very first incarnation is started, which has neither an outcome nor a start failure to report (see [supervision.md](supervision.md#errors-and-cancellation)) |

Unlike a timeout — whose expiry is *captured* as `IsTimedOut` — a cancellation is
**always an error**: the run was abandoned, so there is no result to synthesize. A
token cancelled before the run starts short-circuits without spawning anything.

## Pipelines and clients

A whole **pipeline** has its own deadline and token, bounding the entire chain:

**F#**

```fsharp
task {
    let pipeline =
        (Command.create "producer")
            .Pipe(Command.create "consumer")
            .Timeout(TimeSpan.FromSeconds 30.0)   // whole-chain deadline (mirror: Pipeline.timeout)
            .CancelOn(shutdownToken)              // whole-chain token   (mirror: Pipeline.cancelOn)

    match! pipeline.OutputStringAsync() with
    | Ok result -> printfn $"timedOut={result.IsTimedOut}"
    | Error err -> eprintfn $"{err.Message}" // ProcessError.Cancelled when the token fires
}
```

**C#**

```csharp
var pipeline = new Command("producer")
    .Pipe(new Command("consumer"))
    .Timeout(TimeSpan.FromSeconds(30))   // whole-chain deadline (mirror: Pipeline.timeout)
    .CancelOn(shutdownToken);            // whole-chain token   (mirror: Pipeline.cancelOn)

Console.WriteLine(await pipeline.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => $"timedOut={result.IsTimedOut}",
    { IsOk: false, ErrorValue: var err }   => err.Message, // ProcessError.Cancelled when the token fires
});
```

`Pipeline.Timeout` tears the shared group down at the deadline and reports the
timeout (`IsTimedOut` on `OutputStringAsync`, `Error` on `RunAsync`) — but, unlike a single
command's *captured* timeout, there is no salvaged partial stdout to read back. A
per-stage `Command.Timeout` cannot bound one stage of a chain — a pipeline spawns its
stages directly, so a stage's own deadline never fires — so `.Pipe` rejects it with an
`ArgumentException` instead of silently ignoring it. A per-stage `Command.IdleTimeout` is
rejected the same way (a pipeline captures only the last stage's output and does not monitor
per-stage activity). See [pipelines.md](pipelines.md) for the full chain model.

A **`CliClient`** usually builds and consumes its `Command`s internally, so set
the deadline and token **once on the client** and every command it builds carries
them:

**F#**

```fsharp
let gh =
    (CliClient.create "gh")
        .WithDefaults(fun c ->
            c
                .Timeout(TimeSpan.FromSeconds 30.0)  // applied to every built command
                .CancelOn(shutdownToken))            // …controller cancels → all in-flight runs die
```

**C#**

```csharp
var gh = new CliClient("gh")
    .WithDefaults(c =>
        c
            .Timeout(TimeSpan.FromSeconds(30))  // applied to every built command
            .CancelOn(shutdownToken));          // …controller cancels → all in-flight runs die
```

Clients are cheap — scope cancellation by building **one client per cancellable
scope** with its own token instead of threading tokens through call signatures.
See [testing.md](testing.md) for the `CliClient` wrapper pattern.

## Precedence and interactions

**Timeout vs cancellation.** A timeout is *captured*; a cancellation is *always an
error*. When both land on the same run, **cancellation wins** — you asked the run
to stop mattering, so no result is synthesized and the verb reports
`ProcessError.Cancelled`, even on the capture verbs that would otherwise have
returned an `IsTimedOut` result.

**Which knob for which job:**

| You want | Reach for |
|---|---|
| "This run may not take longer than X" | `Command.Timeout` |
| "Kill it if it stops producing output" | `Command.IdleTimeout` |
| "Let it clean up before the kill" | `Command.Timeout` + `Command.TimeoutGrace` |
| "This operation is flaky, try a few times" | `Command.Retry` |
| "Stop everything when the app shuts down" | `Command.CancelOn` / a verb token + one shared token |
| "Bound a whole multi-stage chain" | `Pipeline.Timeout` / `Pipeline.CancelOn` |
| "Set a deadline/token once for a tool" | `CliClient.WithDefaults(fun c -> c.Timeout(...).CancelOn(...))` |
| "Keep this service alive across crashes" | [supervision.md](supervision.md) `Supervisor` |
| "Tell me when it's *ready*, don't kill it" | readiness probes — [streaming.md](streaming.md) |

---

Next: [Supervision](supervision.md)
