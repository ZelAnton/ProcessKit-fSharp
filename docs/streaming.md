# Streaming & interactive I/O

[Previous: Overview](./)

The one-shot verbs in [Running commands](commands.md) buffer the whole output and
hand it back when the child exits. For a long-running or conversational child you
want the output *as it arrives* — and sometimes a back-channel to write to it.
`Command.StartAsync()` (and the equivalent `IProcessRunner.Start` / `ProcessGroup.Start`)
returns a live `RunningProcess` you drive yourself: stream stdout line by line,
interleave stdout and stderr, write stdin incrementally, wait for the child to
become *ready*, race several children, or profile a run end to end.

The samples below run inside a `task { }` block and use `match!`; the verbs that
return a value directly (`WaitAsync`, `ProfileAsync`, `WaitAllAsync`) use a plain `let!`. From C#
the same surface is `await`-able fluent methods, and the `IAsyncEnumerable<_>`
streams are `await foreach`.

- [Lifecycle](#lifecycle)
- [Streaming stdout line by line](#streaming-stdout-line-by-line)
- [Interleaving stdout and stderr](#interleaving-stdout-and-stderr)
- [Bounding the streaming backlog](#bounding-the-streaming-backlog)
- [Finishing a streamed run](#finishing-a-streamed-run)
- [Interactive stdin](#interactive-stdin)
- [Readiness probes](#readiness-probes)
- [Racing several children](#racing-several-children)
- [Profiling a run](#profiling-a-run)

## Lifecycle

`StartAsync()` spawns the child and returns a `RunningProcess` without waiting for it to
exit. The handle is an `IAsyncDisposable`: a `use` binding inside `task { }` reaps
the whole process tree on scope exit, exactly like the disposal at the end of a
one-shot run.

**F#**

```fsharp
task {
    match! (Command.create "dev-server").StartAsync() with
    | Error err -> eprintfn $"could not start: {err.Message}"
    | Ok proc ->
        use _ = proc // disposing the handle kills the whole tree

        printfn $"pid={proc.Pid} started {proc.StartTime:o}"
        // ... drive the process: stream, write stdin, probe for readiness ...
        printfn $"alive for {proc.Elapsed}; {proc.StdoutLineCount} stdout lines so far"

        let! outcome = proc.WaitAsync() // Outcome: Exited code / Signalled sig / TimedOut
        printfn $"exited: {outcome}"
}
```

**C#**

```csharp
await using var proc = (await new Command("dev-server").StartAsync()).GetValueOrThrow(); // disposing the handle kills the whole tree

Console.WriteLine($"pid={proc.Pid} started {proc.StartTime:o}");
// ... drive the process: stream, write stdin, probe for readiness ...
Console.WriteLine($"alive for {proc.Elapsed}; {proc.StdoutLineCount} stdout lines so far");

var outcome = await proc.WaitAsync(); // Outcome: Exited code / Signalled sig / TimedOut
Console.WriteLine($"exited: {outcome}");
```

`StartAsync()` puts the child in a **private group the handle owns**: dropping the
`RunningProcess` kills the tree, grandchildren included. The shared-group
variant — `group.StartAsync(cmd)` — returns the same kind of handle, but the *group*
controls the tree's fate (see [Process groups](process-groups.md)).

Consume the handle **exactly one way** — stdout is read once:

- `StdoutLinesAsync()` / `OutputEventsAsync()` — stream output as it arrives (below).
- `OutputStringAsync()` / `OutputBytesAsync()` — capture everything, like the one-shot verbs.
- `WaitAsync()` — just the `Outcome`; output is discarded.
- `FinishAsync()` — after streaming stdout, collect the `Outcome` and drained stderr.
- `ProfileAsync()` — capture plus periodic resource samples ([profiling](#profiling-a-run)).

`StdoutLinesAsync()` / `OutputEventsAsync()` need a **piped** stdout, which is the default for
`StartAsync()`; if you set `Command.Stdout` to `StdioMode.Inherit` or `StdioMode.Null`
there is nothing to stream. The live gauges `Pid`, `Elapsed`, `StartTime`,
`StdoutLineCount`, and `StderrLineCount` are cheap to read at any time, including
mid-stream. There is also `Kill()` — "stop it now, I'll `WaitAsync()` for the
`Outcome` myself" — which begins teardown without blocking.

To stop a long-running child *cleanly* — let it flush logs, release locks, and run its
shutdown hooks — use `StopAsync(gracePeriod)` (or `StopAsync()` for a 2-second default,
matching `ProcessGroupOptions.ShutdownTimeout`). It sends the tree a soft signal (SIGTERM),
waits up to the grace window for it to exit on its own, then hard-kills whatever is still
alive, reaps the tree, and returns the honest `Outcome` — the same SIGTERM → grace → SIGKILL
escalation as [`Command.TimeoutGrace`](timeouts-and-cancellation.md) and
[`ProcessGroup.ShutdownAsync`](process-groups.md). It drains the child's output while it
shuts down and reuses an in-flight streaming/capturing session's wait, so it is safe to call
after `StdoutLinesAsync()`/`OutputEventsAsync()` or alongside `FinishAsync`/`WaitAsync`, and
is idempotent with `Kill`/`Dispose`. A soft signal needs a mechanism that has one: on
**Windows** (no per-tree graceful signal) and on a **shared** group from
`group.StartAsync(cmd)` (no per-child graceful signal) the grace is skipped and the child is
hard-killed at once — exactly as `TimeoutGrace` already degrades there. A handle from
`StartAsync()` (its own private group) gets the full graceful stop on Unix.

A command's [`Timeout`](timeouts-and-cancellation.md) and `CancelOn` token **bound
the stream**: at the deadline (or on cancellation) the tree is killed, the pipes
close, and the stream ends — a streamed run can't hang past its deadline. After a
cancelled run, `FinishAsync()` reports `ProcessError.Cancelled`.

## Streaming stdout line by line

`StdoutLinesAsync()` returns an `IAsyncEnumerable<string>` that yields decoded lines as
the child produces them — no waiting for exit, no full-output buffering. In F#,
drive the enumerator directly:

**F#**

```fsharp
task {
    match! (Command.create "git" |> Command.args [ "log"; "--oneline"; "-n"; "50" ]).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"commit: {e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
await using var proc = (await new Command("git").Args(["log", "--oneline", "-n", "50"]).StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"commit: {line}");
```

From C# the same loop is simply `await foreach (var line in proc.StdoutLinesAsync()) { ... }`.

While you stream stdout, stderr is drained in the background, so a noisy child can
never block on a full stderr pipe. The `OnStdoutLine` / `OnStderrLine` handlers and
the output buffer policy from [Running commands](commands.md) still apply to a
streamed run — a handler sees each line on the pump, in addition to your loop.

## Interleaving stdout and stderr

When the *order* of stdout relative to stderr matters — a build tool that prints
progress to one and diagnostics to the other — `OutputEventsAsync()` returns an
`IAsyncEnumerable<OutputEvent>` that merges both channels in arrival order. Each
`OutputEvent` carries `IsStdout` / `IsStderr` and the line `Text`:

**F#**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.args [ "build"; "-c"; "Release" ]).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.OutputEventsAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true ->
                    let ev = e.Current
                    if ev.IsStdout then printfn $"out| {ev.Text}"
                    else eprintfn $"err| {ev.Text}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
await using var proc = (await new Command("dotnet").Args(["build", "-c", "Release"]).StartAsync()).GetValueOrThrow();

await foreach (var ev in proc.OutputEventsAsync())
{
    if (ev.IsStdout)
        Console.WriteLine($"out| {ev.Text}");
    else
        Console.Error.WriteLine($"err| {ev.Text}");
}
```

From C#, `await foreach (var ev in proc.OutputEventsAsync()) { ... }`. Choose `OutputEventsAsync()`
*or* `StdoutLinesAsync()` for a given run — both consume stdout, so they are alternatives,
not companions.

`OutputEventsAsync()` tags each line with the stream it *came from*, keeping the two
channels distinguishable. When you instead want them merged into one stream — with the
real byte-for-byte interleaving preserved, but no origin tag — reach for
[`Command.MergeStderr`](commands.md#merging-stderr-into-stdout-21) (a shell `2>&1`): the
child's stderr is folded into its stdout at the OS level, so `StdoutLinesAsync()` alone
yields every line in order and `OutputEventsAsync()` emits only `Stdout` events (there is
no longer a separate stderr stream to tag).

## Bounding the streaming backlog

By default, the channel that feeds `StdoutLinesAsync()` / `OutputEventsAsync()` / `WaitForLineAsync()`
is **unbounded**: a producer far outrunning your consumer (a chatty child, a slow line handler) just
grows the in-flight backlog — exactly the behavior ProcessKit has always had. `Command.StreamBuffer`
opts in to a bounded channel instead, capping that backlog with one of four `StreamFullMode`s:

- **`Backpressure`** (the default for `StreamBufferPolicy.Bounded(capacity)`) — the pump stops
  draining the OS pipe once the channel is full, so the child itself observably blocks writing to a
  full stdout/stderr pipe until your consumer catches up. Bounds memory losslessly, at the cost of the
  child's timing — pick this for a *trusted* producer you genuinely want to pace against your consumer
  (tailing a log, a pipeline stage).
- **`DropOldest`** — "tail" semantics: once full, the oldest queued line is discarded to make room for
  the newest. Lossy but bounded.
- **`DropNewest`** — "head" semantics: once full, the incoming line is discarded and what's already
  queued is kept.
- **`Error`** — fail loud: once the cap is reached, the streaming enumerator throws (carrying
  `ProcessError.OutputTooLarge`) instead of silently dropping anything.

Both `DropOldest` and `DropNewest` bump `RunningProcess.DroppedStreamLineCount` — a live counter (like
`StdoutLineCount`/`StderrLineCount`) so a lossy policy's drops are always visible, never silent:

**F#**

```fsharp
task {
    let command =
        (Command.create "chatty-tool")
            .StreamBuffer(StreamBufferPolicy.Bounded(1000, StreamFullMode.DropOldest))

    match! command.StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"{e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()

        if proc.DroppedStreamLineCount > 0 then
            printfn $"dropped {proc.DroppedStreamLineCount} lines to stay within the bound"
}
```

**C#**

```csharp
var command = new Command("chatty-tool")
    .StreamBuffer(StreamBufferPolicy.Bounded(1000, StreamFullMode.DropOldest));

await using var proc = (await command.StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine(line);

if (proc.DroppedStreamLineCount > 0)
    Console.WriteLine($"dropped {proc.DroppedStreamLineCount} lines to stay within the bound");
```

**The backpressure deadlock footgun.** `StreamFullMode.Backpressure` slows the *child*, not your code
— but if your consumption loop itself never resumes (it's stuck waiting on something that, in turn,
waits for the child to finish), the child can never finish either: it's blocked writing to a pipe
nobody is reading, forever. This is the same full-duplex hazard as the
[interactive-stdin deadlock](#interactive-stdin) above, just on the read side instead of the write
side. Two things to know before opting in:

- A `Command.Timeout` kills the *child* at the deadline, but that alone does **not** free a writer your
  own pump is parked on if you also never read again — the child dying doesn't hand the pump anything
  new to write, but a pump already blocked *inside* a `WriteAsync` call only unblocks when either the
  channel gets read from again or the `RunningProcess` itself is disposed. In other words: pairing
  `Backpressure` with `Command.Timeout` bounds the *child's* lifetime, not necessarily your consumer's.
- Give your **own** consumption loop a deadline (a `CancellationToken` passed to
  `GetAsyncEnumerator(token)`, or a read-side timeout around each `MoveNextAsync()`), and make sure you
  `Dispose`/`DisposeAsync` the `RunningProcess` promptly if you give up on it — disposal always
  unblocks a writer parked on backpressure, so the pump can wind down instead of leaking forever as an
  abandoned background task.

If you can't reason about your consumer always resuming, prefer `DropOldest`/`DropNewest` (never
blocks the child) or `Error` (fails loud instead of stalling) over `Backpressure`.

## Finishing a streamed run

When the stream ends (stdout closed), collect the rest with `FinishAsync()`, which returns
`Result<Finished, ProcessError>`. `Finished` carries the `Outcome` and the `Stderr`
that was drained while you streamed:

**F#**

```fsharp
task {
    match! (Command.create "build-everything").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"> {e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()

        match! proc.FinishAsync() with
        | Ok finished ->
            if finished.Outcome <> Outcome.Exited 0 then
                eprintfn $"failed ({finished.Outcome}):\n{finished.Stderr}"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("build-everything").StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"> {line}");

var finished = (await proc.FinishAsync()).GetValueOrThrow();
if (finished.Outcome is not { IsExited: true, Code.Value: 0 }) // anything but a clean exit 0
    Console.Error.WriteLine($"failed ({finished.Outcome}):\n{finished.Stderr}");
```

Use `FinishAsync()` after you have streamed stdout. If you only need the exit status and
don't care about output, `WaitAsync()` returns the `Outcome` directly and discards the
captured output; if you skipped streaming altogether, `OutputStringAsync()` /
`OutputBytesAsync()` buffer and return everything just like the one-shot verbs.

## Interactive stdin

Conversational tools — write a request, read the response, repeat. Keep stdin open
with `KeepStdinOpen`, then take the writer with `TakeStdin()`, which returns a
`ProcessStdin option` (`Some` once; `None` if stdin wasn't kept open or was already
taken):

**F#**

```fsharp
task {
    // `bc` evaluates each stdin line and prints the result.
    match! (Command.create "bc" |> Command.keepStdinOpen).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        match proc.TakeStdin() with
        | Some stdin ->
            do! stdin.WriteLineAsync "2 + 2" // writes "2 + 2\n", flushed
            do! stdin.WriteLineAsync "6 * 7"
            do! stdin.FinishAsync() // send EOF so bc exits
        | None -> ()

        // ... then read proc.StdoutLinesAsync() for the answers.
        ()
}
```

**C#**

```csharp
// `bc` evaluates each stdin line and prints the result.
await using var proc = (await new Command("bc").KeepStdinOpen().StartAsync()).GetValueOrThrow();

if (proc.TakeStdin() is { Value: var stdin }) // Some(stdin); None is null and won't match
{
    await stdin.WriteLineAsync("2 + 2"); // writes "2 + 2\n", flushed
    await stdin.WriteLineAsync("6 * 7");
    await stdin.FinishAsync(); // send EOF so bc exits
}

// ... then read proc.StdoutLinesAsync() for the answers.
```

`ProcessStdin` offers `WriteLineAsync(line)` (appends a newline and flushes),
`WriteAsync(bytes)` (raw bytes, for binary input), `FlushAsync()`, and `FinishAsync()` (close
stdin / send EOF). Disposing the writer — or the whole `RunningProcess` — closes
stdin too; `FinishAsync()` just makes the EOF explicit and awaitable. The write verbs
(`WriteAsync` / `WriteLineAsync` / `FlushAsync`) each take an optional `CancellationToken`, so a
write to a child that has stopped reading (a full stdin pipe) can be bounded rather than blocking
forever — a cancelled write throws `OperationCanceledException` (and, as with any cancellable stream
write, may already have delivered part of its bytes, so abandon the session rather than retrying a
timed-out write). `FinishAsync` is idempotent and uncancellable (it mirrors `DisposeAsync`); bound
the writes/flush before closing, not the close.

**Avoid the full-duplex deadlock.** A child's stdout pipe has a finite OS buffer;
once it fills, the child blocks *writing* stdout until something reads it. If you
push a large interactive stdin while nothing drains the child's stdout, the child
stops reading stdin (blocked on stdout), your `WriteAsync` parks waiting for stdin buffer
space, and neither side progresses. The `bc` example above is safe because it
interleaves one small write with one read. When you both feed a sizable stdin **and**
the child produces output, write stdin from one task and drain stdout from another:

**F#**

```fsharp
task {
    match! (Command.create "transform" |> Command.keepStdinOpen).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        match proc.TakeStdin() with
        | Some stdin ->
            // Producer: feed a large stdin on its own task.
            let writer =
                task {
                    for line in bigInput do
                        do! stdin.WriteLineAsync line

                    do! stdin.FinishAsync()
                }

            // Consumer: drain stdout concurrently on this task.
            let e = proc.StdoutLinesAsync().GetAsyncEnumerator()

            try
                let mutable go = true

                while go do
                    match! e.MoveNextAsync() with
                    | true -> handle e.Current
                    | false -> go <- false
            finally
                e.DisposeAsync().AsTask().Wait()

            do! writer
        | None -> ()
}
```

**C#**

```csharp
await using var proc = (await new Command("transform").KeepStdinOpen().StartAsync()).GetValueOrThrow();

if (proc.TakeStdin() is { Value: var stdin }) // Some(stdin); None is null and won't match
{
    // Producer: feed a large stdin on its own task.
    var writer = Task.Run(async () =>
    {
        foreach (var line in bigInput)
            await stdin.WriteLineAsync(line);

        await stdin.FinishAsync();
    });

    // Consumer: drain stdout concurrently on this task.
    await foreach (var line in proc.StdoutLinesAsync())
        handle(line);

    await writer;
}
```

For *one-directional* streamed input (a channel, a file tail) you don't need
interactivity at all — give the command `Stdin.FromLines seq`,
`Stdin.FromAsyncLines asyncSeq`, or `Stdin.FromStream stream` and let ProcessKit's
background writer feed it; those sources run concurrently with the output pumps and
never deadlock. See the stdin source table in [Running commands](commands.md).

## Readiness probes

"Start a server, then use it" needs the server to be *ready*, not merely started.
Three probes replace the arbitrary sleep, each bounded by its own deadline and each
returning a `Result`:

**F#**

```fsharp
task {
    match! (Command.create "my-server").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        // 1. A line on stdout (returns the matching line):
        match! proc.WaitForLineAsync((fun line -> line.Contains "listening on"), TimeSpan.FromSeconds 10.0) with
        | Ok banner -> printfn $"server says: {banner}"
        | Error(ProcessError.NotReady(program, timeout)) -> eprintfn $"{program} not ready after {timeout}"
        | Error err -> eprintfn $"{err.Message}"

        // 2. A TCP port accepting connections:
        let endpoint = IPEndPoint(IPAddress.Loopback, 8080)

        match! proc.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "port is open"
        | Error err -> eprintfn $"{err.Message}"

        // 3. Any async predicate (an HTTP /health endpoint, a file appearing, …):
        match! proc.WaitForAsync((fun () -> healthCheck ()), TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "healthy"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("my-server").StartAsync()).GetValueOrThrow();

// 1. A line on stdout (returns the matching line):
Console.WriteLine(await proc.WaitForLineAsync(line => line.Contains("listening on"), TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true, ResultValue: var banner }                => $"server says: {banner}",
    { IsOk: false, ErrorValue: ProcessError.NotReady nr } => $"{nr.Program} not ready after {nr.Timeout}",
    { IsOk: false, ErrorValue: var err }                  => err.Message,
});

// 2. A TCP port accepting connections:
var endpoint = new IPEndPoint(IPAddress.Loopback, 8080);

Console.WriteLine(await proc.WaitForPortAsync(endpoint, TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "port is open",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// 3. Any async predicate (an HTTP /health endpoint, a file appearing, …):
Console.WriteLine(await proc.WaitForAsync(() => healthCheck(), TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "healthy",
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

Probe semantics are deliberately uniform:

- A probe that can't pass within its deadline fails with **`ProcessError.NotReady`** —
  distinct from `ProcessError.Timeout`, which is the run's own deadline.
- A probe also fails *fast* once readiness can no longer happen: the child exits, or
  (for `WaitForLineAsync`) its stdout closes — no waiting out a 10s deadline on a dead
  server.
- A failed probe **never kills the child.** You decide what happens next: retry, log
  and continue, or tear down.
- All three probes background-drain the child's piped stdout/stderr while polling, so a chatty
  child that writes more than one OS pipe buffer of startup output (~64 KiB on Linux) before
  becoming ready can't block in `write()` and spuriously fail the probe with `NotReady`.
  `WaitForLineAsync` hands the drained stdout back to you (consumed up to and including the
  matching line — continue with `FinishAsync()` or further streaming afterwards); `WaitForPortAsync`
  / `WaitForAsync` discard what they drain and stop draining once the probe concludes. Either way, a
  capture verb called afterward (`OutputStringAsync`/`OutputBytesAsync`/a fresh
  `StdoutLinesAsync`/`OutputEventsAsync`) only sees output the child wrote *after* the probe
  concluded — run probes before a capturing verb if you need the complete output.

`WaitForAsync` takes a function returning `Task<bool>` (`Func<Task<bool>>` from C#), so any
async health check fits — re-evaluated until it returns `true` or the deadline elapses.

## Racing several children

`RunningProcess.WaitAny` races several started handles and reports whichever exits
first — the natural primitive for "first answer wins" or "restart whatever died". It
returns `WaitAnyResult` directly (no `Result` wrapper), carrying the winner's `Index`
in the array you passed and its `Outcome`. The array itself must be non-null,
non-empty, and free of null elements — a violation throws (`ArgumentNullException`/
`ArgumentException`) rather than reporting through a `Result`, the same contract
`WaitAllAsync` below uses:

**F#**

```fsharp
task {
    // Bound the race with a per-command Timeout — WaitAny applies none of its own.
    let withDeadline name =
        Command.create name |> Command.timeout (TimeSpan.FromSeconds 30.0)

    match! (withDeadline "replica-a").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok a ->
        use _ = a

        match! (withDeadline "replica-b").StartAsync() with
        | Error err -> eprintfn $"{err.Message}"
        | Ok b ->
            use _ = b

            let! result = RunningProcess.WaitAnyAsync [| a; b |]
            printfn $"contender #{result.Index} exited first with {result.Outcome}"
}
```

**C#**

```csharp
// Bound the race with a per-command Timeout — WaitAny applies none of its own.
Command withDeadline(string name) =>
    new Command(name).Timeout(TimeSpan.FromSeconds(30));

await using var a = (await withDeadline("replica-a").StartAsync()).GetValueOrThrow();
await using var b = (await withDeadline("replica-b").StartAsync()).GetValueOrThrow();

var first = await RunningProcess.WaitAnyAsync([a, b]);
Console.WriteLine($"contender #{first.Index} exited first with {first.Outcome}");
```

To join a fixed set instead of racing it, `RunningProcess.WaitAll` waits for *all* of
them and returns every `Outcome` in input order (an `Outcome[]` directly — no `Result`
wrapper), under the same non-null/non-empty/no-null-element contract:

**F#**

```fsharp
let! outcomes = RunningProcess.WaitAllAsync [| a; b |]
printfn $"{outcomes.Length} children done"
```

**C#**

```csharp
var outcomes = await RunningProcess.WaitAllAsync([a, b]);
Console.WriteLine($"{outcomes.Length} children done");
```

Both apply **no per-process timeout** (bound the race with a `Command.Timeout`, as
above) and do **no output pumping** — drain chatty children first, or give them a
bounded output buffer policy, so a child can't stall on a full pipe while you wait.

## Profiling a run

A `RunningProcess` reports its own resource usage live, and `ProfileAsync()` turns a whole
run into a summary. The live gauges read the *child process itself* at any moment:

**F#**

```fsharp
task {
    match! (Command.create "crunch").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        // Live, mid-run:
        printfn $"pid={proc.Pid} elapsed={proc.Elapsed} cpu={proc.CpuTime} peak={proc.PeakMemoryBytes}"

        // Capture + sample on an interval until exit (returns a RunProfile directly):
        let! profile = proc.ProfileAsync(TimeSpan.FromMilliseconds 100.0)

        printfn $"exit={profile.ExitCode} wall={profile.Duration} samples={profile.Samples}"
        printfn $"cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpuCores}"
}
```

**C#**

```csharp
await using var proc = (await new Command("crunch").StartAsync()).GetValueOrThrow();

// Live, mid-run:
Console.WriteLine($"pid={proc.Pid} elapsed={proc.Elapsed} cpu={proc.CpuTime} peak={proc.PeakMemoryBytes}");

// Capture + sample on an interval until exit (returns a RunProfile directly):
var profile = await proc.ProfileAsync(TimeSpan.FromMilliseconds(100));

Console.WriteLine($"exit={profile.ExitCode} wall={profile.Duration} samples={profile.Samples}");
Console.WriteLine($"cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpuCores}");
```

`ProfileAsync()` with no argument uses a default sampling interval; `ProfileAsync(interval)`
samples at the cadence you pick. The resulting `RunProfile` exposes `ExitCode`,
`Duration` (wall clock), `CpuTime` (user + kernel), `PeakMemoryBytes`, the number of
`Samples` taken, and `AvgCpuCores` — CPU time over wall time, so a value near `1.7` means
roughly 1.7 cores were busy on average.

These figures describe the started child, not a whole tree — for the tree's
aggregate use `ProcessGroup.Stats` / `SampleStatsAsync` ([Process groups](process-groups.md)).
Availability follows the platform: full CPU and memory on Windows and the Linux
cgroup backend, and `None` where the kernel doesn't account per-process cheaply — see
[Platform support](platform-support.md).

---

Next: [Pipelines](pipelines.md)
