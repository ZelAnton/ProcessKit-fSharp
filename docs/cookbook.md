# ProcessKit cookbook

[‹ docs index](README.md)

Task-oriented, idiomatic examples for every part of the public API. The run and capture
verbs return `Task<Result<_, ProcessError>>`, so the F# samples below run inside a `task { }`
block and use `match!` (a few `RunningProcess` members — `Wait`, `Profile` — return their
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
- [Logging](#logging)
- [Dependency injection](#dependency-injection)
- [Testing without subprocesses](#testing-without-subprocesses)

## Running a command

Build a `Command` (an immutable value), then call a verb. `Run` requires a zero (or
accepted) exit and returns stdout with trailing whitespace trimmed.

**F#**

```fsharp
open ProcessKit

task {
    let cmd = Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]

    match! cmd.Run() with
    | Ok sha -> printfn $"HEAD is {sha}"
    | Error err -> eprintfn $"git failed: {err.Message}"
}
```

**C#**

```csharp
using ProcessKit;

var cmd = new Command("git").Args(new[] { "rev-parse", "HEAD" });

var sha = await cmd.Run();
if (sha.IsOk)
    Console.WriteLine($"HEAD is {sha.ResultValue}");
else
    Console.Error.WriteLine($"git failed: {sha.ErrorValue.Message}");
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
    .Args(new[] { "build", "-c", "Release" })
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
var cmd = new Command("dotnet").Args(new[] { "build", "-c", "Release" }).CurrentDir("/repo");
```

Use `RunUnit` when you only care that it succeeded:

**F#**

```fsharp
match! (Command.create "mkdir" |> Command.arg "out").RunUnit() with
| Ok () -> ()
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var result = await new Command("mkdir").Arg("out").RunUnit();
if (result.IsError)
    Console.Error.WriteLine(result.ErrorValue.Message);
```

## Capturing output

`OutputString` / `OutputBytes` return a `ProcessResult<_>` — a *non-zero exit is data here,
not an error*. Inspect `Stdout`, `Stderr`, `Code`, `IsSuccess`, `Duration`, `Outcome`.

**F#**

```fsharp
match! (Command.create "ls" |> Command.arg "-la").OutputString() with
| Ok result ->
    printfn $"exit={result.Code} success={result.IsSuccess} in {result.Duration}"
    printfn $"{result.Stdout}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var output = await new Command("ls").Arg("-la").OutputString();
if (output.IsOk)
{
    var result = output.ResultValue;
    Console.WriteLine($"exit={result.Code} success={result.IsSuccess} in {result.Duration}");
    Console.WriteLine(result.Stdout);
}
else
    Console.Error.WriteLine(output.ErrorValue.Message);
```

`OutputBytes` is the binary companion (`ProcessResult<byte[]>`), for non-text output.

## Error handling

`ProcessError` is a discriminated union — pattern-match it, or use `.Message` for a short
description. The capture verbs only error on *failure to run* (spawn / not-found / I/O /
timeout / cancellation), never on a non-zero exit.

**F#**

```fsharp
match! (Command.create "definitely-not-a-program").OutputString() with
| Ok result -> printfn $"{result.Stdout}"
| Error(ProcessError.NotFound(program, _)) -> eprintfn $"not installed: {program}"
| Error(ProcessError.Timeout(program, timeout, _, _)) -> eprintfn $"{program} timed out after {timeout}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var output = await new Command("definitely-not-a-program").OutputString();
if (output.IsOk)
    Console.WriteLine(output.ResultValue.Stdout);
else if (output.ErrorValue is ProcessError.NotFound notFound)
    Console.Error.WriteLine($"not installed: {notFound.program}");
else if (output.ErrorValue is ProcessError.Timeout timeout)
    Console.Error.WriteLine($"{timeout.program} timed out after {timeout.timeout}");
else
    Console.Error.WriteLine(output.ErrorValue.Message);
```

Classifiers help with retry/diagnostic logic:

**F#**

```fsharp
match! cmd.Run() with
| Ok _ -> ()
| Error err when ProcessError.isNotFound err -> installThenRetry ()
| Error err when ProcessError.isTransient err -> scheduleRetry ()   // spawn / I/O blips
| Error err -> fail err
```

**C#**

```csharp
var result = await cmd.Run();
if (result.IsOk) { }
else if (result.ErrorValue.IsNotFound)
    installThenRetry();
else if (result.ErrorValue.IsTransient)
    scheduleRetry();   // spawn / I/O blips
else
    fail(result.ErrorValue);
```

The success-requiring verbs (`Run` / `RunUnit`) additionally turn a non-zero exit into
`ProcessError.Exit(program, code, stdout, stderr)`.

## Exit codes and probing

**F#**

```fsharp
let! code = (Command.create "grep" |> Command.args [ "pattern"; "file" ]).ExitCode()  // Ok 0 / Ok 1 / ...
let! found = (Command.create "which" |> Command.arg "git").Probe()                     // Ok true if exit 0
```

**C#**

```csharp
var code = await new Command("grep").Args(new[] { "pattern", "file" }).ExitCode();  // Ok 0 / Ok 1 / ...
var found = await new Command("which").Arg("git").Probe();                          // Ok true if exit 0
```

`Probe` is `true` when the command runs and exits zero — handy for feature detection.

## Accepting non-zero exits

Some tools use non-zero exits as information (e.g. `grep` returns 1 for "no match"). Tell
ProcessKit which codes count as success:

**F#**

```fsharp
let grep =
    Command.create "grep"
    |> Command.args [ "ERROR"; "app.log" ]
    |> Command.okCodes [ 0; 1 ]   // 1 ("no match") is not a failure

match! grep.Run() with
| Ok output -> printfn $"matches:\n{output}"
| Error err -> eprintfn $"{err.Message}"   // a real failure (e.g. exit 2)
```

**C#**

```csharp
var grep = new Command("grep")
    .Args(new[] { "ERROR", "app.log" })
    .OkCodes(new[] { 0, 1 });   // 1 ("no match") is not a failure

var result = await grep.Run();
if (result.IsOk)
    Console.WriteLine($"matches:\n{result.ResultValue}");
else
    Console.Error.WriteLine(result.ErrorValue.Message);   // a real failure (e.g. exit 2)
```

`OkCodes` sets which exit codes `ProcessResult.IsSuccess`, `Run`/`RunUnit`, and supervisor crash
detection accept — the codes *replace* the default `{0}` (include `0` to keep it, as `[ 0; 1 ]` does
above).

## Parsing output

`Parse` maps stdout through a function (requires success); `TryParse` lets the parser fail;
`FirstLine` returns the first stdout line matching a predicate.

**F#**

```fsharp
let! version = (Command.create "node" |> Command.arg "--version").Parse(fun s -> s.TrimStart('v'))
let! port    = (Command.create "myserver").FirstLine(fun line -> line.StartsWith "Listening on ")
```

**C#**

```csharp
var version = await new Command("node").Arg("--version").Parse(s => s.TrimStart('v'));
var port = await new Command("myserver").FirstLine(line => line.StartsWith("Listening on "));
```

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

Sources: `Stdin.FromString`, `FromBytes`, `FromFile path`, `FromReader stream`,
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

match! pipeline.Run() with
| Ok count -> printfn $"{count} error lines"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var pipeline = new Command("cat").Arg("access.log")
    .Pipe(new Command("grep").Arg("ERROR"))
    .Pipe(new Command("wc").Arg("-l"));

var count = await pipeline.Run();
if (count.IsOk)
    Console.WriteLine($"{count.ResultValue} error lines");
else
    Console.Error.WriteLine(count.ErrorValue.Message);
```

A pipeline supports the same verbs as a command (`Run`/`OutputString`/`ExitCode`/…) plus
`Timeout` / `CancelOn`. Let a stage fail without failing the pipeline with
`Command.uncheckedInPipe`. The pipe-style module mirror is `Pipeline.create` / `Pipeline.pipe`.

## Streaming and interactive I/O

`Start()` returns a live `RunningProcess`. Stream stdout line by line as an
`IAsyncEnumerable`. (`use` ensures the tree is killed on scope exit.)

**F#**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.arg "watch").Start() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok proc ->
        use _ = proc
        let lines = proc.StdoutLines()
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
var started = await new Command("dotnet").Arg("watch").Start();
if (started.IsError)
    Console.Error.WriteLine(started.ErrorValue.Message);
else
{
    await using var proc = started.ResultValue;

    await foreach (var line in proc.StdoutLines())
        Console.WriteLine($"> {line}");
}
```

From C# this is simply `await foreach (var line in proc.StdoutLines()) { ... }`.

`OutputEvents()` interleaves stdout and stderr as `OutputEvent` values (`IsStdout`/`IsStderr`,
`.Text`). Write to a running process's stdin via `TakeStdin()`:

**F#**

```fsharp
match proc.TakeStdin() with
| Some stdin ->
    do! stdin.WriteLine "command one"
    do! stdin.Flush()
    do! stdin.Finish()   // close stdin (EOF)
| None -> ()
```

**C#**

```csharp
var stdin = proc.TakeStdin();
if (stdin != null) // FSharpOption: None is null
{
    await stdin.Value.WriteLine("command one");
    await stdin.Value.Flush();
    await stdin.Value.Finish();   // close stdin (EOF)
}
```

Race or await several started processes with `RunningProcess.WaitAny` / `WaitAll`.

## Readiness probes

Wait for a started process to become ready before proceeding:

**F#**

```fsharp
match! (Command.create "myserver").Start() with
| Ok proc ->
    use _ = proc
    // Wait up to 10s for a log line, a TCP port, or a custom predicate.
    match! proc.WaitForLine((fun l -> l.Contains "ready"), TimeSpan.FromSeconds 10.0) with
    | Ok _ -> printfn "server is up"
    | Error err -> eprintfn $"never became ready: {err.Message}"   // ProcessError.NotReady on timeout
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
using System;

var started = await new Command("myserver").Start();
if (started.IsOk)
{
    await using var proc = started.ResultValue;
    // Wait up to 10s for a log line, a TCP port, or a custom predicate.
    var ready = await proc.WaitForLine(l => l.Contains("ready"), TimeSpan.FromSeconds(10));
    if (ready.IsOk)
        Console.WriteLine("server is up");
    else
        Console.Error.WriteLine($"never became ready: {ready.ErrorValue.Message}");   // ProcessError.NotReady on timeout
}
else
    Console.Error.WriteLine(started.ErrorValue.Message);
```

Also `WaitForPort(endpoint, timeout)` and `WaitFor(predicateReturningTask, timeout)`.

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
using System;

var cmd = new Command("slow-job")
    .Timeout(TimeSpan.FromSeconds(30))   // kill at the deadline -> Outcome.TimedOut
    .Retry(3, TimeSpan.FromMilliseconds(200), err => err.IsTransient);
```

`TimeoutGrace` sends SIGTERM, waits a grace window, then SIGKILL (atomic on Windows). Tie a
run to a `CancellationToken` with `CancelOn`, or pass a token to any verb overload
(`cmd.Run(ct)`). A cancelled run is always an `Error` (`ProcessError.Cancelled`).

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

        match! group.Start(Command.create "build-everything") with
        | Ok _proc ->
            group.Signal Signal.Term |> ignore     // signal the whole tree
            group.Suspend() |> ignore              // freeze it
            group.Resume() |> ignore               // thaw it
            match group.Members() with
            | Ok pids -> printfn $"{pids.Count} processes in the tree"
            | Error _ -> ()
            do! group.Shutdown(TimeSpan.FromSeconds 5.0)   // graceful: SIGTERM -> grace -> SIGKILL
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using System;

var created = ProcessGroup.Create();
if (created.IsError)
    Console.Error.WriteLine(created.ErrorValue.Message);
else
{
    using var group = created.ResultValue;   // disposes (and reaps the whole tree) on scope exit

    var proc = await group.Start(new Command("build-everything"));
    if (proc.IsOk)
    {
        group.Signal(Signal.Term);     // signal the whole tree
        group.Suspend();               // freeze it
        group.Resume();                // thaw it
        var members = group.Members();
        if (members.IsOk)
            Console.WriteLine($"{members.ResultValue.Count} processes in the tree");
        await group.Shutdown(TimeSpan.FromSeconds(5));   // graceful: SIGTERM -> grace -> SIGKILL
    }
    else
        Console.Error.WriteLine(proc.ErrorValue.Message);
}
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
if (created.IsOk)
{
    using var group = created.ResultValue;   // ... run within the limited group
}
else
    Console.Error.WriteLine($"limits unavailable: {created.ErrorValue.Message}");
```

## Stats and profiling

**F#**

```fsharp
match group.Stats() with
| Ok stats ->
    printfn $"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}"
| Error _ -> ()

// A periodic series (IAsyncEnumerable) for live dashboards:
let series = group.SampleStats(TimeSpan.FromSeconds 1.0)
```

**C#**

```csharp
using System;

var stats = group.Stats();
if (stats.IsOk)
    Console.WriteLine($"active={stats.ResultValue.ActiveProcessCount} cpu={stats.ResultValue.TotalCpuTime} peak={stats.ResultValue.PeakMemoryBytes}");

// A periodic series (IAsyncEnumerable) for live dashboards:
var series = group.SampleStats(TimeSpan.FromSeconds(1));
```

Per-run profiling captures exit code, duration, CPU, and peak memory:

**F#**

```fsharp
match! (Command.create "heavy-job").Start() with
| Ok proc ->
    use _ = proc
    let! profile = proc.Profile()
    printfn $"exit={profile.ExitCode} cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} samples={profile.Samples}"
| Error _ -> ()
```

**C#**

```csharp
var started = await new Command("heavy-job").Start();
if (started.IsOk)
{
    await using var proc = started.ResultValue;
    var profile = await proc.Profile();
    Console.WriteLine($"exit={profile.ExitCode} cpu={profile.CpuTime} peak={profile.PeakMemoryBytes} samples={profile.Samples}");
}
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
        .Run()

match! outcome with
| Ok result -> printfn $"stopped: {result.Stopped} after {result.Restarts} restarts"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
using System;

var outcome = new Supervisor(new Command("worker"))
    .Restart(RestartPolicy.OnCrash)
    .Backoff(TimeSpan.FromSeconds(1), 2.0)        // base delay, multiplier
    .MaxBackoff(TimeSpan.FromMinutes(1))
    .Jitter(true)
    .MaxRestarts(20)
    .StormPause(TimeSpan.FromMinutes(5))          // pause after a burst of failures
    .Run();

var result = await outcome;
if (result.IsOk)
    Console.WriteLine($"stopped: {result.ResultValue.Stopped} after {result.ResultValue.Restarts} restarts");
else
    Console.Error.WriteLine(result.ErrorValue.Message);
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

let! sha = git.Run [ "rev-parse"; "HEAD" ]
let! log = git.OutputString [ "log"; "--oneline"; "-n"; "10" ]
```

**C#**

```csharp
using System;

var git = new CliClient("git")
    .WithDefaults(c => c.CurrentDir("/repo").Timeout(TimeSpan.FromSeconds(30)));

var sha = await git.Run(new[] { "rev-parse", "HEAD" });
var log = await git.OutputString(new[] { "log", "--oneline", "-n", "10" });
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
var sha = await Exec.run("git", new[] { "rev-parse", "HEAD" });
var info = await Exec.outputString("dotnet", new[] { "--info" });
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
using System.Linq;
using System.Threading;

var runner = new JobRunner();
var commands = files.Select(f => new Command("gzip").Arg(f));
var results = await Exec.outputAll(4, runner, commands, CancellationToken.None); // at most 4 live at once
```

## Logging

Opt in to structured lifecycle events (spawn, exit, timeout, retry, supervisor restart).
**argv and the environment are never logged** — only the program name and non-secret facts.

**F#**

```fsharp
let cmd = Command.create "deploy" |> Command.withLogger logger   // any Microsoft.Extensions.Logging ILogger
```

**C#**

```csharp
var cmd = new Command("deploy").WithLogger(logger);   // any Microsoft.Extensions.Logging ILogger
```

No-op and free when no logger is set.

## Dependency injection

The `ProcessKit.Extensions.DependencyInjection` package registers an `IProcessRunner`:

**F#**

```fsharp
open Microsoft.Extensions.DependencyInjection
open ProcessKit.Extensions.DependencyInjection

services.AddProcessKit() |> ignore
// When the container also has an ILoggerFactory, runs emit ProcessKit's lifecycle events.

// Later, injected as IProcessRunner:
type Deployer(runner: IProcessRunner) =
    member _.Deploy() = Runner.run runner CancellationToken.None (Command.create "deploy")
```

**C#**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using ProcessKit;
using ProcessKit.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;

services.AddProcessKit();
// When the container also has an ILoggerFactory, runs emit ProcessKit's lifecycle events.

// Later, injected as IProcessRunner:
public class Deployer
{
    private readonly IProcessRunner runner;
    public Deployer(IProcessRunner runner) => this.runner = runner;

    public Task<FSharpResult<string, ProcessError>> Deploy() =>
        runner.Run(new Command("deploy"), CancellationToken.None);
}
```

`AddProcessKit` uses `TryAdd`, so a pre-existing `IProcessRunner` registration is left intact.

## Testing without subprocesses

`ProcessKit.Testing` provides subprocess-free `IProcessRunner`s. `ScriptedRunner` returns
canned replies:

**F#**

```fsharp
open ProcessKit.Testing

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
using ProcessKit;
using ProcessKit.Testing;
using System.Threading;

var runner = new ScriptedRunner()
    .On(new[] { "git", "rev-parse", "HEAD" }, Reply.Ok("abc123"))
    .When(cmd => cmd.Program == "flaky", Reply.Fail(1, "boom"))
    .Fallback(Reply.Ok(""));

// Inject `runner` wherever an IProcessRunner is expected — no real processes run.
var sha = await runner.Run(new Command("git").Args(new[] { "rev-parse", "HEAD" }), CancellationToken.None);
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
using ProcessKit;
using ProcessKit.Testing;

// Record (wraps a real runner), then save:
var recorder = RecordReplayRunner.Record("fixture.json", new JobRunner());
// ... drive recorder as an IProcessRunner ...
recorder.Save();

// Replay later with no subprocess (an unmatched call is ProcessError.CassetteMiss):
var replay = RecordReplayRunner.Replay("fixture.json");
if (replay.IsOk)
{
    // use replay.ResultValue as an IProcessRunner
}
else
    Console.Error.WriteLine(replay.ErrorValue.Message);
```
