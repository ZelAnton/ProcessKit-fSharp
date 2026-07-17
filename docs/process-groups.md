# Process groups

[Previous: Overview](./)

A `ProcessGroup` ties the lifetime of a whole child-process **tree** to a single
disposable value: every process you start into the group — and everything *those*
processes spawn — is killed when the group is disposed. An owner that returns
early, throws, or has its task dropped never leaks subprocesses, because the
kernel object behind the group (a **Windows Job Object**, a **Linux cgroup v2**,
or a **POSIX process group**) reaps even grandchildren you never knew existed.
That whole-tree containment is the reason this library exists:
`System.Diagnostics.Process` reaches the direct child at best, so a build tool's
compiler children, the real payload behind a `cmd /c …` / `sh -c …` wrapper, or a
test's helper servers can outlive a timeout or an exception as orphans.

You rarely create a group by hand for one-shot runs: every one-shot verb
(`RunAsync`, `OutputStringAsync`, …) already spawns into a fresh private group that dies
with the run. Reach for an explicit `ProcessGroup` when **several children should
share one fate**, or when you need the group-level verbs below — signals,
suspend/resume, member listing, resource limits, or stats.

- [Creating a group](#creating-a-group)
- [Putting processes in](#putting-processes-in)
- [Tearing down: dispose, terminate, shutdown](#tearing-down-dispose-terminate-shutdown)
- [Signals and suspend/resume](#signals-and-suspendresume)
- [Listing members](#listing-members)
- [Resource limits](#resource-limits)
- [Stats](#stats)

## Creating a group

`ProcessGroup.Create()` builds an empty, unbounded group on the current platform.
It returns a `Result<ProcessGroup, ProcessError>` — match it, then bind the group
with `use` so it (and the tree it contains) is reaped on scope exit:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Error err -> eprintfn $"could not create a group: {err.Message}"
    | Ok group ->
        use group = group // disposes — and hard-kills the whole tree — on scope exit
        // ... start children into `group` ...
        ()
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine($"could not create a group: {err.Message}");
    return;
}

using var group = created.GetValueOrThrow(); // disposes — and hard-kills the whole tree — on scope exit
// ... start children into `group` ...
```

`ProcessGroup.Create(options)` takes a `ProcessGroupOptions` to tune the
graceful-shutdown window and apply whole-tree resource limits (see
[Resource limits](#resource-limits)):

**F#**

```fsharp
let options = ProcessGroupOptions().WithShutdownTimeout(TimeSpan.FromSeconds 10.0)

match ProcessGroup.Create options with
| Ok group ->
    use group = group
    () // ...
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var options = new ProcessGroupOptions().WithShutdownTimeout(TimeSpan.FromSeconds(10));

var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err.Message);
    return;
}

using var group = created.GetValueOrThrow(); // ...
```

Two read-only properties report what you actually got. `Options` echoes back the
`ProcessGroupOptions` the group was created with (its `ShutdownTimeout` and
`Limits`); `Mechanism` reports the OS primitive containing the tree:

**F#**

```fsharp
match group.Mechanism with
| Mechanism.JobObject -> printfn "Windows Job Object"
| Mechanism.CgroupV2 -> printfn "Linux cgroup v2"
| Mechanism.ProcessGroup -> printfn "POSIX process group"
| _ -> ()
```

**C#**

```csharp
Console.WriteLine(group.Mechanism switch
{
    { IsJobObject: true }    => "Windows Job Object",
    { IsCgroupV2: true }     => "Linux cgroup v2",
    { IsProcessGroup: true } => "POSIX process group",
    _                        => "unknown mechanism",
});
```

Which mechanism you get is not a free choice — it follows the platform and
whether you asked for limits:

- **Windows** always uses a **Job Object** (`Mechanism.JobObject`).
- **Linux** uses a **cgroup v2** (`Mechanism.CgroupV2`) only when you request
  resource limits *and* the host can deliver them; for plain containment — and on
  any Linux host without delegated cgroup v2 — it uses a **POSIX process group**
  (`Mechanism.ProcessGroup`).
- **macOS / BSD** always use a **POSIX process group** (`Mechanism.ProcessGroup`).

Because the mechanism is reported rather than assumed, a weaker backend is never
a silent downgrade — you can branch on `Mechanism` if a capability matters. The
full per-OS matrix lives in [platform-support.md](platform-support.md).

## Putting processes in

A `ProcessGroup` **is itself an `IProcessRunner`**, so the same run/capture
vocabulary you use on a `Command` works against the shared group — every child
lands in the one container.

The direct door is `StartAsync(command)`, which returns a live `RunningProcess` (the
full streaming / stdin / readiness surface from
[streaming.md](streaming.md)). The key ownership rule: **the group owns the
child's lifetime.** Disposing the returned `RunningProcess` detaches only that
run's I/O; the child keeps running until you reap the whole tree
(`ShutdownAsync` / dispose) or kill just that run with its own `Kill`.

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok group ->
        use group = group

        match! group.StartAsync(Command.create "dev-server") with
        | Ok server ->
            // `server` streams/probes as usual, but the GROUP owns its lifetime.
            let! _ready = server.WaitForLineAsync((fun l -> l.Contains "ready"), System.TimeSpan.FromSeconds 10.0)
            ()
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err.Message);
    return;
}

using var group = created.GetValueOrThrow();

var started = await group.StartAsync(new Command("dev-server"));
if (started is { IsOk: false, ErrorValue: var startErr })
{
    Console.Error.WriteLine(startErr.Message);
    return;
}

var server = started.GetValueOrThrow();
// `server` streams/probes as usual, but the GROUP owns its lifetime.
var ready = await server.WaitForLineAsync(l => l.Contains("ready"), TimeSpan.FromSeconds(10));
```

To **capture** a child to completion inside the shared group, drive the group
through the `IProcessRunner` verbs in the `Runner` module — they take the runner,
a `CancellationToken`, and the `Command`:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group

        match! Runner.outputString group CancellationToken.None (Command.create "probe-tool") with
        | Ok result -> printfn $"exit={result.Code}: {result.Stdout}"
        | Error err -> eprintfn $"{err.Message}"
    // `Runner.outputBytes` is the binary companion; `Runner.start` mirrors `group.StartAsync`.
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err.Message);
    return;
}

using var group = created.GetValueOrThrow();

Console.WriteLine(await Runner.outputString(group, CancellationToken.None, new Command("probe-tool")) switch
{
    { IsOk: true, ResultValue: var result }  => $"exit={result.Code}: {result.Stdout}",
    { IsOk: false, ErrorValue: var runErr } => runErr.Message,
});
// `Runner.outputBytes` is the binary companion; `Runner.start` mirrors `group.StartAsync`.
```

> **Capture normalization, and a Windows caveat.** A capture through a shared group goes through the
> same path as the default runner, so output encoding, line-ending normalization, `OkCodes`, and the
> `OutputBuffer` policy match exactly — a `ProcessGroup` runner is interchangeable with the default one.
> One platform caveat: on Windows a per-run `Timeout` / `CancelOn` hard-kills only the run's *leader*
> process (its descendants stay in the shared Job until the group is torn down). So if a descendant
> inherited the leader's stdout/stderr pipe and outlives it, the capture can stall past the deadline
> until that descendant exits or the group is disposed. POSIX kills the leader's whole process group, so
> it is unaffected. For a hard per-run deadline on Windows, give the run its own group (the default
> runner) rather than a shared one.

Because a group satisfies `IProcessRunner`, you can also hand it to anything that
accepts a runner so a whole fleet shares one kill-on-dispose container: pass it as
the runner to `Exec.outputAll` / `Exec.outputAllBytes`, or to
`Supervisor.WithRunner` so every restarted incarnation stays in the same group
(see [supervision.md](supervision.md)).

## Tearing down: dispose, terminate, shutdown

There are three ways out, from blunt to graceful:

| Verb | What happens | When to use it |
|---|---|---|
| dispose (`use` / `Dispose()` / `DisposeAsync()`) | Immediate **hard kill** of the whole tree, then releases the container | The safety net — always on, even on an exception or early return |
| `group.KillAll()` | The same hard kill, but the group **stays usable** for further spawns; idempotent | Explicit teardown mid-flight when you want to keep the group |
| `group.ShutdownAsync()` / `group.ShutdownAsync(grace)` | **Graceful**: on Unix `SIGTERM` → wait the grace window → `SIGKILL` survivors; on Windows a best-effort `WM_CLOSE` to any GUI child's windows → wait the grace window → atomic Job kill of survivors. Releases the group | A clean service stop |

`ProcessGroup` implements both `IDisposable` and `IAsyncDisposable`, so a `use`
binding reaps the tree deterministically on scope exit — disposing is a pure hard
kill with no grace, which is exactly what you want as the guaranteed backstop.
For an orderly stop, prefer `ShutdownAsync`, which awaits a `Task`:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! _service = group.StartAsync(Command.create "my-service")

        // SIGTERM, give it 5s to flush and exit, then SIGKILL any straggler:
        do! group.ShutdownAsync(TimeSpan.FromSeconds 5.0)
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err.Message);
    return;
}

using var group = created.GetValueOrThrow();
await group.StartAsync(new Command("my-service"));

// SIGTERM, give it 5s to flush and exit, then SIGKILL any straggler:
await group.ShutdownAsync(TimeSpan.FromSeconds(5));
```

`ShutdownAsync()` with no argument uses the group's configured
`Options.ShutdownTimeout` (the default is 2 seconds; set it with
`WithShutdownTimeout`). A child that handles `SIGTERM` and exits ends the grace
**early** — `ShutdownAsync` returns as soon as the tree is empty, not after the full
window. `ShutdownAsync` and dispose are idempotent with each other, so a `use`-bound
group you also `ShutdownAsync` explicitly is safe. Note that a *suspended* tree can
still be hard-killed (dispose / `KillAll`), but a graceful `ShutdownAsync` opens
with a `SIGTERM` a frozen tree cannot act on — `Resume` first for a clean stop
(see below).

## Signals and suspend/resume

Beyond teardown, a group can broadcast a signal to every member, or freeze and
thaw the whole tree. All of these are synchronous and return
`Result<unit, ProcessError>`.

`Signal(signal)` delivers a portable `Signal` to every process in the group:

**F#**

```fsharp
let reload (group: ProcessGroup) =
    match group.Signal Signal.Hup with // "reload your configuration"
    | Ok () -> ()
    | Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
void reload(ProcessGroup group)
{
    if (group.Signal(Signal.Hup) is { IsOk: false, ErrorValue: var err }) // "reload your configuration"
        Console.Error.WriteLine(err.Message);
}
```

The portable `Signal` values are `Signal.Term`, `Signal.Kill`, `Signal.Int`,
`Signal.Hup`, `Signal.Quit`, `Signal.Usr1`, `Signal.Usr2`, and the raw escape
hatch `Signal.Other n` for any other signal number.

| Platform | Deliverable signals |
|---|---|
| Linux (cgroup or process group), macOS / BSD | Any — `Term`, `Kill`, `Int`, `Hup`, `Quit`, `Usr1`, `Usr2`, `Other n` |
| Windows | `Kill` (maps to the Job terminate); `Int` / `Term` as a best-effort soft stop (see below); anything else → `ProcessError.Unsupported` |

On **Windows**, `Signal.Int` and `Signal.Term` map to a best-effort soft stop built
from two individually-targeted mechanisms:

- a console **CTRL+BREAK** to each child started with `Command.WindowsCtrlSignals()`
  (spawned in its own console process group), and
- a **`WM_CLOSE`** posted to the top-level windows of every member that has one —
  the standard graceful close a windowed app (an Electron/GUI tool) turns into its
  own shutdown, exactly what `taskkill` (without `/F`) does. It is targeted strictly
  by process id, so it never reaches a window outside the group, and needs no opt-in.

Either mechanism reaching at least one member is a best-effort `Ok` (delivery is not
compliance — a child may install its own handler or a window may prompt/veto the
close). The call returns `ProcessError.Unsupported` **only** when the group has
*neither* a CTRL-capable child *nor* any member with a top-level window — nothing to
soft-signal at all — never a silent downgrade to the hard Job kill. A child with no
window is simply a `WM_CLOSE` no-op, not a regression.

`Signal.Kill` always takes the same atomic whole-tree kill path as
`KillAll`, so it can't miss a process forked mid-broadcast; other signals are
a best-effort per-member broadcast against a tree that may be forking at that
instant. An already-exited member is skipped, and an empty group accepts any
deliverable signal trivially. On Windows, an undeliverable signal fails fast:

**F#**

```fsharp
match group.Signal Signal.Hup with
| Ok () -> ()
| Error(ProcessError.Unsupported operation) -> eprintfn $"not on this platform: {operation}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
if (group.Signal(Signal.Hup) is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err switch
    {
        ProcessError.Unsupported { Operation: var op } => $"not on this platform: {op}",
        _                                              => err.Message,
    });
```

`Suspend()` freezes the whole tree (to snapshot it, to starve a runaway while you
investigate, or to pause background work) and `Resume()` thaws it:

**F#**

```fsharp
let pauseWhile (group: ProcessGroup) (inspect: unit -> unit) =
    group.Suspend() |> ignore // the whole tree stops consuming CPU
    inspect ()
    group.Resume() |> ignore
```

**C#**

```csharp
void pauseWhile(ProcessGroup group, Action inspect)
{
    group.Suspend(); // the whole tree stops consuming CPU
    inspect();
    group.Resume();
}
```

Suspend/resume work wherever a container exists, but the machinery differs:

- **Linux cgroup v2** — a single `cgroup.freeze` write; atomic over the subtree.
- **Linux process group, macOS / BSD** — a `SIGSTOP` / `SIGCONT` broadcast;
  level-triggered, so it is idempotent.
- **Windows** — a per-thread suspend walk over every member. Best-effort against
  threads churning mid-walk, and **counted**: N `Suspend` calls need N `Resume`
  calls.

A practical rule: `Resume` before starting new work into the group, and `Resume`
before a graceful `ShutdownAsync`. See [platform-support.md](platform-support.md) for
the caveats in full.

## Listing members

`Members()` returns a point-in-time snapshot of the live member pids as an
`IReadOnlyList<int>`, wrapped in a `Result`:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! _a = group.StartAsync(Command.create "worker-a")
        let! _b = group.StartAsync(Command.create "worker-b")

        match group.Members() with
        | Ok pids -> printfn $"{pids.Count} live members: {pids}"
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err.Message);
    return;
}

