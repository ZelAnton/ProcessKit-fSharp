# ProcessKit cookbook

[‹ docs index](README.md)

Task-oriented, idiomatic examples for every part of the public API. The run and capture
verbs return `Task<Result<_, ProcessError>>`, so the F# samples below run inside a `task { }`
block and use `match!` (a few `RunningProcess` members — `WaitAsync`, `ProfileAsync` — return their
value directly). Where a snippet writes `let! r = cmd.Verb()` for brevity, `r` is the
`Result<_, ProcessError>` you then match. From C# the same surface is `await`-able fluent
methods.

- [Running a command](#running-a-command)
- [Capturing output](#capturing-output)
- [Error handling](#error-handling)
- [Exit codes and probing](#exit-codes-and-probing)
- [Accepting non-zero exits](#accepting-non-zero-exits)
- [Parsing output](#parsing-output)
- [Standard input](#standard-input)
- [Pipelines](#pipelines)
- [Streaming and interactive I/O](#streaming-and-interactive-io)
- [Readiness probes](#readiness-probes)
- [Timeouts, cancellation, retry](#timeouts-cancellation-retry)
- [Process groups and tree control](#process-groups-and-tree-control)
- [Resource limits](#resource-limits)
- [Stats and profiling](#stats-and-profiling)
- [Supervision](#supervision)
- [CliClient](#cliclient)
- [Top-level Exec helpers](#top-level-exec-helpers)
- [Preflight: is a program installed?](#preflight-is-a-program-installed)
- [Logging, tracing & metrics](#logging-tracing--metrics)
- [Dependency injection](#dependency-injection)
- [Testing without subprocesses](#testing-without-subprocesses)

## Running a command

Build a `Command` (an immutable value), then call a verb. `RunAsync` requires a zero (or
accepted) exit and returns stdout with trailing whitespace trimmed.

**F#**

```fsharp
task {
    let cmd = Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]

    match! cmd.RunAsync() with
    | Ok sha -> printfn $"HEAD is {sha}"
    | Error err -> eprintfn $"git failed: {err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("git").Args(["rev-parse", "HEAD"]);

Console.WriteLine(await cmd.RunAsync() switch
{
    { IsOk: true, ResultValue: var sha }  => $"HEAD is {sha}",
    { IsOk: false, ErrorValue: var err } => $"git failed: {err.Message}",
});
```

The builder is fluent and immutable — each method returns a new `Command`:

**F#**

```fsharp
let cmd =
    Command.create "dotnet"
    |> Command.args [ "build"; "-c"; "Release" ]
    |> Command.currentDir "/repo"
    |> Command.env "DOTNET_NOLOGO" "1"
```

**C#**

```csharp
var cmd = new Command("dotnet")
    .Args(["build", "-c", "Release"])
    .CurrentDir("/repo")
    .Env("DOTNET_NOLOGO", "1");
```

The same in method style (identical from C#):

**F#**

```fsharp
let cmd = (Command "dotnet").Args([ "build"; "-c"; "Release" ]).CurrentDir("/repo")
```

**C#**

```csharp
var cmd = new Command("dotnet").Args(["build", "-c", "Release"]).CurrentDir("/repo");
```

Use `RunUnitAsync` when you only care that it succeeded:

**F#**

```fsharp
match! (Command.create "mkdir" |> Command.arg "out").RunUnitAsync() with
| Ok () -> ()
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
if (await new Command("mkdir").Arg("out").RunUnitAsync() is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

## Capturing output

`OutputStringAsync` / `OutputBytesAsync` return a `ProcessResult<_>` — a *non-zero exit is data here,
not an error*. Inspect `Stdout`, `Stderr`, `Code`, `IsSuccess`, `Duration`, `Outcome`.

**F#**

```fsharp
match! (Command.create "ls" |> Command.arg "-la").OutputStringAsync() with
| Ok result ->
    printfn $"exit={result.Code} success={result.IsSuccess} in {result.Duration}"
    printfn $"{result.Stdout}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
switch (await new Command("ls").Arg("-la").OutputStringAsync())
{
    case { IsOk: true, ResultValue: var result }:
        Console.WriteLine($"exit={result.Code} success={result.IsSuccess} in {result.Duration}");
        Console.WriteLine(result.Stdout);
        break;
    case { IsOk: false, ErrorValue: var err }:
        Console.Error.WriteLine(err.Message);
        break;
}
```

`OutputBytesAsync` is the binary companion (`ProcessResult<byte[]>`), for non-text output.

## Error handling

`ProcessError` is a discriminated union — pattern-match it, or use `.Message` for a short
description. The capture verbs only error on *failure to run* (spawn / not-found / I/O /
timeout / cancellation), never on a non-zero exit.

**F#**

```fsharp
match! (Command.create "definitely-not-a-program").OutputStringAsync() with
| Ok result -> printfn $"{result.Stdout}"
| Error(ProcessError.NotFound(program, _)) -> eprintfn $"not installed: {program}"
| Error(ProcessError.Timeout(program, timeout, _, _)) -> eprintfn $"{program} timed out after {timeout}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
Console.WriteLine(await new Command("definitely-not-a-program").OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result }                => result.Stdout,
    { IsOk: false, ErrorValue: ProcessError.NotFound n }  => $"not installed: {n.Program}",
    { IsOk: false, ErrorValue: ProcessError.Timeout t }   => $"{t.Program} timed out after {t.Timeout}",
    { IsOk: false, ErrorValue: var err }                  => err.Message,
});
```

Classifiers help with retry/diagnostic logic:

**F#**

```fsharp
match! cmd.RunAsync() with
| Ok _ -> ()
| Error err when ProcessError.isNotFound err -> installThenRetry ()
| Error err when ProcessError.isTransient err -> scheduleRetry ()   // spawn / I/O blips
| Error err -> fail err
```

**C#**

```csharp
switch (await cmd.RunAsync())
{
    case { IsOk: true }:
        break;
    case { IsOk: false, ErrorValue: { IsNotFound: true } }:
        installThenRetry();
        break;
    case { IsOk: false, ErrorValue: { IsTransient: true } }:
        scheduleRetry();   // spawn / I/O blips
        break;
    case { IsOk: false, ErrorValue: var err }:
        fail(err);
        break;
}
```

The success-requiring verbs (`RunAsync` / `RunUnitAsync`) additionally turn a non-zero exit into
`ProcessError.Exit(program, code, stdout, stderr)`.

## Exit codes and probing

**F#**

```fsharp
let! code = (Command.create "grep" |> Command.args [ "pattern"; "file" ]).ExitCodeAsync()  // Ok 0 / Ok 1 / ...
let! found = (Command.create "which" |> Command.arg "git").ProbeAsync()                     // Ok true if exit 0
```

**C#**

```csharp
var code = await new Command("grep").Args(["pattern", "file"]).ExitCodeAsync();  // Ok 0 / Ok 1 / ...
var found = await new Command("which").Arg("git").ProbeAsync();                  // Ok true if exit 0
```

`ProbeAsync` is `true` when the command runs and exits zero — handy for feature detection.

## Accepting non-zero exits

Some tools use non-zero exits as information (e.g. `grep` returns 1 for "no match"). Tell
ProcessKit which codes count as success:

**F#**

```fsharp
let grep =
    Command.create "grep"
    |> Command.args [ "ERROR"; "app.log" ]
    |> Command.okCodes [ 0; 1 ]   // 1 ("no match") is not a failure

match! grep.RunAsync() with
| Ok output -> printfn $"matches:\n{output}"
| Error err -> eprintfn $"{err.Message}"   // a real failure (e.g. exit 2)
```

**C#**

```csharp
var grep = new Command("grep")
    .Args(["ERROR", "app.log"])
    .OkCodes([0, 1]);   // 1 ("no match") is not a failure

Console.WriteLine(await grep.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => $"matches:\n{output}",
    { IsOk: false, ErrorValue: var err }   => err.Message,   // a real failure (e.g. exit 2)
});
```

`OkCodes` sets which exit codes `ProcessResult.IsSuccess`, `RunAsync`/`RunUnitAsync`, and supervisor crash
detection accept — the codes *replace* the default `{0}` (include `0` to keep it, as `[ 0; 1 ]` does
above).

## Parsing output

`ParseAsync` maps stdout through a function (requires success); `TryParseAsync` uses the standard
.NET try-parse shape, so C# can pass `int.TryParse` (and friends) with an explicit type argument
(`TryParseAsync<int>(int.TryParse)` — needed because the BCL parsers are overloaded) and a `false`
return becomes `ProcessError.Parse` — F# reaches for the `Result`-returning `Runner.tryParse` instead;
`OutputJsonAsync<'T>` deserializes stdout as JSON via `System.Text.Json` (same explicit-type-argument
need, since there is no parser argument to infer `'T` from — `OutputJsonAsync<int>()`), takes an
optional `JsonSerializerOptions` overload, and turns invalid JSON into `ProcessError.Parse` just like
a rejecting parser; `FirstLineAsync` returns the first stdout line matching a predicate.

**F#**

```fsharp
let! version = (Command.create "node" |> Command.arg "--version").ParseAsync(fun s -> s.TrimStart('v'))
let! widget  = (Command.create "widget-cli" |> Command.arg "get").OutputJsonAsync<Widget>()
let! port    = (Command.create "myserver").FirstLineAsync(fun line -> line.StartsWith "Listening on ")
```

**C#**

```csharp
var version = await new Command("node").Arg("--version").ParseAsync(s => s.TrimStart('v'));
var count = await new Command("git").Args(["rev-list", "--count", "HEAD"]).TryParseAsync<int>(int.TryParse);
var widget = await new Command("widget-cli").Arg("get").OutputJsonAsync<Widget>();
var port = await new Command("myserver").FirstLineAsync(line => line.StartsWith("Listening on "));
```

A plain F# record deserializes through STJ's constructor-based deserialization by default, matching
JSON keys to the record's field names case-sensitively; mark the record `[<CLIMutable>]` for the
classic default-constructor-plus-settable-properties shape, or pass `options` with
`PropertyNameCaseInsensitive = true` for case-insensitive matching.

## Standard input

Feed input with a `Stdin` source:

**F#**

```fsharp
let cmd =
    Command.create "grep"
    |> Command.arg "needle"
    |> Command.stdin (Stdin.FromString "haystack\nneedle\nmore")
```

**C#**

```csharp
var cmd = new Command("grep")
    .Arg("needle")
    .Stdin(Stdin.FromString("haystack\nneedle\nmore"));
```

Sources: `Stdin.FromString`, `FromBytes`, `FromFile path`, `FromStream stream`,
`FromLines seq`, `FromAsyncLines asyncSeq`, and `Stdin.Empty`. For interactive writing, see
[streaming](#streaming-and-interactive-io).

## Pipelines

`Pipe` wires each stage's stdout into the next stage's stdin — no shell — and runs the whole
chain in one kill-on-dispose group. The exit status follows shell **pipefail**.

**F#**

```fsharp
let pipeline =
    (Command.create "cat" |> Command.arg "access.log")
        .Pipe(Command.create "grep" |> Command.arg "ERROR")
        .Pipe(Command.create "wc" |> Command.arg "-l")

match! pipeline.RunAsync() with
| Ok count -> printfn $"{count} error lines"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var pipeline = new Command("cat").Arg("access.log")
    .Pipe(new Command("grep").Arg("ERROR"))
    .Pipe(new Command("wc").Arg("-l"));

Console.WriteLine(await pipeline.RunAsync() switch
{
    { IsOk: true, ResultValue: var count } => $"{count} error lines",
    { IsOk: false, ErrorValue: var err }  => err.Message,
});
```

A pipeline supports the same verbs as a command (`RunAsync`/`OutputStringAsync`/`ExitCodeAsync`/…) plus
`Timeout` / `CancelOn`. Let a stage fail without failing the pipeline with
`Command.uncheckedInPipe`. The pipe-style module mirror is `Pipeline.create` / `Pipeline.pipe`.

## Streaming and interactive I/O

`StartAsync()` returns a live `RunningProcess`. Stream stdout line by line as an
`IAsyncEnumerable`. (`use` ensures the tree is killed on scope exit.)

**F#**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.arg "watch").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let lines = proc.StdoutLinesAsync()
        let e = lines.GetAsyncEnumerator()

        try
            let mutable go = true
            while go do
                match! e.MoveNextAsync() with
                | true -> printfn $"> {e.Current}"
                | false -> go <- false
        finally
            e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
await using var proc = (await new Command("dotnet").Arg("watch").StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"> {line}");
```

From C# this is simply `await foreach (var line in proc.StdoutLinesAsync()) { ... }`.

`OutputEventsAsync()` interleaves stdout and stderr as `OutputEvent` values (`IsStdout`/`IsStderr`,
`.Text`). Write to a running process's stdin via `TakeStdin()`:

**F#**

```fsharp
match proc.TakeStdin() with
| Some stdin ->
    do! stdin.WriteLineAsync "command one"
    do! stdin.FlushAsync()
    do! stdin.FinishAsync()   // close stdin (EOF)
| None -> ()
```

**C#**

```csharp
if (proc.TakeStdin() is { Value: var stdin }) // Some(stdin); None is null and won't match
{
    await stdin.WriteLineAsync("command one");
    await stdin.FlushAsync();
    await stdin.FinishAsync();   // close stdin (EOF)
}
```

Race or await several started processes with `RunningProcess.WaitAny` / `WaitAllAsync`.

## Readiness probes

Wait for a started process to become ready before proceeding:

**F#**

```fsharp
match! (Command.create "myserver").StartAsync() with
| Ok proc ->
    use _ = proc
    // Wait up to 10s for a log line, a TCP port, or a custom predicate.
    match! proc.WaitForLineAsync((fun l -> l.Contains "ready"), TimeSpan.FromSeconds 10.0) with
    | Ok _ -> printfn "server is up"
    | Error err -> eprintfn $"never became ready: {err.Message}"   // ProcessError.NotReady on timeout
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
await using var proc = (await new Command("myserver").StartAsync()).GetValueOrThrow();

// Wait up to 10s for a log line, a TCP port, or a custom predicate.
Console.WriteLine(await proc.WaitForLineAsync(l => l.Contains("ready"), TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true }        => "server is up",
    { IsOk: false, ErrorValue: var err } => $"never became ready: {err.Message}",   // ProcessError.NotReady on timeout
});
```

Also `WaitForPortAsync(endpoint, timeout)` and `WaitForAsync(predicateReturningTask, timeout)`.

## Timeouts, cancellation, retry

**F#**

```fsharp
let cmd =
    Command.create "slow-job"
    |> Command.timeout (TimeSpan.FromSeconds 30.0)   // kill at the deadline -> Outcome.TimedOut
    |> Command.retry 3 (TimeSpan.FromMilliseconds 200.0) (fun err -> ProcessError.isTransient err)
```

**C#**

```csharp
var cmd = new Command("slow-job")
    .Timeout(TimeSpan.FromSeconds(30))   // kill at the deadline -> Outcome.TimedOut
    .Retry(3, TimeSpan.FromMilliseconds(200), err => err.IsTransient);
```

`TimeoutGrace` sends SIGTERM, waits a grace window, then SIGKILL (atomic on Windows). Tie a
run to a `CancellationToken` with `CancelOn`, or pass a token to any verb's optional
token parameter (`cmd.RunAsync(ct)`). A cancelled run is always an `Error` (`ProcessError.Cancelled`).

## Process groups and tree control

A `ProcessGroup` is a kill-on-dispose container for a whole process *tree* (Windows Job
Object / Linux cgroup v2 / POSIX process group). It is itself an `IProcessRunner`.

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok group ->
        use group = group   // disposes (and reaps the whole tree) on scope exit

        match! group.StartAsync(Command.create "build-everything") with
        | Ok _proc ->
            group.Signal Signal.Term |> ignore     // signal the whole tree
            group.Suspend() |> ignore              // freeze it
            group.Resume() |> ignore               // thaw it
            match group.Members() with
            | Ok pids -> printfn $"{pids.Count} processes in the tree"
            | Error _ -> ()
            do! group.ShutdownAsync(TimeSpan.FromSeconds 5.0)   // graceful: SIGTERM -> grace -> SIGKILL
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using var group = ProcessGroup.Create().GetValueOrThrow();   // disposes (and reaps the whole tree) on scope exit

await group.StartAsync(new Command("build-everything"));

group.Signal(Signal.Term);     // signal the whole tree
group.Suspend();               // freeze it
group.Resume();                // thaw it
if (group.Members() is { IsOk: true, ResultValue: var pids })
    Console.WriteLine($"{pids.Count} processes in the tree");
await group.ShutdownAsync(TimeSpan.FromSeconds(5));   // graceful: SIGTERM -> grace -> SIGKILL
```

Portable `Signal` values: `Term`, `Kill`, `Int`, `Hup`, `Quit`, `Usr1`, `Usr2`,
`Signal.Other n`. On Windows only `Kill` is delivered. Share one container across a fleet by
passing the group as the `IProcessRunner` (e.g. to a `Supervisor`).

## Resource limits

Cap the whole tree's memory, process count, or CPU. Enforced by a Windows Job Object or a
Linux cgroup v2; where no limit-capable container exists, creation fails fast with
`ProcessError.ResourceLimit` rather than running unbounded.

**F#**

```fsharp
let options =
    ProcessGroupOptions()
        .WithMemoryMax(512L * 1024L * 1024L)   // 512 MiB
        .WithMaxProcesses(64)
        .WithCpuQuota(1.5)                      // 1.5 cores

match ProcessGroup.Create options with
| Ok group ->
    use group = group   // ... run within the limited group
    ()
| Error err -> eprintfn $"limits unavailable: {err.Message}"
```

**C#**

```csharp
var options = new ProcessGroupOptions()
    .WithMemoryMax(512L * 1024L * 1024L)   // 512 MiB
    .WithMaxProcesses(64)
    .WithCpuQuota(1.5);                     // 1.5 cores

var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine($"limits unavailable: {err.Message}");
    return;
}

using var group = created.GetValueOrThrow();   // ... run within the limited group
```

## Stats and profiling

**F#**

```fsharp
match group.Stats() with
| Ok stats ->
    printfn $"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}"
| Error _ -> ()

// A periodic series (IAsyncEnumerable) for live dashboards:
let series = group.SampleStatsAsync(TimeSpan.FromSeconds 1.0)
```

**C#**

```csharp
if (group.Stats() is { IsOk: true, ResultValue: var stats })
    Console.WriteLine($"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}");

// A periodic series (IAsyncEnumerable) for live dashboards:
var series = group.SampleStatsAsync(TimeSpan.FromSeconds(1));
```

Per-run profiling captures exit code, duration, CPU, and peak memory:

**F#**

```fsharp
match! (Command.create "heavy-job").StartAsync() with
| Ok proc ->
    use _ = proc
    let! profile = proc.ProfileAsync()
    printfn $"exit={profile.ExitCode} cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} samples={profile.Samples}"
| Error _ -> ()
```

**C#**

```csharp
await using var proc = (await new Command("heavy-job").StartAsync()).GetValueOrThrow();
var profile = await proc.ProfileAsync();
Console.WriteLine($"exit={profile.ExitCode} cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} samples={profile.Samples}");
```

## Supervision

Keep a command alive with policy-driven restarts, exponential backoff + jitter, and a
failure-storm guard.

**F#**

```fsharp
let outcome =
    (Supervisor.create (Command.create "worker"))
        .Restart(RestartPolicy.OnCrash)
        .Backoff(TimeSpan.FromSeconds 1.0, 2.0)        // base delay, multiplier
        .MaxBackoff(TimeSpan.FromMinutes 1.0)
        .Jitter(true)
        .MaxRestarts(20)
        .StormPause(TimeSpan.FromMinutes 5.0)          // pause after a burst of failures
        .RunAsync()

match! outcome with
| Ok result -> printfn $"stopped: {result.Stopped} after {result.Restarts} restarts"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var outcome = new Supervisor(new Command("worker"))
    .Restart(RestartPolicy.OnCrash)
    .Backoff(TimeSpan.FromSeconds(1), 2.0)        // base delay, multiplier
    .MaxBackoff(TimeSpan.FromMinutes(1))
    .Jitter(true)
    .MaxRestarts(20)
    .StormPause(TimeSpan.FromMinutes(5))          // pause after a burst of failures
    .RunAsync();

Console.WriteLine(await outcome switch
{
    { IsOk: true, ResultValue: var result } => $"stopped: {result.Stopped} after {result.Restarts} restarts",
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

Supervision runs through any `IProcessRunner` (`WithRunner`), so it is testable without
spawning processes, and it honours `OkCodes` when deciding what counts as a crash.

## CliClient

A reusable handle to one program with shared defaults:

**F#**

```fsharp
let git =
    (CliClient.create "git")
        .WithDefaults(fun c -> c.CurrentDir("/repo").Timeout(TimeSpan.FromSeconds 30.0))

let! sha = git.RunAsync [ "rev-parse"; "HEAD" ]
let! log = git.OutputStringAsync [ "log"; "--oneline"; "-n"; "10" ]
```

**C#**

```csharp
var git = new CliClient("git")
    .WithDefaults(c => c.CurrentDir("/repo").Timeout(TimeSpan.FromSeconds(30)));

var sha = await git.RunAsync(["rev-parse", "HEAD"]);
var log = await git.OutputStringAsync(["log", "--oneline", "-n", "10"]);
```

`WithDefaults` configures the shared defaults with the full `Command` builder; `client.Command args`
builds a configured `Command` without running it.

## Top-level Exec helpers

For one-off runs without first building a `Command`:

**F#**

```fsharp
let! sha = Exec.run "git" [ "rev-parse"; "HEAD" ]
let! info = Exec.outputString "dotnet" [ "--info" ]
```

**C#**

```csharp
var sha = await Exec.run("git", ["rev-parse", "HEAD"]);
var info = await Exec.outputString("dotnet", ["--info"]);
```

Run a batch with bounded concurrency, collecting every result in input order (never
short-circuits):

**F#**

```fsharp
let runner = JobRunner() :> IProcessRunner
let commands = files |> List.map (fun f -> Command.create "gzip" |> Command.arg f)
let! results = Exec.outputAll 4 runner commands CancellationToken.None // at most 4 live at once
```

**C#**

```csharp
var runner = new JobRunner();
var commands = files.Select(f => new Command("gzip").Arg(f));
var results = await Exec.outputAll(4, runner, commands, CancellationToken.None); // at most 4 live at once
```

## Preflight: is a program installed?

`Exec.which` resolves a program to a full path without running it — a `doctor` check for an
install wizard or a wrapper app's startup, cheaper than probing availability by actually launching
the program. It shares the exact `PATH`/`PATHEXT`-aware lookup the spawn path itself falls back on,
so it never disagrees with an actual spawn of the same program name (see
[commands.md → Preflight](commands.md#preflight-is-a-program-installed) for the full contract,
including Windows `PATHEXT` semantics).

**F#**

```fsharp
match Exec.which "git" with
| Ok path -> printfn $"git found at {path}"
| Error err -> eprintfn $"git is not available: {err.Message}"
```

**C#**

```csharp
Console.WriteLine(Exec.which("git") switch
{
    { IsOk: true, ResultValue: var path } => $"git found at {path}",
    { IsOk: false, ErrorValue: var err }  => $"git is not available: {err.Message}",
});
```

The same check on a `CliClient` resolves the client's own program, and is always a **local** host
check — never delegated to a runner injected via `WithRunner` (a `ScriptedRunner` used in the
wrapper's own tests has no bearing on it):

**F#**

```fsharp
match! git.EnsureAvailableAsync() with
| Ok path -> printfn $"git found at {path}"
| Error err -> eprintfn $"git is not available: {err.Message}"
```

**C#**

```csharp
Console.WriteLine(await git.EnsureAvailableAsync() switch
{
    { IsOk: true, ResultValue: var path } => $"git found at {path}",
    { IsOk: false, ErrorValue: var err }  => $"git is not available: {err.Message}",
});
```

## Logging, tracing & metrics

Opt in to structured lifecycle events (spawn, exit, timeout, retry, supervisor restart) — each with a
stable `EventId` and a per-run `RunId` that ties a run's lines together. **argv and the environment are
never logged** — only the program name and non-secret facts.

**F#**

```fsharp
let cmd = Command.create "deploy" |> Command.logger logger   // any Microsoft.Extensions.Logging ILogger
```

**C#**

```csharp
var cmd = new Command("deploy").Logger(logger);   // any Microsoft.Extensions.Logging ILogger
```

No-op and free when no logger is set. ProcessKit also emits a `System.Diagnostics` **trace span** per run
(`ActivitySource` `ProcessKitDiagnostics.ActivitySourceName`) and **metrics** (`Meter`
`ProcessKitDiagnostics.MeterName`) — wire them into OpenTelemetry with `AddSource(...)` / `AddMeter(...)`.
See the [Observability guide](observability.md) for the full event/instrument taxonomy.

## Dependency injection

The `ProcessKit.Extensions.DependencyInjection` package registers an `IProcessRunner`:

**F#**

```fsharp
services.AddProcessKit() |> ignore
// When the container also has an ILoggerFactory, runs emit ProcessKit's lifecycle events.

// Later, injected as IProcessRunner:
type Deployer(runner: IProcessRunner) =
    member _.Deploy() = Runner.run runner CancellationToken.None (Command.create "deploy")
```

**C#**

```csharp
services.AddProcessKit();
// When the container also has an ILoggerFactory, runs emit ProcessKit's lifecycle events.

// Later, injected as IProcessRunner:
public class Deployer(IProcessRunner runner)
{
    public Task<FSharpResult<string, ProcessError>> Deploy() =>
        runner.RunAsync(new Command("deploy"), CancellationToken.None);
}
```

`AddProcessKit` uses `TryAdd`, so a pre-existing `IProcessRunner` registration is left intact.
`AddProcessKit(configure)` / `AddProcessKit(configuration)` set a default timeout / working directory;
`AddProcessKitClient(name, program)` registers keyed per-tool `CliClient`s (the place for retry / encoding
defaults); and `AddProcessKitGroup()` backs the runner with a shared, container-managed `ProcessGroup`
(disposing the provider reaps the whole tree). See the [Dependency injection guide](dependency-injection.md).

## Testing without subprocesses

`ProcessKit.Testing` provides subprocess-free `IProcessRunner`s. `ScriptedRunner` returns
canned replies:

**F#**

```fsharp
let runner =
    (ScriptedRunner())
        .On([ "git"; "rev-parse"; "HEAD" ], Reply.Ok "abc123")
        .When((fun cmd -> cmd.Program = "flaky"), Reply.Fail(1, "boom"))
        .Fallback(Reply.Ok "")

// Inject `runner` wherever an IProcessRunner is expected — no real processes run.
let! sha = Runner.run runner CancellationToken.None (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ])
```

**C#**

```csharp
var runner = new ScriptedRunner()
    .On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123"))
    .When(cmd => cmd.Program == "flaky", Reply.Fail(1, "boom"))
    .Fallback(Reply.Ok(""));

// Inject `runner` wherever an IProcessRunner is expected — no real processes run.
var sha = await runner.RunAsync(new Command("git").Args(["rev-parse", "HEAD"]), CancellationToken.None);
```

`RecordReplayRunner` records real runs to a JSON cassette and replays them hermetically:

**F#**

```fsharp
// Record (wraps a real runner), then save:
let recorder = RecordReplayRunner.Record("fixture.json", JobRunner())
// ... drive recorder as an IProcessRunner ...
recorder.Save() |> ignore

// Replay later with no subprocess (an unmatched call is ProcessError.CassetteMiss):
match RecordReplayRunner.Replay "fixture.json" with
| Ok replay -> () // use `replay` as an IProcessRunner
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
// Record (wraps a real runner), then save:
var recorder = RecordReplayRunner.Record("fixture.json", new JobRunner());
// ... drive recorder as an IProcessRunner ...
recorder.Save();

// Replay later with no subprocess (an unmatched call is ProcessError.CassetteMiss):
var loaded = RecordReplayRunner.Replay("fixture.json");
if (loaded.IsOk)
{
    var replay = loaded.ResultValue; // use `replay` as an IProcessRunner
}
else
    Console.Error.WriteLine(loaded.ErrorValue.Message);
```

Cassettes also cover the `byte[]` capture and streaming (`SpawnAsync`) verbs,
`RecordReplayRunner.Auto` grows a cassette by recording on a miss, and
`RecordReplayOptions` adds arg-normalizer / redaction / file-content-stdin matching —
see [testing.md → Record and replay](testing.md#record-and-replay).

---

Next: [Running commands](commands.md)
