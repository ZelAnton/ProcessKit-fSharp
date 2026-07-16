# Platform support

[Previous: Overview](./)

ProcessKit treats platform behaviour as first-class. Every child you start lives inside the
operating system's own containment primitive, so the kill-on-dispose tree guarantee holds on
Windows, Linux, and macOS/BSD alike. Where a mechanism is genuinely weaker than another, the
difference is reported honestly — the active `Mechanism` is queryable and unsupported operations
return a typed `ProcessError`, never a silent downgrade. This page collects every per-OS
mechanism, capability matrix, and caveat in one place.

- [Containment mechanisms](#containment-mechanisms)
- [Target frameworks](#target-frameworks)
- [Trimming and NativeAOT](#trimming-and-nativeaot)
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
| `ProcessKit` | ✅ | ✅ | Containment is platform P/Invoke with no reflection, dynamic codegen, or reflection-backed `printf`/`%A` on any path except the annotated `OutputJsonAsync` verb (see below). |
| `ProcessKit.Extensions.DependencyInjection` | ✅ | ✅ | Factory-based registration; the `AddProcessKit`/`AddProcessKitGroup` **`IConfiguration`** overloads are the one exception (see below). |
| `ProcessKit.Extensions.Hosting` | ✅ | ✅ | Factory-based DI plus an `IHostedService` wrapper; options come from the AOT-safe `Activator.CreateInstance<T>()` path. |
| `ProcessKit.Testing` | ❌ | ❌ | Not trim/AOT-safe by design — see the boundary below. This is a **test-only** package, referenced from test projects that are not themselves trimmed/AOT-published. |

**The one annotated exception (DI).** `AddProcessKit(IConfiguration)` and `AddProcessKitGroup(IConfiguration)`
bind `ProcessKitOptions` from configuration by reflection, which is not trim/AOT-safe. Both carry
`[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`, so a consumer that calls them from a trimmed/AOT app
gets a precise warning pointing at the overload — exactly as Microsoft's own DI/options packages behave. Use
the `Action<ProcessKitOptions>` overload (or bind configuration yourself and call `configure`) from an AOT app.

**The `OutputJsonAsync` boundary (core).** The typed JSON verb (`Command.OutputJsonAsync<'T>`,
`IProcessRunner.OutputJsonAsync<'T>`, `CliClient.OutputJsonAsync<'T>`, `Pipeline.OutputJsonAsync<'T>`, and the
underlying `Runner.outputJson`) deserializes stdout with the reflection-based
`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)` overload, so — like the DI `IConfiguration`
overloads above — all five surfaces carry `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. Under
NativeAOT, reflection-based `System.Text.Json` deserialization of an arbitrary caller-supplied `'T` is not
supported by default (`JsonSerializer.IsReflectionEnabledByDefault = false`), so calling this verb from a
NativeAOT app without a source-generated resolver fails at run time rather than silently misbehaving. To use
it from a trimmed/AOT app, pass a `JsonSerializerOptions` whose `TypeInfoResolver` comes from a
source-generated `JsonSerializerContext` for `'T` (F# cannot itself author the `System.Text.Json` source
generator — it is a Roslyn/C# generator the F# compiler does not run — but a C# project's generated context
can be passed in from F# or C# alike); otherwise avoid the verb in an AOT-published app. The `aot-smoke` CI
job (below) does not call this verb, so it stays unaffected by this boundary.

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

**How this is validated.** [`samples/FSharp.NativeAot`](../samples/FSharp.NativeAot) is a minimal consumer of
`ProcessKit` **and** `ProcessKit.Extensions.DependencyInjection`, published with `PublishAot=true` and run by
the `aot-smoke` job in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) on both `linux-x64` (POSIX
process-group backend) and `win-x64` (Windows Job Object backend). It spawns a child, captures a non-zero
exit as an honest result, runs a child inside a kill-on-dispose `ProcessGroup`, and runs a child through a
DI-resolved `IProcessRunner` (`AddProcessKit`); the job fails if ilc attributes any warning to a `ProcessKit*`
assembly or if the native binary exits non-zero. So the compatibility above is exercised in a real
ahead-of-time-compiled image, not merely declared in metadata. (`ProcessKit.Extensions.Hosting` shares the
same factory-based, reflection-free pattern; its declaration rests on that analysis rather than a running
hosted-service image in this smoke.)

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

**Unix privilege drop clears supplementary groups unless you set them.** A `Uid`/`Gid`/`User` drop
runs through the `setpriv` helper (util-linux), which by default *clears* the parent's supplementary
groups so the child never keeps root's — but a child dropped to a service user then lacks that user's
group memberships (`docker`, `video`, `adm`, …). Pass `Command.Groups(gids)` to set the child's
supplementary groups explicitly (mapped to `setpriv --groups`); it is honoured only alongside a
`Uid`/`Gid` drop, so requesting it without one fails with `ProcessError.Spawn` rather than being
silently ignored. The whole family is **Unix-only**: on Windows `Uid`/`Gid`/`Groups`/`Setsid`/`Umask`
each fail the spawn with `ProcessError.Unsupported`, never a silent no-op. `setpriv` ships on mainstream
Linux; where it is absent (macOS/BSD) a `Uid`/`Gid`/`Groups` drop fails with a typed `ProcessError.Spawn`
naming the missing helper.

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

Next: [Running in containers](containers.md)