using var group = created.GetValueOrThrow();
await group.StartAsync(new Command("worker-a"));
await group.StartAsync(new Command("worker-b"));

Console.WriteLine((group.Members()) switch
{
    { IsOk: true, ResultValue: var pids }        => $"{pids.Count} live members: {string.Join(", ", pids)}",
    { IsOk: false, ErrorValue: var membersErr } => membersErr.Message,
});
```

What "members" means depends on the mechanism. On **Windows** (Job Object) and
the **Linux cgroup v2** backend, `Members()` lists the **whole tree** — every
descendant pid. On the **POSIX process-group** backend it lists the tracked group
*leaders* only (one pid per started child); their descendants are still contained
and killed with the group, just not enumerated. An exited child still counts until
it is reaped, and because the snapshot is point-in-time, a tree that is actively
forking races it.

To *wait* on members rather than list them, race the started handles with
`RunningProcess.WaitAny` — see [streaming.md](streaming.md).

## Resource limits

Caps are a property of the group, set once at creation through
`ProcessGroupOptions` and enforced by the same kernel object that contains the
tree. The builder is fluent and immutable:

**F#**

```fsharp
task {
    let options =
        ProcessGroupOptions()
            .WithMemoryMax(512L * 1024L * 1024L) // bytes, whole tree (512 MiB)
            .WithMaxProcesses(64)                 // fork-bomb ceiling
            .WithCpuQuota(0.5)                    // half of one core

    match ProcessGroup.Create options with
    | Ok group ->
        use group = group
        let! _sandboxed = group.StartAsync(Command.create "untrusted-tool")
        () // ... runs within the limited group ...
    | Error err -> eprintfn $"limits unavailable: {err.Message}" // ProcessError.ResourceLimit
}
```

**C#**

```csharp
var options = new ProcessGroupOptions()
    .WithMemoryMax(512L * 1024L * 1024L) // bytes, whole tree (512 MiB)
    .WithMaxProcesses(64)                 // fork-bomb ceiling
    .WithCpuQuota(0.5);                   // half of one core

