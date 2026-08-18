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
- [Disk I/O rate limits](#disk-io-rate-limits)
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

### Asking before you create one

`group.Mechanism` answers only once a group exists. To pick a portable policy
*before* the first spawn, `ProcessGroup.Capabilities()` (or
`Capabilities(options)`) returns a `ContainmentCapabilities` snapshot: the
mechanism `Create` would select for those options, plus, per axis — resource
limits, signals, adoption (from a `Process` and, separately, from a bare pid),
PTY and its resize, kill-on-parent-death, and the
platform helper binaries — either a real availability or a typed
`Capability.Unsupported` naming the precondition that is missing:

**F#**

```fsharp
let capabilities = ProcessGroup.Capabilities()

match capabilities.Adoption with
| Capability.Available -> printfn "Adopt() can pull an external process in here"
| Capability.Qualified qualification -> printfn $"Adopt() works, with a caveat: {qualification}"
| Capability.Unsupported requires -> printfn $"no Adopt() on this host; it needs {requires}"
```

**C#**

```csharp
var capabilities = ProcessGroup.Capabilities(options);

if (capabilities.ResourceLimits.MemoryMax is Capability.Unsupported noMemoryCap)
{
    Console.WriteLine($"this host cannot cap the tree's memory; it needs {noMemoryCap.Requires}");
}
```

It creates nothing — no process, no group, no container — and reads neither argv
nor environment. See
[Capability snapshot](platform-support.md#capability-snapshot) for what each axis
answers and what the snapshot does and does not promise.

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

### Adopting an already-running external process

Sometimes the process you need to contain was **not** started through ProcessKit —
another library launched it, you inherited it from a different layer, or you only
have its pid. `Adopt(process)` brings such a process into the group so it obeys the
same whole-tree rules as one started with `StartAsync`: kill-on-dispose, and
participation in `Signal` / `Suspend` / `Resume` / `Members` / `MembersInfo` /
`Stats` and any resource limits. It restores the "kill the whole tree" guarantee for
a wrapper whose child runs outside ProcessKit.

**F#**

```fsharp
// `external` is a live System.Diagnostics.Process started by someone else.
use group = (ProcessGroup.Create() |> function Ok g -> g | Error e -> failwith e.Message)

match group.Adopt external with
| Ok() -> ()                       // now a Job/cgroup member: disposing `group` kills it too
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
using var group = ProcessGroup.Create().GetValueOrThrow();

if (group.Adopt(external) is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

A few deliberate contract points:

- **The argument is a `System.Diagnostics.Process`, not a bare pid.** A raw pid can be
  recycled onto an unrelated process between when you read it and when the adopt lands;
  a live `Process` holds an open OS handle that on **Windows** pins the pid, so the adopt
  cannot race a recycle. On **Linux** there is no handle that pins a pid, so the residual
  (tiny) recycle window between the liveness check and the `cgroup.procs` write cannot be
  fully closed by number alone — an honest limitation, documented rather than hidden.
  Holding no `Process` at all — a pid from a pidfile, a registry, an FFI or IPC boundary —
  is what [`AdoptByPid`](#adopting-from-a-bare-pid) is for; where a live `Process` *is*
  available, this overload stays the stronger of the two.
- **The group keeps only containment; you keep the wait.** Adoption returns
  `Result<unit, _>` — not a `RunningProcess` — because the external process's stdio is not
  ours to stream. The adopted process is **not** ProcessKit's child, so ProcessKit never
  `waitpid`s it or signals its process group; it is contained and killed purely by the OS
  primitive (the Job's `KILL_ON_JOB_CLOSE` / a cgroup `cgroup.kill`), and its real parent
  (or `init`, once reparented) reaps it. **Observe its exit through your own `Process`**
  (`Process.WaitForExitAsync()` / `HasExited`).
- **Honest, typed failures — never a silent success.** Adopting a process that has already
  exited (or whose pid no longer exists — a lost race), one you lack the rights to, or one
  already assigned to an incompatible Windows Job returns `ProcessError.Adopt`. On a
  mechanism that fundamentally cannot adopt — the POSIX process group — you get
  `ProcessError.Unsupported` (see the platform note next).
- **Platform availability.** Windows (Job Object) adopts with or without limits; **Linux**
  can adopt only into a group created **with resource limits** (that is what selects the
  cgroup v2 mechanism). A plain, limit-free group on Linux, and every group on macOS/BSD,
  uses the POSIX process-group mechanism, which cannot relocate a foreign process and
  refuses with `ProcessError.Unsupported`. See
  [platform-support.md](platform-support.md#capability-matrices).

### Adopting from a bare pid

Often the number is all you have: a pid read from a pidfile, one handed over by an
outside supervisor, one that arrived over an IPC or FFI boundary. `AdoptByPid(pid)` is
the door for that case — the same containment as `Adopt`, taken from an `int`.

**F#**

```fsharp
// A pid from outside this process — a pidfile, a registry, an FFI caller.
let pid = 4321
use group = (ProcessGroup.Create() |> function Ok g -> g | Error e -> failwith e.Message)

match group.AdoptByPid pid with
| Ok() -> ()                       // contained from here on: disposing `group` kills it too
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
// A pid from outside this process — a pidfile, a registry, an FFI caller.
var pid = 4321;
using var group = ProcessGroup.Create().GetValueOrThrow();

if (group.AdoptByPid(pid) is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

**A pid is an address, not a handle.** Once a process is reaped the OS may give its
number to an unrelated one, so the number is used to *find* the process and the group is
then bound to an **identity anchor of its own** for whatever that number currently names:

| Mechanism | What the group holds afterwards |
|---|---|
| Windows Job Object | The process **object**. The number is used exactly once, by this call's `OpenProcess`; the assign puts that object in the Job and the kernel keeps membership per object. |
| Linux cgroup v2 | Kernel-maintained **cgroup membership**. A `/proc/<pid>/stat` start-time read on either side of the `cgroup.procs` write *detects* a number that changed hands across it — detection, not prevention (see the failure list below). |
| POSIX process group | The tracked pid **plus** the start-time token read here, **re-read before every probe, signal, suspend/resume and teardown kill**. |

So a process that recycles the number *after* the call is rejected rather than signalled.
What no library can close is the window *before* it — whether `pid` still named the
process you meant when you passed it. Look the number up as late as you can. The token
row carries one residual the other two do not: its resolution (a clock tick on Linux, a
microsecond on macOS) cannot separate two processes that held the number within one tick.

**What the group covers.** Processes the adopted one had *already* started keep their
original containment. What happens to the ones it starts *afterwards* follows the
mechanism: on the Job Object and cgroup v2 a later fork joins the container with its
parent, so the subtree grown from here is contained; on the **POSIX process group** the
process is tracked **individually** — signalled and killed with the group, but its future
forks are not, because no POSIX primitive moves a foreign, already-`exec`ed process into
another process group.

**Ownership is unchanged from `Adopt`:** the group contains, signals, lists and kills it;
it never `waitpid`s it, and no exit status for it is reported through this API. On the
POSIX mechanism, note that a process which exits and is *not* reaped by its own parent
becomes a zombie, and a zombie still answers the identity probe — a graceful
`ShutdownAsync` then waits out its whole grace on it, and no kill can clear it; only its
parent's `wait` can.

**Refusals, each typed and specific:**

- `pid <= 0` and this process's **own** pid are refused with `ProcessError.Adopt` before
  any mechanism is consulted. Neither is adoptable and both are dangerous as numbers: `0`
  means "the caller's own process group" to `kill`, a negative number addresses a process
  group, and adopting ourselves would enlist this process in its own group's teardown.
- A POSIX host with **no start-time identity reader** (the BSDs) returns
  `ProcessError.Unsupported`: with no anchor to capture, tracking the bare number would
  mean SIGKILLing whatever holds it at teardown. Never a silent downgrade.
- A pid that names nothing, an identity that cannot be read (a `hidepid` `/proc` mount,
  another user's process on macOS), a denied `OpenProcess` or `cgroup.procs` write, an
  assign Windows refuses, or a number that changed hands *while the call ran* all return
  `ProcessError.Adopt` with the cause. On cgroup v2 that last case has already written the
  stranger into this group's cgroup, so the call moves it back out to the parent cgroup and
  says so — and where even that is refused, says that the process stays a member of this
  group and will be killed by its teardown.

**Platform availability.** Windows and Linux cgroup v2 as for `Adopt`. The POSIX
process-group mechanism — which cannot `Adopt` at all — *can* adopt by pid wherever this
host has an identity reader (Linux, macOS), so the two axes deliberately differ; ask
`ProcessGroup.Capabilities().AdoptionByPid` rather than assuming they match.

## Tearing down: dispose, terminate, shutdown

There are three ways out, from blunt to graceful:

| Verb | What happens | When to use it |
|---|---|---|
| dispose (`use` / `Dispose()` / `DisposeAsync()`) | Immediate **hard kill** of the whole tree, then releases the container | The safety net — always on, even on an exception or early return |
| `group.KillAll()` | The same hard kill, but the group **stays usable** for further spawns; idempotent | Explicit teardown mid-flight when you want to keep the group |
| `group.ShutdownAsync()` / `group.ShutdownAsync(grace)` | **Graceful**: on Unix the configured `Options.StopSignal` → wait the grace window → `SIGKILL` survivors; on Windows the default uses best-effort `WM_CLOSE` → wait → atomic Job kill. Releases the group | A clean service stop |

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
`WithShutdownTimeout`). Select the soft signal with `WithStopSignal` (default `Signal.Term`). A child that handles it and exits ends the grace
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

For a single live handle, `RunningProcess.Signal(signal)` uses the same backend-safe delivery but
targets only that run's own process group/Job child. It is non-consuming, so the caller can signal and
then continue streaming or await the outcome; lifecycle gates prevent post-teardown PID reuse.

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

### Enriched members

`MembersInfo()` returns the **same membership** as `Members()`, but each pid comes
as a `MemberInfo` carrying its parent pid, executable image name, and OS-reported
start time — the "who is in this tree, who is whose parent, what image, since when"
snapshot, without hand-rolling `System.Diagnostics.Process` / `/proc` reads and
racing process exit yourself:

**F#**

```fsharp
match group.MembersInfo() with
| Ok members ->
    for m in members do
        printfn $"pid={m.Pid} ppid={m.Ppid} exe={m.ExeName} started={m.StartTime}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
if (group.MembersInfo() is { IsOk: true, ResultValue: var members })
    foreach (var m in members)
        Console.WriteLine($"pid={m.Pid} ppid={m.Ppid} exe={m.ExeName} started={m.StartTime}");
```

`Pid` is always present; `Ppid`, `ExeName`, and `StartTime` are each an `option`
and are `None` wherever the platform cannot honestly report them — never a
fabricated value. A member that exits between the enumeration and its metadata read
is **omitted** rather than filled with invented fields, so under an actively
forking-and-exiting tree the result is a subset of `Members()`. The member's
**command line and environment are never included on any platform** — argv
routinely carries secrets and redaction is your policy, the same exclusion the
logging / tracing / metrics paths make. Which enriching fields each platform can
supply is in [platform-support.md](platform-support.md).

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

The six caps are:

- `WithMemoryMax(bytes)` — a whole-tree memory ceiling, in bytes (`int64`).
- `WithMaxProcesses(count)` — the maximum number of processes the tree may hold.
- `WithCpuQuota(cores)` — CPU as a fraction of a **single** core (`0.5` = half a
  core, `2.0` = two cores). On Windows this is converted against the host's CPU
  count and is approximate (a rate cap, not an exact share); on Linux cgroup v2 it
  maps to `cpu.max`.
- `WithCpuAffinity(cores)` — the CPU cores (zero-based logical processor indices)
  the tree may be scheduled on: `[0; 1]` pins it to the first two. The complement
  of the quota — where `WithCpuQuota` bounds *how much* CPU the tree gets,
  this bounds *which* cores it gets it from, so a noisy child can be kept off the
  ones a latency-critical workload runs on. Windows writes a Job Object affinity
  mask (`JOB_OBJECT_LIMIT_AFFINITY`); Linux cgroup v2 writes `cpuset.cpus`. See
  [CPU affinity](#cpu-affinity) for the two platform ceilings.
- `WithCpuTimeMax(duration)` — CPU time, not wall time. Windows applies the Job's
  `PerJobUserTimeLimit`; POSIX installs `RLIMIT_CPU` before each child `exec` (soft limit rounded up
  to seconds, hard limit one second later so `SIGXCPU` can be observed).
- `WithIoMax(target, readBytesPerSecond, writeBytesPerSecond, readOperationsPerSecond, writeOperationsPerSecond)` —
  directional disk bandwidth and IOPS ceilings for one explicit device or volume. The overload using
  `int64` treats zero as unbounded; the option overload uses `None`. At least one direction must be
  bounded, and every supplied rate must be positive.

Linux cgroup v2 also offers the `WithOomGroupKill()` policy. It writes
`memory.oom.group=1`, so an OOM event kills every process in the contained tree as one unit instead
of leaving survivors after the kernel selects a single victim. This semantic has no Job Object or
POSIX process-group equivalent: `ProcessGroup.Create` returns `ProcessError.Unsupported` outside
Linux cgroup v2. It composes naturally with `WithMemoryMax`, but can also protect against an OOM
triggered by an ancestor cgroup.

The configured caps are also readable back: `group.Options.Limits` is a
`ResourceLimits` whose `MemoryMax` (`int64 option`), `MaxProcesses` (`int option`),
`CpuQuota` (`float option`), `CpuTimeMax` (`TimeSpan option`), `OomGroupKill` (`bool`), and `CpuAffinity` (`IReadOnlyList<int> option`, in
ascending order), plus `IoMax` (`IoMax option`), are `Some` only for the limits you set
(`ResourceLimits.None` is the empty set). `IoMax` reads back the target and all four
directional rates accepted by the backend. You can build a `ResourceLimits` value directly
with the same `WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota` / `WithCpuAffinity` /
`WithIoMax` methods if you want to inspect or compose limits before applying them.

Limits need a **real container** — a Windows Job Object or a Linux cgroup v2.

| Capability | Windows Job Object | Linux cgroup v2 | POSIX process group / macOS / BSD |
|---|:---:|:---:|:---:|
| Memory cap | ✅ whole-tree | ✅ whole-tree (`memory.max`) | ❌ |
| Atomic whole-tree OOM kill | ❌ `Unsupported` | ✅ (`memory.oom.group`) | ❌ `Unsupported` |
| Process-count cap | ✅ | ✅ (`pids.max`) | ❌ |
| CPU quota | 🟡 approximate | ✅ (`cpu.max`) | ❌ |
| CPU-time maximum | ✅ whole Job | ✅ per spawned process (`RLIMIT_CPU`) | ✅ per spawned process (`RLIMIT_CPU`) |
| [CPU affinity](#cpu-affinity) | ✅ mask, cores 0–63 | ✅ (`cpuset.cpus`) | ❌ |
| [Disk I/O rate](#disk-io-rate-limits) | ✅ per-volume aggregate | ✅ per-device (`io.max`) | ❌ `Unsupported` |
| [UI restrictions](#windows-ui-restrictions) | ✅ clipboard/desktop/exit-Windows | ❌ `Unsupported` | ❌ `Unsupported` |

Where a requested cap can't be enforced, `Create` **fails fast** with
`ProcessError.ResourceLimit` rather than handing back a silently-unbounded group —
so a limit is a guarantee, not a hint. That covers macOS / BSD and the Linux
process-group fallback (no whole-tree primitive at all), and a Linux host where
cgroup v2 isn't mounted. On Linux, enforcing limits also requires the process to
run at the **real cgroup v2 root** (cgroup v2's "no internal processes" rule lets
the controllers be enabled only there) — so an ordinary container or a
systemd-managed process fails too. The prerequisites are spelled out in
[platform-support.md](platform-support.md).

### Disk I/O rate limits

`WithIoMax` applies one directional I/O policy to one explicit target and is exposed
through both `ResourceLimits.WithIoMax` and `ProcessGroupOptions.WithIoMax`. The target
and rates are preserved in `group.Options.Limits.IoMax` after a successful create or
live update, so callers can read back the accepted policy without reconstructing it
from the builder.

The target has platform-specific meaning:

- **Linux cgroup v2** treats `target` as a `major:minor` block-device key in
  `io.max`. Read bandwidth (`rbps`), write bandwidth (`wbps`), read IOPS (`riops`),
  and write IOPS (`wiops`) are independent; an unbounded direction is rendered as
  `max`. The `io` controller must be delegated. Replacing one target with another
  is two separate `io.max` writes — first clearing the old device key, then writing
  the new key — because each nested device key is updated independently. If a later
  write fails, the backend rolls back the already-applied writes in reverse order,
  including the old target.
- **Windows Job Objects** treats `target` as an NT volume device name. The Job
  Object I/O rate controller provides one aggregate bandwidth ceiling and one
  aggregate IOPS ceiling per volume, so read/write byte rates must match and
  read/write operation rates must match. An unavailable Job I/O API is reported as
  `ProcessError.Unsupported`; an invalid volume or incompatible rates remain a
  typed `ProcessError.ResourceLimit`.

`UpdateLimits` is a full replacement on both limit-capable mechanisms. Passing
`ResourceLimits.None` removes the I/O policy; changing only the rates updates the
same target, while changing the target performs the separate disable/write sequence
described above. A failed live update restores the previous native policy before
returning its typed error, and `group.Options.Limits` changes only after the complete
replacement succeeds.

macOS, BSD, and the Linux POSIX process-group fallback have no whole-tree I/O
controller. `Create` and `UpdateLimits` therefore return
`ProcessError.Unsupported` for `WithIoMax` instead of running the tree without the
requested cap. A Linux cgroup v2 hierarchy without the delegated `io` controller
also returns `Unsupported` before attempting controller writes.

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

### CPU affinity

`WithCpuAffinity(cores)` pins the whole tree to a set of CPU cores — zero-based
logical processor indices, treated as a set rather than an ordered sequence, so
`[3; 0; 2]` and `[0; 2; 3]` are the same pin and both read back ascending. It
composes with the quota: cap how much CPU the tree may burn *and* which cores it
may burn it on.

**F#**

```fsharp
let pinned =
    ProcessGroupOptions()
        .WithCpuQuota(2.0)              // at most two cores' worth of CPU ...
        .WithCpuAffinity([ 2; 3 ])      // ... and only ever on cores 2 and 3
```

**C#**

```csharp
var pinned = new ProcessGroupOptions()
    .WithCpuQuota(2.0)                  // at most two cores' worth of CPU ...
    .WithCpuAffinity(new[] { 2, 3 });   // ... and only ever on cores 2 and 3
```

Invalid sets are rejected at the builder, not deep inside a native call: `null`
throws `ArgumentNullException`, an empty set or a repeated index throws
`ArgumentException` (a set with no core could never run anything, and a repeat is a
typo rather than an intent), and a negative index throws
`ArgumentOutOfRangeException`.

Two further constraints are the platform's, not the builder's, so they are reported
as a typed `ProcessError.ResourceLimit` when the group is created or updated — never
as a pin quietly dropped, and never as an index wrapped onto some other core:

- **Windows** holds the pin in a single pointer-sized affinity mask covering one
  [processor group](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups),
  so only cores `0`–`63` can be named on x64 (`0`–`31` on x86). A machine with more
  logical processors than that splits them across groups the mask cannot reach.
- **Every requested core must exist on the host and be available to this process.**
  A Job's affinity mask has to be a subset of the creating process's own, so pinning
  to core 12 on an 8-core host — or to a core the process has itself been excluded
  from — fails rather than silently landing somewhere else. Linux cgroup v2 applies
  the same rule against the parent cgroup's effective cores.

On Linux, `cpuset` is a **separate cgroup v2 controller**, so the pin needs it
enabled in the parent's `cgroup.subtree_control` — which ProcessKit does for you, on
the same terms as `memory`/`pids`/`cpu` (see [Resource limits](#resource-limits)
for the real-cgroup-root prerequisite). A hierarchy whose `cgroup.controllers` does
not carry `cpuset` at all cannot host a pin, and says so with
`ProcessError.ResourceLimit`. macOS, BSD, and the Linux process-group fallback have
no whole-tree affinity primitive at all, so a pin fails fast there for the same
reason every other cap does.

The pin is live-updatable through [`UpdateLimits`](#updating-limits-on-a-live-group)
with the same replace semantics as every other dimension: passing a limit set
*without* a pin lifts it (the tree may use every core again) rather than leaving the
previous mask in force, and `group.Options.Limits.CpuAffinity` follows only what
actually got applied.

### Windows UI restrictions

A Job Object can also restrict what its tree may do to the **interactive desktop
session** it shares with you — a different axis from the resource caps above, and
one with no POSIX counterpart. `WithUiRestrictions(...)` takes a `[<Flags>]`
`WindowsUiRestrictions` set (combine with `|||` in F#, `|` in C#, or take the lot
with `All`):

| Flag | Denies the tree |
|---|---|
| `Handles` | using USER handles owned by processes outside the job |
| `ReadClipboard` / `WriteClipboard` | reading / writing the clipboard |
| `SystemParameters` | `SystemParametersInfo` (system-wide settings) |
| `DisplaySettings` | `ChangeDisplaySettings` |
| `GlobalAtoms` | the session's global atom table (the job gets its own) |
| `Desktop` | creating or switching desktops |
| `ExitWindows` | logging off, shutting down, or restarting the machine |

**F#**

```fsharp
task {
    // A tool that has no business touching the desktop session it runs in.
    let options =
        ProcessGroupOptions()
            .WithMaxProcesses(32)
            .WithUiRestrictions(
                WindowsUiRestrictions.ReadClipboard
                ||| WindowsUiRestrictions.WriteClipboard
                ||| WindowsUiRestrictions.ExitWindows
            )

    match ProcessGroup.Create options with
    | Ok group ->
        use group = group
        let! _restricted = group.StartAsync(Command.create "untrusted-tool")
        ()
    | Error err -> eprintfn $"{err.Message}" // ProcessError.Unsupported off Windows
}
```

**C#**

```csharp
var uiOptions = new ProcessGroupOptions()
    .WithMaxProcesses(32)
    .WithUiRestrictions(
        WindowsUiRestrictions.ReadClipboard
        | WindowsUiRestrictions.WriteClipboard
        | WindowsUiRestrictions.ExitWindows);

var uiCreated = ProcessGroup.Create(uiOptions);
if (uiCreated is { IsOk: false, ErrorValue: var uiErr })
{
    Console.Error.WriteLine(uiErr.Message); // ProcessError.Unsupported off Windows
    return;
}

using var uiGroup = uiCreated.GetValueOrThrow();
await uiGroup.StartAsync(new Command("untrusted-tool"));
```

The rules match the resource caps, with one deliberate difference in the error:

- **Windows-only.** Off Windows `ProcessGroup.Create` (and `UpdateLimits`) fails with
  `ProcessError.Unsupported`, not `ProcessError.ResourceLimit`. A memory cap is a
  concept every platform has and only some can enforce; a clipboard or desktop
  restriction has no POSIX analogue at all, so it is reported as an unsupported
  operation rather than an unenforceable limit. Either way it is never dropped
  silently.
- **Replace semantics**, like every other dimension:
  `WithUiRestrictions(WindowsUiRestrictions.None)` lifts the restrictions on the next
  apply rather than leaving the previous set in force, and `group.Options.Limits.UiRestrictions`
  reads back what is actually applied.
- A set carrying **undefined bits** (an out-of-range cast) is rejected at the builder
  boundary with `ArgumentOutOfRangeException` rather than written to the Job as an
  unknown restriction class.
- These restrictions bound what the tree may do to the *desktop session*. They are
  **not** a filesystem, network, or registry sandbox — pair them with the resource
  caps and with `Command.WindowsRestrictedToken` / `WindowsIntegrityLevel` (see
  [Running commands](commands.md)) for a real perimeter.

### Updating limits on a live group

Limits are not frozen at creation. `UpdateLimits(ResourceLimits)` re-applies a new
cap set to a **live** group — no recreation, no restart of the children — for
adaptive resource control: tighten memory on a batch that started sagging, widen a
long-lived worker pool's CPU quota under load, or drop a cap you no longer need.
The `ResourceLimits` you pass is a **full replacement** of the caps in force: a
dimension left `None` is reset to *unbounded*, not left at its previous value.

**F#**

```fsharp
// Halve the memory ceiling and drop the CPU cap on an already-running group.
match group.UpdateLimits(ResourceLimits.None.WithMemoryMax(256L * 1024L * 1024L)) with
| Ok() -> () // Options.Limits now reads back the new set
| Error(ProcessError.ResourceLimit message) -> eprintfn $"cannot update limits here: {message}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var updated = group.UpdateLimits(ResourceLimits.None.WithMemoryMax(256L * 1024L * 1024L));
if (updated is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

Behaviour follows the mechanism, honestly and without a silent downgrade:

| Mechanism | `UpdateLimits` |
|---|---|
| Windows Job Object | ✅ re-applies via `SetInformationJobObject` on the live job (caps, affinity mask, **and** UI restrictions) |
| Linux cgroup v2 | ✅ rewrites `memory.max` / `memory.oom.group` / `pids.max` / `cpu.max` / `cpuset.cpus` in place (UI restrictions → `ProcessError.Unsupported`) |
| POSIX process group / macOS / BSD | ❌ `ProcessError.ResourceLimit` (no whole-tree limit primitive to update) |

`CpuTimeMax` is spawn-time on POSIX, including the cgroup backend. A live update that changes it is
rejected before any controller file is written, so `UpdateLimits` never leaves a partially changed
limit set. Windows can replace the Job time limit live with the other Job limits.

On the POSIX process-group mechanism there is no container to re-tune, so
`UpdateLimits` returns `ProcessError.ResourceLimit` — the same typed refusal
`Create` gives for a limited group there — rather than pretending the caps were
applied. It is an **optional runtime operation**: a group that never needs to
change its caps simply never calls it. After a successful update,
`group.Options.Limits` reflects the new set; the call also passes through the
group's lifecycle gate, so invoking it after the group has been disposed/torn down
returns a typed error rather than touching the released container.

## Stats

`Stats()` returns a point-in-time `ProcessGroupStats` snapshot of the group's
resource usage, wrapped in a `Result`:

**F#**

```fsharp
match group.Stats() with
| Ok stats ->
    printfn $"procs={stats.ActiveProcessCount} peakProcs={stats.PeakProcessCount} cpu={stats.TotalCpuTime} read={stats.IoReadBytes} write={stats.IoWriteBytes}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
Console.WriteLine((group.Stats()) switch
{
    { IsOk: true, ResultValue: var stats } => $"procs={stats.ActiveProcessCount} peakProcs={stats.PeakProcessCount} cpu={stats.TotalCpuTime} read={stats.IoReadBytes} write={stats.IoWriteBytes}",
    { IsOk: false, ErrorValue: var err }  => err.Message,
});
```

`ProcessGroupStats` carries `ActiveProcessCount` (an `int`, always populated),
`PeakProcessCount` (`int64 option`), `TotalCpuTime` (`TimeSpan option`),
`PeakMemoryBytes` (`int64 option`), and four `int64 option` I/O counters:
`IoReadBytes`, `IoWriteBytes`, `IoReadOperations`, and `IoWriteOperations`. Linux
cgroup v2 supplies `PeakProcessCount` from the kernel's lifetime `pids.peak` counter
only when `MaxProcesses` is configured and the kernel is version 6.6 or later. This
is the peak number of kernel tasks (processes and their threads), so it is not
directly comparable with `ActiveProcessCount`, which counts process leaders. The
peak is `None` on Windows and the POSIX fallback, and is never estimated from
caller-driven `Stats()` samples. Windows Job Object accounting and Linux cgroup v2
provide the other tree aggregates (cgroup bytes/operations come from block-device
`io.stat` when the I/O controller is delegated to that hierarchy). On the POSIX
process-group backend only the live count is reported and all optional metrics stay
`None`.

`MemberStats()` provides the per-member view when a tree aggregate is not enough:

**F#**

```fsharp
match group.MemberStats() with
| Ok members ->
    for memberStats in members do
        printfn $"pid={memberStats.Pid} cpu={memberStats.CpuTime} rss={memberStats.ResidentMemoryBytes} read={memberStats.IoReadBytes}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
if (group.MemberStats() is { IsOk: true, ResultValue: var members })
    foreach (var member in members)
        Console.WriteLine($"pid={member.Pid} cpu={member.CpuTime} rss={member.ResidentMemoryBytes}");
```

The returned list is a best-effort subset of the membership snapshot: a process that
exits between enumeration and its metric reads is omitted. Windows binds each Job
PID snapshot to a pre-sampling process identity before opening its verified
query-only handle. If query access is denied, a fresh Job membership and process
identity check must still confirm the same generation before the PID is retained
with `None` metrics; same-Job PID reuse is omitted. Linux cgroup reads `/proc/<pid>`
for every whole-tree member: tracked and adopted leaders use pinned identities,
while descendants use a snapshot identity checked again after the read. The POSIX
process-group fallback samples its tracked leaders, and any process adopted by bare
pid against the anchor captured for it. Metrics unavailable on a
platform or for a confirmed inaccessible member are `None`, never fabricated zeroes.
`MemberStats()`
holds the same lifecycle gate as `Members()` and `Stats()`, and returns the typed
released-group error after teardown. It never reads command lines or environments.

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

For a *single run's* end-to-end summary (exit code, duration, CPU, peak memory, and
private-tree I/O where available) rather than a live group series, use
`RunningProcess.ProfileAsync` — see
[streaming.md](streaming.md).

---

Next: [Streaming & interactive I/O](streaming.md)
