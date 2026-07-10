# Platform support

[‹ docs index](README.md)

ProcessKit treats platform behaviour as first-class. Every child you start lives inside the
operating system's own containment primitive, so the kill-on-dispose tree guarantee holds on
Windows, Linux, and macOS/BSD alike. Where a mechanism is genuinely weaker than another, the
difference is reported honestly — the active `Mechanism` is queryable and unsupported operations
return a typed `ProcessError`, never a silent downgrade. This page collects every per-OS
mechanism, capability matrix, and caveat in one place.

- [Containment mechanisms](#containment-mechanisms)
- [Target frameworks](#target-frameworks)
- [Capability matrices](#capability-matrices)
- [Caveats](#caveats)

## Containment mechanisms

A `ProcessGroup` wraps one of three OS primitives. Whichever it gets, disposing the group (or the
live `RunningProcess` from a one-shot verb) reaps the whole tree — children, grandchildren, and
anything they spawned — as a single kernel operation.

| `Mechanism` | Platform | How containment works |
|---|---|---|
| `Mechanism.JobObject` | Windows | A Job Object created with kill-on-close. Children are spawned suspended, assigned to the job, then resumed, so even a grandchild forked in the first instant is already contained. Teardown closes the job handle (`KILL_ON_JOB_CLOSE`) or terminates the job. |
| `Mechanism.CgroupV2` | Linux (when resource limits are requested and a usable cgroup v2 root exists) | A private cgroup under the unified hierarchy. Each child is launched through a small `/bin/sh` helper that joins the cgroup (writes its own pid to `cgroup.procs`) before `exec`ing the target in place, so the target is contained on its first instruction and a child it forks immediately inherits the limits; teardown is `cgroup.kill` followed by removing the cgroup directory. |
| `Mechanism.ProcessGroup` | macOS/BSD, and the Linux default when no limits are requested | POSIX process groups. Each spawned child forms its own process-group id (pgid); teardown sends `SIGKILL` to the tracked pgids (`killpg`). |

### When each mechanism is chosen

The selection at `ProcessGroup.Create` is deterministic per platform:

- **Windows** always uses a **Job Object** (`Mechanism.JobObject`), with or without limits. When
  limits are requested they are applied to the job; if they cannot be applied, creation fails with
  `ProcessError.ResourceLimit`.
- **Linux** uses a **cgroup v2** (`Mechanism.CgroupV2`) *only when resource limits are requested
  and cgroup v2 is mounted and usable at the real cgroup-v2 root*. Without limits, Linux uses the
  **POSIX process group** (`Mechanism.ProcessGroup`) — so an ordinary, limit-free group on Linux
  reports `ProcessGroup`, not `CgroupV2`. If limits are requested but no usable cgroup exists,
  creation fails with `ProcessError.ResourceLimit` rather than running unbounded.
- **macOS / BSD** always use a **POSIX process group** (`Mechanism.ProcessGroup`). They have no
  whole-tree limit primitive, so requesting limits fails fast with `ProcessError.ResourceLimit`.

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
    { IsProcessGroup: true } => "POSIX process group — kill-on-dispose, leaders-only members",
    _                        => "unknown mechanism",
});
```

The `Mechanism.IsJobObject` / `IsCgroupV2` / `IsProcessGroup` properties are the same check in
boolean form, convenient from C#.

## Target frameworks

ProcessKit targets **.NET 8.0** and **.NET 10.0**, and is usable from F# and C# alike. The
containment work is done through platform P/Invoke (Win32 for the Job Object, the cgroup
filesystem and `libc` on Unix), so the supported runtime set is Windows, Linux, and macOS/BSD —
the desktop and server platforms these target frameworks run on.

## Capability matrices

In the matrices below the columns are the three mechanisms. The **POSIX process group** column
covers macOS/BSD *and* the Linux default (a limit-free group), since they share one backend.
Legend: ✅ full support · 🟡 supported with a documented qualification · ❌ not available.

**Whole-tree teardown**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| Kill-on-dispose, whole tree | ✅ | ✅ | ✅ |
| Graceful `ShutdownAsync` (TERM → grace → KILL) | 🟡 atomic kill only | ✅ | ✅ |

`ShutdownAsync(grace)` on Windows has no per-job graceful signal, so it is the atomic Job terminate;
on the Unix mechanisms it is `SIGTERM`, then a grace window, then `SIGKILL`.

**Signals (`Signal`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Signal.Kill` | ✅ maps to Job terminate | ✅ | ✅ |
| Any other signal (`Term`, `Int`, `Hup`, `Quit`, `Usr1`, `Usr2`, `Other n`) | ❌ `ProcessError.Unsupported` | ✅ | ✅ |

**Suspend / resume**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Suspend` / `Resume` the whole tree | ✅ per-process freeze across the job | ✅ `cgroup.freeze` | ✅ `SIGSTOP` / `SIGCONT` |

**Member listing (`Members`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `Members()` snapshot | ✅ whole tree | ✅ whole tree | 🟡 tracked group leaders only |

**Stats (`Stats` / `SampleStatsAsync`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `ActiveProcessCount` | ✅ | ✅ | ✅ |
| `TotalCpuTime` + `PeakMemoryBytes` | ✅ | ✅ | ❌ active count only |

On the POSIX process-group mechanism, `ProcessGroupStats.TotalCpuTime` and `PeakMemoryBytes` are
`None` — only the live process count is available. Windows reads Job Object accounting; the cgroup
mechanism reads `cpu.stat` / `memory.peak`.

**Resource limits (`ProcessGroupOptions`)**

| Capability | Windows (Job Object) | Linux cgroup v2 | POSIX process group |
|---|:---:|:---:|:---:|
| `WithMemoryMax` (whole tree) | ✅ | ✅ | ❌ `ProcessError.ResourceLimit` |
| `WithMaxProcesses` | ✅ | ✅ | ❌ `ProcessError.ResourceLimit` |
| `WithCpuQuota` | 🟡 approximate | ✅ | ❌ `ProcessError.ResourceLimit` |

`WithCpuQuota` is a fraction of a single core (`0.5` = half a core, `2.0` = two cores). On Windows
it is converted against the host's CPU count and is approximate. Because limits need a real
limit-capable container, the POSIX process-group mechanism cannot enforce any of them — requesting
limits where none can apply fails at creation with `ProcessError.ResourceLimit` rather than
returning a silently-unbounded group.

Everything not listed here — capture, line streaming, interactive stdin, encodings, buffer
policies, timeouts, retry, pipelines, supervision, readiness probes, cancellation, and the
testing seams — is platform-agnostic and behaves identically everywhere. See [commands.md](commands.md),
[streaming.md](streaming.md), [pipelines.md](pipelines.md), [supervision.md](supervision.md),
and [testing.md](testing.md).

## Caveats

The honest fine print — mostly consequences of OS semantics, plus a few tracked internal
constraints that do not change the public surface.

**POSIX process groups: a `setsid` child can escape.** The process-group mechanism tracks each
child's pgid, and teardown signals those pgids. A descendant that deliberately starts a new
session (a `setsid` call) gets a fresh process group that the parent group does not track, so it
can outlive the teardown. This is the genuine weakness of the process-group mechanism; it is why
`ProcessGroup.Mechanism` is reported rather than papered over. The Job Object and cgroup v2
mechanisms have no such hole — membership is enforced by the kernel container, not by group
bookkeeping. When this matters, check the active mechanism.

**Windows delivers only `Signal.Kill`.** Windows has no general signal abstraction. `Signal.Kill`
maps to the Job Object terminate; every other `Signal` value (`Term`, `Int`, `Hup`, `Quit`,
`Usr1`, `Usr2`, `Other n`) returns `ProcessError.Unsupported` on Windows. Portable code that needs
a cooperative stop should drive the child another way (a known stdin command, a control file) and
fall back to `ShutdownAsync` / `Signal.Kill` for the hard stop. On the Unix mechanisms the full set is
delivered.

**No whole-tree resource limits on macOS/BSD or the Linux process-group fallback.** Limits require
a Windows Job Object or a Linux cgroup v2; the POSIX process-group mechanism has no primitive to
cap a tree's memory, process count, or CPU. Requesting any limit there makes `ProcessGroup.Create`
return `ProcessError.ResourceLimit` immediately — an unapplied cap is no protection, so the group
is never created unbounded.

**cgroup v2 needs the *real* cgroup root.** The cgroup v2 mechanism is selected on Linux only when
limits are requested *and* a usable cgroup v2 hierarchy is available. Enabling the controllers a
limit needs (writing the parent's `cgroup.subtree_control`) is permitted by cgroup v2's
"no internal processes" rule only at the real hierarchy root. A cgroup *namespace* root — what an
ordinary container or a systemd session/scope/service sees — does not qualify and the write is
refused (surfacing as `ProcessError.ResourceLimit`). In practice real cgroup limit enforcement
needs a minimal init sitting at the true root; elsewhere a limit-free group simply uses the POSIX
process-group mechanism. Check `ProcessGroup.Mechanism` when the limit must not silently fail to
apply.

**Output is decoded as UTF-8 by default.** Captured stdout/stderr text is decoded as UTF-8 unless
you say otherwise. A Windows console program that emits a legacy OEM code page will mis-decode;
set the encoding explicitly per stream with `Command.StdoutEncoding` / `Command.StderrEncoding`
(or `Command.Encoding` for both). For legacy code pages, register the code-page provider first
(`System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`), then pass the
`Encoding` you need.

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
platform — Windows uses a thread-pool registered wait, and POSIX uses an event-driven `SIGCHLD`
registration (see [`CHANGELOG.md`](CHANGELOG.md)) — and the parent side of a child's pipes is now
genuinely asynchronous on both: Windows uses overlapped named pipes over IOCP, and Linux/macOS wrap
each stdio channel's parent end (an `AF_UNIX` socketpair) in a `Socket`/`NetworkStream` whose reads
and writes complete through the runtime's epoll/kqueue event loop — no thread-pool thread parked per
piped stream. So a very large `WaitAllAsync`, a busy `Supervisor`, or a wide `Exec.outputAll` fan-out
of many *piped* children no longer grows thread-pool occupancy in step with the fleet size. This is
an internal characteristic only — the `Task`-based public API is unchanged.

---

Next: [Process groups](process-groups.md) · [Running commands](commands.md) · [Streaming & interactive I/O](streaming.md) · [docs index](README.md)
