# ProcessKit

[![CI](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ProcessKit.svg)](https://www.nuget.org/packages/ProcessKit)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

Async child-process management for .NET with a kernel-backed **no-orphan guarantee**: every
process you start — and everything *it* spawns — lives in a kill-on-dispose container (a
**Windows Job Object**, a **Linux cgroup v2**, or a **POSIX process group**), so no descendant
ever outlives your program.

Beyond spawning a subprocess: run-and-capture, line streaming, interactive stdin, shell-free
pipelines, readiness probes, timeouts & cancellation, supervision with restart/backoff, and a
mockable runner seam for subprocess-free tests.

**F#**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.arg "--version").RunAsync() with
    | Ok version -> printfn $"{version}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
Console.WriteLine(await new Command("dotnet").Arg("--version").RunAsync() switch
{
    { IsOk: true, ResultValue: var version } => version,
    { IsOk: false, ErrorValue: var err }    => $"error: {err.Message}",
});
```

![Cover](https://raw.githubusercontent.com/ZelAnton/ProcessKit-fSharp/main/cover.png)

## Why ProcessKit?

`System.Diagnostics.Process` reaches (at most) the direct child. The processes *it* spawned — a
build tool's compiler children, the real payload behind a wrapper (`cmd /c …`, `sh -c …`), a
test's helper servers — survive a timeout, an exception, or a dropped task, and keep running as
orphans.

ProcessKit spawns every child into the operating system's own containment primitive — a **Job
Object** on Windows, a **cgroup v2** on Linux (with a process-group fallback), a **POSIX process
group** on macOS/BSD — so teardown is a kernel operation over the whole tree, not a best-effort
signal to one pid:

- **Nothing escapes silently.** Disposing the handle or group reaps every descendant,
  grandchildren included. Where a mechanism has a genuine weakness (a `setsid` child escapes a
  POSIX process group), the active `Mechanism` is reported instead of pretending — never a silent
  downgrade.
- **Async-first.** Run-and-capture, line streaming, interactive stdin, readiness probes,
  shell-free pipelines, supervision — all return `Task<…>` and stream as `IAsyncEnumerable<…>`.
- **Honest results.** A non-zero exit is data (`ProcessResult`) until you ask for success; a
  timeout is *captured* in the result; a cancellation is always an error; every platform
  divergence is typed or documented.
- **Testable.** One interface seam (`IProcessRunner`) swaps the real spawner for scripted doubles
  or record/replay cassettes — no subprocess in your tests.

### How it compares

| | whole-tree kill-on-dispose | async | limits / stats | streaming · pipelines · supervision |
|---|:---:|:---:|:---:|:---:|
| `System.Diagnostics.Process` | — | partial | — | — |
| **ProcessKit** | **✓** | **✓** | **✓** | **✓** |

The first column is the differentiator: a child's *descendants* are contained and reaped as a
unit (Job Object / cgroup v2 / process group), not just the direct child.

> **Status: 2.0 — F# rewrite.** ProcessKit 2.x is a ground-up F# library that supersedes the
> author's earlier C# `ProcessKit` package (published through 1.3.2); its first release is
> **2.0.0**. The public API targets [Semantic Versioning](https://semver.org/): breaking changes
> land only in a new major version. See [`CHANGELOG.md`](CHANGELOG.md).
>
> **Although ProcessKit is implemented in F#, it is designed for first-class, idiomatic use from
> both F# and C#** — every public API is meant to be called naturally from either language, and
> every example in this README and the [guides](docs/README.md) is shown in both.

## Install

```bash
dotnet add package ProcessKit
# optional — Microsoft.Extensions.DependencyInjection integration (AddProcessKit)
dotnet add package ProcessKit.Extensions.DependencyInjection
```

Targets **.NET 8.0** and **.NET 10.0**, and is built for first-class use from **F# and C# alike** —
every example below is shown in both. To keep them on the usage code, the snippets omit `open` /
`using` directives: assume `open ProcessKit` (plus the relevant `System` opens) inside a `task { }`
in F#, and `using ProcessKit;` (plus the implicit `System` usings) inside an `async` method in C#.
Every verb returns `Task<Result<_, ProcessError>>`; from C# you pattern-match it —
`result switch { { IsOk: true, ResultValue: var value } => …, { IsOk: false, ErrorValue: var err } => … }`,
`result.Match(onOk, onError)`, `if (result.TryGetValue(out var value, out var error))`, or
`result.GetValueOrThrow()` (throws `ProcessException`).

## Picking a verb

Every run starts with the same builder; the verb you finish with decides what you get back. Every
verb returns `Task<Result<_, ProcessError>>`:

| You want | Call | You get |
|---|---|---|
| stdout, success required | `.RunAsync()` | trimmed `string`; non-zero exit / timeout / kill → `Error` |
| the full outcome, exit code as data | `.OutputStringAsync()` / `.OutputBytesAsync()` | `ProcessResult<_>` — code, stdout, stderr, `IsTimedOut`; never errors on a non-zero exit |
| just the exit code | `.ExitCodeAsync()` | `int` (a timed-out / killed run errors instead of inventing `-1`) |
| a yes/no answer | `.ProbeAsync()` | `bool` — exit 0 → `true`, 1 → `false`, anything else errors |
| a typed value from stdout | `.ParseAsync(f)` / `.TryParseAsync(f)` | `'T` — success required |
| the first matching output line | `.FirstLineAsync(p)` | `string option` — `None` when stdout closes without a match |
| a live handle — streaming, stdin, probes | `.StartAsync()` | `RunningProcess` |

The same vocabulary repeats on every layer (`IProcessRunner`, `CliClient`, `Pipeline`), and
`Exec.run "git" [ "status" ]` / `Exec.outputString …` skip the builder for one-liners.

## Quick start

**F#**

```fsharp
task {
    // Capture output; a non-zero exit does not error on its own.
    match! (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]).OutputStringAsync() with
    | Ok result -> printfn $"HEAD is {result.Stdout.Trim()}"
    | Error err -> eprintfn $"{err.Message}"

    // Require success and get trimmed stdout directly.
    match! (Command.create "dotnet" |> Command.arg "--version").RunAsync() with
    | Ok version -> printfn $"{version}"
    | Error err -> eprintfn $"{err.Message}"

    // Feed stdin.
    let sort = Command.create "sort" |> Command.stdin (Stdin.FromString "banana\napple\n")

    match! sort.OutputStringAsync() with
    | Ok sorted -> printfn $"{sorted.Stdout}"
    | Error err -> eprintfn $"{err.Message}"

    // Share one kill-on-dispose group across several children; disposing the group reaps the
    // whole tree.
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! _server = group.StartAsync(Command.create "some-server")
        // ... work ...
        do! group.ShutdownAsync(TimeSpan.FromSeconds 5.0) // graceful: SIGTERM → wait → SIGKILL (Unix); atomic on Windows
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// Capture output; a non-zero exit does not error on its own.
Console.WriteLine(await new Command("git").Args(["rev-parse", "HEAD"]).OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => $"HEAD is {result.Stdout.Trim()}",
    { IsOk: false, ErrorValue: var err }   => err.Message,
});

// Require success and get trimmed stdout directly.
Console.WriteLine(await new Command("dotnet").Arg("--version").RunAsync() switch
{
    { IsOk: true, ResultValue: var version } => version,
    { IsOk: false, ErrorValue: var err }    => err.Message,
});

// Feed stdin.
Console.WriteLine(await new Command("sort").Stdin(Stdin.FromString("banana\napple\n")).OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var sorted } => sorted.Stdout,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});

// Share one kill-on-dispose group across several children; disposing the group reaps the whole tree.
using var group = ProcessGroup.Create().GetValueOrThrow();
await group.StartAsync(new Command("some-server"));
// ... work ...
await group.ShutdownAsync(TimeSpan.FromSeconds(5)); // graceful: SIGTERM → wait → SIGKILL (Unix); atomic on Windows
```

## Documentation

This README is the quick tour. The **[`docs/` guide set](docs/README.md)** goes deeper on every
capability, with more examples and the platform fine print collected in one place. New here? Skim
the [Cookbook](docs/cookbook.md) first — it maps "I want to …" tasks to working snippets — then
read [Running commands](docs/commands.md) end to end:

| Guide | Covers |
|---|---|
| [Cookbook](docs/cookbook.md) | Task → snippet recipes for everything below; the fastest way in |
| [Running commands](docs/commands.md) | The full `Command` builder and every consuming verb, with error semantics |
| [Process groups](docs/process-groups.md) | Containment, teardown, signals, suspend/resume, members, limits, stats |
| [Streaming & interactive I/O](docs/streaming.md) | Line streaming, conversational stdin, readiness probes, `WaitAnyAsync`, profiling |
| [Pipelines](docs/pipelines.md) | Shell-free `a → b → c`, pipefail attribution, chain timeouts |
| [Timeouts, retries & cancellation](docs/timeouts-and-cancellation.md) | Captured vs raised deadlines, retry classifiers, `CancellationToken` |
| [Supervision](docs/supervision.md) | Restart policies, backoff & jitter, stop conditions, outcomes |
| [Testing your code](docs/testing.md) | The `IProcessRunner` seam, scripted / record-replay doubles, cassettes, `CliClient` |
| [Platform support](docs/platform-support.md) | Mechanisms, every capability matrix, and each caveat |

Where the project is headed: the **[roadmap](ROADMAP.md)**.

## One package, full surface

There are no compile-time feature flags to choose: a single `ProcessKit` package ships the whole
surface, and the optional capabilities are just modules you use when you need them. The
kill-on-dispose tree guarantee is unconditional.

| Capability | Where |
|---|---|
| Tree control — `Signal` / `Suspend` / `Resume` / `Members` | `ProcessGroup` |
| Resource caps — memory / process count / CPU | `ProcessGroupOptions` → `ProcessGroup.Create` |
| Stats & profiling — `Stats` / `SampleStatsAsync` / `ProfileAsync` | `ProcessGroup`, `RunningProcess` |
| Record / replay cassettes | `ProcessKit.Testing.RecordReplayRunner` |
| Lifecycle logging (`Microsoft.Extensions.Logging`) | `Command.Logger` |
| Dependency-injection wiring | `ProcessKit.Extensions.DependencyInjection` (separate package) |

## Capping a group's resources

`ProcessGroupOptions` can bound the whole tree's memory, process count, and CPU at creation, so a
runaway or untrusted child tree can't exhaust the host:

**F#**

```fsharp
task {
    let options =
        ProcessGroupOptions()
            .WithMemoryMax(512L * 1024L * 1024L) // 512 MiB across the tree
            .WithMaxProcesses(64)
            .WithCpuQuota(0.5)                    // half of one core

    match ProcessGroup.Create options with
    | Ok group ->
        use group = group
        let! _job = group.StartAsync(Command.create "untrusted-tool")
        () // ... work ...
    | Error err -> eprintfn $"limits unavailable: {err.Message}" // ProcessError.ResourceLimit
}
```

**C#**

```csharp
var options = new ProcessGroupOptions()
    .WithMemoryMax(512L * 1024L * 1024L) // 512 MiB across the tree
    .WithMaxProcesses(64)
    .WithCpuQuota(0.5);                   // half of one core

var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var limitErr })
{
    Console.Error.WriteLine($"limits unavailable: {limitErr.Message}"); // ProcessError.ResourceLimit
    return;
}

using var group = created.GetValueOrThrow();
await group.StartAsync(new Command("untrusted-tool"));
// ... work ...
```

`WithCpuQuota` is a fraction of a **single** core (`0.5` = half a core, `2.0` = two cores); on
Windows it is converted against the host's CPU count and is approximate. Limits need a real
container — a **Windows Job Object** or a **Linux cgroup v2** — so there is no whole-tree limit on
macOS/BSD or the Linux process-group fallback. When a requested limit can't be enforced,
`Create` returns `ProcessError.ResourceLimit` instead of a silently-unbounded group.

*Deeper: [Process groups → resource limits](docs/process-groups.md#resource-limits).*

## Signalling and pausing the whole tree

Beyond the kill/shutdown teardown verbs, a group can broadcast a signal to every member or freeze
and thaw the whole tree:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! _server = group.StartAsync(Command.create "my-server")

        group.Signal Signal.Hup |> ignore // e.g. "reload configuration"
        group.Suspend() |> ignore         // freeze the whole tree…
        group.Resume() |> ignore          // …and let it run again
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using var group = ProcessGroup.Create().GetValueOrThrow();
await group.StartAsync(new Command("my-server"));

group.Signal(Signal.Hup); // e.g. "reload configuration"
group.Suspend();          // freeze the whole tree…
group.Resume();           // …and let it run again
```

Signals are POSIX-only: on Windows just `Signal.Kill` is deliverable (it maps to the Job Object
terminate) and anything else returns `ProcessError.Unsupported`. Suspend/resume work everywhere a
container exists — `cgroup.freeze` on Linux, `SIGSTOP`/`SIGCONT` on macOS/BSD and the
process-group fallback, per-thread suspension on Windows.

*Deeper: [Process groups → signals, suspend/resume](docs/process-groups.md#signals-and-suspendresume).*

## Inspecting the tree and racing children

`Members()` snapshots the live member pids, and `RunningProcess.WaitAny` races several running
processes, reporting whichever exits first — the natural primitive for supervising a few
long-lived children:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! a = group.StartAsync(Command.create "server-a")
        let! b = group.StartAsync(Command.create "server-b")

        match a, b with
        | Ok a, Ok b ->
            match group.Members() with
            | Ok pids -> printfn $"live pids: {pids}"
            | Error _ -> ()

            match! RunningProcess.WaitAnyAsync [| a; b |] with
            | Ok result -> printfn $"contender #{result.Index} exited first with {result.Outcome}"
            | Error err -> eprintfn $"{err.Message}"
        | _ -> ()
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
using var group = ProcessGroup.Create().GetValueOrThrow();
var a = (await group.StartAsync(new Command("server-a"))).GetValueOrThrow();
var b = (await group.StartAsync(new Command("server-b"))).GetValueOrThrow();

if (group.Members() is { IsOk: true, ResultValue: var pids })
    Console.WriteLine($"live pids: {string.Join(", ", pids)}");

Console.WriteLine(await RunningProcess.WaitAnyAsync([a, b]) switch
{
    { IsOk: true, ResultValue: var first } => $"contender #{first.Index} exited first with {first.Outcome}",
    { IsOk: false, ErrorValue: var err }  => err.Message,
});
```

`Members()` lists the whole tree on Windows (Job Object) and Linux (cgroup); the POSIX
process-group backend lists the tracked group *leaders* only. `WaitAnyAsync` applies no per-process
timeout (bound the race with a `Command.Timeout`) and does no output pumping — drain chatty
children first.

*Deeper: [Process groups → members](docs/process-groups.md#listing-members) ·
[Streaming → racing children](docs/streaming.md#racing-several-children).*

## Running many at once

`WaitAllAsync` joins a fixed set of started handles, returning every outcome in order;
`Exec.outputAll` runs a whole batch of commands with a **concurrency cap**, so fanning out
hundreds of commands can't exhaust file descriptors or the process table:

**F#**

```fsharp
task {
    let runner = JobRunner() :> IProcessRunner

    // 200 conversions, but never more than 8 processes alive at once.
    let commands = [ for i in 0..199 -> Command.create "convert" |> Command.arg $"{i}.png" ]
    let! results = Exec.outputAll 8 runner commands CancellationToken.None
    let failed = results |> Array.filter (fun r -> match r with Ok o -> not o.IsSuccess | Error _ -> true)
    printfn $"{failed.Length} conversions failed"
}
```

**C#**

```csharp
var runner = new JobRunner();

// 200 conversions, but never more than 8 processes alive at once.
var commands = Enumerable.Range(0, 200).Select(i => new Command("convert").Arg($"{i}.png"));
var results = await Exec.outputAll(8, runner, commands, CancellationToken.None);

// A failure is either an Error, or an Ok whose run was not successful.
var failed = results.Count(r => r is { IsOk: false } or { IsOk: true, ResultValue: { IsSuccess: false } });
Console.WriteLine($"{failed} conversions failed");
```

`Exec.outputAll` is **collect-all**: each element is one command's independent `Result`, so a
non-zero exit never short-circuits the batch — the caller folds the outcomes. Pass a
`ProcessGroup` (which is itself an `IProcessRunner`) instead of `JobRunner()` to keep every child
in one shared kill-on-dispose group. `Exec.outputAllBytes` is the identical fan-out with each
result captured as `byte[]`.

## Sampling stats over time

A point-in-time `Stats()` becomes a series with `SampleStatsAsync`, and a single run can be profiled
end-to-end:

**F#**

```fsharp
task {
    // A one-shot summary of a single run:
    match! (Command.create "crunch").StartAsync() with
    | Ok proc ->
        use _ = proc
        let! profile = proc.ProfileAsync()
        printfn $"exit={profile.ExitCode} took={profile.Duration} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpuCores}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// A one-shot summary of a single run:
await using var proc = (await new Command("crunch").StartAsync()).GetValueOrThrow();
var profile = await proc.ProfileAsync();
Console.WriteLine($"exit={profile.ExitCode} took={profile.Duration} peak={profile.PeakMemoryBytes} avgCpu={profile.AvgCpuCores}");
```

`Stats()`/`SampleStatsAsync` report full CPU/memory on Windows and the Linux cgroup backend, and active
counts only on the POSIX process-group fallback; `ProfileAsync` samples the started child itself.

*Deeper: [Process groups → stats](docs/process-groups.md#stats) ·
[Streaming → profiling a run](docs/streaming.md#profiling-a-run).*

## Supervising a long-lived child

Where `Command.Retry` replays one run until it succeeds, a `Supervisor` keeps a child **alive**:
it restarts the command per policy whenever it exits, with bounded restarts and exponential
backoff (jittered by default so a restarted fleet doesn't stampede):

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "my-server" |> Command.args [ "--port"; "8080" ]))
            .Restart(RestartPolicy.OnCrash)          // Always | OnCrash | Never
            .MaxRestarts(5)
            .Backoff(TimeSpan.FromMilliseconds 200.0, 2.0) // base, multiplier (cap: MaxBackoff)
            .StormPause(TimeSpan.FromSeconds 15.0)   // crash-loop guard (off by default)

    match! supervisor.RunAsync() with
    | Ok outcome -> printfn $"ended after {outcome.Restarts} restarts: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("my-server").Args(["--port", "8080"]))
    .Restart(RestartPolicy.OnCrash)               // Always | OnCrash | Never
    .MaxRestarts(5)
    .Backoff(TimeSpan.FromMilliseconds(200), 2.0) // base, multiplier (cap: MaxBackoff)
    .StormPause(TimeSpan.FromSeconds(15));         // crash-loop guard (off by default)

Console.WriteLine(await supervisor.RunAsync() switch
{
    { IsOk: true, ResultValue: var outcome } => $"ended after {outcome.Restarts} restarts: {outcome.Stopped}",
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

`RunAsync()` reports a `SupervisionOutcome` — the final run's result, the restart count, and why
supervision stopped. The opt-in **failure-storm guard** distinguishes "fails rarely" from
"crash-looping": past `FailureThreshold` the supervisor takes one collective `StormPause` instead
of hammering restarts at backoff speed. Supervision runs through the `IProcessRunner` seam: pass
`.WithRunner(group)` to keep every incarnation in one shared kill-on-dispose group, or a
`ScriptedRunner` to test supervision logic hermetically.

*Deeper: [Supervision](docs/supervision.md).*

## Waiting for a child to be ready

"Start a server, then use it" needs the server to be *ready*, not merely started. Three probes
replace the arbitrary sleep:

**F#**

```fsharp
task {
    match! (Command.create "my-server").StartAsync() with
    | Ok proc ->
        use _ = proc

        // Wait for the startup banner (returns the matching line)…
        match! proc.WaitForLineAsync((fun l -> l.Contains "listening on"), TimeSpan.FromSeconds 10.0) with
        | Ok banner -> printfn $"server says: {banner}"
        | Error err -> eprintfn $"never became ready: {err.Message}" // ProcessError.NotReady

        // …or for a TCP port to accept connections, or any async health check:
        // do! proc.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 10.0)
        // do! proc.WaitForAsync((fun () -> healthCheck ()), TimeSpan.FromSeconds 10.0)
        ()
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("my-server").StartAsync()).GetValueOrThrow();

// Wait for the startup banner (returns the matching line)…
Console.WriteLine(await proc.WaitForLineAsync(l => l.Contains("listening on"), TimeSpan.FromSeconds(10)) switch
{
    { IsOk: true, ResultValue: var banner } => $"server says: {banner}",
    { IsOk: false, ErrorValue: var err }   => $"never became ready: {err.Message}", // ProcessError.NotReady
});

// …or for a TCP port to accept connections, or any async health check:
// await proc.WaitForPortAsync(endpoint, TimeSpan.FromSeconds(10));
// await proc.WaitForAsync(() => healthCheck(), TimeSpan.FromSeconds(10));
```

A probe that doesn't pass within its deadline — or that can no longer pass (the child exits; for
`WaitForLineAsync`, its stdout closes) — fails with `ProcessError.NotReady` (distinct from a timeout)
and **does not kill the child**: the caller decides what happens next.

*Deeper: [Streaming → readiness probes](docs/streaming.md#readiness-probes).*

## Pipelines without a shell

`a → b → c` without a shell string — stages connected in-process (a relay, not a shell), so no
quoting or injection surface, and every stage lives in one shared kill-on-dispose group:

**F#**

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

**C#**

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

The outcome is **pipefail**: `Stdout` is the last stage's output, while the exit code, stderr, and
reported program come from the first stage that didn't exit cleanly (or the last stage when all
succeed). For a consumer that legitimately stops reading early (the `producer | head -1` shape),
mark that stage `Command.uncheckedInPipe` and pipefail skips it. `Pipeline.Timeout` bounds the
whole chain.

*Deeper: [Pipelines](docs/pipelines.md).*

## Environment and spawn flags

**F#**

```fsharp
task {
    // Set / unset individual variables, or clear the environment entirely.
    let! _ =
        (Command.create "worker"
         |> Command.env "DOTNET_ENVIRONMENT" "Production"
         |> Command.envRemove "GIT_DIR")
            .RunAsync()

    // Scorched earth: the child starts with an empty environment.
    let! _ = (Command.create "hermetic-tool" |> Command.envClear).RunAsync()

    // Windows: no console window flashing up from a GUI app (a harmless no-op elsewhere).
    let! _ = (Command.create "helper" |> Command.createNoWindow).RunAsync()
    ()
}
```

**C#**

```csharp
// Set / unset individual variables, or clear the environment entirely.
await new Command("worker")
    .Env("DOTNET_ENVIRONMENT", "Production")
    .EnvRemove("GIT_DIR")
    .RunAsync();

// Scorched earth: the child starts with an empty environment.
await new Command("hermetic-tool").EnvClear().RunAsync();

// Windows: no console window flashing up from a GUI app (a harmless no-op elsewhere).
await new Command("helper").CreateNoWindow().RunAsync();
```

ProcessKit wires **pipes**, not a pseudo-terminal, so a tool that *demands* a tty — an `ssh` /
`sudo` password prompt, some credential helpers — won't get one. Drive such tools
non-interactively instead (key-based auth, `ssh -o BatchMode=yes`, `GIT_TERMINAL_PROMPT=0`), or
feed a known answer over [interactive stdin](docs/streaming.md#interactive-stdin).

*Deeper: [Running commands → environment](docs/commands.md#environment).*

## Cancelling a run

Hand a command a `CancellationToken`; cancelling the token kills the process tree, and every
consuming path reports `ProcessError.Cancelled`:

**F#**

```fsharp
task {
    use cts = new CancellationTokenSource()
    let job = (Command.create "long-job").RunAsync(cts.Token)

    // elsewhere — a shutdown signal, a sibling failure, a UI button:
    cts.Cancel()

    match! job with
    | Error(ProcessError.Cancelled _) -> printfn "cancelled"
    | _ -> ()
}
```

**C#**

```csharp
using var cts = new CancellationTokenSource();
var job = new Command("long-job").RunAsync(cts.Token);

// elsewhere — a shutdown signal, a sibling failure, a UI button:
cts.Cancel();

if (await job is { IsOk: false, ErrorValue: { IsCancelled: true } })
    Console.WriteLine("cancelled");
```

Unlike a timeout — whose expiry is *captured* in the result as `IsTimedOut` — cancellation is
**always an error**: the run was abandoned, so there is no result to inspect. A token cancelled
*before* the run starts short-circuits without spawning anything. Tie a token to a command for its
whole lifetime with `Command.CancelOn(token)`, or set it once on a `CliClient` with
`WithDefaults(fun c -> c.CancelOn token)`.

*Deeper: [Timeouts, retries & cancellation](docs/timeouts-and-cancellation.md).*

## Async streaming and interactive I/O

The one-shot helpers above buffer the whole output. For long-running or conversational children,
`StartAsync()` returns a live `RunningProcess` you can drive asynchronously.

### Stream stdout line by line

`StdoutLinesAsync()` is an `IAsyncEnumerable<string>` — process each line as it arrives, no waiting for
the child to exit. From C# this is `await foreach (var line in proc.StdoutLinesAsync())`; from F#,
enumerate it (`open FSharp.Control` for `TaskSeq`, or use the enumerator directly):

**F#**

```fsharp
task {
    match! (Command.create "git" |> Command.args [ "log"; "--oneline"; "-n"; "50" ]).StartAsync() with
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

        // After the stream ends, collect the outcome and stderr (drained in the background).
        match! proc.FinishAsync() with
        | Ok finished -> if finished.Outcome <> Outcome.Exited 0 then eprintfn $"{finished.Stderr}"
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("git").Args(["log", "--oneline", "-n", "50"]).StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"commit: {line}");

// After the stream ends, collect the outcome and stderr (drained in the background).
var finished = (await proc.FinishAsync()).GetValueOrThrow();
if (finished.Outcome is not { IsExited: true, Code.Value: 0 }) // anything but a clean exit 0
    Console.Error.WriteLine(finished.Stderr);
```

The command's `Timeout` **bounds the stream**: at the deadline the tree is killed, the pipes
close, and the stream ends.

### Interactive stdin — write requests, read responses

Keep stdin open with `KeepStdinOpen`, take the writer with `TakeStdin()`, then interleave writes
and reads:

**F#**

```fsharp
task {
    match! (Command.create "bc" |> Command.keepStdinOpen).StartAsync() with
    | Ok proc ->
        use _ = proc

        match proc.TakeStdin() with
        | Some stdin ->
            do! stdin.WriteLineAsync "2 + 2"
            do! stdin.WriteLineAsync "6 * 7"
            do! stdin.FinishAsync() // send EOF so bc finishes
        | None -> ()
        // …then read proc.StdoutLinesAsync() for the answers.
        ()
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("bc").KeepStdinOpen().StartAsync()).GetValueOrThrow();

if (proc.TakeStdin() is { Value: var stdin }) // Some(stdin); None is null and won't match
{
    await stdin.WriteLineAsync("2 + 2");
    await stdin.WriteLineAsync("6 * 7");
    await stdin.FinishAsync(); // send EOF so bc finishes
}
// …then read proc.StdoutLinesAsync() for the answers.
```

> For a **large** interactive stdin, write from one task and read `StdoutLinesAsync()` from another —
> otherwise the child can block writing stdout while you block writing stdin, a full-duplex
> deadlock. The non-interactive `Stdin.From*` sources are written on a background task and never
> deadlock.

*Deeper: [Streaming & interactive I/O](docs/streaming.md).*

## Wrapping a CLI tool

`CliClient` turns a typed wrapper around an external tool (`git`, `gh`, …) into just its parsers —
the runner is injectable, so the wrapper is hermetically testable with a `ScriptedRunner` (no
subprocess):

**F#**

```fsharp
task {
    let git =
        (CliClient.create "git")
            .WithDefaults(fun c -> c.CurrentDir("/repo").Timeout(TimeSpan.FromSeconds 30.0))

    match! git.RunAsync [ "rev-parse"; "HEAD" ] with
    | Ok sha -> printfn $"{sha}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var git = new CliClient("git")
    .WithDefaults(c => c.CurrentDir("/repo").Timeout(TimeSpan.FromSeconds(30)));

Console.WriteLine(await git.RunAsync(["rev-parse", "HEAD"]) switch
{
    { IsOk: true, ResultValue: var sha }  => sha,
    { IsOk: false, ErrorValue: var err } => err.Message,
});
```

*Deeper: [Testing your code → CliClient](docs/testing.md#cliclient).*

## Recording and replaying runs

`RecordReplayRunner` turns real runs into a JSON cassette once, then replays them
deterministically — fast, hermetic, no subprocess in CI:

**F#**

```fsharp
task {
    // Record once against the real tool, then save:
    let recorder = RecordReplayRunner.Record("fixtures/git.json", JobRunner())
    let! _ = Runner.run recorder System.Threading.CancellationToken.None (Command.create "git" |> Command.arg "--version")
    recorder.Save() |> ignore

    // Replay everywhere else — no subprocess, identical results:
    match RecordReplayRunner.Replay "fixtures/git.json" with
    | Ok replay -> () // use `replay` as an IProcessRunner
    | Error err -> eprintfn $"{err.Message}" // ProcessError.CassetteMiss on an unmatched call
}
```

**C#**

```csharp
// Record once against the real tool, then save:
var recorder = RecordReplayRunner.Record("fixtures/git.json", new JobRunner());
await recorder.RunAsync(new Command("git").Arg("--version"), CancellationToken.None);
recorder.Save();

// Replay everywhere else — no subprocess, identical results:
if (RecordReplayRunner.Replay("fixtures/git.json") is { IsOk: true, ResultValue: var replay })
{
    // use `replay` as an IProcessRunner
}
else
{
    // ProcessError.CassetteMiss on an unmatched call
}
```

Entries are matched by program + args + cwd + a stdin **source digest**; environment override
*values never reach the file* (only the variable names). `program`, `args`, `stdout`, and `stderr`
*are* stored verbatim and can carry secrets — review a fixture before committing it; on Unix the
file is written `0600`.

*Deeper: [Testing your code → record/replay](docs/testing.md#record-and-replay).*

## Observability and dependency injection

Opt into structured lifecycle events (spawn, exit, timeout, retry, supervisor restart) with
`Command.Logger` — **argv and the environment are never logged**, only the program name and
non-secret facts. The separate `ProcessKit.Extensions.DependencyInjection` package registers an
`IProcessRunner` for `Microsoft.Extensions.DependencyInjection` consumers with `AddProcessKit()`
(logger-aware when the container has an `ILoggerFactory`).

*Deeper: [Testing your code → the runner seam](docs/testing.md).*

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). To report a
security issue, follow [SECURITY.md](SECURITY.md).

## License

Licensed under the [MIT License](LICENSE) © Anton Zhelezniakou.
