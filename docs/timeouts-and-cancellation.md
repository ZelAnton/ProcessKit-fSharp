# Timeouts, retries & cancellation

[‹ docs index](README.md)

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
- [Captured vs raised: the decision table](#captured-vs-raised-the-decision-table)
- [Retries](#retries)
- [Cancellation](#cancellation)
- [Pipelines and clients](#pipelines-and-clients)
- [Precedence and interactions](#precedence-and-interactions)

## Timeouts

`Command.Timeout(duration)` (mirror: `Command.timeout`) kills the **whole process
tree** at the deadline — not just the direct child, so a wrapper script's
grandchildren die too. The run's `Outcome` becomes `Outcome.TimedOut`.

```fsharp
open ProcessKit
open System

task {
    // Captured: a non-zero exit / timeout is data on the capture verbs.
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 5.0)

    match! cmd.OutputString() with
    | Ok result when result.IsTimedOut ->
        // Code is None on a timeout; the partial output captured before the kill is kept.
        printfn $"timed out; partial stdout before the kill: {result.Stdout}"
    | Ok result -> printfn $"exited {result.Code}: {result.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

The same command finished with a success-checking verb raises the deadline as a
typed error instead:

```fsharp
open ProcessKit
open System

task {
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 5.0)

    match! cmd.Run() with
    | Ok stdout -> printfn $"{stdout}"
    | Error(ProcessError.Timeout(program, timeout, _, _)) ->
        eprintfn $"{program} exceeded {timeout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

`ProcessError.Timeout(program, timeout, stdout, stderr)` carries the partial
stdout/stderr captured before the kill — a hung tool's last words are still
available on the error, not discarded.

> **Two distinct deadline families — keep them apart.** `Command.Timeout` is the
> *run's own contract* (this guide): it kills the tree. The readiness probes'
> `within` parameter (`WaitForLine` / `WaitForPort` / `WaitFor`, see
> [streaming.md](streaming.md)) is a different deadline: it gives
> `ProcessError.NotReady` and **never kills the child** — the caller decides what
> happens next.

## Graceful timeout

By default the deadline **hard-kills** the tree at once. Add
`Command.TimeoutGrace(grace)` (mirror: `Command.timeoutGrace`) to give the tree a
chance to clean up: at the deadline it is sent `SIGTERM`, allowed up to the grace
window to exit, then `SIGKILL`ed — the same SIGTERM → wait → SIGKILL tier as
[`ProcessGroup.Shutdown`](process-groups.md). A signal-handling child that exits
ends the grace early.

```fsharp
open ProcessKit
open System

task {
    let cmd =
        Command.create "slow-tool"
        |> Command.timeout (TimeSpan.FromSeconds 30.0)
        |> Command.timeoutGrace (TimeSpan.FromSeconds 5.0) // SIGTERM, wait up to 5s, then SIGKILL

    let! _ = cmd.OutputString()
    ()
}
```

`IsTimedOut` is `true` regardless of whether the child exited on the signal or was
`SIGKILL`ed after the grace — the deadline is what fired. **On Windows there is no
signal tier:** `TimeoutGrace` is accepted but the deadline kills the Job Object
atomically, so the grace window has no effect there.

## Captured vs raised: the decision table

The same timeout lands differently depending on the verb you finish with. The
capture verbs treat the deadline as *data*; the success-checking verbs raise it.

| Verb | A timeout deadline becomes |
|---|---|
| `OutputString()` / `OutputBytes()` | `Ok` result with `IsTimedOut = true`, `Code = None`, `Outcome = Outcome.TimedOut`, partial output kept |
| `Run()` / `RunUnit()` | `Error (ProcessError.Timeout(program, timeout, stdout, stderr))` — partial output attached |
| `ExitCode()` | `Error (ProcessError.Timeout …)` — it will not invent a sentinel code |
| `Probe()` | `Error (ProcessError.Timeout …)` |
| `Parse(f)` / `TryParse(f)` | `Error (ProcessError.Timeout …)` — both require success, so the deadline is raised |
| `Start()` + streaming | the stream **ends** at the deadline (tree killed, pipes closed); a following `Finish()` reports `Outcome.TimedOut` |
| `ProcessResult.ensureSuccess` on a captured result | `Error (ProcessError.Timeout …)` — the same conversion `Run` does for you |
| `FirstLine(p)` | the stream closes at the deadline; if no line matched first, you get `Ok None` (it is not a success-checking verb) |

Streaming makes the "captured" half concrete — the deadline bounds the stream,
and the outcome is readable afterwards:

```fsharp
open ProcessKit
open System

task {
    let cmd =
        Command.create "chatty-job"
        |> Command.timeout (TimeSpan.FromSeconds 10.0)

    match! cmd.Start() with
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLines().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"> {e.Current}"
                | false -> go <- false // the stream ends when the deadline kills the tree
        finally
            e.DisposeAsync().AsTask().Wait()

        match! proc.Finish() with
        | Ok finished when finished.Outcome.IsTimedOut -> eprintfn "killed at the deadline"
        | Ok _ -> ()
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

## Retries

`Command.Retry(maxAttempts, delay, predicate)` (mirror: `Command.retry`) replays a
failed run, sleeping `delay` between tries, retrying only while `predicate`
accepts the error. The predicate is a `Func<ProcessError, bool>` (from F#, a
plain `ProcessError -> bool` through the module mirror).

`maxAttempts` counts **additional** attempts *after* the first, so `Retry 3` runs
the command at most four times.

```fsharp
open ProcessKit
open System

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

    match! cmd.Run() with
    | Ok body -> printfn $"{body}"
    | Error err -> eprintfn $"gave up: {err.Message}"
}
```

The two built-in classifiers are ready to drop in as predicates:

- `ProcessError.isTransient` — `true` for `Spawn` and `Io` errors (spawn races,
  transient I/O blips) that may succeed on another try.
- `ProcessError.isNotFound` — `true` for a program-not-found failure (usually a
  reason to install-then-retry rather than to blindly replay).

```fsharp
let cmd =
    Command.create "flaky-tool"
    |> Command.retry 5 (TimeSpan.FromMilliseconds 200.0) ProcessError.isTransient
```

**Where retry earns its keep.** Retry replays the run whenever a verb yields an
`Error` your predicate accepts. The success-checking verbs (`Run` / `RunUnit` /
`ExitCode` / `Probe` / `Parse` / `TryParse`) are where that matters: they turn a
non-zero exit into `ProcessError.Exit` and a timeout into `ProcessError.Timeout`,
so your classifier can act on the *outcome* of the run. The capture verbs
(`OutputString` / `OutputBytes`) keep a non-zero exit **and** a timeout as data —
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
backoff shape, a different loop condition.

## Cancellation

Hand any verb a `System.Threading.CancellationToken`; cancelling the token kills
the run's tree and makes every consuming path report `ProcessError.Cancelled`.
Every verb has a `CancellationToken` overload (`cmd.Run(token)`,
`cmd.OutputString(token)`, …):

```fsharp
open ProcessKit
open System.Threading

task {
    use cts = new CancellationTokenSource()
    let job = (Command.create "long-export").Run(cts.Token)

    // elsewhere — a shutdown signal, a sibling failure, a UI button:
    cts.Cancel()

    match! job with
    | Error(ProcessError.Cancelled program) -> printfn $"{program} cancelled"
    | _ -> ()
}
```

Or tie a token to a command for its whole lifetime with `Command.CancelOn(token)`
(mirror: `Command.cancelOn`) — it is **linked in addition to** any per-verb token,
so either source cancels the run:

```fsharp
let cmd = Command.create "long-export" |> Command.cancelOn shutdownToken
let! _ = cmd.Run() // also cancels if shutdownToken fires
```

The contract, path by path:

| Situation | Behavior |
|---|---|
| Cancel during `Run` / `OutputString` / `OutputBytes` / `ExitCode` / `Probe` / `Parse` | tree killed → `Error (ProcessError.Cancelled program)` |
| Cancel during streaming (`StdoutLines`) | the stream **ends**; the following `Finish()` reports `ProcessError.Cancelled` |
| Token already cancelled **before** the run | short-circuits before spawning — no process is ever created |
| `FirstLine` mid-run | surfaces `ProcessError.Cancelled` once the token fires (not `Ok None`) |
| Under `Retry` | terminal — the built-in classifiers reject `Cancelled` and the loop stops re-trying |
| Under a [supervision.md](supervision.md) `Supervisor` | terminal — supervision returns `Cancelled` instead of restarting into a still-cancelled token |

Unlike a timeout — whose expiry is *captured* as `IsTimedOut` — a cancellation is
**always an error**: the run was abandoned, so there is no result to synthesize. A
token cancelled before the run starts short-circuits without spawning anything.

## Pipelines and clients

A whole **pipeline** has its own deadline and token, bounding the entire chain:

```fsharp
open ProcessKit
open System

task {
    let pipeline =
        (Command.create "producer")
            .Pipe(Command.create "consumer")
            .Timeout(TimeSpan.FromSeconds 30.0)   // whole-chain deadline (mirror: Pipeline.timeout)
            .CancelOn(shutdownToken)              // whole-chain token   (mirror: Pipeline.cancelOn)

    match! pipeline.OutputString() with
    | Ok result -> printfn $"timedOut={result.IsTimedOut}"
    | Error err -> eprintfn $"{err.Message}" // ProcessError.Cancelled when the token fires
}
```

`Pipeline.Timeout` tears the shared group down at the deadline and reports the
timeout (`IsTimedOut` on `OutputString`, `Error` on `Run`) — but, unlike a single
command's *captured* timeout, there is no salvaged partial stdout to read back. A
per-stage `Command.Timeout` instead kills just that stage and folds into pipefail.
See [pipelines.md](pipelines.md) for the full chain model.

A **`CliClient`** usually builds and consumes its `Command`s internally, so set
the deadline and token **once on the client** and every command it builds carries
them:

```fsharp
open ProcessKit
open System

let gh =
    (CliClient.create "gh")
        .DefaultTimeout(TimeSpan.FromSeconds 30.0)   // applied to every built command
        .DefaultCancelOn(shutdownToken)              // …controller cancels → all in-flight runs die
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
| "Let it clean up before the kill" | `Command.Timeout` + `Command.TimeoutGrace` |
| "This operation is flaky, try a few times" | `Command.Retry` |
| "Stop everything when the app shuts down" | `Command.CancelOn` / a verb token + one shared token |
| "Bound a whole multi-stage chain" | `Pipeline.Timeout` / `Pipeline.CancelOn` |
| "Set a deadline/token once for a tool" | `CliClient.DefaultTimeout` / `DefaultCancelOn` |
| "Keep this service alive across crashes" | [supervision.md](supervision.md) `Supervisor` |
| "Tell me when it's *ready*, don't kill it" | readiness probes — [streaming.md](streaming.md) |

---

Next: [supervision.md](supervision.md) ·
[streaming.md](streaming.md) ·
[pipelines.md](pipelines.md) ·
[commands.md](commands.md) ·
[testing.md](testing.md)
