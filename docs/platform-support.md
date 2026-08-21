# Platform support

[Previous: Overview](./)

ProcessKit treats platform behaviour as first-class. Every child you start lives inside the
operating system's own containment primitive, so the kill-on-dispose tree guarantee holds on
Windows, Linux, FreeBSD, and macOS/the other BSDs alike. Where a mechanism is genuinely weaker than another, the
difference is reported honestly — the active `Mechanism` is queryable and unsupported operations
return a typed `ProcessError`, never a silent downgrade. This page collects every per-OS
mechanism, capability matrix, and caveat in one place.

- [Containment mechanisms](#containment-mechanisms)
- [Capability snapshot](#capability-snapshot)
- [Target frameworks](#target-frameworks)
- [Trimming and NativeAOT](#trimming-and-nativeaot)
- [Capability matrices](#capability-matrices)
- [Caveats](#caveats)

## Containment mechanisms

A `ProcessGroup` wraps one of four OS primitives. Whichever it gets, disposing the group (or the
live `RunningProcess` from a one-shot verb) reaps the whole tree — children, grandchildren, and
anything they spawned — as a single kernel operation.

| `Mechanism` | Platform | How containment works |
|---|---|---|
| `Mechanism.JobObject` | Windows | A Job Object created with kill-on-close. Children are spawned suspended, assigned to the job, then resumed, so even a grandchild forked in the first instant is already contained. Teardown closes the job handle (`KILL_ON_JOB_CLOSE`) or terminates the job. |
| `Mechanism.CgroupV2` | Linux (when resource limits are requested and a usable cgroup v2 root exists) | A private cgroup under the unified hierarchy. Each child is launched through a small `/bin/sh` helper that joins the cgroup (writes its own pid to `cgroup.procs`) before `exec`ing the target in place, so the target is contained on its first instruction and a child it forks immediately inherits the limits; teardown is `cgroup.kill` followed by removing the cgroup directory. |
| `Mechanism.ProcessReaper` | FreeBSD | The kernel **process reaper** (`procctl(2)`'s `PROC_REAP_ACQUIRE`), layered over the POSIX process group. This process becomes the reaper of its whole descendant tree, so every descendant stays inside it — a `setsid` escapee included. Membership is `PROC_REAP_GETPIDS`; teardown and every whole-tree signal are `PROC_REAP_KILL` per subtree. |
| `Mechanism.ProcessGroup` | macOS and the other BSDs, and the Linux default when no limits are requested | POSIX process groups. Each spawned child forms its own process-group id (pgid); teardown sends `SIGKILL` to the tracked pgids (`killpg`). |

### When each mechanism is chosen

The selection at `ProcessGroup.Create` is deterministic per platform:

- **Windows** always uses a **Job Object** (`Mechanism.JobObject`), with or without limits. When
  limits are requested they are applied to the job; if they cannot be applied, creation fails with
  `ProcessError.ResourceLimit`.
- **Linux** uses a **cgroup v2** (`Mechanism.CgroupV2`) *only when whole-tree resource limits are requested
  and cgroup v2 is mounted and usable at the real cgroup-v2 root*. Without limits, Linux uses the
  **POSIX process group** (`Mechanism.ProcessGroup`) — so an ordinary, limit-free group on Linux
  reports `ProcessGroup`, not `CgroupV2`. A CPU-time-only group also uses `ProcessGroup` and applies
  `RLIMIT_CPU` per spawned child. If whole-tree limits are requested but no usable cgroup exists,
  creation fails with `ProcessError.ResourceLimit` rather than running unbounded.
- **FreeBSD** uses the **process reaper** (`Mechanism.ProcessReaper`) for a limit-free group: it is the
  one unix outside Linux with a real whole-tree containment primitive, so it is preferred over the plain
  process group rather than folded into it. Reaper status is acquired once per process, permanently, at the
  first `ProcessGroup.Create`; if that acquisition is refused the group falls back to the POSIX process
  group and reports `Mechanism.ProcessGroup` — the created group's own `Mechanism` is always the final
  word, never an assumption. The reaper is a containment *relationship*, not a container: it accounts for
  nothing, so a whole-tree limit is refused here exactly as on the other BSDs (`ProcessError.ResourceLimit`,
  never a per-process `RLIMIT_*` surrogate presented as a whole-tree cap), while a CPU-time-only limit
  remains available per child.
- **macOS and the other BSDs** always use a **POSIX process group** (`Mechanism.ProcessGroup`). They have
  no whole-tree limit primitive, so requesting one fails fast with `ProcessError.ResourceLimit`; a
  CPU-time-only limit remains available per child.

### Reading the active mechanism

`ProcessGroup.Mechanism` reports which primitive you actually got, so code that depends on a
guarantee can check rather than assume:

**F#**

```fsharp
match ProcessGroup.Create() with
| Ok group ->
    use group = group

    match group.Mechanism with
    | Mechanism.JobObject -> printfn "Windows Job Object — whole-tree kill, members, stats"
    | Mechanism.CgroupV2 -> printfn "Linux cgroup v2 — whole-tree kill, signals, limits, stats"
    | Mechanism.ProcessReaper -> printfn "FreeBSD process reaper — whole-tree kill/signal/members, setsid escapees included"
    | Mechanism.ProcessGroup -> printfn "POSIX process group — kill-on-dispose, leaders-only members"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
using var group = ProcessGroup.Create().GetValueOrThrow();

Console.WriteLine(group.Mechanism switch
{
    { IsJobObject: true }    => "Windows Job Object — whole-tree kill, members, stats",
    { IsCgroupV2: true }     => "Linux cgroup v2 — whole-tree kill, signals, limits, stats",
    { IsProcessReaper: true } => "FreeBSD process reaper — whole-tree kill/signal/members",
    { IsProcessGroup: true } => "POSIX process group — kill-on-dispose, leaders-only members",
    _                        => "unknown mechanism",
});
```

The `Mechanism.IsJobObject` / `IsCgroupV2` / `IsProcessReaper` / `IsProcessGroup` properties are the same
check in boolean form, convenient from C#.

## Capability snapshot

`ProcessGroup.Mechanism` answers only once a group exists. A long-lived orchestrator that has to
pick a portable policy *before* the first spawn would otherwise have to create a group and try each
operation to find out what it gets. `ProcessGroup.Capabilities()` — or `Capabilities(options)` for a
specific `ProcessGroupOptions` — answers the same questions up front, as an immutable
`ContainmentCapabilities` snapshot:

**F#**

```fsharp
let capabilities = ProcessGroup.Capabilities(ProcessGroupOptions().WithMemoryMax(512L * 1024L * 1024L))

match capabilities.Mechanism, capabilities.Creation with
| Some mechanism, _ -> printfn $"a limited group here is contained by {mechanism}"
| None, Capability.Unsupported requires -> printfn $"these options cannot be honoured here; they need {requires}"
| None, _ -> ()

match capabilities.ResourceLimits.CpuAffinity with
| Capability.Available -> printfn "the tree can be pinned to cores"
| Capability.Qualified qualification -> printfn $"pinning works, but: {qualification}"
| Capability.Unsupported requires -> printfn $"no pinning here; it needs {requires}"
```

**C#**

```csharp
var capabilities = ProcessGroup.Capabilities();

if (capabilities.Adoption is Capability.Unsupported noAdopt)
{
    Console.WriteLine($"this host cannot adopt an external process: it needs {noAdopt.Requires}");
}

foreach (var helper in capabilities.Helpers)
{
    Console.WriteLine($"{helper.Name} ({helper.Purpose}): {helper.Availability}");
}
```

**Nothing is created.** Taking a snapshot starts no process, creates no group, and touches no
container; it reads no argv and no environment value, and reports none.

### What each axis answers

Every axis is a `Capability`, which is deliberately three-valued rather than a `bool`: `Available`,
`Qualified` (available under a stated qualification), or `Unsupported` (with the precondition that is
missing). `Capability.Detail` reads the qualification or the precondition without matching on the
case. There is no bare "no" anywhere in the snapshot — an axis that is not plainly available always
says why, and the matching verb still refuses with its own typed `ProcessError`.

| Member | Answers | Matrix |
|---|---|---|
| `Mechanism` | the primitive `Create(options)` would select, or `None` when these options cannot be honoured here | [Containment mechanisms](#containment-mechanisms) |
| `Creation` | whether `Create(options)` can succeed, and under what qualification | [When each mechanism is chosen](#when-each-mechanism-is-chosen) |
| `ResourceLimits` | one `Capability` per `ResourceLimits` dimension (`MemoryMax`, `OomGroupKill`, `MaxProcesses`, `CpuQuota`, `CpuTimeMax`, `CpuAffinity`, `IoMax`, `UiRestrictions`, and `LiveUpdate` for `UpdateLimits`) | [Resource limits](#capability-matrices) |
| `Signals` | `Kill`, `SoftStop` (`Signal.Int`/`Signal.Term`), and `Arbitrary` (every other signal) | [Signals](#capability-matrices) |
| `Adoption` | `ProcessGroup.Adopt` | [Adopting an external process](#capability-matrices) |
| `AdoptionByPid` | `ProcessGroup.AdoptByPid` — a separate axis, because a bare pid needs an identity *anchor* rather than a relocation primitive, and the two answers differ on the POSIX process group | [Adopting an external process](#capability-matrices) |
| `Pty` / `PtyResize` | `Command.Pty`, and `RunningProcess.ResizeAsync` on such a run | [PTY capabilities](#pseudo-terminal-pty-capabilities) |
| `KillOnParentDeath` / `KillOnParentDeathScope` | `Command.KillOnParentDeath`, and how far its cleanup reaches | [Reaping on sudden parent death](#capability-matrices) |
| `Helpers` | the external binaries this platform's spawn paths load (`setpriv`, `setsid`, `prlimit`, `/bin/sh`; `cmd.exe` on Windows), what each is for, and whether this host holds it | [Caveats](#caveats) |

Two reading rules keep the answers honest, and are worth knowing before you branch on them:

- **The limit dimensions answer for the host, not for the mechanism these options select.** On Linux,
  *asking* for a whole-tree cap is itself what selects the cgroup v2 mechanism, so reporting
  `MemoryMax` as unsupported for a limit-free options set would understate a host that can enforce it
  the moment it is requested. `Mechanism` and `Creation` are the members that answer for the options
  as they stand; `Adoption`, `AdoptionByPid` and `Signals` follow the mechanism those options select.
- **`Adoption` and `AdoptionByPid` can disagree on the same host, deliberately.** They ask different
  questions: whether the mechanism can *move* a foreign process into the container, and whether it can
  *anchor* a bare number safely. A limit-free Linux or a macOS group answers no to the first and yes to
  the second.
- **A mounted cgroup v2 hierarchy is reported as `Qualified`, not `Available`.** Enabling the
  controllers a cap needs is permitted only at the *real* hierarchy root, and a cgroup namespace root
  (an ordinary container, a systemd scope) is indistinguishable from it without attempting the write —
  which a snapshot must not do. "The hierarchy exists" and "the cap can be enforced" are neighbouring
  facts, and the snapshot reports them as such rather than merging them into one claim. This is not a
  disagreement with the ✅ the [matrices below](#capability-matrices) give cgroup v2 for those caps:
  a matrix row answers "does this *mechanism* support the cap" (it does, fully), while the snapshot
  answers "can this *host* give me that mechanism with the cap enforced" — which is only settled when
  `ProcessGroup.Create` attempts the delegation.

### What it does not promise

It is a snapshot, not a guarantee. Each value is read from the platform facts in force at the moment
of the call — a mounted cgroup v2 hierarchy, a helper present in a trusted directory, the ConPTY
entry point exported by this Windows build — and a host can gain or lose any of them afterwards. So
the answer is the answer for *now*: the verb itself stays the authority at the moment it runs, and
still returns its own typed `ProcessError` if the ground moved. Nothing is cached for exactly that
reason, and the spawn and creation paths keep resolving every fact themselves rather than trusting a
snapshot. The one thing the snapshot is guaranteed to agree with is the *decision*: the mechanism it
reports comes from the very selection `ProcessGroup.Create` dispatches on, and each capability from
the very probe the corresponding spawn path consults.

## Target frameworks

ProcessKit targets **.NET 8.0** and **.NET 10.0**, and is usable from F# and C# alike. The
containment work is done through platform P/Invoke (Win32 for the Job Object, the cgroup
filesystem and `libc` on Unix), so the supported runtime set is Windows, Linux, and macOS/BSD —
the desktop and server platforms these target frameworks run on.

The full test suite (minus the `Stress` category) runs in CI's `test` job matrix on
`ubuntu-latest`, `ubuntu-24.04-arm`, `windows-11-arm`, `windows-latest`, and `macos-latest` — so the native syscall
layer (direct `syscall(2)` invocations, `siginfo` struct layout, signal/epoll handling in
`Native.Posix.fs`) is verified on Linux ARM64 as well as x64, not merely asserted correct by
argument-passing convention. macOS's GitHub-hosted runner is Apple Silicon (arm64) already; Windows
CI now covers both x64 (windows-latest) and ARM64 (windows-11-arm). On ARM64, `actions/setup-dotnet`
auto-resolves the .NET SDK; no x64-specific test fences were required (native P/Invoke code for Job Objects,
overlapped named-pipe I/O, and struct marshalling is pointer-width-safe). This ARM64 coverage is documented
reasoning pending the first real post-merge CI run on the windows-11-arm leg.

## Trimming and NativeAOT

CLI tools — a common consumer of a process library — increasingly ship as `PublishTrimmed` or
NativeAOT images, so ProcessKit's runtime packages declare their compatibility explicitly and back the
claim with a CI smoke that actually publishes and runs a NativeAOT consumer.

| Package | `IsTrimmable` | `IsAotCompatible` | Notes |
|---|:---:|:---:|---|
| `ProcessKit` | ✅ | ✅ | Containment is platform P/Invoke with no reflection, dynamic codegen, or reflection-backed `printf`/`%A`; the reflection-based JSON overload is annotated, while the `JsonTypeInfo` overload is AOT-safe (see below). |
| `ProcessKit.Extensions.DependencyInjection` | ✅ | ✅ | Factory-based registration; the `AddProcessKit`/`AddProcessKitGroup` **`IConfiguration`** overloads are the one exception (see below). |
| `ProcessKit.Extensions.Hosting` | ✅ | ✅ | Factory-based DI plus an `IHostedService` wrapper; options come from the AOT-safe `Activator.CreateInstance<T>()` path. |
| `ProcessKit.Testing` | ❌ | ❌ | Not trim/AOT-safe by design — see the boundary below. This is a **test-only** package, referenced from test projects that are not themselves trimmed/AOT-published. |

**The one annotated exception (DI).** `AddProcessKit(IConfiguration)` and `AddProcessKitGroup(IConfiguration)`
bind `ProcessKitOptions` from configuration by reflection, which is not trim/AOT-safe. Both carry
`[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`, so a consumer that calls them from a trimmed/AOT app
gets a precise warning pointing at the overload — exactly as Microsoft's own DI/options packages behave. Use
the `Action<ProcessKitOptions>` overload (or bind configuration yourself and call `configure`) from an AOT app.

**The `OutputJsonAsync` boundary (core).** The existing typed JSON verb (`Command.OutputJsonAsync<'T>`,
`IProcessRunner.OutputJsonAsync<'T>`, `CliClient.OutputJsonAsync<'T>`, `Pipeline.OutputJsonAsync<'T>`, and the
underlying `Runner.outputJson`) uses reflection-based `JsonSerializer.Deserialize(string, Type,
JsonSerializerOptions)` and remains annotated `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. For
trimmed/NativeAOT applications, use the additive `OutputJsonAsync(typeInfo)` overload on each object surface,
or `Runner.outputJsonTyped`, with source-generated `JsonTypeInfo<'T>` metadata. Those overloads call the
metadata-based `JsonSerializer.Deserialize(string, JsonTypeInfo)` API and carry no trimming/AOT annotations.
F# cannot itself author the Roslyn `System.Text.Json` source generator, but a C# project's generated context
can pass its `JsonTypeInfo<'T>` to F# or C# alike. The `aot-smoke` CI job (below) does not call this verb, so
it stays unaffected by this boundary.

**The `ProcessKit.Testing` boundary.** The record/replay cassette surface (`RecordReplayRunner`) serializes
and deserializes with reflection-based `System.Text.Json`. F# cannot use the `System.Text.Json` source
generator (it is a Roslyn/C# source generator that the F# compiler does not run), so the usual
AOT remedy is unavailable. Rather than emit silent "assembly was not verified" warnings, the package is
honestly **not** declared trimmable/AOT-compatible. Because it is meant to be referenced only from test
projects — code never shipped inside a trimmed/AOT application — this is a boundary in practice, not a
limitation of what you deploy.

**F# runtime baseline.** `FSharp.Core` — the F# runtime every F# assembly depends on — is not fully
trim/AOT-annotated (its `printf`/quotation/reflection surface), so a NativeAOT publish of *any* F#
application surfaces `IL2104`/`IL3053` warnings **attributed to `FSharp.Core`**, independent of ProcessKit.
Those are a known F# baseline, not a ProcessKit defect; warnings attributed to a `ProcessKit*` assembly would
be. ProcessKit's own assemblies publish warning-free.

**How this is validated.** [`samples/FSharp.NativeAot`](https://github.com/ZelAnton/ProcessKit-fSharp/tree/main/samples/FSharp.NativeAot) is a minimal consumer of
`ProcessKit` **and** `ProcessKit.Extensions.DependencyInjection`, published with `PublishAot=true` and run by
the `aot-smoke` job in the [CI workflow](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/.github/workflows/ci.yml) on both `linux-x64` (POSIX
process-group backend) and `win-x64` (Windows Job Object backend). It spawns a child, captures a non-zero
exit as an honest result, runs a child inside a kill-on-dispose `ProcessGroup`, and runs a child through a
DI-resolved `IProcessRunner` (`AddProcessKit`); the job fails if ilc attributes any warning to a `ProcessKit*`
assembly or if the native binary exits non-zero. So the compatibility above is exercised in a real
ahead-of-time-compiled image, not merely declared in metadata. (`ProcessKit.Extensions.Hosting` shares the
same factory-based, reflection-free pattern; its declaration rests on that analysis rather than a running
hosted-service image in this smoke.)

## Capability matrices

In the matrices below the columns are three of the four mechanisms. The **POSIX process group** column
covers macOS/the other BSDs *and* the Linux default (a limit-free group), since they share one backend.
The FreeBSD **process reaper** is not given a column of its own because it *is* that backend plus a
whole-tree layer: every row below reads the same for it except the handful listed under
[FreeBSD process reaper: what changes](#freebsd-process-reaper-what-changes), which is the complete list of
differences. Legend: ✅ full support · 🟡 supported with a documented qualification · ❌ not available.

**Whole-tree teardown**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| Kill-on-dispose, whole tree | ✅ | ✅ | ✅ |
| Graceful `ShutdownAsync` (configured soft signal → grace → hard kill) | 🟡 best-effort `WM_CLOSE` → grace → atomic kill | ✅ | ✅ |

`ShutdownAsync(grace)` on Windows has no per-job graceful signal, but a **windowed** child (Electron/GUI
tool) closes gracefully on a best-effort `WM_CLOSE` posted to its top-level windows: the soft phase posts
one to every member's windows, waits up to the grace window for the tree to drain, then unconditionally
terminates the Job — so a child with no window (or one that vetoes the close) is still hard-killed exactly
as before, and the kill-on-dispose guarantee is never weakened. On the Unix mechanisms it is the
configured `ProcessGroupOptions.StopSignal` (default `Signal.Term`), then a grace window, then `SIGKILL`.

**Adopting an external process (`Adopt`, `AdoptByPid`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Adopt(process)` an already-running external process | ✅ `AssignProcessToJobObject` | 🟡 write pid to `cgroup.procs` — **limited groups only** | ❌ `ProcessError.Unsupported` |
| `AdoptByPid(pid)` the same, from a bare pid | ✅ the process **object** behind one `OpenProcess` | 🟡 cgroup membership + a start-time read either side of the write — **limited groups only** | 🟡 tracked individually against a re-verified start-time token, and only for a target this caller may signal (Linux/macOS) — ❌ `ProcessError.Unsupported` on the BSDs |

`Adopt` brings a process ProcessKit did *not* start into the container, so kill-on-dispose and every
whole-tree control/stat/limit thereafter covers it. It takes a `System.Diagnostics.Process` (not a raw
pid) so the caller's open handle pins the pid against recycling on Windows. **Linux** can adopt only into
a group created **with resource limits** (which is what selects the cgroup v2 mechanism); a limit-free
Linux group and every macOS/BSD group use the POSIX process-group mechanism, which cannot relocate a
foreign process (`setpgid` moves only our own children, before `exec`) and refuses honestly with
`ProcessError.Unsupported` — never a silent no-op. A dead/gone pid, missing rights, or a process already
in an incompatible Job returns the typed `ProcessError.Adopt`. The adopted process is not ProcessKit's
child: it is contained and killed through the OS primitive alone (`KILL_ON_JOB_CLOSE` / `cgroup.kill`) and
never `waitpid`ed, so its exit is observed through the caller's own `Process`, not a `RunningProcess`.

`AdoptByPid` is the same containment for a caller who holds only the number (a pidfile, a registry, an
FFI or IPC boundary). Because a pid is an address rather than a handle, it captures an identity **anchor**
of its own for whatever the number currently names and binds the group to that — the process object on
Windows, kernel cgroup membership plus a start-time read on either side of the `cgroup.procs` write on
cgroup v2, and the pid plus a start-time token **re-read before every probe, signal, suspend/resume and
teardown kill** on the POSIX process group. So the row that cannot `Adopt` at all *can* adopt by pid,
which is why the two axes are reported separately (`Capabilities().Adoption` vs `.AdoptionByPid`): that
mechanism contains by tracking rather than by moving, and tracking a foreign number is safe once anchored.
Its qualification is real, though: only the adopted process itself is contained there, not the processes
it forks afterwards. A platform with **no start-time reader** (the BSDs) has no anchor to take and returns
`ProcessError.Unsupported` rather than tracking a bare number teardown would later SIGKILL whoever holds.
`pid <= 0` and this process's own pid are refused up front with `ProcessError.Adopt`, and so is a number
that changed hands during the call — on cgroup v2 that case is rolled back by moving the stranger out to
the parent cgroup, or, if even that is refused, reported as still contained here. A process this caller
may **not signal** (another user's, a protected one) is refused with `ProcessError.Adopt` too: the POSIX
process group asks with an explicit `kill(pid, 0)` probe at adoption, because reading a start-time anchor
proves only that the process can be identified — on Linux `/proc/<pid>/stat` is world-readable — never
that it can be controlled, while the Job Object and cgroup v2 rows reach the same refusal through their
denied `OpenProcess`/`cgroup.procs` write. Ownership is unchanged:
nothing adopted is ever `waitpid`ed by ProcessKit. The window this cannot close is the one before the
call — whether the number still named the intended process when you passed it — so where a live `Process`
is available, `Adopt` remains the stronger of the two on Windows.

**Launching outside containment (`Command.LaunchDetached`)**

The deliberate inverse of `Adopt`: instead of pulling a process *into* the container, it launches one
that never enters any — the opt-out for spawn-and-forget work (a self-updater, a restart-myself
relaunch, a daemon handed to the OS). See
[Detached launch](commands.md#detached-launch-spawn-and-forget) for the full contract and the typed
refusals; the platform divergences are:

| Capability | Windows | Linux / macOS / BSD (POSIX) |
|---|:---:|:---:|
| Detachment mechanism | ✅ created running, assigned to **no** Job Object, no handle retained | ✅ `POSIX_SPAWN_SETSID` — its own session, no controlling terminal |
| Survives a terminal/console close | 🟡 only with `CreateNoWindow()` (or `WindowsCtrlSignals()`) — it otherwise shares the caller's console | ✅ a new session cannot be reached by the terminal's hangup |
| Leaves no entry behind when it exits first | ✅ nothing references the process | ✅ a private reaper consumes the direct leader's wait status while this process lives |

Both platforms return the same `DetachedProcess` (pid + start-time identity) and neither exposes the
child's exit through that descriptor — that is what "detached" means here. On POSIX, the private reaper
consumes the direct leader's wait status while this process lives; if the parent exits first, the OS
reparents the child and its new supervisor owns reaping. Returning the *real* target pid therefore does
not require double-forking through a helper, and ProcessKit never claims arbitrary processes outside its
own child ownership.

The opt-out covers the containment **ProcessKit** creates, not one your own process was placed in by
someone else: a child of a job-bound Windows process joins that job by kernel rule (breakaway is not
requested — most ambient jobs forbid it, so asking would turn a working launch into a spawn failure),
and a Linux child inherits your cgroup, so a `systemctl stop` of your unit still reaps it. Work that
must survive that belongs with the platform's own supervisor, not with a child process.

**Additional child file descriptors (`Command.ExtraFd`)**

| Capability | Windows | Linux / macOS / BSD (POSIX) |
|---|:---:|:---:|
| Full-duplex channel at child fd 3+ | ❌ `ProcessError.Unsupported` | ✅ socketpair + explicit `dup2` |
| Parent access through `RunningProcess.TakeExtraFd` | ❌ | ✅ one-time `Stream` claim |

Each configured target must be unique and at least 3. The socketpair is close-on-exec by default and
only the explicitly mapped child end survives the spawn, so concurrent children do not inherit one
another's control channels. Pipelines, detached launches, and in-memory/cassette runners reject this
feature because they cannot expose or preserve the per-run parent stream.

**Reaping on sudden parent death (`Command.KillOnParentDeath`)**

Kill-on-dispose covers the parent tearing the group down; it cannot cover the parent being killed
*outright* (SIGKILL, a crash, a Windows `TerminateProcess`), because no `Dispose`/finalizer runs.
`Command.KillOnParentDeath()` opts a child in to being reaped in that case, and
`Command.KillOnParentDeathScope()` reports the honest, platform-fixed scope (independent of whether
the verb was set):

| Capability | Windows (Job Object) | Linux | macOS/BSD |
|---|:---:|:---:|:---:|
| `KillOnParentDeathScope()` | `WholeTree` | `DirectChildOnly` | `Nothing` |
| Reap child on sudden parent death | ✅ whole tree, no opt-in needed | 🟡 direct child only | ❌ `ProcessError.Unsupported` |

- **Windows** already reaps the **whole tree** with no extra action: every child lives in a Job Object
  created with `KILL_ON_JOB_CLOSE` whose sole handle the parent owns, so the kernel's handle rundown on
  parent death closes that last handle and terminates the Job. `KillOnParentDeath()` is a documented
  no-op there, not a silent one.
- **Linux** arms `PR_SET_PDEATHSIG(SIGKILL)` on the child through the `setpriv --pdeathsig` helper,
  reaching the **direct child only** (see the caveat below for what that excludes). A parent that dies
  before the arming lands is handled by the child itself: it verifies its parent is still the process
  that spawned it and terminates instead of running your program if it is not.
- **macOS/BSD** have no `PR_SET_PDEATHSIG` analog, so a set value fails the spawn with
  `ProcessError.Unsupported` — never a silent no-op.

**Signals (`Signal`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Signal.Kill` | ✅ maps to Job terminate | ✅ | ✅ |
| `Signal.Int` / `Signal.Term` | 🟡 best-effort CTRL+BREAK (a `WindowsCtrlSignals()` child) and/or `WM_CLOSE` (a windowed member); `Unsupported` only when the group has neither | ✅ | ✅ |
| Any other signal (`Hup`, `Quit`, `Usr1`, `Usr2`, `Other n`) | ❌ `ProcessError.Unsupported` | ✅ | ✅ |

**Suspend / resume**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Suspend` / `Resume` the whole tree | ✅ per-process freeze across the job | ✅ `cgroup.freeze` | ✅ `SIGSTOP` / `SIGCONT` |

**Member listing (`Members`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Members()` snapshot | ✅ whole tree | ✅ whole tree | 🟡 tracked group leaders, plus any `AdoptByPid` member whose anchor still matches |

`MembersInfo()` returns that same membership enriched per pid (`MemberInfo`: `Pid`, `Ppid`, `ExeName`,
`StartTime`). Enrichment follows the **OS**, not the mechanism — on Linux both the cgroup v2 and the
process-group backend read `/proc` identically. Every enriching field is an `option`, `None` where the
platform cannot honestly report it (never a fabricated value); a member that exits between enumeration
and its metadata read is omitted, not invented; and the member's command line and environment are never
included on any platform.

| `MemberInfo` field | Windows (Job Object) | Linux (cgroup v2 or process group) | macOS | other BSD |
|---|:---:|:---:|:---:|:---:|
| `Pid` | ✅ | ✅ | ✅ | ✅ |
| `Ppid` | ✅ process snapshot | ✅ `/proc/<pid>/stat` | ✅ `proc_pidinfo` | ❌ |
| `ExeName` | ✅ image file name (`foo.exe`) | ✅ `/proc` `comm` (~15 chars) | ✅ `proc_pidinfo` | ❌ |
| `StartTime` | ✅ | ✅ | ✅ | 🟡 best-effort |

`ExeName` is a base **image name**, never an argv — Windows reports the full `foo.exe`; Linux/macOS the
kernel `comm` (truncated to ~15 chars). `StartTime` is `System.Diagnostics.Process.StartTime`; on a BSD
other than macOS, where no per-pid parent/image reader exists, only the pid and a best-effort start time
are reported.

**Standalone process lookup (`ProcessLookup.processInfo`, `processIsAlive`)**

The bare-pid companion to `MembersInfo()` above (no group needed) reuses the exact same per-pid readers,
so the two never disagree, but its `Ok None` / `Error` boundary and reuse-protection availability are
worth their own row because it is queried directly against an arbitrary pid rather than a group's own
known-live membership:

| Behaviour | Windows | Linux | macOS | other BSD |
|---|:---:|:---:|:---:|:---:|
| Existence/permission oracle | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` | `/proc/<pid>/stat` read | `proc_pidinfo(PROC_PIDTBSDINFO)` | zero-signal `kill(pid, 0)` probe |
| A process that may exist but cannot be inspected | ✅ typed `ProcessError.Io` (denied `OpenProcess`) | ✅ typed `ProcessError.Io` (`EACCES` — `hidepid=1` only, see below) | ✅ typed `ProcessError.Io` (any errno but `ESRCH`) | ✅ typed `ProcessError.Io` (any errno but `ESRCH`/`EPERM`) |
| `processIsAlive` reuse protection (`Some startTime` verified against the live process) | ✅ | ✅ | ✅ | 🟡 best-effort, same as the `StartTime` row above — a `Process.StartTime` read that fails for this one pid right now is `ProcessError.Io`, never `Unsupported` (there is no platform where the reader is categorically absent) |

Two divergences to read before relying on either outcome:

- **`hidepid=1` vs `hidepid=2`/`subset=pid` on Linux.** The `EACCES`→`Error` / `ENOENT`→`Ok None` split
  above is exact only for `hidepid=1` (the process directory is visible, its contents refused). Under
  `hidepid=2`/`hidepid=invisible` (or `mount -o subset=pid`) the kernel makes another user's
  `/proc/<pid>` **invisible**, so `stat` on it returns the same `ENOENT` a genuinely gone pid does — a
  live foreign process on such a host reads as `Ok None`, indistinguishable from "never existed". There
  is no syscall-level signal that separates the two cases there.
- **A POSIX zombie (exited, not yet `wait`ed by its real parent) still reads `Ok(Some _)`.** None of the
  Linux/macOS/bare-BSD readers inspect the `stat` state field, so a zombie answers exactly like a live
  process on every POSIX platform. **Windows has no equivalent state** — an exited process is simply
  gone (`Ok None`) whether or not anything collected its status. A reaper-style consumer of
  `processIsAlive` on POSIX must not read `Ok true` as "still doing work", only as "not yet collected".

**Stats (`Stats` / `SampleStatsAsync`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `ActiveProcessCount` | ✅ | ✅ | ✅ |
| `PeakProcessCount` | ❌ `None` | 🟡 `pids.peak` with `MaxProcesses`, Linux 6.6+ | ❌ `None` |
| `TotalCpuTime` + `PeakMemoryBytes` | ✅ | ✅ | ❌ active count only |
| `IoReadBytes` / `IoWriteBytes` + operation counts | ✅ Job aggregate | 🟡 `io.stat` when I/O is delegated | ❌ `None` |

On the POSIX process-group mechanism, all optional `ProcessGroupStats` metrics are `None` — only the
live process count is available. Windows reads Job Object accounting but has no lifetime peak-process
counter. On Linux cgroup v2, `pids.peak` is available only when `MaxProcesses` is configured (which
delegates the `pids` controller) and the kernel is version 6.6 or later. It measures the peak number of
kernel tasks, including both processes and their threads, and is therefore not directly comparable
with `ActiveProcessCount`, which counts process leaders. The cgroup mechanism also reads `cpu.stat`,
`memory.peak`, and, when that controller is delegated, block-device counters from `io.stat`; an
unavailable controller file yields `None` rather than a fabricated zero or a sampled estimate.

**Resource limits (`ProcessGroupOptions`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `WithMemoryMax` (whole tree) | ✅ | ✅ | ❌ `ProcessError.ResourceLimit` |
| `WithMaxProcesses` | ✅ | ✅ | ❌ `ProcessError.ResourceLimit` |
| `WithCpuQuota` | 🟡 approximate | ✅ | ❌ `ProcessError.ResourceLimit` |
| `WithCpuTimeMax` | ✅ Job aggregate | ✅ per child `RLIMIT_CPU` | ✅ per child `RLIMIT_CPU` |
| `WithCpuAffinity` (pin the tree to cores) | 🟡 `JOB_OBJECT_LIMIT_AFFINITY`, cores 0–63 only | ✅ `cpuset.cpus` (needs the `cpuset` controller) | ❌ `ProcessError.ResourceLimit` |
| `WithUiRestrictions` (clipboard/desktop/exit-Windows) | ✅ `JOBOBJECT_BASIC_UI_RESTRICTIONS` | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` |

`WithCpuQuota` is a fraction of a single core (`0.5` = half a core, `2.0` = two cores). On Windows
it is converted against the host's CPU count and is approximate. Whole-tree limits need a real
limit-capable container; the POSIX process-group mechanism supports only the per-child CPU-time
rlimit. Any unsupported request fails at creation with `ProcessError.ResourceLimit` rather than
returning a silently-unbounded group.

**Per-process resource limits (`Command.Rlimit`)**

| Capability | Windows | Linux | macOS / BSD |
|---|:---:|:---:|:---:|
| `Rlimit(Cpu\|Core\|Data\|FileSize\|NoFile\|Stack, soft, hard)` | ❌ `ProcessError.Unsupported` | ✅ via util-linux `prlimit` | ❌ `ProcessError.ResourceLimit` (no util-linux) |

`Command.Rlimit` caps ONE process rather than the tree: the value is applied to the child before its
program starts (`setrlimit(2)` semantics) and inherited individually by every descendant, each of which
may lower it further or raise its soft value back to the inherited hard one. Values are in the
resource's own unit — bytes for `Core`/`Data`/`FileSize`/`Stack`, seconds for `Cpu`, a count for
`NoFile` — and there is no "unlimited" value, because the builder exists to lower what the child
inherited. Applying it needs a helper that can call `setrlimit` between the spawn and the `exec`, which
on .NET means an external one: util-linux's `prlimit`, resolved only from a trusted system directory
(`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and never from `PATH`, exactly as `setpriv` is. A host
holding it in none of them — macOS/BSD, which have no util-linux, or a minimal image — refuses the
spawn with `ProcessError.ResourceLimit`; Windows, which has no `setrlimit` concept at all, refuses with
`ProcessError.Unsupported` and offers the whole-tree Job Object caps above instead. Where `Cpu` meets
the group's `WithCpuTimeMax`, the stricter of the two values is what the child gets
(see [Resource limits](process-groups.md#per-process-limits-on-a-command)).

`WithCpuAffinity` pins the tree to a set of zero-based core indices and carries two platform ceilings,
both reported as a typed `ProcessError.ResourceLimit` at creation/update rather than as a silently
dropped pin. **Windows:** `JOBOBJECT_BASIC_LIMIT_INFORMATION.Affinity` is one pointer-sized mask
covering a single [processor group](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups),
so only cores `0`–`63` are nameable on x64 (`0`–`31` on x86); a host with more logical processors
splits them across groups the mask cannot reach. **Linux:** `cpuset` is a controller a hierarchy may
simply not carry, and unlike `memory`/`pids`/`cpu` its absence is not implied by cgroup v2 being
mounted — where `cgroup.controllers` omits it, no pin can be enforced. On both, every requested core
must exist on the host and be available to the caller: a Job's affinity mask must be a subset of the
creating process's own, and a cgroup's `cpuset.cpus` a subset of the parent's effective cores. macOS
and BSD have no whole-tree affinity primitive at all (nor even a per-process one comparable to
`sched_setaffinity`), so the refusal there is unconditional.

`WithUiRestrictions` is the one dimension that is **Windows-only rather than
limit-capable-container-only**: it restricts what the contained tree may do to the interactive desktop
session (clipboard, desktops, display/system parameters, global atoms, `ExitWindows`), which no POSIX
primitive — cgroup v2 included — has any analogue for. That is why it refuses with
`ProcessError.Unsupported` rather than the `ResourceLimit` the caps above use: a memory cap is a
concept everywhere and merely unenforceable on some mechanisms, while a clipboard restriction does not
exist off Windows at all. Either way the request is never silently dropped.

These caps are also updatable on a **live** group via `ProcessGroup.UpdateLimits(ResourceLimits)` —
an optional runtime operation that re-applies a full replacement cap set without recreating the
group or restarting its children. It follows the same platform matrix as creation: the Windows Job
Object re-applies via `SetInformationJobObject` (the caps, the affinity mask, **and** the UI
restrictions), the Linux cgroup v2 mechanism rewrites `memory.max` / `pids.max` / `cpu.max` /
`cpuset.cpus` (and refuses a set carrying UI
restrictions with `ProcessError.Unsupported`, leaving the previous caps untouched), and the POSIX
process-group mechanism (macOS/BSD, or Linux without cgroup v2) returns
`ProcessError.ResourceLimit` — never a silent no-op. See
[process-groups.md](process-groups.md) for the API and semantics.

**Post-run limit evidence (`ProcessGroup.LimitEvidence()`)**

The post-mortem counterpart to the admission matrix above: `ProcessError.ResourceLimit` answers *could
the cap be applied at all*, `LimitEvidence()` answers *did a cap this group carried then actually fire* —
the question an exit code or signal cannot, since a cap-driven kill and a self-inflicted crash look
identical from the outside. Each cell is the `LimitVerdict` an axis can produce on that mechanism and the
counter it is read from; nothing here is re-derived from the requested `ResourceLimits` or inferred from
the run's outcome:

| Axis | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Memory` (`WithMemoryMax`) | 🟡 `Unknown` on an axis ever capped; `NotTripped` on one never capped | ✅ `memory.events.local` (preferred) / `memory.events`, key `oom` | ❌ `Unknown`, unconditionally |
| `Processes` (`WithMaxProcesses`) | 🟡 same qualification | ✅ `pids.events.local` (preferred) / `pids.events`, key `max` | ❌ `Unknown`, unconditionally |
| `Cpu` (`WithCpuQuota`) | 🟡 same qualification | ✅ `cpu.stat`, key `nr_throttled` | ❌ `Unknown`, unconditionally |
| Read **before** teardown has completed | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` |

Linux cgroup v2 is the only mechanism with real evidence to read. For an axis this group actually capped,
the first listed counter file that reads successfully decides: a non-zero value is `Tripped`, a
present-but-zero one is an authoritative `NotTripped`, and a file that reads but lacks the key — or every
candidate failing to read at all (an older kernel, a controller this hierarchy never enabled, a cgroup
already gone) — is the honest `Unknown`. An axis it never capped is `NotTripped` with no read at all,
exactly as on the Job Object below, so a limit-free group costs no counter reads.
The memory axis reads `oom` deliberately, not `oom_kill`: the latter also counts a *global* host OOM kill
of a member, which would misattribute a system-wide event to this group's own cap.

The **Windows Job Object** keeps no post-mortem record that any of these caps fired — no `memory.events` /
`pids.events` / `cpu.stat` analogue exists — so every axis it ever capped reads `Unknown`: a measured
conclusion about what a Job actually preserves, not an unimplemented reader. An axis it never capped still
reads `NotTripped` without touching native at all (nothing was capped, so nothing could fire). The **POSIX
process group** answers `Unknown` on every axis *unconditionally*, including one this group never capped:
it has no whole-tree resource-accounting apparatus whatsoever — the same reason `Create`/`UpdateLimits`
refuse any whole-tree cap on it — so unlike the Job Object it has no "nothing was capped" case to report
`NotTripped` from either.

On **every** mechanism, a `NotTripped` the `Cpu` axis would otherwise report is downgraded to `Unknown`
whenever the group also carries a `ResourceLimits.CpuTimeMax`: that cap (Windows job-time, POSIX per-child
`RLIMIT_CPU`) has no post-mortem counter anywhere, and neither a Job's accounting nor `cpu.stat`'s
`nr_throttled` can attribute a trip of it — so "the quota did not throttle" is not the same honest "no"
once a `CpuTimeMax` is in play. A real `Tripped` from quota-throttle evidence is never downgraded.
`ResourceLimits.IoMax` and `WithCpuAffinity` have **no** `LimitEvidence` axis at all on any mechanism — no
containment primitive here keeps a "this whole-tree I/O rate or affinity cap engaged" record, so there is
nothing honest to report for them, not even `Unknown`.

The evidence is available **only after the group has been torn down** (`ShutdownAsync`/`Dispose`/
`DisposeAsync`, or the finalizer) — the opposite lifetime rule `Stats()` follows. It is captured exactly
once, from the still-live container, in the instant before its counters (and, on cgroup v2, the cgroup
directory itself) are destroyed, then cached, so every later read returns that same snapshot. Which axes
are queried is a **sticky** record: an axis an `UpdateLimits` call names joins it whether that call then
succeeds or fails, so a cap that fired and was later lifted is still answered from the real counter rather
than a guessed `NotTripped`. See [Limit Evidence](process-groups.md#limit-evidence) for the API and
examples.

**Linux I/O scheduling priority (`Command.IoPriority`)**

| Capability | Windows | Linux | macOS / BSD |
|---|:---:|:---:|:---:|
| `IoPriority(Idle \| BestEffort level \| RealTime level)` | ❌ `ProcessError.Unsupported` | ✅ `ioprio_set(2)` on the spawning thread, inherited by the child at clone | ❌ `ProcessError.Unsupported` (no such system call) |
| The same on `Command.LaunchDetached` | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (owner-applied; the verb gives ownership up) | ❌ `ProcessError.Unsupported` |
| `RealTime` without `CAP_SYS_ADMIN` (`CAP_SYS_NICE` on Linux 5.14+) | — | ❌ `ProcessError.Spawn` | — |

This is a **separate axis** from the CPU-scheduling `Command.Priority`, which is supported on every
platform and never returns `Unsupported`: `Priority` orders the child's claim on the *processor*,
`IoPriority` its claim on a *block device*. Windows has no per-process I/O scheduling class at all —
its nearest relatives are a whole-Job disk *rate* ceiling (`ResourceLimits.WithIoMax`, in the table
above) and the CPU priority class — so the request is refused rather than approximated, exactly as
`Command.Rlimit` is.

It needs **no helper binary**: `posix_spawn` has no I/O-priority attribute and no managed code may run
in a forked child on .NET, but Linux copies the creating task's I/O priority into the new task and the
value survives `exec`, so ProcessKit arms the spawning thread across the spawn and restores it right
after. The priority is therefore in force for the child's *first* block-device request, is inherited by
every descendant, and rides through every helper `exec` on the POSIX path (`setpriv`, `prlimit`,
`setsid --ctty`, the cgroup launcher) — so unlike `Command.Rlimit` it composes with `Command.Arg0` and
needs nothing installed on the host. Two remaining honest gaps are typed `ProcessError.Unsupported`
rather than a silent no-op: a kernel (or seccomp filter) answering `ENOSYS`, and a Linux architecture
whose `ioprio_set` system call number ProcessKit does not know (x86-64, x86, arm, arm64, riscv64,
loongarch64, s390x, and ppc64le are known).

**What no platform promises** is that the class changes the order requests are *served* in: Linux
honours I/O priorities under the **BFQ** scheduler (and the historical CFQ), while `mq-deadline`,
`kyber`, and `none` — the common defaults for NVMe — largely ignore them. The recording of the class on
the child is what this builder guarantees. See
[Running commands](commands.md#linux-io-scheduling-priority-iopriority).

**`argv[0]` override (`Command.Arg0`)**

| Capability | Windows | Linux / macOS / BSD (POSIX) |
|---|:---:|:---:|
| Override `argv[0]` independently of `Program` | ❌ `ProcessError.Unsupported` (no separate `argv[0]` contract) | ✅ distinct `argv[0]` on `posix_spawnp` |
| Combined with a `Uid`/`Gid`/`Groups`/`KillOnParentDeath` drop | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (`setpriv` has no `argv[0]` seam) |
| Combined with `Command.Pty` | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (`setsid --ctty` has no `argv[0]` seam) |
| Combined with a run under the Linux cgroup backend | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (the `/bin/sh` migration launcher has no `argv[0]` seam) |
| Combined with `ResourceLimits.CpuTimeMax` on the POSIX process-group mechanism | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (the `/bin/sh` `RLIMIT_CPU` shim has no `argv[0]` seam) |
| Combined with any `Command.Rlimit` value | ❌ `ProcessError.Unsupported` | ❌ `ProcessError.Unsupported` (the util-linux `prlimit` helper has no `argv[0]` seam) |
| Combined with a lone `Setsid` (no privilege drop) | ❌ `ProcessError.Unsupported` | ✅ composes normally (no helper involved) |

`Program` alone still drives PATH/`PreferLocal` resolution, preflight, and spawn diagnostics — the
override changes only what the child observes as `argv[0]`. Every refusal above is a typed
`ProcessError.Unsupported`, checked before any child exists, never a silent fallback to `Program` or
a misapplication to a wrapping helper's own `argv[0]`. See
[Running commands](commands.md#posix-argv0-override-arg0).

**Windows privilege drop (`Command.WindowsRestrictedToken` / `WindowsIntegrityLevel`)**

The mirror image of the Unix privilege drop below: Windows has no `setuid`, so a child is hardened by
handing it a weakened copy of the caller's own token instead of a different identity.

| Capability | Windows | Linux / macOS / BSD (POSIX) |
|---|:---:|:---:|
| `WindowsRestrictedToken()` (no privilege but `SeChangeNotifyPrivilege`) | ✅ `CreateRestrictedToken` + `CreateProcessAsUser` | ❌ `ProcessError.Unsupported` |
| `WindowsIntegrityLevel(Medium/Low/Untrusted)` | ✅ `SetTokenInformation(TokenIntegrityLevel)` | ❌ `ProcessError.Unsupported` |
| Either, combined with `Command.Pty` | ❌ `ArgumentException` at the builder (ConPTY spawns through a call that cannot carry the token) | ❌ same builder refusal |
| Either, combined with `Uid`/`Gid`/`Groups`/`Umask`/`Setsid` | ❌ `ArgumentException` at the builder | ❌ same builder refusal |

Both apply to the **direct child** (and, through token inheritance, the descendants it starts); they
are honoured on the contained spawn and on `Command.LaunchDetached` alike. Neither can raise
privilege — a restricted token only loses rights and Windows refuses to raise a token's integrity, so
there is deliberately no elevating variant. A host policy that refuses to let ProcessKit assign the
derived token fails the spawn with a typed `ProcessError.Spawn` naming that refusal, never a silent
fallback to an unhardened child. The cross-platform pair is rejected at the *builder* rather than at
the spawn because each half is `Unsupported` on the platform the other half needs, so such a command
could not run anywhere. See [commands.md](commands.md) and [hardening.md](hardening.md).

**Windows named-pipe readiness probe (`RunningProcess.WaitForNamedPipeAsync`)**

| Capability | Windows | Linux / macOS / BSD (POSIX) |
|---|:---:|:---:|
| Wait for a named pipe endpoint to accept a client | ✅ `CreateFileW`, tried against duplex/read-only/write-only client access | ❌ `ProcessError.Unsupported`, checked before any poll attempt |
| A pipe busy with another client (`ERROR_PIPE_BUSY`) | ✅ counts as **ready** — proves a server created the pipe | n/a |

Symmetric with `WaitForSocketAsync`'s `AF_UNIX` gate: this probe is Windows-only because a Windows
named pipe has no portable equivalent, and every other platform fails immediately with a typed
`ProcessError.Unsupported`, never a silent downgrade to some other transport or an inevitable hang. See
[Readiness probes](streaming.md#readiness-probes).

### Pseudo-terminal (PTY) capabilities

`Command.Pty` gives a child a controlling terminal and one merged stdout+stderr stream. Every
unavailable case is a typed `ProcessError.Unsupported`; ProcessKit never quietly falls back to
pipes.

| Capability | Windows (ConPTY) | Linux (`openpty` + `setsid --ctty`) | macOS/BSD (POSIX pgid + ctty helper) |
|---|:---:|:---:|:---:|
| PTY spawn | ✅ Windows 10 1809+ | ✅ | 🟡 needs a controlling-terminal helper |
| `ResizeAsync` on a PTY | ✅ `ResizePseudoConsole` | ✅ `TIOCSWINSZ` + `SIGWINCH` | ✅ `TIOCSWINSZ` + `SIGWINCH` |
| `ResizeAsync` on a non-PTY | ❌ `Unsupported` | ❌ `Unsupported` | ❌ `Unsupported` |
| Containment under PTY | ✅ Job Object | ✅ cgroup v2 or pgid | ✅ pgid |
| U+0003 (Ctrl+C) sent through interactive stdin | 🟡 delivered as input, but does not interrupt by default | ✅ terminal `VINTR` | ✅ terminal `VINTR` |

Windows older than 10 version 1809 returns `Unsupported`. Linux needs the `setsid --ctty` helper
in one of the trusted system directories (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`, where util-linux
installs it — the helper is never taken from `PATH`, see
[Hardening → Where the Unix helper binaries come from](hardening.md#where-the-unix-helper-binaries-come-from))
as well as a usable PTY device. `openpty` exists on macOS/BSD, but their standard `setsid` does not
provide `--ctty`; until a helper is supplied, a PTY spawn there is `Unsupported` rather than a
controlling-terminal-less half implementation.

Apart from the Ctrl+C row above, everything not listed here — capture, line streaming, interactive stdin, encodings, buffer
policies, timeouts, retry, pipelines, supervision, readiness probes, cancellation, redirecting
stdout/stderr straight to a file (`Command.StdoutToFile`/`StderrToFile` — an inheritable file
handle in `STARTUPINFO` on Windows, a file fd via a `posix_spawn` file action on POSIX; the same
create/truncate/append semantics and the same builder-boundary conflict rules on every platform),
and the testing seams — is platform-agnostic and behaves identically everywhere. See [commands.md](commands.md),
[streaming.md](streaming.md), [pipelines.md](pipelines.md), [supervision.md](supervision.md),
and [testing.md](testing.md).

Every ConPTY child receives `CREATE_NEW_PROCESS_GROUP` for isolation, whether or not
`WindowsCtrlSignals()` is enabled. By Windows contract that creation flag disables the process's
default Ctrl+C handling, so writing U+0003 to ConPTY input does not interrupt it. The
`WindowsCtrlSignals()` opt-in only registers the leader for ProcessKit's targeted CTRL+BREAK path;
it does not add or remove the process-group flag.

### FreeBSD process reaper: what changes

`Mechanism.ProcessReaper` is the POSIX process-group backend plus a whole-tree layer, so every row in the
matrices above reads the same for it. These are the differences, in full:

| Capability | POSIX process group | FreeBSD process reaper |
|---|:---:|:---:|
| Kill-on-dispose reaches a `setsid` descendant | ❌ it leaves the tracked pgid (see the caveat below) | ✅ `pi_subtree` is fixed at fork and never rewritten, so the kernel still walks it |
| `Members()` / `MembersInfo()` / `Stats().ActiveProcessCount` | the tracked group **leaders** only | ✅ every live descendant of every child the group started (`PROC_REAP_GETPIDS`) |
| `Signal` / `Suspend` / `Resume` / the graceful soft tier | `killpg` per tracked pgid | `PROC_REAP_KILL` per subtree — the same signal vocabulary, delivered once per process, escapees included |
| Orphaned descendants (the daemonising double fork) | re-parent to `init`, which reaps them | re-parent to **this** process; ProcessKit `waitpid`s those corpses itself, and never one it forked (that exit status belongs to whoever started it) |
| Per-member CPU/memory in `MemberStats()` | Linux `/proc`, macOS `proc_pidinfo` | ❌ honestly absent — FreeBSD has no `/proc` by default and this port carries no `sysctl(KERN_PROC)` reader, so a member is reported with its pid and no invented figures |
| Whole-tree resource limits | ❌ `ProcessError.ResourceLimit` | ❌ `ProcessError.ResourceLimit` — the reaper contains a tree but accounts for nothing in it, so nothing changes here |
| `Adopt` / `AdoptByPid` | ❌ / 🟡 (❌ on the BSDs, which have no start-time reader) | ❌ / ❌ — the reaper holds this process's own *descendants*, and `PROC_REAP_ACQUIRE` does not re-attach even children forked before it, let alone a process started outside this tree |

`ProcessGroup.Capabilities()` reports `Creation` as `Qualified` on FreeBSD rather than `Available`, and
says why: acquiring reaper status is a permanent, process-wide side effect, so a snapshot predicts the
mechanism instead of proving it by performing it. Creation does not fail either way — a host where the
acquisition is refused silently gets the POSIX process group and the created group reports
`Mechanism.ProcessGroup`.

## Caveats

The honest fine print — mostly consequences of OS semantics, plus a few tracked internal
constraints that do not change the public surface.

**Windows ConPTY sidecar ownership.** The `conhost` / `OpenConsole.exe` sidecar created for a
ConPTY is not a Job Object member. That is a real difference from the child process tree, not a
hidden containment claim: ProcessKit owns the sidecar through the pseudoconsole handle and closes it
deterministically with `ClosePseudoConsole` during teardown. The child itself is still born inside
the Job Object.

**Windows PTY stdio binding on a headless launcher.** A ConPTY child's standard handles always come
from the pseudoconsole, but the two Windows launch environments need different mechanisms for that:
a console-attached launcher severs its own console handles in the child's startup information, while
a headless one (a service-hosted CI step, a redirected test host) instead replaces its own three
standard-handle slots with null for the length of the `CreateProcess` call and restores them
immediately afterwards. ProcessKit serializes that short window with all of its own Windows spawn
paths, so no command it starts — including one inheriting the caller's stdio — can observe the null
slots; it cannot coordinate code outside ProcessKit, so a concurrent foreign spawn with inherited
stdio, or a first-time `Console` access on another thread, can still race it. Run PTY sessions from a
dedicated helper process where that matters. See [PTY → Platform support](pty.md#platform-support).

**Windows PTY echo belongs to the child.** `PtyConfig.Echo = false` clears the POSIX slave terminal's
`ECHO` bit before spawn, but Windows echo is controlled by the child's `CONIN$` console mode. ConPTY
does not expose a supported parent-side pre-spawn override, so a Windows credential prompt must
suppress its own echo. This is documented rather than silently treating `Echo = false` as a Windows
guarantee.

**Windows `.cmd`/`.bat` shims launch through `cmd.exe`.** A Windows bare name whose only `PATH`
match carries a non-`.exe` extension (the `.cmd`/`.bat` wrappers `npm`, `yarn`, `az`, and many
dotnet-tool shims ship) is unreachable by the OS's own bare-name search, which appends only `.exe` —
yet `Exec.which` locates it through the same `PATHEXT`-aware lookup. ProcessKit closes that
`which`-vs-spawn gap: it substitutes the resolved absolute path into the launch, and routes a
`.cmd`/`.bat` through `cmd.exe /d /c` (a batch file is not a directly-launchable image). Because a
batch wrapper reintroduces a shell, arguments are quoted for `cmd.exe`'s own grammar, not just the
ordinary argv rules — a metacharacter such as `&`, `|`, `<`, `>`, or `"` is delivered literally,
never executed (the "BatBadBut" class, CVE-2024-24576). An argument `cmd.exe` cannot escape at all —
a `%`, a `!`, or a line break — fails the spawn with a typed `ProcessError.Spawn` rather than
launching unsafely. A `.exe` match on an unchanged child `PATH` (see the next entry), a path-form
program, and anything on POSIX are unaffected (POSIX has no `PATHEXT`; the OS resolves them exactly
as before).

**Windows puts the child's `PATH` into the bare-name search in place of the process's — it does not
confine the search to it.** The OS's own bare-name search runs in the *parent's* context — it walks
the calling process's `PATH`, never the environment block the child is given — so a command that
overrides, removes, or clears the child's `PATH` (`Env("PATH", …)`, `EnvRemove("PATH")`, `EnvClear`)
would otherwise launch a same-named executable from the process's own `PATH`. ProcessKit resolves
such a command against its effective child `PATH` and substitutes the resolved absolute path into the
launch, on every Windows launch path (ordinary, `Pty`/ConPTY, and `LaunchDetached`), so the image
that runs is the one `Command.ResolveProgram()` reports for the same command. The rest of the
`CreateProcessW` search order is preserved around that `PATH`: the application directory, the
*process's* current directory (the one the command sets with `CurrentDir` applies only after the
image has been chosen, and Windows drops this entry entirely when
`NoDefaultCurrentDirectoryInExePath` is set in the environment), the system directory followed by its
legacy 16-bit counterpart, and the Windows directory are all searched **before** it — exactly the
order `Command.ResolveProgram()` reports. A child `PATH` is therefore not an image-pinning
mechanism: a bare `curl` still resolves to `System32\curl.exe` even when the child `PATH` names
another one, and a program sitting in the process's current directory is still reachable from a
command whose child `PATH` is empty. Pass an absolute program path, or use `PreferLocal` (consulted
before every directory above), when one specific image must run. `ProcessError.NotFound` — carrying
the same `Searched` value the preflight reports — is returned before any process is created only when
that entire search, the child's `PATH` included, finds nothing. A command that leaves the child's
`PATH` alone keeps the OS's own search unchanged.

**POSIX also launches a bare name from the effective child `PATH`.** libc's `posix_spawnp` searches
the launching process's native environment rather than the separate `envp` block supplied for the
child. A command using `Env("PATH", …)`, `EnvRemove("PATH")`, or `EnvClear` is therefore resolved
first even when its resulting PATH string equals the process value, and the absolute executable is
substituted into both direct POSIX launch paths (ordinary and `LaunchDetached`). The selected image is
exactly the one `Command.ResolveProgram()` reports for the same configuration; a miss returns its
identical `ProcessError.NotFound` / `Searched` before any native spawn, so a same-named executable from
the process `PATH` cannot run instead. An inherited absent or empty process `PATH` is resolved too:
libc may otherwise use a default system path or the current directory, search locations that
`ResolveProgram()` deliberately does not invent for an empty PATH. By contrast, each empty component
inside a **non-empty** POSIX `PATH` is the effective working directory at that exact position: `:dir`,
`dir:`, and `dir::other` search it before, after, or between the named entries. Relative named entries
use that same base. For a command this is its configured `CurrentDir`, or the process's current
directory when none is set; `Exec.which` always uses the process current directory because it resolves
the host process `PATH`. A wholly empty or absent `PATH` still has no entries and never gains this
current-directory search. POSIX resolution is narrower than Windows resolution: after `PreferLocal`,
it walks only the effective `PATH`, without application/current/system-directory entries around it.
Only an untouched, non-empty inherited child `PATH` delegates its bare name to `posix_spawnp` exactly
as before. Prefer-local hits and path-form programs likewise keep their existing behavior; a relative
path-form program still resolves against the child's working directory.

**`Command.WindowsRawArg` is Windows-only.** It appends a trusted fragment verbatim after all
ordinarily quoted arguments for children with a non-MSVCRT parser. POSIX has an argv vector rather
than a mutable raw command line, so requesting it there fails with `ProcessError.Unsupported`.
Automatic `.cmd`/`.bat` wrapping is also refused when raw fragments are present; invoke `cmd.exe`
explicitly if its grammar is intentionally the parser. See
[Running commands](commands.md#windows-raw-command-line-fragments) for ordering and injection rules.

**`Command.Arg0` is Unix-only.** It overrides the child's `argv[0]` independently of the program
that is actually launched (multicall binaries, login-shell conventions). Windows has no separate
`argv[0]` contract (`CreateProcessW` takes one raw command line), so requesting it there fails with
`ProcessError.Unsupported` — the mirror image of `WindowsRawArg` above. On POSIX it further refuses
(same typed error, at spawn time) when combined with a knob whose spawn path re-`exec`s the target
by name through a helper with no seam of its own for a distinct `argv[0]`: a `Uid`/`Gid`/`Groups`/
`KillOnParentDeath` drop (`setpriv`), `Pty` (`setsid --ctty`), a run under the Linux cgroup
backend (the `/bin/sh` migration launcher), a `ResourceLimits.CpuTimeMax` run on the POSIX
process-group mechanism (the `/bin/sh` `RLIMIT_CPU` shim), or any `Command.Rlimit` value
(the util-linux `prlimit` helper) — see
[Running commands](commands.md#posix-argv0-override-arg0).

**POSIX process groups: a `setsid` child can escape.** The process-group mechanism tracks each
child's pgid, and teardown signals those pgids. A descendant that deliberately starts a new
session (a `setsid` call) gets a fresh process group that the parent group does not track, so it
can outlive the teardown. This is the genuine weakness of the process-group mechanism; it is why
`ProcessGroup.Mechanism` is reported rather than papered over. The Job Object, cgroup v2 and FreeBSD
process-reaper mechanisms have no such hole — membership is enforced by the kernel (a container for the
first two, the reaper's per-descendant subtree tag for the third), not by group bookkeeping. When this
matters, check the active mechanism.

**FreeBSD: being the reaper is an obligation, not only a capability.** Acquiring reaper status makes an
orphaned descendant re-parent onto *this* process instead of onto `init`, which is exactly the containment
`Mechanism.ProcessReaper` exists for — and it transfers `init`'s duty along with it: when such a process
exits it becomes a zombie of this process and someone must `wait` for it. ProcessKit discharges that on
every reaper read (membership, delivery, teardown) plus a short bounded drain at teardown, and it collects
only processes it did **not** fork itself, so no run verb's exit status is ever stolen. Two consequences
are worth planning for: reaper status is process-wide and is never released while the process lives (it is
shared by every live `ProcessGroup`, and possibly by your own code, which may have acquired it first), and
a descendant that outlives every group still re-parents here rather than to `init`.

**Unix privilege drop clears supplementary groups unless you set them.** A `Uid`/`Gid`/`User` drop
runs through the `setpriv` helper (util-linux), which by default *clears* the parent's supplementary
groups so the child never keeps root's — but a child dropped to a service user then lacks that user's
group memberships (`docker`, `video`, `adm`, …). Pass `Command.Groups(gids)` to set the child's
supplementary groups explicitly (mapped to `setpriv --groups`); it is honoured only alongside a
`Uid`/`Gid` drop, so requesting it without one fails with `ProcessError.Spawn` rather than being
silently ignored. The whole family is **Unix-only**: on Windows `Uid`/`Gid`/`Groups`/`Setsid`/`Umask`
each fail the spawn with `ProcessError.Unsupported`, never a silent no-op. The helper is loaded only from
a trusted system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and launched by absolute path,
never resolved on `PATH`, so it cannot be hijacked by a planted binary — see
[Hardening → Where the Unix helper binaries come from](hardening.md#where-the-unix-helper-binaries-come-from).
`setpriv` ships there on mainstream Linux; where no trusted directory holds it (macOS/BSD, and non-FHS
layouts such as NixOS) a `Uid`/`Gid`/`Groups` drop fails with a typed `ProcessError.Spawn` naming the
missing helper.

**A Windows-hardened child keeps the caller's identity.** `WindowsRestrictedToken` and
`WindowsIntegrityLevel` reduce *privilege* and *write* access; they do not change **who** the child is.
It still runs as the caller, so it can read whatever the caller can read and open network connections
freely — privilege reduction, not isolation. In particular a secret the caller can read is a secret the
child can read, which is why `Command.EnvClear` and the rest of the perimeter in
[hardening.md](hardening.md) still matter. Two further honest edges: the child's already-open stdio
handles keep working at any integrity level (their access check happened in the parent — by design, or
it could not report anything back), and at `Untrusted` many programs cannot start at all, which surfaces
as that child's own non-zero exit rather than as a ProcessKit error.

**`KillOnParentDeath` reaps only the direct child on Linux, and only up to a set-uid `exec`.** The
opt-in `Command.KillOnParentDeath()` reaps a child when its parent dies *suddenly*, but the guarantee is
platform-specific — `Command.KillOnParentDeathScope()` reports the honest scope. On **Linux** it is
armed as `PR_SET_PDEATHSIG(SIGKILL)` via the `setpriv --pdeathsig` helper (util-linux, loaded from a
trusted system directory rather than `PATH` exactly as the privilege drop is; a helper absent from all of
them is a typed `ProcessError.Spawn`, like the privilege drop) and reaches the **direct child only**: the
parent-death signal is **not inherited** across a `fork`, so a **grandchild** the child spawns is not
covered — with the child's parent gone, nothing reaps its cgroup/pgroup. The kernel also **resets** the
signal when the child `execve`s a **set-uid/set-gid** image, so for a `sudo`-like child it holds only up
to that `exec`. And because the parent-death signal fires when the **spawning thread** (not merely the
process) exits — and ProcessKit spawns on a thread-pool thread .NET may retire while the process lives —
the reap is best-effort and can, in principle, fire early if that thread is reclaimed. The one window the
signal cannot cover — the parent dying *before* it is armed, which would leave it bound to the reaper
that adopted the orphan — is closed separately: the child compares its parent against the pid captured
before the spawn and `SIGKILL`s itself rather than running your program when they differ, so it needs
`/bin/sh` alongside `setpriv` (absent, the spawn fails with a typed `ProcessError.Spawn`). On **Windows**
the whole tree is reaped with no opt-in (the Job Object's `KILL_ON_JOB_CLOSE` fires when the kernel
closes the dead parent's last Job handle during process rundown). On **macOS/BSD** there is no analog,
so a request fails the spawn with `ProcessError.Unsupported` rather than pretending the cleanup happens.

**Windows has a narrow signal mapping.** `Signal.Kill` terminates the Job/run; `Signal.Int` and
`Signal.Term` use best-effort CTRL+BREAK for opted-in console children and/or WM_CLOSE for windowed
children. Other values return `ProcessError.Unsupported`. A custom `Command.StopSignal` — and, on the
same terms, a custom `Command.CancelSignal` — is likewise refused at spawn on Windows instead of being
silently replaced.

**No whole-tree resource limits on macOS/BSD or the Linux process-group fallback.** Limits require
a Windows Job Object or a Linux cgroup v2; the POSIX process-group mechanism has no primitive to
cap a tree's memory, process count, CPU quota, or affinity. `CpuTimeMax` is the exception: POSIX
enforces it per spawned process through `RLIMIT_CPU`. Requesting any other limit there makes `ProcessGroup.Create`
return `ProcessError.ResourceLimit` immediately — an unapplied cap is no protection, so the group
is never created unbounded. See [Running in containers](containers.md#which-mechanism-you-actually-get-in-a-container)
for what this means in practice inside Docker/Kubernetes.

**cgroup v2 needs the *real* cgroup root.** The cgroup v2 mechanism is selected on Linux only when
limits are requested *and* a usable cgroup v2 hierarchy is available. Enabling the controllers a
limit needs (writing the parent's `cgroup.subtree_control`) is permitted by cgroup v2's
"no internal processes" rule only at the real hierarchy root. A cgroup *namespace* root — what an
ordinary container or a systemd session/scope/service sees — does not qualify and the write is
refused (surfacing as `ProcessError.ResourceLimit`). In practice real cgroup limit enforcement
needs a minimal init sitting at the true root; elsewhere a limit-free group simply uses the POSIX
process-group mechanism. Check `ProcessGroup.Mechanism` when the limit must not silently fail to
apply. See [Running in containers](containers.md) for the container-specific consequences —
`PID 1`, minimal/shell-less images, and container-level limits vs `ProcessGroupOptions` limits.

**Output is decoded as UTF-8 by default.** Captured stdout/stderr text is decoded as UTF-8 unless
you say otherwise. A Windows console program that emits a legacy OEM code page will decode incorrectly;
`Command.ConsoleEncoding()` fixes that in one call — it resolves this host's console output code page
(or the system OEM code page when the process has no console), registers the code-page provider itself,
and is a no-op off Windows. To name a different encoding, set it explicitly per stream with
`Command.StdoutEncoding` / `Command.StderrEncoding` (or `Command.Encoding` for both), registering the
code-page provider first (`System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`)
if it is a legacy code page.

**POSIX pgid reuse.** Process-group signalling is inherently best-effort against pid/pgid reuse:
between a child exiting and the group teardown running, the OS can recycle that pgid for an
unrelated process. The backend prunes dead entries on every probe to keep the window minimal, but
it cannot be eliminated at the process-group layer — the cgroup v2 mechanism (used when limits are
requested) closes it, since membership is kernel-enforced.

**In-flight line without a byte cap, and streaming backlog.** `OutputBufferPolicy.MaxBytes` bounds the
in-flight (not-yet-terminated) line too for the buffered verbs — it is force-flushed at the cap, so a
newline-free flood can't outgrow the buffer. Without a byte cap, a single not-yet-terminated line still
grows until end of stream (`MaxBytes` does not apply to the streaming verbs, which are consumer-paced
instead). By default, a streamed consumer (`StdoutLinesAsync` / `OutputEventsAsync`) that stops draining
while the child keeps writing grows the backing channel unbounded. Opt in to
`Command.StreamBuffer`/`StreamBufferPolicy` to cap that channel instead — `Backpressure`,
`DropOldest`/`DropNewest`, or `Error`; see [Streaming](streaming.md#bounding-the-streaming-backlog) — or
pair an untrusted or chatty child with a `Command.Timeout`, which bounds the run and ends the stream at
the deadline either way.

**One consumption per `RunningProcess`.** The streaming verbs compose in one session
(`WaitForLineAsync` → `StdoutLinesAsync` → `FinishAsync`); `OutputStringAsync` / `OutputBytesAsync` / `WaitAsync` / `ProfileAsync` are
each a standalone terminal. The handle enforces this: once one consumer has claimed the output
pipes, a second, conflicting one is refused rather than racing two readers on the same pipe — the
`Result`-returning verbs return `ProcessError.Unsupported`, while `WaitAsync` / `ProfileAsync` / `StdoutLinesAsync`
/ `OutputEventsAsync` throw `InvalidOperationException`. Pick one consumption model per handle.

**Concurrency-friendly I/O.** Waiting on a running child no longer blocks a dedicated thread on either
platform — Windows uses a thread-pool registered wait, Linux uses pidfd/epoll, macOS uses
`EVFILT_PROC` on one shared kqueue, and the remaining POSIX fallback uses an event-driven `SIGCHLD`
registration (see the [changelog](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/CHANGELOG.md)) — and the parent side of a child's pipes is now
genuinely asynchronous on both: Windows uses overlapped named pipes over IOCP, and Linux/macOS wrap
each stdio channel's parent end (an `AF_UNIX` socketpair) in a `Socket`/`NetworkStream` whose reads
and writes complete through the runtime's epoll/kqueue event loop — no thread-pool thread parked per
piped stream. So a very large `WaitAllAsync`, a busy `Supervisor`, or a wide `Exec.outputAll` fan-out
of many *piped* children no longer grows thread-pool occupancy in step with the fleet size. This is
an internal characteristic only — the `Task`-based public API is unchanged.

---

Next: [Hardening untrusted children](hardening.md)
