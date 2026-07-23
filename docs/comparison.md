# Comparison and migration guide

[Previous: Overview](./)

How ProcessKit compares to `System.Diagnostics.Process` and three widely-used
third-party process-running libraries for .NET — [CliWrap](https://github.com/Tyrrrz/CliWrap),
[Medallion.Shell](https://github.com/madelson/MedallionShell), and
[SimpleExec](https://github.com/adamralph/simple-exec) — plus short "was → now" recipes for the
most common migration patterns.

This is not a "everyone else is bad" pitch: all three libraries are solid, widely used, and each
has real strengths ProcessKit doesn't try to match (see each section below). The comparison below
is scoped to the axes where ProcessKit makes a deliberate, load-bearing choice; if a library isn't
mentioned on an axis, assume it does not attempt that particular guarantee, not that it is somehow
deficient in general. All descriptions reflect the *current, public API surface* of the alternative
libraries at the time of writing (2026) — if a library ships a newer capability after this guide
was written, treat this page as possibly stale on that one row rather than authoritative.

## At a glance

Choose the library by the guarantee you need, rather than trying to scan a six-column feature
matrix:

### `System.Diagnostics.Process`

- **Best at:** zero-dependency, low-level access to every `ProcessStartInfo` option.
- **Compared with ProcessKit:** it tracks only the direct child. `ExitCode` is available as data,
  but start failures throw and timeout/signal/non-zero cases are not one typed result. Its output
  callbacks require manual event lifecycle management; there are no built-in readiness probes,
  pipelines, supervision, runner seam, or observability.

### CliWrap

- **Best at:** fluent command construction, shell-free pipelines, and streaming output.
- **Compared with ProcessKit:** cancellation can terminate the tree for one invocation, but there
  is no persistent disposable group shared by several commands. Non-zero exits throw by default
  unless validation is disabled, and it has no readiness probes, supervision, formal runner seam,
  or built-in secret-safe telemetry.

### Medallion.Shell

- **Best at:** straightforward synchronous or asynchronous commands and shell-free pipelines.
- **Compared with ProcessKit:** `Command.Result` exposes `ExitCode`/`Success` without throwing,
  but it does not provide whole-tree containment or a closed typed error model for start, timeout,
  and signal failures. Readiness probes, supervision, a runner seam, and observability are absent.

### SimpleExec

- **Best at:** deliberately small build-script and CLI glue, with convenient console echoing.
- **Compared with ProcessKit:** it is exception-first for non-zero exits (unless `noThrow` is set)
  and has no line streaming, pipeline model, whole-tree containment, readiness probes,
  supervision, runner substitution, or built-in telemetry. Disable command echo before passing
  sensitive arguments.

> **What ProcessKit combines:** kernel-backed kill-on-dispose containment for the whole process
> tree, honest typed outcomes, async line streaming and readiness probes, shell-free pipelines,
> restart supervision, the `IProcessRunner` testing seam, and secret-safe logging, tracing, and
> metrics.
## `System.Diagnostics.Process`

The BCL type every .NET process library — including ProcessKit — is ultimately built on. Its
strengths: zero extra dependency, full control over every `ProcessStartInfo` knob, and it is the
lowest common denominator every other library on this page eventually needs to escape hatch to for
something exotic.

Where ProcessKit differs: `Process` only ever tracks the one process it started directly. Anything
*that* process spawns — a build tool's compiler workers, the real payload behind `cmd /c`/`sh -c`,
a test's helper server — is invisible to `Process` and outlives a `Process.Kill()`, a timeout, or a
crashed test runner as an orphan. Getting `Process` to contain a whole tree requires hand-rolling a
Windows Job Object or a Linux cgroup/process-group yourself; ProcessKit does that unconditionally,
underneath every verb, as the default rather than an advanced technique.

## CliWrap

A mature, widely-adopted, dependency-free library with a clean fluent builder
(`Cli.Wrap("git").WithArguments(...)`), first-class shell-free piping via the `|` operator, and a
`PipeSource`/`PipeTarget` abstraction that lets stdin/stdout attach to a `Stream`, a file, a
`StringBuilder`, or another command with the same syntax — genuinely nice API design, and a good
default choice for scripting scenarios that don't need tree containment or supervision.

Where ProcessKit differs: CliWrap's honest-by-default posture stops at *this run's* exit code —
by default a non-zero exit throws `CommandExecutionException`, and turning that off
(`CommandResultValidation.None`) still leaves the caller pattern-matching loosely-typed exceptions
for the timeout/cancellation/not-found cases rather than one closed, structured error type. There is
also no persistent container object: CliWrap's tree-aware cancellation kills what *that* invocation
spawned, but there is nothing to `use`/dispose across several related commands the way
`ProcessGroup` does, no readiness-probe helpers, no supervision, and no injectable runner interface
for hermetic unit tests (you mock `PipeSource`/`PipeTarget` streams instead of the process runner
itself).

## Medallion.Shell

A small, pragmatic cross-platform wrapper with the same appealing `|` pipe operator as CliWrap, a
synchronous-*and*-async API (useful in non-async call sites), and a straightforward
`Command.Run(...)` entry point that reads naturally for simple "run this, get the result" call
sites.

Where ProcessKit differs: Medallion.Shell does not attempt whole-tree containment (only the
directly-spawned process is tracked/killed), does not have a typed error result — `Command.Result`
gives you `ExitCode` and `Success`, but timeouts, signals, and spawn failures aren't unified into
one pattern-matchable type — and, like CliWrap, has no readiness-probe helpers, no supervision, and
no formal mockable seam (tests either run the real process or wrap `Command` themselves).

## SimpleExec

The simplest of the three: a couple of static methods (`Command.Run`, `Command.ReadAsync`) designed
for build scripts and CLI tooling glue (it shows up a lot in `Cake`/`Nuke`/custom build scripts). Its
minimalism is the point — no pipeline DSL, no event streams, just "run this and get stdout, or throw."
It also echoes the command line and output to the console by default, which is genuinely convenient
in CI logs for a build script.

Where ProcessKit differs: SimpleExec is exception-first (a non-zero exit throws
`SimpleExec.ExitCodeException` unless the caller passes `noThrow: true` — there's no separate
honest-result verb to opt into instead), has no line-by-line streaming API, no shell-free pipeline
concept, no whole-tree containment, and no runner seam to substitute in tests. Its console-echo
default is also worth flagging from a secrets standpoint: it prints the full command line (including
arguments) to the console by default, which is the opposite of ProcessKit's "argv and environment
values are never logged automatically" stance — fine for a build script's own trusted commands, but
worth turning off (`echoCommand: false`) before echoing anything derived from external input.

## Measured comparison

The qualitative differences above are backed by a small BenchmarkDotNet suite
(`benchmarks/ProcessKit.Benchmarks/ComparisonBenchmarks.fs`) that runs the *same* three scenarios
against ProcessKit, raw `System.Diagnostics.Process`, and CliWrap:

1. **Single spawn + capture** — start one short-lived shell child and capture its stdout.
2. **Streaming** — read ~2000 lines of stdout from a child, line by line, without buffering the
   whole payload in memory.
3. **Concurrent batch** — fan out `N` short-lived children concurrently and capture each one's
   stdout.

MedallionShell and SimpleExec are not part of the numeric comparison — see their sections above for
why (SimpleExec has no line-streaming API at all, so scenario 2 has nothing equivalent to measure;
MedallionShell's async surface is shaped differently enough that a faithful three-scenario harness
for it was judged not worth the added benchmark-project surface right now).

> **Measured**: 2026-07-17, Windows 11 (10.0.22621), Intel Core i9-9880H, .NET SDK 10.0.301 / .NET
> 10.0.9 runtime, BenchmarkDotNet v0.15.8, in-process toolchain. Scenario 1 also ran BenchmarkDotNet's
> full statistically-rigorous default job; scenarios 2 and 3 used the reduced-iteration `Job.ShortRun`
> configuration (the same one `.github/workflows/benchmarks.yml` uses). This machine had other
> concurrent builds running while measuring, so the absolute numbers are noisy (see the wide
> `Error`/`StdDev` spread, especially the CliWrap FanOut=8 row below) — read this as an
> order-of-magnitude trend, not a lab-grade number; reproduce locally on a quiet machine for a
> tighter measurement via `dotnet run -c Release --project benchmarks/ProcessKit.Benchmarks --
> --filter "*ComparisonBenchmarks*"` or a scenario-specific class name
> (`SingleSpawnCaptureBenchmarks` / `StreamingBenchmarks` / `ConcurrentBatchBenchmarks`).

Mean wall-clock time per operation:

| Scenario | RawProcess | ProcessKit | CliWrap |
|---|---:|---:|---:|
| Single spawn + capture | 12.55 ms | 12.55 ms | 39.75 ms |
| Streaming ~2000 lines | 288.5 ms | 293.4 ms | 327.2 ms |
| Concurrent batch, FanOut=2 | 190.6 ms | 97.5 ms | 173.5 ms |
| Concurrent batch, FanOut=8 | 54.3 ms | 53.4 ms | 753.7 ms |

Allocated managed memory per operation (`MemoryDiagnoser`, same runs):

| Scenario | RawProcess | ProcessKit | CliWrap |
|---|---:|---:|---:|
| Single spawn + capture | 15.14 KB | 65.86 KB | 48.17 KB |
| Streaming ~2000 lines | 1093.09 KB | 427.38 KB | 1123.25 KB |
| Concurrent batch, FanOut=2 | 30.71 KB | 133.11 KB | 96.69 KB |
| Concurrent batch, FanOut=8 | 121.39 KB | 526.31 KB | 382.81 KB |

Reading these: ProcessKit's per-call latency tracks the raw `Process` baseline closely across every
scenario, within this run's measurement noise (the concurrent-batch rows even show ProcessKit
*faster* than the baseline — plausibly noise from the shared machine rather than a generalizable
result, so don't read too much into that one comparison). Where ProcessKit consistently costs more is
allocated memory per call, which matches the trade-off described qualitatively above: the whole-tree
kill-on-drop container, the typed structured `Result` (rather than a bare exit code or a thrown
exception), and the streaming/backpressure plumbing all cost allocations that a bare `Process` call
— or CliWrap's thinner honest-by-default posture — doesn't pay for.

## Migration recipes

Every snippet below assumes `open ProcessKit` (F#) / `using ProcessKit;` (C#), matching the rest of
the docs. See [Running commands](commands.md), [Streaming & interactive I/O](streaming.md), and
[Pipelines](pipelines.md) for the full picture of each verb used here.

### `Process.Start` + manual stream reading → a verb

**Before (F#, raw `System.Diagnostics.Process`)**

```fsharp
task {
    let psi =
        ProcessStartInfo("git", "rev-parse HEAD", RedirectStandardOutput = true, UseShellExecute = false)

    use proc = Process.Start psi
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    do! proc.WaitForExitAsync()

    if proc.ExitCode <> 0 then
        eprintfn $"git failed with {proc.ExitCode}"
    else
        printfn $"HEAD is {stdout.Trim()}"
}
```

**After (F#, ProcessKit)**

```fsharp
task {
    match! (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]).OutputStringAsync() with
    | Ok result -> printfn $"HEAD is {result.Stdout.Trim()}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**Before (C#, raw `System.Diagnostics.Process`)**

```csharp
var psi = new ProcessStartInfo("git", "rev-parse HEAD")
{
    RedirectStandardOutput = true,
    UseShellExecute = false,
};

using var process = Process.Start(psi)!;
var stdout = await process.StandardOutput.ReadToEndAsync();
await process.WaitForExitAsync();

Console.WriteLine(process.ExitCode != 0
    ? $"git failed with {process.ExitCode}"
    : $"HEAD is {stdout.Trim()}");
```

**After (C#, ProcessKit)**

```csharp
Console.WriteLine(await new Command("git").Args(["rev-parse", "HEAD"]).OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => $"HEAD is {result.Stdout.Trim()}",
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

`OutputStringAsync()` captures stdout/stderr and reports the exit code as data — no manual
`WaitForExitAsync` + `ReadToEndAsync` ordering to get right, and the whole tree it spawns is
contained and reaped even if the task is abandoned mid-run.

### A CliWrap pipeline → `Pipeline`

**Before (C#, CliWrap)**

```csharp
var result = await (
    Cli.Wrap("git").WithArguments(["log", "--format=%an"])
    | Cli.Wrap("sort")
    | Cli.Wrap("uniq").WithArguments("-c")
).ExecuteBufferedAsync();

Console.WriteLine(result.StandardOutput);
```

**After (C#, ProcessKit)**

```csharp
var pipeline = new Command("git").Args(["log", "--format=%an"])
    .Pipe(new Command("sort"))
    .Pipe(new Command("uniq").Arg("-c"));

Console.WriteLine(await pipeline.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var output } => output.Stdout,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

**Before (F#, CliWrap)** — CliWrap's `|` is a plain operator overload on `.NET` types, so it works
unchanged from F#:

```fsharp
task {
    let cmd =
        Cli.Wrap("git").WithArguments [ "log"; "--format=%an" ]
        |> fun git -> git | Cli.Wrap "sort" | Cli.Wrap("uniq").WithArguments "-c"

    let! result = cmd.ExecuteBufferedAsync()
    printfn $"{result.StandardOutput}"
}
```

**After (F#, ProcessKit)**

```fsharp
task {
    let pipeline =
        (Command.create "git" |> Command.args [ "log"; "--format=%an" ])
            .Pipe(Command.create "sort")
            .Pipe(Command.create "uniq" |> Command.arg "-c")

    match! pipeline.OutputStringAsync() with
    | Ok out -> printfn $"{out.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

`Pipeline` reports **pipefail** semantics out of the box: the exit code, stderr, and the reported
failing program come from the first stage that didn't exit cleanly, not just the last one — the
same information CliWrap's `ExecuteBufferedAsync()` does not surface per-stage without extra
plumbing. See [Pipelines](pipelines.md) for `Command.uncheckedInPipe` (the `producer | head -1`
case) and chain timeouts.

### Event-based output subscription → line streaming

**Before (C#, CliWrap's event stream)**

```csharp
await foreach (var cmdEvent in Cli.Wrap("dotnet").WithArguments(["build", "-c", "Release"]).ListenAsync())
{
    switch (cmdEvent)
    {
        case StandardOutputCommandEvent stdOut:
            Console.WriteLine(stdOut.Text);
            break;
        case StandardErrorCommandEvent stdErr:
            Console.Error.WriteLine(stdErr.Text);
            break;
        case ExitedCommandEvent exited:
            Console.WriteLine($"exited {exited.ExitCode}");
            break;
    }
}
```

**After (C#, ProcessKit)**

```csharp
await using var proc = (await new Command("dotnet").Args(["build", "-c", "Release"]).StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine(line);

var finished = (await proc.FinishAsync()).GetValueOrThrow();
Console.WriteLine($"exited {finished.Outcome}");
```

**Before (F#, `Process.OutputDataReceived`)**

```fsharp
let psi = ProcessStartInfo("dotnet", "build -c Release", RedirectStandardOutput = true, UseShellExecute = false)
use proc = new Process(StartInfo = psi)
proc.OutputDataReceived.Add(fun args -> if args.Data <> null then printfn $"{args.Data}")
proc.Start() |> ignore
proc.BeginOutputReadLine()
proc.WaitForExit()
```

**After (F#, ProcessKit)**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.args [ "build"; "-c"; "Release" ]).StartAsync() with
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

        match! proc.FinishAsync() with
        | Ok finished -> printfn $"exited {finished.Outcome}"
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

For a callback-style tee instead of consuming the async stream directly (logging/progress bars
alongside capture), see `Command.onStdoutLine` / `.OnStdoutLine(...)` in
[Running commands → Line handlers and tees](commands.md#line-handlers-and-tees) — the closest
equivalent to CliWrap's event-stream callback style, without giving up buffered capture.

## See also

- [Overview → Why ProcessKit?](./#why-processkit) — the elevator pitch and the
  core differentiators.
- [Running commands](commands.md), [Streaming & interactive I/O](streaming.md),
  [Pipelines](pipelines.md), [Supervision](supervision.md),
  [Testing your code](testing.md) — the full guide set each recipe above links into.

---

Next: [Cookbook](cookbook.md)
