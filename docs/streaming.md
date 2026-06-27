# Streaming & interactive I/O

[‹ docs index](README.md)

The one-shot verbs in [Running commands](commands.md) buffer the whole output and
hand it back when the child exits. For a long-running or conversational child you
want the output *as it arrives* — and sometimes a back-channel to write to it.
`Command.Start()` (and the equivalent `IProcessRunner.Start` / `ProcessGroup.Start`)
returns a live `RunningProcess` you drive yourself: stream stdout line by line,
interleave stdout and stderr, write stdin incrementally, wait for the child to
become *ready*, race several children, or profile a run end to end.

The samples below run inside a `task { }` block and use `match!`; the verbs that
return a value directly (`Wait`, `Profile`, `WaitAll`) use a plain `let!`. From C#
the same surface is `await`-able fluent methods, and the `IAsyncEnumerable<_>`
streams are `await foreach`.

- [Lifecycle](#lifecycle)
- [Streaming stdout line by line](#streaming-stdout-line-by-line)
- [Interleaving stdout and stderr](#interleaving-stdout-and-stderr)
- [Finishing a streamed run](#finishing-a-streamed-run)
- [Interactive stdin](#interactive-stdin)
- [Readiness probes](#readiness-probes)
- [Racing several children](#racing-several-children)
- [Profiling a run](#profiling-a-run)

## Lifecycle

`Start()` spawns the child and returns a `RunningProcess` without waiting for it to
exit. The handle is an `IAsyncDisposable`: a `use` binding inside `task { }` reaps
the whole process tree on scope exit, exactly like the disposal at the end of a
one-shot run.

**F#**

```fsharp
open ProcessKit
open System

task {
    match! (Command.create "dev-server").Start() with
    | Error err -> eprintfn $"could not start: {err.Message}"
    | Ok proc ->
        use _ = proc // disposing the handle kills the whole tree

        printfn $"pid={proc.Pid} started {proc.StartTime:o}"
        // ... drive the process: stream, write stdin, probe for readiness ...
        printfn $"alive for {proc.Elapsed}; {proc.StdoutLineCount} stdout lines so far"

        let! outcome = proc.Wait() // Outcome: Exited code / Signalled sig / TimedOut
        printfn $"exited: {outcome}"
}
```

**C#**

```csharp
using ProcessKit;
using System;

var started = await new Command("dev-server").Start();
if (started.IsError)
    Console.Error.WriteLine($"could not start: {started.ErrorValue.Message}");
else
{
    await using var proc = started.ResultValue; // disposing the handle kills the whole tree

    Console.WriteLine($"pid={proc.Pid} started {proc.StartTime:o}");
    // ... drive the process: stream, write stdin, probe for readiness ...
    Console.WriteLine($"alive for {proc.Elapsed}; {proc.StdoutLineCount} stdout lines so far");

    var outcome = await proc.Wait(); // Outcome: Exited code / Signalled sig / TimedOut
    Console.WriteLine($"exited: {outcome}");
}
```

`Start()` puts the child in a **private group the handle owns**: dropping the
`RunningProcess` kills the tree, grandchildren included. The shared-group
variant — `group.Start(cmd)` — returns the same kind of handle, but the *group*
controls the tree's fate (see [Process groups](process-groups.md)).

Consume the handle **exactly one way** — stdout is read once:

- `StdoutLines()` / `OutputEvents()` — stream output as it arrives (below).
- `OutputString()` / `OutputBytes()` — capture everything, like the one-shot verbs.
- `Wait()` — just the `Outcome`; output is discarded.
- `Finish()` — after streaming stdout, collect the `Outcome` and drained stderr.
- `Profile()` — capture plus periodic resource samples ([profiling](#profiling-a-run)).

`StdoutLines()` / `OutputEvents()` need a **piped** stdout, which is the default for
`Start()`; if you set `Command.Stdout` to `StdioMode.Inherit` or `StdioMode.Null`
there is nothing to stream. The live gauges `Pid`, `Elapsed`, `StartTime`,
`StdoutLineCount`, and `StderrLineCount` are cheap to read at any time, including
mid-stream. There is also `StartKill()` — "stop it now, I'll `Wait()` for the
`Outcome` myself" — which begins teardown without blocking.

A command's [`Timeout`](timeouts-and-cancellation.md) and `CancelOn` token **bound
the stream**: at the deadline (or on cancellation) the tree is killed, the pipes
close, and the stream ends — a streamed run can't hang past its deadline. After a
cancelled run, `Finish()` reports `ProcessError.Cancelled`.

## Streaming stdout line by line

`StdoutLines()` returns an `IAsyncEnumerable<string>` that yields decoded lines as
the child produces them — no waiting for exit, no full-output buffering. In F#,
drive the enumerator directly:

**F#**

```fsharp
open ProcessKit

task {
    match! (Command.create "git" |> Command.args [ "log"; "--oneline"; "-n"; "50" ]).Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLines().GetAsyncEnumerator()

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
using ProcessKit;
using System;

var started = await new Command("git").Args(new[] { "log", "--oneline", "-n", "50" }).Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    await foreach (var line in proc.StdoutLines())
        Console.WriteLine($"commit: {line}");
}
```

From C# the same loop is simply `await foreach (var line in proc.StdoutLines()) { ... }`.

While you stream stdout, stderr is drained in the background, so a noisy child can
never block on a full stderr pipe. The `OnStdoutLine` / `OnStderrLine` handlers and
the output buffer policy from [Running commands](commands.md) still apply to a
streamed run — a handler sees each line on the pump, in addition to your loop.

## Interleaving stdout and stderr

When the *order* of stdout relative to stderr matters — a build tool that prints
progress to one and diagnostics to the other — `OutputEvents()` returns an
`IAsyncEnumerable<OutputEvent>` that merges both channels in arrival order. Each
`OutputEvent` carries `IsStdout` / `IsStderr` and the line `Text`:

**F#**

```fsharp
open ProcessKit

task {
    match! (Command.create "dotnet" |> Command.args [ "build"; "-c"; "Release" ]).Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.OutputEvents().GetAsyncEnumerator()

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
using ProcessKit;
using System;

var started = await new Command("dotnet").Args(new[] { "build", "-c", "Release" }).Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    await foreach (var ev in proc.OutputEvents())
    {
        if (ev.IsStdout)
            Console.WriteLine($"out| {ev.Text}");
        else
            Console.Error.WriteLine($"err| {ev.Text}");
    }
}
```

From C#, `await foreach (var ev in proc.OutputEvents()) { ... }`. Choose `OutputEvents()`
*or* `StdoutLines()` for a given run — both consume stdout, so they are alternatives,
not companions.

## Finishing a streamed run

When the stream ends (stdout closed), collect the rest with `Finish()`, which returns
`Result<Finished, ProcessError>`. `Finished` carries the `Outcome` and the `Stderr`
that was drained while you streamed:

**F#**

```fsharp
open ProcessKit

task {
    match! (Command.create "build-everything").Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let e = proc.StdoutLines().GetAsyncEnumerator()

        try
            let mutable go = true

            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"> {e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()

        match! proc.Finish() with
        | Ok finished ->
            if finished.Outcome <> Outcome.Exited 0 then
                eprintfn $"failed ({finished.Outcome}):\n{finished.Stderr}"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using ProcessKit;
using System;

var started = await new Command("build-everything").Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    await foreach (var line in proc.StdoutLines())
        Console.WriteLine($"> {line}");

    var finished = await proc.Finish();
    if (finished.IsOk)
    {
        if (!finished.ResultValue.Outcome.Equals(Outcome.NewExited(0)))
            Console.Error.WriteLine($"failed ({finished.ResultValue.Outcome}):\n{finished.ResultValue.Stderr}");
    }
    else
        Console.Error.WriteLine(finished.ErrorValue.Message);
}
```

Use `Finish()` after you have streamed stdout. If you only need the exit status and
don't care about output, `Wait()` returns the `Outcome` directly and discards the
captured output; if you skipped streaming altogether, `OutputString()` /
`OutputBytes()` buffer and return everything just like the one-shot verbs.

## Interactive stdin

Conversational tools — write a request, read the response, repeat. Keep stdin open
with `KeepStdinOpen`, then take the writer with `TakeStdin()`, which returns a
`ProcessStdin option` (`Some` once; `None` if stdin wasn't kept open or was already
taken):

**F#**

```fsharp
open ProcessKit

task {
    // `bc` evaluates each stdin line and prints the result.
    match! (Command.create "bc" |> Command.keepStdinOpen).Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        match proc.TakeStdin() with
        | Some stdin ->
            do! stdin.WriteLine "2 + 2" // writes "2 + 2\n", flushed
            do! stdin.WriteLine "6 * 7"
            do! stdin.Finish() // send EOF so bc exits
        | None -> ()

        // ... then read proc.StdoutLines() for the answers.
        ()
}
```

**C#**

```csharp
using ProcessKit;

// `bc` evaluates each stdin line and prints the result.
var started = await new Command("bc").KeepStdinOpen().Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    var stdin = proc.TakeStdin();
    if (stdin != null) // FSharpOption: None is null
    {
        await stdin.Value.WriteLine("2 + 2"); // writes "2 + 2\n", flushed
        await stdin.Value.WriteLine("6 * 7");
        await stdin.Value.Finish(); // send EOF so bc exits
    }

    // ... then read proc.StdoutLines() for the answers.
}
```

`ProcessStdin` offers `WriteLine(line)` (appends a newline and flushes),
`Write(bytes)` (raw bytes, for binary input), `Flush()`, and `Finish()` (close
stdin / send EOF). Disposing the writer — or the whole `RunningProcess` — closes
stdin too; `Finish()` just makes the EOF explicit and awaitable.

**Avoid the full-duplex deadlock.** A child's stdout pipe has a finite OS buffer;
once it fills, the child blocks *writing* stdout until something reads it. If you
push a large interactive stdin while nothing drains the child's stdout, the child
stops reading stdin (blocked on stdout), your `Write` parks waiting for stdin buffer
space, and neither side progresses. The `bc` example above is safe because it
interleaves one small write with one read. When you both feed a sizable stdin **and**
the child produces output, write stdin from one task and drain stdout from another:

**F#**

```fsharp
open ProcessKit

task {
    match! (Command.create "transform" |> Command.keepStdinOpen).Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        match proc.TakeStdin() with
        | Some stdin ->
            // Producer: feed a large stdin on its own task.
            let writer =
                task {
                    for line in bigInput do
                        do! stdin.WriteLine line

                    do! stdin.Finish()
                }

            // Consumer: drain stdout concurrently on this task.
            let e = proc.StdoutLines().GetAsyncEnumerator()

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
using ProcessKit;

var started = await new Command("transform").KeepStdinOpen().Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    var stdin = proc.TakeStdin();
    if (stdin != null) // FSharpOption: None is null
    {
        // Producer: feed a large stdin on its own task.
        var writer = Task.Run(async () =>
        {
            foreach (var line in bigInput)
                await stdin.Value.WriteLine(line);

            await stdin.Value.Finish();
        });

        // Consumer: drain stdout concurrently on this task.
        await foreach (var line in proc.StdoutLines())
            handle(line);

        await writer;
    }
}
```

For *one-directional* streamed input (a channel, a file tail) you don't need
interactivity at all — give the command `Stdin.FromLines seq`,
`Stdin.FromAsyncLines asyncSeq`, or `Stdin.FromReader stream` and let ProcessKit's
background writer feed it; those sources run concurrently with the output pumps and
never deadlock. See the stdin source table in [Running commands](commands.md).

## Readiness probes

"Start a server, then use it" needs the server to be *ready*, not merely started.
Three probes replace the arbitrary sleep, each bounded by its own deadline and each
returning a `Result`:

**F#**

```fsharp
open ProcessKit
open System
open System.Net

task {
    match! (Command.create "my-server").Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        // 1. A line on stdout (returns the matching line):
        match! proc.WaitForLine((fun line -> line.Contains "listening on"), TimeSpan.FromSeconds 10.0) with
        | Ok banner -> printfn $"server says: {banner}"
        | Error(ProcessError.NotReady(program, timeout)) -> eprintfn $"{program} not ready after {timeout}"
        | Error err -> eprintfn $"{err.Message}"

        // 2. A TCP port accepting connections:
        let endpoint = IPEndPoint(IPAddress.Loopback, 8080)

        match! proc.WaitForPort(endpoint, TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "port is open"
        | Error err -> eprintfn $"{err.Message}"

        // 3. Any async predicate (an HTTP /health endpoint, a file appearing, …):
        match! proc.WaitFor((fun () -> healthCheck ()), TimeSpan.FromSeconds 10.0) with
        | Ok() -> printfn "healthy"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using ProcessKit;
using System;
using System.Net;

var started = await new Command("my-server").Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    // 1. A line on stdout (returns the matching line):
    var banner = await proc.WaitForLine(line => line.Contains("listening on"), TimeSpan.FromSeconds(10));
    if (banner.IsOk)
        Console.WriteLine($"server says: {banner.ResultValue}");
    else if (banner.ErrorValue is ProcessError.NotReady nr)
        Console.Error.WriteLine($"{nr.program} not ready after {nr.timeout}");
    else
        Console.Error.WriteLine(banner.ErrorValue.Message);

    // 2. A TCP port accepting connections:
    var endpoint = new IPEndPoint(IPAddress.Loopback, 8080);

    var port = await proc.WaitForPort(endpoint, TimeSpan.FromSeconds(10));
    if (port.IsOk)
        Console.WriteLine("port is open");
    else
        Console.Error.WriteLine(port.ErrorValue.Message);

    // 3. Any async predicate (an HTTP /health endpoint, a file appearing, …):
    var healthy = await proc.WaitFor(() => healthCheck(), TimeSpan.FromSeconds(10));
    if (healthy.IsOk)
        Console.WriteLine("healthy");
    else
        Console.Error.WriteLine(healthy.ErrorValue.Message);
}
```

Probe semantics are deliberately uniform:

- A probe that can't pass within its deadline fails with **`ProcessError.NotReady`** —
  distinct from `ProcessError.Timeout`, which is the run's own deadline.
- A probe also fails *fast* once readiness can no longer happen: the child exits, or
  (for `WaitForLine`) its stdout closes — no waiting out a 10s deadline on a dead
  server.
- A failed probe **never kills the child.** You decide what happens next: retry, log
  and continue, or tear down.
- `WaitForLine` consumes stdout up to (and including) the matching line — continue with
  `Finish()` or further streaming afterwards. `WaitForPort` / `WaitFor` don't touch the
  pipes at all.

`WaitFor` takes a function returning `Task<bool>` (`Func<Task<bool>>` from C#), so any
async health check fits — re-evaluated until it returns `true` or the deadline elapses.

## Racing several children

`RunningProcess.WaitAny` races several started handles and reports whichever exits
first — the natural primitive for "first answer wins" or "restart whatever died". It
returns `Result<WaitAnyResult, ProcessError>`, where `WaitAnyResult` carries the winner's
`Index` in the array you passed and its `Outcome`:

**F#**

```fsharp
open ProcessKit
open System

task {
    // Bound the race with a per-command Timeout — WaitAny applies none of its own.
    let withDeadline name =
        Command.create name |> Command.timeout (TimeSpan.FromSeconds 30.0)

    match! (withDeadline "replica-a").Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok a ->
        use _ = a

        match! (withDeadline "replica-b").Start() with
        | Error err -> eprintfn $"{err.Message}"
        | Ok b ->
            use _ = b

            match! RunningProcess.WaitAny [| a; b |] with
            | Ok result -> printfn $"contender #{result.Index} exited first with {result.Outcome}"
            | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using ProcessKit;
using System;

// Bound the race with a per-command Timeout — WaitAny applies none of its own.
Command withDeadline(string name) =>
    new Command(name).Timeout(TimeSpan.FromSeconds(30));

var startedA = await withDeadline("replica-a").Start();
if (startedA.IsError)
    Console.Error.WriteLine(startedA.ErrorValue.Message);
else
{
    await using var a = startedA.ResultValue;

    var startedB = await withDeadline("replica-b").Start();
    if (startedB.IsError)
        Console.Error.WriteLine(startedB.ErrorValue.Message);
    else
    {
        await using var b = startedB.ResultValue;

        var result = await RunningProcess.WaitAny(new[] { a, b });
        if (result.IsOk)
            Console.WriteLine($"contender #{result.ResultValue.Index} exited first with {result.ResultValue.Outcome}");
        else
            Console.Error.WriteLine(result.ErrorValue.Message);
    }
}
```

To join a fixed set instead of racing it, `RunningProcess.WaitAll` waits for *all* of
them and returns every `Outcome` in input order (an `Outcome[]` directly — no `Result`
wrapper):

**F#**

```fsharp
let! outcomes = RunningProcess.WaitAll [| a; b |]
printfn $"{outcomes.Length} children done"
```

**C#**

```csharp
var outcomes = await RunningProcess.WaitAll(new[] { a, b });
Console.WriteLine($"{outcomes.Length} children done");
```

Both apply **no per-process timeout** (bound the race with a `Command.Timeout`, as
above) and do **no output pumping** — drain chatty children first, or give them a
bounded output buffer policy, so a child can't stall on a full pipe while you wait.

## Profiling a run

A `RunningProcess` reports its own resource usage live, and `Profile()` turns a whole
run into a summary. The live gauges read the *child process itself* at any moment:

**F#**

```fsharp
open ProcessKit
open System

task {
    match! (Command.create "crunch").Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc

        // Live, mid-run:
        printfn $"pid={proc.Pid} elapsed={proc.Elapsed} cpu={proc.CpuTime} peak={proc.PeakMemoryBytes}"

        // Capture + sample on an interval until exit (returns a RunProfile directly):
        let! profile = proc.Profile(TimeSpan.FromMilliseconds 100.0)

        printfn $"exit={profile.ExitCode} wall={profile.Duration} samples={profile.Samples}"
        printfn $"cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpu}"
}
```

**C#**

```csharp
using ProcessKit;
using System;

var started = await new Command("crunch").Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    // Live, mid-run:
    Console.WriteLine($"pid={proc.Pid} elapsed={proc.Elapsed} cpu={proc.CpuTime} peak={proc.PeakMemoryBytes}");

    // Capture + sample on an interval until exit (returns a RunProfile directly):
    var profile = await proc.Profile(TimeSpan.FromMilliseconds(100));

    Console.WriteLine($"exit={profile.ExitCode} wall={profile.Duration} samples={profile.Samples}");
    Console.WriteLine($"cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpu}");
}
```

`Profile()` with no argument uses a default sampling interval; `Profile(interval)`
samples at the cadence you pick. The resulting `RunProfile` exposes `ExitCode`,
`Duration` (wall clock), `CpuTime` (user + kernel), `PeakMemoryBytes`, the number of
`Samples` taken, and `AvgCpu` — CPU time over wall time, so a value near `1.7` means
roughly 1.7 cores were busy on average.

These figures describe the started child, not a whole tree — for the tree's
aggregate use `ProcessGroup.Stats` / `SampleStats` ([Process groups](process-groups.md)).
Availability follows the platform: full CPU and memory on Windows and the Linux
cgroup backend, and `None` where the kernel doesn't account per-process cheaply — see
[Platform support](platform-support.md).

---

Next: [Pipelines](pipelines.md) ·
[Timeouts, retries & cancellation](timeouts-and-cancellation.md) ·
[Supervision](supervision.md)