var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine($"limits unavailable: {err.Message}"); // ProcessError.ResourceLimit
    return;
}

using var group = created.GetValueOrThrow();
await group.StartAsync(new Command("untrusted-tool")); // ... runs within the limited group ...
```

The three caps are:

- `WithMemoryMax(bytes)` — a whole-tree memory ceiling, in bytes (`int64`).
- `WithMaxProcesses(count)` — the maximum number of processes the tree may hold.
- `WithCpuQuota(cores)` — CPU as a fraction of a **single** core (`0.5` = half a
  core, `2.0` = two cores). On Windows this is converted against the host's CPU
  count and is approximate (a rate cap, not an exact share); on Linux cgroup v2 it
  maps to `cpu.max`.

The configured caps are also readable back: `group.Options.Limits` is a
`ResourceLimits` whose `MemoryMax` (`int64 option`), `MaxProcesses` (`int option`),
and `CpuQuota` (`float option`) are `Some` only for the limits you set
(`ResourceLimits.None` is the empty set). You can build a `ResourceLimits` value
directly with the same `WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota`
methods if you want to inspect or compose limits before applying them.

Limits need a **real container** — a Windows Job Object or a Linux cgroup v2.

| Capability | Windows Job Object | Linux cgroup v2 | POSIX process group / macOS / BSD |
|---|:---:|:---:|:---:|
| Memory cap | ✅ whole-tree | ✅ whole-tree (`memory.max`) | ❌ |
| Process-count cap | ✅ | ✅ (`pids.max`) | ❌ |
| CPU quota | 🟡 approximate | ✅ (`cpu.max`) | ❌ |

Where a requested cap can't be enforced, `Create` **fails fast** with
`ProcessError.ResourceLimit` rather than handing back a silently-unbounded group —
so a limit is a guarantee, not a hint. That covers macOS / BSD and the Linux
process-group fallback (no whole-tree primitive at all), and a Linux host where
cgroup v2 isn't mounted. On Linux, enforcing limits also requires the process to
run at the **real cgroup v2 root** (cgroup v2's "no internal processes" rule lets
the controllers be enabled only there) — so an ordinary container or a
systemd-managed process fails too. The prerequisites are spelled out in
[platform-support.md](platform-support.md).

**F#**

```fsharp
match ProcessGroup.Create options with
| Ok group ->
    use group = group
    () // ...
