# ProcessKit

[![CI](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ProcessKit.svg)](https://www.nuget.org/packages/ProcessKit)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

Async child-process management for .NET with a kernel-backed **no-orphan guarantee**: every
process you start — and everything *it* spawns — lives in a kill-on-dispose container (a
**Windows Job Object**, a **Linux cgroup v2**, the **`procctl(2)` process reaper** on FreeBSD, or a
**POSIX process group**), so no descendant ever outlives your program.

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
Object** on Windows, a **cgroup v2** on Linux (with a process-group fallback), the **`procctl(2)`
process reaper** on FreeBSD, a **POSIX process group** on macOS and the other BSDs — so teardown is
a kernel operation over the whole tree, not a best-effort signal to one pid:

- **Nothing escapes silently.** Disposing the handle or group reaps every descendant,
  grandchildren included. Where a mechanism has a genuine weakness (a `setsid` child escapes a
  POSIX process group), the active `Mechanism` is reported instead of pretending — never a silent
  downgrade.
- **Detached failures are cleaned up.** The explicit `Command.LaunchDetached()` opt-out still
  tears down the whole fresh POSIX session and reaps its leader if post-spawn priority setup fails,
  before returning the typed `ProcessError.Spawn`.
- **Async-first.** Run-and-capture, line/Content-Length streaming, interactive stdin, readiness
  probes, shell-free pipelines, supervision — all return `Task<…>` and stream as
  `IAsyncEnumerable<…>`.
- **Honest results.** A non-zero exit is data (`ProcessResult`) until you ask for success; a
  timeout is *captured* in the result; a cancellation is always an error; every platform
  divergence is typed or documented. A `ProcessError.Timeout` from ProcessKit carries the exact
  configured total, idle, or pipeline deadline that fired, while `ProcessResult.Duration` remains
  actual wall-clock time including teardown. If a custom `IProcessRunner` returns `Outcome.TimedOut`
  without that internal cause, the configured duration is unknown and the error honestly falls back
  to the result's actual elapsed duration. Output handlers and tees are rejected with
  `StdioMode.Null`/`Inherit`, where no parent-side stream exists for them to observe.
- **Testable.** One interface seam (`IProcessRunner`) swaps the real spawner for scripted doubles,
  deterministic fault injection, or record/replay cassettes — no subprocess in your tests.

### How it compares

| | whole-tree kill-on-dispose | async | limits / stats | streaming · pipelines · supervision |
|---|:---:|:---:|:---:|:---:|
| `System.Diagnostics.Process` | — | partial | — | — |
| **ProcessKit** | **✓** | **✓** | **✓** | **✓** |

The first column is the differentiator: a child's *descendants* are contained and reaped as a
unit (Job Object / cgroup v2 / process reaper / process group), not just the direct child.

For a fuller, axis-by-axis comparison against `System.Diagnostics.Process`, CliWrap,
Medallion.Shell, and SimpleExec — plus "was → now" migration snippets for the most common
patterns — see **[Comparison and migration guide](docs/comparison.md)**.

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
# optional — Microsoft.Extensions.Hosting integration (supervised child as IHostedService)
dotnet add package ProcessKit.Extensions.Hosting
# test projects only — subprocess-free test doubles (ScriptedRunner / FakeProcess / record-replay)
dotnet add package ProcessKit.Testing
```

Each companion package depends on the matching `ProcessKit` version, so installing one pulls in the
core automatically.

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

`OutputBytesAsync()` reads stdout without decoding it. If that read fails before teardown with an
`IOException` or `ObjectDisposedException`, the task surfaces a `ProcessException` whose `Error` is
`ProcessError.Io` and whose detail is the underlying stream message. The same exceptions caused by a
concurrent `StopAsync()` or disposal are treated as the expected end of capture instead.

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
        do! group.ShutdownAsync(TimeSpan.FromSeconds 5.0) // configured soft signal → wait → hard kill
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
await group.ShutdownAsync(TimeSpan.FromSeconds(5)); // configured soft signal → wait → hard kill
```

## Documentation

This README is the quick tour. The **[`docs/` guide set](docs/README.md)** goes deeper on every
capability, with more examples and the platform fine print collected in one place. New here? Skim
the [Cookbook](docs/cookbook.md) first — it maps "I want to …" tasks to working snippets — then
read [Running commands](docs/commands.md) end to end. The same guides are published as a browsable
site at **<https://zelanton.github.io/ProcessKit-fSharp/>**; for a member-by-member lookup instead of
a narrative guide, browse the generated
**[API reference](https://zelanton.github.io/ProcessKit-fSharp/api/)** alongside it.

Prefer compiled examples? The [`samples/`](samples/) directory contains runnable F# and C# console
projects covering capture, streaming readiness, pipelines, supervision, DI, a hosted worker service,
health reporting, and test doubles.

| Guide | Covers |
|---|---|
| [Cookbook](docs/cookbook.md) | Task → snippet recipes for everything below; the fastest way in |
| [Coming from ProcessKit-rs](docs/from-rust.md) | Rust-to-.NET mappings for verbs, ownership, errors, optional features, and test seams |
| [Scripting with F# Interactive](docs/scripting.md) | `.fsx` setup, one-off helpers, reusable CLI clients, Ctrl+C cleanup, and portable tool resolution |
| [Running commands](docs/commands.md) | The full `Command` builder and every consuming verb, with error semantics |
| [Process groups](docs/process-groups.md) | Containment, teardown, signals, suspend/resume, members, limits, stats |
| [Streaming & interactive I/O](docs/streaming.md) | Line/Content-Length streaming, conversational stdin, readiness probes, `WaitAnyAsync`, profiling |
| [Performance & scalability](docs/performance.md) | Event-driven waits, buffer/backpressure tuning, fleet sizing, benchmark interpretation |
| [Pipelines](docs/pipelines.md) | Shell-free `a → b → c`, pipefail attribution, chain timeouts |
| [Timeouts, retries & cancellation](docs/timeouts-and-cancellation.md) | Captured vs raised deadlines, retry classifiers, `CancellationToken` |
| [Supervision](docs/supervision.md) | Restart policies, backoff & jitter, stop conditions, outcomes |
| [Testing your code](docs/testing.md) | The `IProcessRunner` seam, scripted/fault-injecting/record-replay doubles, cassettes, `CliClient` |
| [Platform support](docs/platform-support.md) | Mechanisms, every capability matrix, and each caveat |

Where the project is headed: the **[roadmap](ROADMAP.md)**.

## One runtime package, plus opt-in side packages

There are no compile-time feature flags to choose: the `ProcessKit` runtime package ships the whole
production surface, and its optional capabilities are just modules you use when you need them. The
kill-on-dispose tree guarantee is unconditional. Two concerns live in **separate, opt-in packages**
so they never reach a consumer's production dependency graph: the **test doubles**
(`ProcessKit.Testing` — a subprocess-free `ScriptedRunner`/`FakeProcess` and the disk/JSON
record-replay `RecordReplayRunner`, referenced only from test projects) and the
**dependency-injection wiring** (`ProcessKit.Extensions.DependencyInjection`).

| Capability | Where |
|---|---|
| Tree control — `Signal` / `Suspend` / `Resume` / `Members` | `ProcessGroup` |
| Resource caps — memory / process count / CPU quota, affinity, time, and disk I/O | `ProcessGroupOptions` → `ProcessGroup.Create` |
| Stats & profiling — `Stats` / `SampleStatsAsync` / `ProfileAsync` | `ProcessGroup`, `RunningProcess` |
| Test doubles — `ScriptedRunner` / `FakeProcess` | `ProcessKit.Testing` (separate package) |
| Record / replay cassettes | `ProcessKit.Testing.RecordReplayRunner` (separate package) |
| Observability — logging, tracing & metrics ([guide](docs/observability.md)) | `Command.Logger`, `ProcessKitDiagnostics` (`ActivitySource` / `Meter`) |
| Dependency-injection wiring | `ProcessKit.Extensions.DependencyInjection` (separate package) |

Public core and `ProcessKit.Testing` APIs that reject a required reference argument with
`ArgumentNullException` report the signature name in `ParamName`. Multi-argument entry points keep
their existing validation order, so the first invalid argument remains deterministic for C# callers.
Public range-validation entry points in core, dependency-injection, and hosting APIs likewise report
the rejected signature parameter in `ArgumentOutOfRangeException.ParamName`; methods with multiple
range-checked inputs preserve their existing first-failure order.

On Windows, `Command.WindowsRawArg` is an explicit escape hatch for trusted fixed fragments required
by non-MSVCRT parsers. Ordinary arguments are still quoted first; raw fragments are appended verbatim,
are rejected on POSIX and for automatically wrapped `.cmd`/`.bat` targets, and must never contain
untrusted input. See the [full contract and examples](docs/commands.md#windows-raw-command-line-fragments).

## Capping a group's resources

`ProcessGroupOptions` can bound memory, process count, CPU quota/affinity, and CPU time at creation, so a
runaway or untrusted child tree can't exhaust the host:

**F#**

```fsharp
task {
    let options =
        ProcessGroupOptions()
            .WithMemoryMax(512L * 1024L * 1024L) // 512 MiB across the tree
            .WithMaxProcesses(64)
            .WithCpuQuota(0.5)                    // half of one core
            .WithCpuTimeMax(TimeSpan.FromSeconds 30.0)
            .WithIoMax("259:0", 100L * 1024L * 1024L, 100L * 1024L * 1024L, 1000L, 1000L)

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
    .WithCpuQuota(0.5)                    // half of one core
    .WithCpuTimeMax(TimeSpan.FromSeconds(30));

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
`WithCpuTimeMax` is the exception to that container rule: Windows applies it to the Job as a whole,
while POSIX applies `RLIMIT_CPU` to each spawned run before `exec`, so it also works on macOS/BSD and
the Linux process-group fallback. Its live value cannot be changed for already-running POSIX children.

`WithIoMax` adds directional disk-bandwidth and IOPS ceilings for one target. On Linux, use a cgroup
v2 device key such as `259:0`; the four rates are independent `io.max` fields. On Windows, use the
NT volume device name (for example, `\Device\HarddiskVolume3`); the Job Object applies aggregate
bandwidth and IOPS ceilings, so each read/write pair must match. The `int64` overload uses `0` for
an unbounded direction; the option overload uses `None`. Every supplied rate must be positive and
at least one direction must be bounded. This is an enforce-or-refuse capability: a missing Linux
`io` controller, an unavailable Windows I/O controller, or any POSIX process-group backend returns
typed `ProcessError.Unsupported` rather than creating an unrestricted group.

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

`RunningProcess.Signal` targets one run without consuming the handle; `ProcessGroup.Signal` broadcasts.
On Windows `Kill` maps to termination, while `Int`/`Term` use the documented best-effort
CTRL+BREAK/WM_CLOSE paths and other signals return `ProcessError.Unsupported`. Suspend/resume work everywhere a
container exists — `cgroup.freeze` on Linux, `SIGSTOP`/`SIGCONT` on macOS/BSD and the
process-group fallback, per-thread suspension on Windows.

After `ProcessGroup.KillAll()` returns `Ok`, the group is reusable. On Linux cgroup v2, both atomic
`cgroup.kill` and the legacy per-member fallback explicitly thaw and verify `cgroup.freeze=0`; an
unverified thaw returns `ProcessError.Io`, while an already-unfrozen or removed freezer remains a
best-effort success. On kernels without `cgroup.kill`, the fallback can additionally return
`ProcessError.Io` when a member could not be signalled and the group is still populated afterwards. That
fallback pins each member and reconfirms its cgroup membership before delivering SIGKILL, so a recycled
pid is skipped rather than killed. An error makes no reuse guarantee; final disposal still runs its
bounded best-effort drain and cgroup-reclaim attempt.

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

            let! result = RunningProcess.WaitAnyAsync [| a; b |]
            printfn $"contender #{result.Index} exited first with {result.Outcome}"
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

var first = await RunningProcess.WaitAnyAsync([a, b]);
Console.WriteLine($"contender #{first.Index} exited first with {first.Outcome}");
```

`Members()` lists the whole tree on Windows (Job Object), Linux (cgroup) and FreeBSD (process
reaper); the POSIX process-group backend lists the tracked group *leaders* only. `WaitAnyAsync` applies no per-process
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

Need the batch to stop early instead? `Exec.outputAllWithPolicy` / `outputAllBytesWithPolicy` take
an explicit `BatchPolicy`: `BatchPolicy.CollectAll` behaves exactly like `outputAll` itself, while
`BatchPolicy.FailFast` stops starting new commands and cancels every command already running on the
batch's first `Error` (see [cookbook.md → Top-level Exec helpers](docs/cookbook.md#top-level-exec-helpers)
for the full contract).

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

`Stats()`/`SampleStatsAsync` report full CPU/memory on Windows and the Linux cgroup backend. On Linux
cgroup v2, `PeakProcessCount` is available from the kernel's lifetime `pids.peak` counter only when
`MaxProcesses` is configured and the kernel is version 6.6 or later; otherwise it is `None`. The
counter measures kernel tasks (processes and their threads), so it is not directly comparable with
`ActiveProcessCount`, which counts process leaders. Windows and the POSIX process-group fallback also
return `None` rather than estimating the peak from samples. The POSIX fallback otherwise reports
active counts only; `ProfileAsync` samples the started child itself.

For attribution inside a shared tree, `MemberStats()` returns a point-in-time `MemberStats` record for
each member: its `Pid`, cumulative `CpuTime`, current `ResidentMemoryBytes`, and optional per-process
I/O counters (`IoReadBytes`, `IoWriteBytes`, `IoReadOperations`, and `IoWriteOperations`). Windows binds
each Job PID snapshot to a pre-sampling process identity before opening the query handle; protected or
otherwise inaccessible members are retained with `None` metrics only when that identity and current Job
membership still match. A PID whose generation changed is omitted, including reuse inside the same Job.
Linux reads `/proc` (including per-process I/O when available) for the whole cgroup membership: tracked and
adopted leaders use pinned identities, while descendants use a snapshot identity checked again after the
read. A member that exits or changes identity during the sample is omitted, and unsupported metrics remain
`None` rather than becoming zeroes. The call returns the same typed lifecycle error as `Stats()` after
the group is released.

*Deeper: [Process groups → stats](docs/process-groups.md#stats) ·
[Streaming → profiling a run](docs/streaming.md#profiling-a-run).*

## Supervising a long-lived child

Where `Command.Retry` replays one run with a fixed pause,
`Command.RetryBackoff` provides bounded exponential backoff with optional jitter for that same
finite operation. A `Supervisor` instead keeps a child **alive**:
it restarts the command per policy whenever it exits, with bounded restarts and exponential
backoff (jittered by default so a restarted fleet doesn't stampede):

Both retry classifiers receive typed `ProcessError` values. If a classifier throws, the consuming
verb returns the terminal `ProcessError.RetryPredicate`, preserving the failed attempt in its
`Original` field and the callback exception message in `Detail`; the raw exception does not escape
and no further attempt starts. See [timeouts, retries & cancellation](docs/timeouts-and-cancellation.md)
for the full contract.

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

`Supervisor.StartAsync()` exposes a live `SupervisionSession`. Its `StopAsync` gracefully stops a
spawned process, while a capture-only runner is interrupted immediately through the capture token;
if at least one incarnation produced a result, the session completes with `StopReason.Stopped`.
Otherwise it returns the last error, or `ProcessError.Cancelled` when there is no error to report.
External token cancellation also remains `ProcessError.Cancelled`.

The optional `LivenessMemory` probe intentionally samples attributable **peak** tree memory for each
incarnation. A transient peak remains a violation after current usage falls, so choose a threshold above
expected startup spikes when they should not cause a restart; unsupported backends return a typed error.

Exceptions from Supervisor's StopWhen, GiveUpWhen, OnRestart, and OnStormPause callbacks are returned
as a typed ProcessError.Io from RunAsync or SupervisionSession.Completion. The error names the callback
and keeps the available result/error context; the raw exception does not escape, no later incarnation
is launched, and normal supervision teardown still runs.

*Deeper: [Supervision](docs/supervision.md).*

## Waiting for a child to be ready

"Start a server, then use it" needs the server to be *ready*, not merely started. Nine probes —
a stdout line, a stderr line, a newline-free stderr prompt, a TCP port, a Unix socket, a Windows
named pipe, an HTTP endpoint, a filesystem path, or any async predicate of your own — replace the
arbitrary sleep:

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
`WaitForLineAsync`, its stdout closes; for `WaitForStderrLineAsync`/`WaitForStderrTailAsync`, its
stderr does) — fails with `ProcessError.NotReady` (distinct from a timeout)
and **does not kill the child**: the caller decides what happens next.

If a `WaitForStderrLineAsync` or `WaitForStderrTailAsync` predicate throws, the returned task faults
with that same exception, whether the predicate examined output retained between waits or output that
arrived after the wait was armed. Catch it around `await`; the failed wait leaves the stderr pump and
other waits running, and a retained observation is not consumed merely because its predicate threw.
This includes `OperationCanceledException` and its subclasses: only cancellation of the wait itself —
through the caller's token or its deadline — becomes a typed `Cancelled` or `NotReady` result. A
predicate may also start another stderr readiness wait on the same handle without blocking; the outer
scan claims first, then the nested wait sees only the retained observations that remain.

HTTP readiness and supervisor liveness accept a caller-owned `HttpClient` for authentication headers,
custom TLS validation, proxies, or alternate transports. ProcessKit reuses but never mutates or
disposes that client; the caller retains its lifetime. HTTP probe URIs must be absolute, explicit
acceptable-status sets must be non-empty, and invalid Unix-socket paths fail before polling begins.

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
reported program come from the rightmost checked failure, or from the real last stage when there is
none. For a consumer that legitimately stops reading early (the `producer | head -1` shape), mark
the producer `Command.uncheckedInPipe` and pipefail skips its expected broken-pipe death, including
`SIGPIPE` on POSIX, when selecting the culprit. If the last stage is unchecked and voluntarily exits,
its real exit code is accepted and preserved; signal, timeout, and unobserved outcomes remain failures.
`Pipeline.Timeout` bounds the whole chain.

A genuine failure while reading an upstream stage's stdout in the inter-stage relay is returned as
`ProcessError.Io`, even if the downstream stage receives a truncated EOF and exits successfully. This
is distinct from an expected downstream broken-pipe or whole-chain teardown race when a consumer exits
early or the pipeline is cancelled; those races remain quiet and are classified from the process outcomes.

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

        // After the stream ends, collect the outcome, stderr, and any output-truncation signal.
        match! proc.FinishAsync() with
        | Ok finished ->
            if finished.Outcome <> Outcome.Exited 0 || finished.Truncated then
                eprintfn $"{finished.Stderr}"
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
await using var proc = (await new Command("git").Args(["log", "--oneline", "-n", "50"]).StartAsync()).GetValueOrThrow();

await foreach (var line in proc.StdoutLinesAsync())
    Console.WriteLine($"commit: {line}");

// After the stream ends, collect the outcome, stderr, and any output-truncation signal.
var finished = (await proc.FinishAsync()).GetValueOrThrow();
if (finished.Outcome is not { IsExited: true, Code.Value: 0 } || finished.Truncated)
    Console.Error.WriteLine(finished.Stderr);
```

The command's `Timeout` **bounds the stream**: at the deadline the tree is killed, the pipes
close, and the stream ends.

### Stream stdout as bytes

`RunningProcess.StdoutChunksAsync()` yields byte-exact `ReadOnlyMemory<byte>` chunks without decoding
or line framing. It is the exclusive stdout consumer for that process, so choose it instead of
`StdoutLinesAsync()` (and any other stdout-consuming verb); call `FinishAsync()` afterwards to collect
the outcome and drained stderr:

**C#**

```csharp
await using var proc = (await new Command("producer")
    .StreamBuffer(StreamBufferPolicy.Bounded(64, StreamFullMode.Backpressure))
    .StartAsync()).GetValueOrThrow();

await using var destination = Console.OpenStandardOutput();
await foreach (var chunk in proc.StdoutChunksAsync())
    await destination.WriteAsync(chunk);

var finished = (await proc.FinishAsync()).GetValueOrThrow();
```

`StreamBuffer` bounds queued chunks; `Backpressure` preserves every byte while slowing the child
when the consumer falls behind. If a consumer is abandoned, `FinishAsync`, `StopAsync`, shared exit
waits, and disposal release the parked writer before waiting for the process outcome. See
[Streaming & interactive I/O](docs/streaming.md) for the full streaming and lifecycle contract.

For bounded parent-side log rotation, attach a caller-owned `RotatingFileSink` through `StdoutTee`
or `StderrTee` and dispose it after the run. A later `WriteAsync` or `FlushAsync` fails with an
`ObjectDisposedException` that names `RotatingFileSink`.

For conversational language-server, build-server, or MCP-style children, use the
[JSON-RPC 2.0 session layer](docs/streaming.md#json-rpc-sessions-lsp--bsp--mcp). It rejects every
incoming frame whose `jsonrpc` member is missing, non-string, or not exactly `"2.0"` with a typed
`ProcessError.Parse` before request, notification, or response routing. A response `id` that is a
boolean, object, or array likewise ends the session with `ProcessError.Parse`; a valid string,
number, or `null` that does not identify a pending request is still discarded as an unknown or late
response. Its decoded-message backlog may drop old notifications, counted by `DroppedMessages`;
evicting a peer request ends the session with `ProcessError.OutputTooLarge`, whose totals are zero
because this backlog does not count total messages or bytes. A peer request accepts one response that
starts writing; if response-body encoding throws before that point, the same `JsonRpcMessage` can be
retried, while any attempt after frame writing starts remains rejected.

### Interactive stdin — write requests, read responses

Keep stdin open with `KeepStdinOpen`, take the writer with `TakeStdin()`, then interleave writes
and reads. Take it before you drive the handle to completion: the writer has exactly one owner, so
a completion verb that finds it untaken ends the child's input itself (which is what keeps such a
verb from hanging on a child that reads to EOF) and `TakeStdin()` then returns `None` — see
[Who owns the kept-open writer](docs/streaming.md#who-owns-the-kept-open-writer).

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

`WriteLineAsync` appends LF for an ordinary stdin pipe or POSIX PTY, and CR for Windows ConPTY so
console line readers receive Enter. `WriteAsync` always sends exactly the supplied bytes.
Windows ConPTY children also start in a fresh console process group, isolating a default `Pty` run
from CTRL+C broadcasts on the caller's console; `WindowsCtrlSignals()` is still required to expose
that leader to ProcessKit's directed CTRL+BREAK API.

> For a **large** interactive stdin, write from one task and read `StdoutLinesAsync()` from another —
> otherwise the child can block writing stdout while you block writing stdin, a full-duplex
> deadlock. The non-interactive `Stdin.From*` sources are written on a background task and never
> deadlock.

For an expect-style `PtySession`, construction claims the interactive writer without waiting for a
`Stdin(source)` feeder to finish. `SendAsync`, `SendLineAsync`, and `CloseStdinAsync` wait for that
feeder before writing or closing, so a slow source or a child that is not currently reading stdin
cannot block session construction or let two writers use the pipe at once. Its regex `ExpectAsync`
overload matches an immutable output snapshot without holding the session's shared window lock, so a
costly regex does not block incoming output or another window operation. Consumption is conditional
on that snapshot still being current, preventing stale or concurrent matches from consuming different
text or the same text twice. A regex `MatchTimeout` is returned as `ProcessError.NotReady` with that
match budget; another matcher exception is returned as `ProcessError.Io`, rather than faulting the
returned task. Caller cancellation and the per-pattern deadline are checked before retrying a stale
snapshot or classifying a matcher failure; caller cancellation wins when both limits have fired, while
a successful current match is still consumed. The close verbs on all
interactive sessions — `PtySession.CloseStdinAsync(cancellationToken)`,
`ContentLengthSession.FinishInputAsync(cancellationToken)`, and
`JsonRpcSession.FinishInputAsync(cancellationToken)` — return
`Error(ProcessError.Cancelled program)` when cancellation fires while waiting for the feeder or
send gate; no EOF is delivered in that case. Once the writer/gate has been claimed and EOF delivery
starts, it is not cancellable. The no-token overloads remain equivalent to passing
`CancellationToken.None`.

*Deeper: [Streaming & interactive I/O](docs/streaming.md).*

### Additional POSIX file-descriptor channels

For protocols that use a control channel outside stdin/stdout/stderr, add a full-duplex child
descriptor with `ExtraFd(3)` and claim its parent `Stream` once from the started process:

```fsharp
match! (Command.create "worker" |> Command.extraFd 3).StartAsync() with
| Ok proc ->
    use _ = proc
    use channel = proc.TakeExtraFd(3) |> Option.defaultWith (fun () -> failwith "missing fd 3")
    // Read and write the protocol through channel; the child uses fd 3.
    ()
| Error err -> eprintfn $"{err.Message}"
```

Targets must be unique and at least 3. Windows, pipelines, detached launches, and the in-memory
testing/cassette runners return `ProcessError.Unsupported` rather than dropping the channel.

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

`WithDefaults` is shared by every client invocation, so its stdin source must be replayable
(`FromString`, `FromBytes`, or `FromFile`). Attach a one-shot `FromStream`, `FromLines`, or
`FromAsyncLines` source to the individual command returned by `client.Command(...)` instead.

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
*values* reach the file only as a hashed fingerprint (alongside the variable names). `program`,
`args`, `stdout`, `stderr`, and a recorded **failure**'s own text — its streams, detail, JSON-RPC
`data`, and the `PATH` a `NotFound` searched (the one environment *value* a cassette keeps, an
`Env("PATH", …)` override included) — *are* stored verbatim and can carry secrets. `WithRedaction`
scrubs the captured text and every one of those failure fields; `program`/`args` are stored as given
unless you add the opt-in `WithCommandProjection`, which decides what the file keeps for those two
(matching still keys on a fingerprint of the *invoked* command line, so a projected cassette replays
what an unprojected one would). Review a fixture before committing it; on Unix the file is written `0600`. A crash writes
no cassette behind your back *until you declare the recording finished*: the best-effort flush on
dispose happens only after `Complete()` marks it so, and a scope left by a failed assertion before
that call creates no cassette and changes none already on disk. That mark is the whole gate — dispose
is never told how the scope ended — so a throw *after* `Complete()` still flushes the run's verbatim
argv/output: complete last, after whatever can fail. `Save()` stays the unconditional,
error-reporting way to persist a recording.
Duplicate matches replay in capture order and then repeat the last entry. If a strict `Replay` or an
`Auto` hit is cancelled before its matched entry is accepted, it returns `ProcessError.Cancelled`
without advancing that order; the next non-cancelled text, bytes, or `SpawnAsync` call receives the
same entry. Lookup, cancellation acceptance, and cursor advancement are one gated operation, so a
cancelled call cannot require a rollback that could undo a concurrent replay.
Capture replay and live handles reconstructed by `SpawnAsync` both preserve the recorded duration
and truncation state; a stricter output-buffer policy on the replay command can additionally mark
the reconstructed handle's result as truncated.

*Deeper: [Testing your code → record/replay](docs/testing.md#record-and-replay).*

## Observability and dependency injection

Opt into structured lifecycle events (spawn, exit, timeout, retry, supervisor restart) with
`Command.Logger` — each with a stable `EventId` and a per-run `RunId` for correlation. ProcessKit also
emits a `System.Diagnostics` **trace span** per run (`ActivitySource` `ProcessKitDiagnostics.ActivitySourceName`)
and OpenTelemetry-ready **metrics** (`Meter` `ProcessKitDiagnostics.MeterName`). **argv and the
environment are never logged, traced, or tagged** — only the program name and non-secret facts. The
separate `ProcessKit.Extensions.DependencyInjection` package registers an `IProcessRunner` for
`Microsoft.Extensions.DependencyInjection` consumers with `AddProcessKit()` (logger-aware when the
container has an `ILoggerFactory`). The runners registered by `AddProcessKit()` and
`AddProcessKitGroup()` reject a null `Command` synchronously with `ArgumentNullException`
(`ParamName = "command"`) before applying defaults or logging and before delegating to their
underlying runner. A keyed `AddProcessKitClient(..., configure)` callback must likewise return a
non-null `CliClient` for the registered program; resolution rejects a null result with
`ArgumentNullException` and a client for another program with `ArgumentException`, both naming
`configure`.
The DI registration overloads also name each null argument after their public signatures
(`services`, `configure`, `configuration`, `name`, or `program`); the Hosting registration and
configuration overloads do the same for `services`, `name`, `command`, `configureSupervisor`, and
`configure`.

*Deeper: [Observability](docs/observability.md).*

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). To report a
security issue, follow [SECURITY.md](SECURITY.md).

## License

Licensed under the [MIT License](LICENSE) © Anton Zhelezniakou.
