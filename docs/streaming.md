# Streaming & interactive I/O

[Previous: Overview](./)

The one-shot verbs in [Running commands](commands.md) buffer the whole output and
hand it back when the child exits. For a long-running or conversational child you
want the output *as it arrives* — and sometimes a back-channel to write to it.
`Command.StartAsync()` (and the equivalent `IProcessRunner.Start` / `ProcessGroup.Start`)
returns a live `RunningProcess` you drive yourself: stream stdout line by line,
stream stdout as byte chunks,
interleave stdout and stderr, write stdin incrementally, wait for the child to
become *ready*, race several children, or profile a run end to end.

The samples below run inside a `task { }` block and use `match!`; the verbs that
return a value directly (`WaitAsync`, `ProfileAsync`, `WaitAllAsync`) use a plain `let!`. From C#
the same surface is `await`-able fluent methods, and the `IAsyncEnumerable<_>`
streams are `await foreach`.

- [Lifecycle](#lifecycle)
- [Streaming stdout line by line](#streaming-stdout-line-by-line)
- [Streaming stdout as byte chunks](#streaming-stdout-as-byte-chunks)
- [Streaming NDJSON / JSON Lines](#streaming-ndjson--json-lines)
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

`StdoutLinesAsync()` / `StdoutChunksAsync()` / `OutputEventsAsync()` need a **piped** stdout, which is the default for
`StartAsync()`; if you set `Command.Stdout` to `StdioMode.Inherit` or `StdioMode.Null`
there is nothing to stream. The live gauges `Pid`, `Elapsed`, `StartTime`,
`StdoutLineCount`, and `StderrLineCount` are cheap to read at any time, including
mid-stream. There is also `Kill()` — "stop it now, I'll `WaitAsync()` for the
`Outcome` myself" — which begins teardown without blocking.

To stop a long-running child *cleanly* — let it flush logs, release locks, and run its
shutdown hooks — use `StopAsync(gracePeriod)` (or `StopAsync()` for a 2-second default,
matching `ProcessGroupOptions.ShutdownTimeout`). It sends the tree the command's configured soft signal
(`Command.StopSignal`, default `Signal.Term`),
waits up to the grace window for it to exit on its own, then hard-kills whatever is still
alive, reaps the tree, and returns the honest `Outcome` — the same configured-soft-signal → grace → hard-kill
escalation as [`Command.TimeoutGrace`](timeouts-and-cancellation.md) and
[`ProcessGroup.ShutdownAsync`](process-groups.md). It drains the child's output while it
shuts down and reuses an in-flight streaming/capturing session's wait, so it is safe to call
after `StdoutLinesAsync()`/`OutputEventsAsync()` or alongside `FinishAsync`/`WaitAsync`, and
is idempotent with `Kill`/`Dispose`. A soft signal needs a mechanism that has one: on
**Windows** (no per-tree graceful signal) and on a **shared** group from
`group.StartAsync(cmd)` (no per-child graceful signal) the grace is skipped and the child is
hard-killed at once — exactly as `TimeoutGrace` already degrades there. A handle from
`StartAsync()` (its own private group) gets the full graceful stop on Unix.

`Signal(signal)` is the non-consuming control verb for one live handle. It targets that run's own
containment unit and leaves `WaitAsync`/streaming available afterwards; after teardown it fails without
touching a potentially recycled pid. Use `ProcessGroup.Signal` when the intent is a group-wide broadcast.
For a pipeline, stage 0 owns `StopSignal`; setting a custom value on a later stage is rejected because
the chain has one broadcast soft-stop phase.

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

## Streaming stdout as byte chunks

`StdoutChunksAsync()` returns an `IAsyncEnumerable<ReadOnlyMemory<byte>>`. It performs no text
decoding or line framing: each non-empty item contains exactly the bytes returned by one underlying
read, including NUL bytes, invalid UTF-8, and boundaries inside a multibyte character. Every item owns
its backing array, so it remains valid after the next chunk arrives. Use this for archives, media,
compressed data, and other output where text is the wrong abstraction.

**F#**

```fsharp
task {
    match! (Command.create "git" |> Command.args [ "archive"; "HEAD" ]).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let destination = Stream.Null

        let e = proc.StdoutChunksAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                let! more = e.MoveNextAsync()

                if more then
                    do! destination.WriteAsync(e.Current)
                else
                    go <- false
        finally
            e.DisposeAsync().AsTask().Wait()

        match! proc.FinishAsync() with
        | Ok finished -> printfn $"archive finished: {finished.Outcome}"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("git").Args(["archive", "HEAD"]).StartAsync()).GetValueOrThrow();
using var destination = Stream.Null;

await foreach (var chunk in proc.StdoutChunksAsync())
    await destination.WriteAsync(chunk);

var finished = (await proc.FinishAsync()).GetValueOrThrow();
```

The chunk stream uses the configured `StdoutTee` as a raw byte tee. `MergeStderr` is supported: the
merged bytes arrive through stdout in the order supplied by the operating system. `Pty` is also
supported, but the terminal remains a terminal — its normal echo, newline, and other line-discipline
behaviour can transform bytes before ProcessKit reads them. `Inherit` and `Null` stdout have nothing
to stream.

Chunk streaming claims the stdout pipe exactly once. `OutputStringAsync()`, `OutputBytesAsync()`,
`WaitAsync()`, `StdoutLinesAsync()`, `OutputEventsAsync()`, and framed/interactive sessions are
refused on the same handle; a second `StdoutChunksAsync()` is refused too. After consuming chunks,
call `FinishAsync()` to await the process and obtain drained stderr. `StopAsync()` and disposal remain
valid lifecycle operations; `WaitAsync()` is not a companion to a claimed streaming session.

The default channel is unbounded for compatibility with the other streaming verbs. For bounded
memory, set `Command.StreamBuffer`: its capacity counts unread chunks, and `Backpressure` pauses
the stdout pump before it reads more when the channel is full, preserving every byte. The two drop
modes intentionally trade byte preservation for bounded lossy output (`DroppedStreamLineCount` is
the existing dropped-stream-item counter); `Error` ends the stream with `ProcessError.OutputTooLarge`.
`OutputBuffer`'s line/byte caps do not apply to chunk contents. A genuine stdout read failure ends
the enumerator and `FinishAsync()` with `ProcessError.Io`; an `IOException`/`ObjectDisposedException`
caused by this handle's teardown is quiet.

## Streaming NDJSON / JSON Lines

Many CLIs stream their output as one JSON document per line — NDJSON / JSON Lines
(`docker events --format json`, `kubectl get -w -o json`, `rg --json`). Rather than
combining `StdoutLinesAsync()` with your own `JsonSerializer` call on every line,
`StdoutJsonLinesAsync<'T>()` does it for you: a thin typed wrapper over
`StdoutLinesAsync()` that deserializes each non-empty line into a `'T` as it arrives.
It shares the very same exclusive-consumption gate, `LineTerminator`, and
`StreamBuffer` policy as `StdoutLinesAsync()` — pick one or the other for a given run,
same as `StdoutLinesAsync()` / `OutputEventsAsync()` above:

**F#**

```fsharp
type Event = { Type: string; Message: string }

task {
    match! (Command.create "docker" |> Command.args [ "events"; "--format"; "json" ]).StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutJsonLinesAsync<Event>().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"{e.Current.Type}: {e.Current.Message}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
record Event(string Type, string Message);

await using var proc = (await new Command("docker").Args(["events", "--format", "json"]).StartAsync()).GetValueOrThrow();

await foreach (var ev in proc.StdoutJsonLinesAsync<Event>())
    Console.WriteLine($"{ev.Type}: {ev.Message}");
```

A blank line (after the `LineTerminator` policy is applied) is skipped silently —
never deserialized — a common NDJSON producer quirk (a trailing blank line, a
keep-alive newline). A non-empty line that fails to deserialize ends the enumeration
with an exception carrying `ProcessError.Parse`, exactly like `OutputJsonAsync<'T>`'s
`ProcessError.Parse` ([Running commands](commands.md#consuming-verbs)) — never a raw,
undocumented exception. `StdoutJsonLinesAsync<'T>(options)` takes an optional
`JsonSerializerOptions` (omitted uses the BCL defaults) and deserializes via
reflection, so it is not trim-/NativeAOT-safe; for a trimmed/NativeAOT app, pass a
source-generated `JsonTypeInfo<'T>` to the `StdoutJsonLinesAsync<'T>(typeInfo)`
overload instead (`MyJsonContext.Default.MyType` from a `[JsonSerializable]`-annotated
`JsonSerializerContext`) — no reflection, no `RequiresUnreferencedCode`/
`RequiresDynamicCode`. Call `FinishAsync()` afterwards for stderr + outcome, same as
after `StdoutLinesAsync()`.

## Interleaving stdout and stderr

When the *order* of stdout relative to stderr matters — a build tool that prints
progress to one and diagnostics to the other — `OutputEventsAsync()` returns an
`IAsyncEnumerable<OutputEvent>` that merges both channels in arrival order. Each event's
`OutputLine` carries its `Text`, `TimestampUtc` (captured from the command's `TimeProvider`), and a
one-based `Sequence` shared by stdout and stderr for that run. The sequence records the order in
which the independently-drained streams reached ProcessKit's line-framing boundary, so a collected
transcript can be sorted unambiguously even if later processing is concurrent:

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
                    if ev.IsStdout then printfn $"{ev.Sequence} out| {ev.Text}"
                    else eprintfn $"{ev.Sequence} err| {ev.Text}"
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
        Console.WriteLine($"{ev.Sequence} out| {ev.Text}");
    else
        Console.Error.WriteLine($"{ev.Sequence} err| {ev.Text}");
}
```

`FakeProcess`, `ScriptedRunner`, and cassette replay all run through the same framing path. Their
sequence numbers are therefore deterministic for a given emitted order. Timestamps are synthesized
when the fake/replay stream is consumed; attach a deterministic `Command.TimeProvider` when a test
needs stable timestamp values. Cassettes continue to store output text rather than capture-time
metadata, so existing cassette versions remain compatible.

From C#, `await foreach (var ev in proc.OutputEventsAsync()) { ... }`. Choose `OutputEventsAsync()`
*or* `StdoutLinesAsync()` for a given run — both consume stdout, so they are alternatives,
not companions. A [`PtySession`](pty.md#automating-an-interactive-cli-expect-and-send), which reads
the same output unframed to wait for terminal prompts, is a third alternative to those two.

`OutputEventsAsync()` tags each line with the stream it *came from*, keeping the two
channels distinguishable. When you instead want them merged into one stream — with the
real byte-for-byte interleaving preserved, but no origin tag — reach for
[`Command.MergeStderr`](commands.md#merging-stderr-into-stdout-21) (a shell `2>&1`): the
child's stderr is folded into its stdout at the OS level, so `StdoutLinesAsync()` alone
yields every line in order and `OutputEventsAsync()` emits only `Stdout` events (there is
no longer a separate stderr stream to tag).

## Redirecting a stream straight to a file

Everything above pumps output *through the parent*: a background pump drains the child's
stdout/stderr pipe, and the log lives only as long as that pump does. For a long-running child
whose output you just want on disk — a service's log file under a `Supervisor`, a build's full
transcript — that is wasted work and an extra point of failure. `Command.StdoutToFile(path,
append)` / `Command.StderrToFile(path, append)` instead redirect the stream **straight to a file
at the OS level**: the child is handed the open file as its stdout/stderr handle/fd *on the spawn*
(Windows: an inheritable file handle in `STARTUPINFO`; POSIX: a file fd via a `posix_spawn` file
action), so the child writes the file directly, with **zero copying through the parent and no
pump**. The file keeps growing even after the parent process — or a pump that would have drained a
pipe — is gone. `append = false` creates the file (truncating an existing one); `append = true`
appends.

**F#**

```fsharp
// Both streams to their own log files; the parent captures neither. The child writes them
// directly and they survive the parent.
task {
    let cmd =
        Command.create "my-service"
        |> Command.stdoutToFile "/var/log/my-service.out" true // append
        |> Command.stderrToFile "/var/log/my-service.err" true

    match! cmd.RunAsync() with
    | Ok _ -> ()
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("my-service")
    .StdoutToFile("/var/log/my-service.out", append: true)
    .StderrToFile("/var/log/my-service.err", append: true);

await cmd.RunAsync();
```

A redirected stream has **no parent-side stream at all** — `ProcessResult.Stdout`/`Stderr` is
empty, the streaming stdout/stderr verbs yield nothing, and the matching `OutputEvent` is never
produced, exactly like `StdioMode.Null`. Because of that, the knobs and verbs that *need* a
parent-side view of the same stream are rejected at the builder boundary with an
`ArgumentException` (in either chaining order), rather than silently never firing:

| Combined with `StdoutToFile` (the stdout stream) | Combined with `StderrToFile` (the stderr stream) |
|---|---|
| `StdoutTee`, `OnStdoutLine` — rejected (no parent stdout to observe) | `StderrTee`, `OnStderrLine` — rejected (no parent stderr to observe) |
| `MergeStderr` — rejected (it folds stderr into the observed stdout, which is absent) | `MergeStderr` — rejected (it removes the separate stderr this redirects) |
| `Pty` — rejected (a terminal replaces all stdio with one device) | `Pty` — rejected (same) |

What **is** allowed and useful:

- **Redirect one stream to a file, capture the other normally.** `StdoutToFile` leaves stderr on
  its ordinary pipe, so `ProcessResult.Stderr`, `OnStderrLine`, `StderrTee`, and the stderr
  streaming verbs all still work — and vice versa for `StderrToFile`.
- **Redirect both streams**, each to its own file (`StdoutToFile` + `StderrToFile`).
- The buffered stdout/stderr *of the non-redirected stream* is captured exactly as always.

`StdoutToFile`/`StderrToFile` and `Stdout(mode)`/`Stderr(mode)` are both *destination* setters for
the same stream, so the **last one in the chain wins** — a later `Stdout(StdioMode.Null)` clears a
prior `StdoutToFile`, and vice versa. A bad path (missing directory, denied permission) fails the
spawn with `ProcessError.Spawn`, never a silent drop of the child's output.

### Rotating a long-lived log

When the log must stay bounded, use a caller-owned `RotatingFileSink` as a tee. The active file is
the requested path; `.1` is the newest archive and `.N` the oldest. Writes are split at the byte
limit, and archives beyond `maxFiles` are deleted:

```fsharp
use log = new RotatingFileSink("/var/log/my-service.log", 64L * 1024L * 1024L, 5)

let command =
    Command.create "my-service"
    |> Command.stdoutTee log
```

```csharp
using var log = new RotatingFileSink("/var/log/my-service.log", 64L * 1024L * 1024L, 5);
var command = new Command("my-service").StdoutTee(log);
```

This deliberately has the opposite lifetime trade-off from `StdoutToFile`: rotation requires the
parent-side pump, so it stops when the parent exits. The stream remains captured as usual, and the
caller owns the sink. A write, flush, delete, or rename failure propagates through the existing tee
error contract and fails the run; ProcessKit never silently drops log bytes. Use separate sink
instances for stdout and stderr.

## Bounding the streaming backlog

By default, the channel that feeds `StdoutLinesAsync()` / `StdoutChunksAsync()` / `OutputEventsAsync()` / `WaitForLineAsync()`
is **unbounded**: a producer far outrunning your consumer (a chatty child, a slow line handler) just
grows the in-flight backlog — exactly the behavior ProcessKit has always had. `Command.StreamBuffer`
opts in to a bounded channel instead, capping that backlog with one of four `StreamFullMode`s:

- **`Backpressure`** (the default for `StreamBufferPolicy.Bounded(capacity)`) — the pump stops
  draining the OS pipe once the channel is full, so the child itself observably blocks writing to a
  full stdout/stderr pipe until your consumer catches up. Bounds memory losslessly, at the cost of the
  child's timing — pick this for a *trusted* producer you genuinely want to pace against your consumer
  (tailing a log, a pipeline stage).
- **`DropOldest`** — "tail" semantics: once full, the oldest queued item is discarded to make room for
  the newest. Lossy but bounded (for chunks, this deliberately drops bytes).
- **`DropNewest`** — "head" semantics: once full, the incoming item is discarded and what's already
  queued is kept.
- **`Error`** — fail loud: once the cap is reached, the streaming enumerator throws (carrying
  `ProcessError.OutputTooLarge`) instead of silently dropping anything.

Both `DropOldest` and `DropNewest` bump `RunningProcess.DroppedStreamLineCount` — a live counter (like
`StdoutLineCount`/`StderrLineCount`; for byte streaming it counts dropped chunks) so a lossy policy's
drops are always visible, never silent:

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

When a line or chunk stream ends (stdout closed), collect the rest with `FinishAsync()`, which returns
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

## Streaming a pipeline's final stage

Everything in this chapter has a **pipeline** counterpart. A [`Pipeline`](pipelines.md)
normally runs to completion behind its buffering verbs, but `Pipeline.StartAsync()` starts
it as a live session — a `PipelineSession`, the multi-stage analogue of `RunningProcess` —
and streams the **final** stage's stdout exactly as `StdoutLinesAsync` /
`StdoutJsonLinesAsync` / `OutputEventsAsync` / `WaitForLineAsync` do above:

**F#**

```fsharp
task {
    let pipeline =
        (Command.create "journalctl" |> Command.args [ "-f" ])
            .Pipe(Command.create "grep" |> Command.args [ "--line-buffered"; "ERROR" ])

    match! pipeline.StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok session ->
        use session = session
        let e = session.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"error: {e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()

        // FinishAsync reaps the WHOLE chain and reports the pipefail outcome + that
        // stage's stderr — identical to Pipeline.RunAsync, never a final-stage-only view.
        match! session.FinishAsync() with
        | Ok finished -> printfn $"chain finished: {finished.Outcome}"
        | Error err -> eprintfn $"{err.Message}"
}
```

`FinishAsync` / `StopAsync` reap and classify the entire chain (not just the final stage),
so a non-zero exit deep in the pipe still surfaces as the pipefail representative's
`Outcome`, and stopping or disposing tears down **every** stage. The single-consumption,
timeout, and cancellation rules are the ones you already know from `RunningProcess`. See
[Streaming a pipeline](pipelines.md#streaming-a-pipeline) for the full session surface.

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

## Content-Length framed sessions (LSP / DAP)

Language servers, debug adapters, and BSP servers usually do not speak newline-delimited JSON.
They frame each byte payload as `Content-Length: N`, CRLF, a blank CRLF line, then exactly `N`
payload bytes. `ContentLengthSession` owns a live handle's stdout and exposes those payloads as a
single `IAsyncEnumerable<byte[]>`; build the command with `KeepStdinOpen` to send frames back.

**F#**

<!-- docsnippet:imports System.Text -->
```fsharp
task {
    let command = (Command.create "language-server").KeepStdinOpen()

    match! command.StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use proc = proc
        let session = ContentLengthSession(proc)
        let initialize = Encoding.UTF8.GetBytes "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\"}"

        match! session.SendAsync initialize with
        | Error err -> eprintfn $"{err.Message}"
        | Ok() ->
            let frames = session.FramesAsync().GetAsyncEnumerator()

            try
                let! received = frames.MoveNextAsync()

                if received then
                    printfn "received %d bytes" frames.Current.Length
            finally
                frames.DisposeAsync().AsTask().Wait()
}
```

**C#**

<!-- docsnippet:imports System.Text -->
```csharp
await using var process =
    (await new Command("language-server").KeepStdinOpen().StartAsync()).GetValueOrThrow();
var session = new ContentLengthSession(process);

var initialize = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialize\"}");
(await session.SendAsync(initialize)).GetValueOrThrow();

await foreach (var frame in session.FramesAsync())
    Console.WriteLine($"received {frame.Length} bytes");
```

The default maximum payload in either direction is 16 MiB; pass a smaller positive `maxFrameBytes`
to the constructor for an untrusted peer. Oversized, duplicate/missing `Content-Length`, non-ASCII
headers, bare-LF headers, and truncated payloads fail the enumerator with `ProcessException`
carrying `ProcessError.Parse` before a misleading partial frame is yielded. Extra headers such as
`Content-Type` are accepted. `SendAsync` serializes concurrent callers so header/payload pairs never
interleave; after cancelling a send, abandon the session because the child may have received a
prefix — the exception being an interruption that lands while the call is still queued behind another
send or waiting for a `Stdin(source)` feeder, which cannot have written a byte (`JsonRpcSession`, layered
on this type, tells the two apart so a cancelled call does not end its conversation).
`FinishInputAsync` closes framed stdin and lets the child observe EOF.

Payloads remain raw for byte accuracy and NativeAOT-friendly caller control. For typed JSON, pass
each frame to `JsonSerializer.Deserialize(frame, MyJsonContext.Default.Message)` (or the matching
source-generated `JsonTypeInfo`) and serialize outgoing values to UTF-8 bytes before `SendAsync`.
The session is the sole stdout consumer: do not combine it with `OutputStringAsync`, line/NDJSON
streaming, `PtySession`, or another framed session on the same handle. Stderr is drained separately
and still reaches `StderrTee`.

`Command.StreamBuffer` bounds the *unread frame backlog* the same way it bounds a line stream, so a
chatty server cannot grow the parent's memory without limit while your consumer lags. Only the two
lossless full modes apply: `Backpressure` paces the parser — and, through the pipe, the child —
against your consumer, and `Error` faults the frame stream at the cap. `DropOldest`/`DropNewest` are
refused at construction with `ProcessError.Unsupported`: dropping a queued frame would delete a
protocol message the peer is correlating with a request, and no consumer could tell. Leaving
`StreamBuffer` unset keeps the default unbounded backlog.

With a bounded backlog, drain `FramesAsync()` **concurrently** with your sends rather than awaiting a
send first — backpressure deliberately stops the parser (and the child) once the backlog is full, so
a consumer that only starts reading after some other await can stall the very child it waits on. The
constructor itself never waits on the child: on a `Stdin(source)` + `KeepStdinOpen` run the source
feeder is awaited by the first `SendAsync`/`FinishInputAsync` instead, so you always get the session
back and can start draining frames (the interactive writer still never shares the pipe with the
feeder).

## JSON-RPC sessions (LSP / BSP / MCP)

Framing bytes is only half of driving a language server. The other half is the protocol those
frames carry: JSON-RPC 2.0, where every request needs a unique `id`, every answer must be matched
back to the call that is waiting for it, and the peer sends notifications and its own requests down
the same stream at any time. `JsonRpcSession` is that layer — it owns one `ContentLengthSession`
over the handle and turns it into `RequestAsync` / `NotifyAsync` / a stream of incoming messages.

**Debug adapters are not JSON-RPC peers.** DAP borrows LSP's `Content-Length` framing but not its
envelope — its messages are `{"seq":1,"type":"request","command":"next","arguments":{}}` and
`{"seq":7,"type":"response","request_seq":1,"success":true,...}`, with no `jsonrpc`, `method`, or
`id` member. `JsonRpcSession` ends on the first such frame with `ProcessError.Parse` instead of
guessing at it; drive a debug adapter with `ContentLengthSession` (above) and decode that envelope
yourself.

**F#**

<!-- docsnippet:imports System.Text.Json -->
```fsharp
task {
    let command = (Command.create "language-server").KeepStdinOpen()

    match! command.StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use proc = proc
        let session = JsonRpcSession(proc)

        // Raw JSON in, raw JSON out: no serializer at all, so this path is always trim-/AOT-safe.
        match! session.RequestRawAsync("initialize", """{"processId":null}""", TimeSpan.FromSeconds 30.0) with
        | Error err -> eprintfn $"{err.Message}"
        | Ok capabilities ->
            printfn $"server capabilities: {capabilities}"
            let! _ = session.NotifyRawAsync("initialized", "{}")

            // Notifications and the server's own requests arrive here, never through RequestAsync.
            let incoming = session.MessagesAsync().GetAsyncEnumerator()

            try
                let! received = incoming.MoveNextAsync()

                if received && incoming.Current.IsRequest then
                    let! _ = session.RespondErrorAsync(incoming.Current, -32601, "Method not found")
                    ()
            finally
                incoming.DisposeAsync().AsTask().Wait()
}
```

**C#**

<!-- docsnippet:imports System.Text.Json -->
```csharp
record HoverParams(string File, int Line);
record HoverResult(string Contents);

await using var server =
    (await new Command("language-server").KeepStdinOpen().StartAsync()).GetValueOrThrow();
var rpc = new JsonRpcSession(server);

var hover = await rpc.RequestAsync<HoverParams, HoverResult>(
    "textDocument/hover",
    new HoverParams("Program.fs", 12),
    options: null,
    timeout: TimeSpan.FromSeconds(10));

Console.WriteLine(hover switch
{
    { IsOk: true, ResultValue: var value } => value.Contents,
    { ErrorValue: ProcessError.JsonRpc e } => $"server refused: {e.Code} {e.Detail}",
    { ErrorValue: var err } => err.Message,
});
```

The overloads above serialize by reflection. In a trimmed or NativeAOT application pass
source-generated metadata instead — every verb has a `JsonTypeInfo` overload, and the `...RawAsync`
verbs need no metadata at all:

<!-- docsnippet:ignore reason: needs a source-generated JsonSerializerContext, which must be a top-level partial type the snippet harness cannot host -->
```csharp
var hover = await rpc.RequestAsync(
    "textDocument/hover",
    new HoverParams("Program.fs", 12),
    LspJson.Default.HoverParams,
    LspJson.Default.HoverResult,
    TimeSpan.FromSeconds(10));
```

Every failure is a typed `ProcessError`, never a raw exception and never a silent wait:

| What happened | Result |
|---|---|
| The peer answered with an `error` object | `ProcessError.JsonRpc` with its `Method`, `Code`, `Detail`, and the raw JSON of `Data` |
| The request timed out (timeout overloads) | `ProcessError.Timeout`; the waiter is dropped, so a late answer is discarded |
| The `CancellationToken` fired | `ProcessError.Cancelled` |
| A timeout or token interrupted a send mid-frame | The same `ProcessError.Timeout`/`Cancelled` — and it ends the session, because the peer may have received a truncated frame |
| A timeout or token ended a send before it wrote anything | The same `ProcessError.Timeout`/`Cancelled`, failing only that call — nothing reached the peer, so the session stays usable |
| A `StreamBuffer` cap with `StreamFullMode.Error` filled up | `ProcessError.OutputTooLarge`, ending the session like a protocol failure (see the backlog note below) |
| The peer's framed output ended before answering | `ProcessError.Io` — and every later verb fails the same way instead of waiting forever |
| The `result` does not fit the requested type | `ProcessError.Parse` (a JSON `null` result included — read it with `RequestRawAsync`) |
| The peer sent something that is not a JSON-RPC message | `ProcessError.Parse`, ending the session: pending requests all fail with it and `MessagesAsync` faults with `ProcessException` |

Requests may be issued concurrently — each gets its own `id`, and answers are routed by `id`, never
by arrival order. Without a timeout a request waits until the peer answers, its output ends, or the
token fires; pass a timeout for a peer that can go silent while still running. That budget covers the
whole call, not just the wait: a peer that stops reading its own stdin blocks the write once the pipe
buffer fills, and the request fails with `ProcessError.Timeout` there too rather than hanging. Since
such a write may have delivered only part of a frame — which no peer can resynchronize from — a send
interrupted *while it was writing* ends the conversation: pending requests fail with that same error
and later requests/sends report it instead of writing into a stream the peer can no longer read.
Incoming messages are unaffected (a torn *outgoing* frame does not corrupt what the peer says) and
keep arriving on `MessagesAsync` until the peer's output ends.

A send that was interrupted **before** it wrote anything is the ordinary case, and it fails alone: an
already-cancelled token, a per-request timeout that elapses while the call is still queued behind
another send, or one that elapses while the very first send is still waiting for a `Stdin(source)`
feeder to hand over the pipe, all leave the peer's stdin untouched. Cancelling one request — the
completion an editor abandons on the next keystroke — therefore never ends the conversation, and a
per-request timeout bounds its own call rather than the session. The framing layer underneath reports
which of the two happened, so "the session is over" always means a frame really was being written.

Two backlogs sit behind a session, with separate knobs. The third constructor argument
(`messageBacklog`, 1024 by default) bounds the *decoded* messages waiting for `MessagesAsync`, dropping
the oldest and counting them in `DroppedMessages`. `Command.StreamBuffer` bounds the *raw frame*
backlog underneath, through the `ContentLengthSession` this session owns, and only its lossless full
modes apply there: `Backpressure` paces the peer against the router, and `Error` ends the conversation
with `ProcessError.OutputTooLarge` at the cap. `DropOldest`/`DropNewest` are refused when the session is
constructed — the constructor throws `ProcessException` carrying `ProcessError.Unsupported`, since
dropping a queued frame would delete a message the peer is correlating with a request. Leaving
`StreamBuffer` unset keeps the default unbounded frame backlog.

`MessagesAsync` is a single-consumer stream of everything that is *not* an answer to your own
requests: notifications (`IsRequest` false) and the peer's own requests (`IsRequest` true, answer
them with `RespondAsync` / `RespondRawAsync` / `RespondErrorAsync`, which echo its `id` verbatim).
Read `ParamsJson` or call `ParamsAs<T>`; answering a notification is a typed
`ProcessError.Unsupported`, since the peer is not waiting for one. The backlog is bounded (1024
messages by default, the third constructor argument): when a consumer falls behind, the oldest
messages are dropped and counted in `DroppedMessages` rather than growing without limit or stalling
the answers other calls are waiting for.

This session owns the handle exactly as `ContentLengthSession` does — it creates that session
itself, so the frames are never exposed for a second reader — and `FinishInputAsync` closes the
peer's stdin for the usual `shutdown`/`exit` handshake. Dispose the `RunningProcess` (or its owning
`ProcessGroup`) to reap the tree.

## Readiness probes

"Start a server, then use it" needs the server to be *ready*, not merely started.
Five probes replace the arbitrary sleep, each bounded by its own deadline and each
returning a `Result`:

**F#**

<!-- docsnippet:imports System.Net, System.Net.Http -->
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

        // 3. A Unix domain socket accepting connections:
        match! proc.WaitForSocketAsync("/run/my-server.sock", TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "socket is open"
        | Error(ProcessError.Unsupported detail) -> eprintfn $"this host can't dial AF_UNIX: {detail}"
        | Error err -> eprintfn $"{err.Message}"

        // 4. An HTTP endpoint (any 2xx response is ready by default):
        let health = Uri("http://127.0.0.1:8080/health")

        match! proc.WaitForHttpAsync(health, TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "HTTP health check passed"
        | Error err -> eprintfn $"{err.Message}"

        // Supply a configured, caller-owned client for auth headers, custom TLS, proxies, or UDS HTTP.
        use healthClient = new HttpClient()
        healthClient.DefaultRequestHeaders.Add("Authorization", "Bearer local-health-token")

        match! proc.WaitForHttpAsync(health, healthClient, TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "configured HTTP health check passed"
        | Error err -> eprintfn $"{err.Message}"

        // 5. Any async predicate (a file appearing, a custom dependency check, …):
        match! proc.WaitForAsync((fun () -> healthCheck ()), TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "healthy"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

<!-- docsnippet:imports System.Net, System.Net.Http -->
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

// 3. A Unix domain socket accepting connections:
Console.WriteLine(await proc.WaitForSocketAsync("/run/my-server.sock", TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "socket is open",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// 4. An HTTP endpoint (any 2xx response is ready by default):
var health = new Uri("http://127.0.0.1:8080/health");

Console.WriteLine(await proc.WaitForHttpAsync(health, TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "HTTP health check passed",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// Supply a configured, caller-owned client for auth headers, custom TLS, proxies, or UDS HTTP.
using var healthClient = new HttpClient();
healthClient.DefaultRequestHeaders.Add("Authorization", "Bearer local-health-token");

Console.WriteLine(await proc.WaitForHttpAsync(health, healthClient, TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "configured HTTP health check passed",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// 5. Any async predicate (a file appearing, a custom dependency check, …):
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
- All five probes background-drain the child's piped stdout/stderr while polling, so a chatty
  child that writes more than one OS pipe buffer of startup output (~64 KiB on Linux) before
  becoming ready can't block in `write()` and spuriously fail the probe with `NotReady`.
  `WaitForLineAsync` hands the drained stdout back to you (consumed up to and including the
  matching line — continue with `FinishAsync()` or further streaming afterwards); `WaitForPortAsync` /
  `WaitForSocketAsync` / `WaitForHttpAsync` / `WaitForAsync` discard what they drain and stop draining
  once the probe concludes. Either way, a capture verb called afterward (`OutputStringAsync`/
  `OutputBytesAsync`/a fresh `StdoutLinesAsync`/`OutputEventsAsync`) only sees output the child wrote
  *after* the probe concluded — run probes before a capturing verb if you need the complete output.
- `WaitForSocketAsync` requires the host to support `AF_UNIX` sockets (Windows 10 1809+, any current
  Linux/macOS); a host without that support fails immediately with `ProcessError.Unsupported`, before
  ever attempting to dial — never a silent downgrade or a hang.

`WaitForAsync` takes a function returning `Task<bool>` (`Func<Task<bool>>` from C#), so any
async health check fits — re-evaluated until it returns `true` or the deadline elapses.

`WaitForHttpAsync` sends GET requests every 50ms until it receives a 2xx response. Pass a
`seq&lt;int&gt;` of acceptable status codes or a `Func<HttpResponseMessage, bool>` overload when a
non-2xx response or response-specific validation defines readiness. Every HTTP overload also accepts
a caller-owned `HttpClient`, enabling authentication headers, custom certificate validation, proxies,
and transports such as HTTP over a Unix domain socket; ProcessKit reuses but never mutates or disposes
that client. HTTP probe URIs must be absolute, and an explicit acceptable-status sequence must contain
at least one value. `WaitForSocketAsync` likewise rejects a socket path that the platform cannot encode
before polling begins instead of spending the full timeout on a permanently invalid endpoint.
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
        printfn $"read={profile.IoReadBytes} write={profile.IoWriteBytes}"
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
Console.WriteLine($"read={profile.IoReadBytes} write={profile.IoWriteBytes}");
```

`ProfileAsync()` with no argument uses a default sampling interval; `ProfileAsync(interval)`
samples at the cadence you pick. The resulting `RunProfile` exposes `ExitCode`,
`Duration` (wall clock), `CpuTime` (user + kernel), `PeakMemoryBytes`, the number of
`Samples` taken, and `AvgCpuCores` — CPU time over wall time, so a value near `1.7` means
roughly 1.7 cores were busy on average. `IoReadBytes`, `IoWriteBytes`,
`IoReadOperations`, and `IoWriteOperations` report the whole private containment tree
when one is attributable to this run (currently the per-run Windows Job Object).

CPU and memory describe the started child; the I/O counters describe its private tree.
A run started inside a shared `ProcessGroup` leaves profile I/O as `None`, because the
group aggregate also includes siblings. That includes Linux cgroup v2: sample its
`io.stat` aggregate explicitly through `ProcessGroup.Stats` / `SampleStatsAsync`
([Process groups](process-groups.md)). See the [platform matrix](platform-support.md#process-groups)
for availability.

---

Next: [Pseudo-terminal (PTY)](pty.md)