| Error(ProcessError.ResourceLimit message) -> eprintfn $"cannot enforce limits here: {message}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var err })
{
    Console.Error.WriteLine(err switch
    {
        ProcessError.ResourceLimit { Detail: var m } => $"cannot enforce limits here: {m}",
        _                                            => err.Message,
    });
    return;
}

using var group = created.GetValueOrThrow(); // ...
```

## Stats

`Stats()` returns a point-in-time `ProcessGroupStats` snapshot of the group's
resource usage, wrapped in a `Result`:

**F#**

```fsharp
match group.Stats() with
| Ok stats ->
    printfn $"procs={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
Console.WriteLine((group.Stats()) switch
{
    { IsOk: true, ResultValue: var stats } => $"procs={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}",
    { IsOk: false, ErrorValue: var err }  => err.Message,
});
```

`ProcessGroupStats` carries `ActiveProcessCount` (an `int`, always populated),
`TotalCpuTime` (`TimeSpan option`), and `PeakMemoryBytes` (`int64 option`). CPU
time and peak memory are available where the kernel accounts for the whole tree —
**Windows** (Job Object accounting) and the **Linux cgroup v2** backend; on the
**POSIX process-group** backend only the live count is reported and the two
`option` fields stay `None`.

`SampleStatsAsync(interval)` turns the snapshot into a periodic series as an
`IAsyncEnumerable<ProcessGroupStats>` — the first sample immediately, then one per
`interval`:

**F#**

```fsharp
task {
    let series = group.SampleStatsAsync(TimeSpan.FromSeconds 1.0)
    let e = series.GetAsyncEnumerator()

    try
        let mutable go = true

        while go do
            match! e.MoveNextAsync() with
            | true -> printfn $"rss now: {e.Current.PeakMemoryBytes}"
            | false -> go <- false
    finally
        e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
await foreach (var s in group.SampleStatsAsync(TimeSpan.FromSeconds(1)))
    Console.WriteLine($"rss now: {s.PeakMemoryBytes}");
```

From C# this is simply `await foreach (var s in group.SampleStatsAsync(interval))`. The
sampler is **pull-based**: it samples only as you pull the enumeration and runs no
background task, so it neither keeps the group alive nor leaks if you abandon it.
The series ends on the first snapshot the group can no longer report (notably once
the group has been torn down) or when the enumerator's token fires.

For a *single run's* end-to-end summary (exit code, duration, CPU, peak memory)
rather than a live group series, use `RunningProcess.Profile` — see
[streaming.md](streaming.md).

---

Next: [Streaming & interactive I/O](streaming.md)
