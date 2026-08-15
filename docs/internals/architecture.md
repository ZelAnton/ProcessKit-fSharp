# ProcessKit internal architecture

This guide is for contributors changing ProcessKit's internals. It assumes familiarity with the public command and streaming APIs. For the consumer-facing streaming contract, see [Streaming](../streaming.md).

The central invariant is stronger than “kill the process”: every started child is placed in an operating-system containment unit, and dropping the owner tears down the contained tree. Pipe draining, waiting, and containment therefore have to be designed as one lifecycle.

## Module map and compilation order

F# resolves declarations strictly from top to bottom. The `<Compile Include>` list in `ProcessKit.fsproj` is the dependency graph, not presentation order: a file may use only declarations in earlier files. Moving `Backend.fs` above the native files, for example, would make its native references unavailable; moving a public verb below a consumer of that verb has the same effect.

The files currently compile in this exact order. The headings are architectural groupings, not additional build boundaries.

### Core types, errors, and results

1. `ProcessError.fs` — error union and error helpers.
2. `ProcessException.fs` — exception wrapper used where an async stream must fault.
3. `ResultExtensions.fs` — .NET-friendly `Result` extensions.
4. `Outcome.fs` — process termination outcome.
5. `Diagnostics.fs` — public diagnostics names and event IDs.
6. `Log.fs` — internal structured lifecycle logging.
7. `Diag.fs` — activities and metrics.
8. `RunTelemetryScope.fs` — exactly-once run telemetry lifetime.
9. `Mechanism.fs` — selected containment mechanism.
10. `KillOnParentDeathScope.fs` — per-platform scope of parent-death cleanup.
11. `Signal.fs` — portable signal model.
12. `CgroupCpuMax.fs` — cgroup v2 `cpu.max` quota formatting and parsing.
13. `Limits.fs` — resource and process-group options.
14. `ProcessResult.fs` — captured result.
15. `TryParser.fs` — .NET try-parse adapter.
16. `OutputPolicy.fs` — buffered and streaming overflow policies.
17. `OutputEvent.fs` — stdout/stderr event model.
18. `Finished.fs` — finish result.
19. `Stats.fs` — group/run statistics and samplers.
20. `Stdin.fs` — stdin source model.
21. `PostKillReap.fs` — bounded post-kill reap budget and the ledger that adopts an unfinished wait.
22. `PostExitDrain.fs` — bounded post-exit output drain and the severable read end it cuts.
23. `Timeouts.fs` — timeout normalization.
24. `Backoff.fs` — exponential-backoff and jitter math for retries and supervision.
25. `Priority.fs` — priority model and native mapping.
26. `LineTerminator.fs` — line-ending rules.
27. `RotatingFileSink.fs` — size-rotating tee sink for long-lived logs.
28. `Command.fs` — immutable command configuration and builder API.
29. `MemberInfo.fs` — per-member identity snapshot of a contained tree.
30. `ReportJson.fs` — write-only JSON projection of results, stats, run profiles, and member snapshots.
31. `DetachedProcess.fs` — pid + start-time descriptor of a launch made outside containment.

### Native and platform layer

32. `Native.Common.fs` — shared spawned-process representation and signal-delivery result.
33. `Native.Windows.fs` — Win32 process, pipe, Job Object, console-control, console code page, limits, and accounting calls.
34. `Native.Posix.fs` — `posix_spawn`, process groups, signals, and `waitpid` registry.
35. `Native.Cgroup.fs` — Linux cgroup v2 discovery, controls, membership, and accounting.
36. `Capabilities.fs` — containment mechanism selection and the three-valued capability snapshot probed from it.
37. `ConsoleEncoding.fs` — console/OEM code-page resolution for decoding legacy child output.

### Backend, pump, and channels

38. `Backend.fs` — containment interface and its three implementations.
39. `Pump.fs` — pipe decoding, line/raw buffering, tees, and stdin pumping.
40. `StreamChannel.fs` — streaming channel construction and full-mode behavior.
41. `ProcessStdin.fs` — interactive stdin handle.
42. `ReadinessProbe.fs` — readiness polling.
43. `RunningHost.fs` — the spawned-host contract a live handle is built from.
44. `ConsumptionGate.fs` — consumption-claim state machine and terminal-wait ledger of one handle.
45. `RunTerminal.fs` — one handle's shared terminal waits, bounds, tokens, and teardown.
46. `ExpectWindow.fs` — bounded expect window and ANSI filtering for interactive sessions.
47. `OutputSessions.fs` — one handle's output pumps, streaming channels, and session shapes.
48. `ReadinessRace.fs` — readiness probing raced against the child's own exit.
49. `RunningProcess.fs` — the public live-handle facade over the six files above: every verb, composed from the claim gate, the terminal waits, and the output sessions.

### Runner and verbs

50. `ContentLengthSession.fs` — `Content-Length` framed byte transport over a live handle.
51. `JsonRpcSession.fs` — typed JSON-RPC 2.0 conversation over that framed transport.
52. `PtySession.fs` — expect-style interaction over a live handle.
53. `IProcessRunner.fs` — injectable runner seam.
54. `Runner.fs` — capture primitives and reusable verbs.
55. `ProcessRunnerExtensions.fs` — .NET extensions for custom runners.
56. `DelegatingProcessRunner.fs` — runner decorator base.
57. `ProcessGroup.fs` — containment owner and shared-group runner.
58. `JobRunner.fs` — default private-group runner.
59. `CommandVerbs.fs` — default-runner `Command` extensions.
60. `PipelineRunner.fs` — internal pipeline execution.
61. `Pipeline.fs` — pipeline public API.
62. `Supervisor.fs` — restart supervision.
63. `CliClient.fs` — configured command client.
64. `Exec.fs` — concise execution entry points.

When adding a file, place it after everything it consumes and before everything that consumes it. Alphabetical sorting or SDK globbing would silently destroy this ordering model.

## Data flow: spawn, pump, verb

The default path begins at a `Command` verb. `CommandVerbs` selects the shared default `JobRunner`; `Runner` supplies reusable verb semantics; `JobRunner` creates a private `ProcessGroup`. An explicitly created group follows the same lower stack but owns several children.

```text
consumer
   |
   v
Command verb / Runner -------- cancellation, timeout, retry
   |
   v
JobRunner (private group) or ProcessGroup (shared group)
   |
   +--> ProcessGroup.SpawnInto
   |       |
   |       +--> IContainmentBackend.Spawn --> Native.Windows/Posix --> OS process + pipes
   |       `--> IContainmentBackend.Track --> Job / pgid / cgroup membership
   |
   +--> RunningProcess
           |
           +--> Pump.readLines ------> LineBuffer (capture verb)
           |         `--------------> StreamChannel (live streaming verb)
           +--> stdin pump ----------> child stdin
           `--> Wait ----------------> backend.Wait --> OS wait/reap
                       |
                       v
                 Outcome / ProcessResult / public verb result

signals: consumer -> ProcessGroup.Signal -> backend -> Job/console event, killpg, or cgroup members
termination: timeout/cancel/dispose -> KillChild or KillTree -> Wait/reap -> Release/HardRelease
```

`Spawn` returns OS handles and optional pipe streams but does not finish the ownership transaction. `Track` establishes the backend's teardown record (and, for cgroup v2, migrates the PID). Only then may `RunningProcess` expose the child. A track failure must leave no live uncontained child.

Output pumps run concurrently with the wait. They must continuously drain piped stdout and stderr, even when capture retention is full, or the child can block in an OS pipe and never reach exit. Capture verbs accumulate output in `LineBuffer` or `RawBuffer`; streaming verbs feed channels. A channel configured for backpressure is the deliberate exception: it lets the consumer pace the pump and therefore may eventually pace the child.

Interactive raw sessions share a fourth `Consumption` state alongside the buffered and two line-streaming ones. `RunningProcess.StartInteractiveSession`, the claim behind `PtySession`, drains the pipes through `Pump.readTextUntilDone` instead of `readLines`, feeding decoded chunks into an `ExpectWindow` (a bounded sliding window plus an optional transcript). A terminal prompt carries no line terminator, so a line pump would hold it until a newline that only arrives after the input the prompt is waiting for. `StartContentLengthSession` instead hands stdout to a byte parser that validates CRLF headers and emits exact payload frames while stderr drains independently. Both sessions retain the single claim, memoized exit wait reused by `ExitTask`, kill-on-pump-fault, teardown-race classification, and byte-exact tees; line-shaped observers have nothing to observe. A `PtySession` waiter's whole verdict — matched, still waiting, or ended — remains one locked `ExpectWindow` step, never a match test followed by a separate end-of-output test that could lose a final match racing EOF.

Protocol layers stack on that framed claim rather than duplicating it. `JsonRpcSession` takes no claim of its own: it *creates* the one `ContentLengthSession` over the handle, enumerates its `FramesAsync()` exactly once from a detached `backgroundTask` router, and never exposes it — a second frame reader would tear one peer's messages between two consumers. The router is the only place protocol state is mutated: it decodes each frame, completes the waiter registered under that `id`, or queues a notification/peer request onto a bounded drop-oldest channel (dropping is counted, and blocking there would stall the very answers pending requests await). Waiter registration and the session's terminal error are decided under one lock, so a request racing the peer's exit either joins the routing table and is failed by it or fails immediately — never waits for an answer that can no longer arrive. Being a `backgroundTask` matters for the same reason it does in `Pump.feedStdin`: a caller may block on a request from a single-threaded `SynchronizationContext`, and a router that captured it would be waiting to resume on the thread blocked on it. A per-request deadline is armed *before* the frame is written and reused for the wait that follows, so one budget covers the whole call: a peer that stops reading its own stdin blocks the write once the pipe buffer fills, which is precisely the half a wait-only deadline would leave unbounded. Because such an interrupted write may have delivered a partial frame — and no peer can resynchronize from one — it becomes the session's terminal error instead of a per-call one.

A stream is *not* always a parent pipe. `StdioMode.Null`/`Inherit`, and `Command.StdoutToFile`/`StderrToFile`, hand the child a std handle/fd that never round-trips through the parent — for a file redirect it is an inheritable file handle in `STARTUPINFO` (Windows) or a file fd installed by a `posix_spawn` file action (POSIX), opened on the parent and dropped there right after the spawn, so the child owns it alone and writes the file directly with **no** parent pump. `Spawn` returns `None` for that stream, so no pump, capture buffer, or channel is created for it, and its capture verbs observe nothing — the redirected output lives only in the file, which keeps growing after the parent (or a pump draining a pipe) is gone. The builder rejects combining a file redirect with anything that needs a parent-side view of the same stream (the tees, per-line handlers, `MergeStderr`, `Pty`).

Natural exit, explicit kill, timeout, cancellation, and disposal converge on waiting/reaping and teardown. A verb may transform the resulting `Outcome`, but it must not bypass ownership cleanup.

## Containment backend contract

`Backend.fs` defines the complete internal contract as follows (comments omitted here):

```fsharp
type internal IContainmentBackend =
    abstract Mechanism: Mechanism
    abstract Spawn: Command -> Result<Native.Common.Spawned, ProcessError>
    abstract Track: Native.Common.Spawned -> Result<unit, ProcessError>
    abstract Adopt: int -> Result<unit, ProcessError>
    abstract Release: Native.Common.Spawned -> unit
    abstract Wait: nativeint -> Task<Outcome>
    abstract PidOf: Native.Common.Spawned -> int option
    abstract KillChild: Native.Common.Spawned -> unit
    abstract KillTree: unit -> unit
    abstract GracefulKillTree: Signal -> TimeSpan -> Task
    abstract SignalChild: Native.Common.Spawned * Signal -> Result<unit, ProcessError>
    abstract Members: unit -> Result<int list, ProcessError>
    abstract Signal: Signal -> Result<unit, ProcessError>
    abstract Suspend: unit -> Result<unit, ProcessError>
    abstract Resume: unit -> Result<unit, ProcessError>
    abstract Stats: unit -> Result<ProcessGroupStats, ProcessError>
    abstract MemberStats: unit -> Result<MemberStats list, ProcessError>
    abstract UpdateLimits: ResourceLimits -> Result<unit, ProcessError>
    abstract HardRelease: unit -> unit
```

The current interface has 19 abstract members:

- `Mechanism` identifies the primitive honestly.
- `Spawn` starts a child, initially not in the backend's tracking collection.
- `Track` completes containment/tracking; on error it is responsible for killing and reaping the child.
- `Adopt` places an already-running **external** process (started outside ProcessKit) into the container by pid, so it obeys the same whole-tree rules as a `Track`ed child. Because it is *not* our child it is deliberately kept out of the reap ledger `Track` feeds (never `waitpid`ed, never `killpg`ed — see K-016); the container primitive alone contains and kills it (Windows `AssignProcessToJobObject` → `KILL_ON_JOB_CLOSE`; cgroup v2 write to `cgroup.procs` → `cgroup.kill`), and it joins `Members`/`Stats` for free because those read the live Job / `cgroup.procs`. The POSIX process-group backend cannot relocate a foreign process (`setpgid` only moves our own children before `exec`) and returns `ProcessError.Unsupported`; a supported backend returns a typed `ProcessError.Adopt` on a runtime failure (dead/gone pid, missing rights, or a process already in an incompatible Job).
- `Release` stops tracking a child already reaped by a normal run.
- `Wait` waits for and decodes one child outcome.
- `PidOf` retrieves a known PID.
- `KillChild` kills one child's containment subtree where applicable.
- `KillTree` immediately kills the whole container without releasing it.
- `GracefulKillTree` requests termination, waits for the grace period, then escalates where supported.
- `Members` snapshots membership.
- `Signal` broadcasts a signal.
- `Suspend` and `Resume` control the tree.
- `Stats` snapshots resource use.
- `MemberStats` snapshots per-member CPU, resident-memory, and optional I/O counters. The backend owns
  membership enumeration and the native identity gate: a vanished member is omitted, an unknown or
  changed POSIX start-time token is excluded, and Windows binds the Job PID snapshot to a pre-sampling
  process identity before opening the query handle. A query-inaccessible Windows member is retained with
  `None` metrics only when a fresh Job membership and identity check still confirm that generation. Linux
  cgroup sampling preserves adopted and descendant members: tracked/adopted leaders use pinned tokens,
  while other members use an identity captured from the cgroup snapshot and checked again after reading.
  The public `ProcessGroup` call runs this operation under the lifecycle gate, so it cannot race teardown.
- `UpdateLimits` re-applies a full replacement resource-limit set to the live container (Job Object / cgroup v2); the process-group mechanism has no primitive to update and returns `ProcessError.ResourceLimit`. Because the caps land through several sequential native writes, a limit-capable backend captures the container's prior caps and best-effort restores them if a write fails partway, so an `Error` leaves the live container on the previous set — never a silent mix that `Options.Limits` would misreport (only an also-failed restore is indeterminate, and its `ProcessError.ResourceLimit` message says so).
- `HardRelease` performs the once-only hard teardown and frees the container.

`ProcessGroup.MembersInfo` is deliberately **not** a backend member: it layers an enriched, point-in-time snapshot over `Members`. `ProcessGroup` takes the pid snapshot under the lifecycle lock (exactly as `Members` does), then enriches each pid **off** the lock through platform metadata — one `CreateToolhelp32Snapshot` walk on Windows (parent pid + image name), a per-pid `/proc/<pid>/stat` read on Linux, `proc_pidinfo` on macOS — plus `System.Diagnostics.Process.StartTime` for the start time (the shared `Native.Common.readProcessStartTime`). A pid whose metadata can no longer be read has exited and is omitted rather than fabricated, and the command line and environment are never read on any path. Enrichment follows the OS, not the mechanism (both Linux backends read `/proc`), so it needed no new `IContainmentBackend` member and no test-double fan-out.

`TrackedChildren<'T>` serializes `Add`, `Remove`, `Snapshot`, and `Drain` behind one lock. `Drain` is essential during teardown: it atomically transfers ownership of every recorded child to the teardown path. A mere snapshot would allow a racing cleanup to act twice on a recycled PID or handle.

`GracefulTeardown.poll` supplies the graceful-stop shape shared by all three backends: post the soft stop, poll with each delay bounded by the lesser of 50 ms and the remaining grace budget until empty or the grace period expires, then force-kill survivors. POSIX/cgroup delivery uses the command or group's configured `StopSignal` (default `Signal.Term`); Windows posts `WM_CLOSE` to members' top-level windows for its default soft phase. The poll-then-unconditional-hard-kill escalation is otherwise identical. `PosixReap.leader` pairs `killpg` with `waitpid`; killing a process group does not reap the direct child that ProcessKit owns.

The implementation is split across four files. `Native.Windows.fs`, `Native.Posix.fs`, and `Native.Cgroup.fs` provide the OS-specific operations; `Backend.fs` composes them into `JobObjectBackend`, `ProcessGroupBackend`, and `CgroupBackend`, respectively, behind `IContainmentBackend`.

### Windows Job Object

`JobObjectBackend` owns one Job handle plus child process handles. `Native.Windows.spawnWindows` creates the process suspended, assigns it to the Job, and only then resumes it. This prevents a child from escaping by forking before assignment. The Job has `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; closing the Job is the final kill-on-drop guarantee. Requested memory, process-count, CPU, and CPU-affinity limits are applied through Job Object limit APIs, and `UpdateLimits` re-issues the same `SetInformationJobObject` calls on the live Job to replace the caps in force (a dimension now `None` is written back as unbounded, and the CPU rate control is disabled when no quota is set) — run synchronously under the lifecycle lock like the other control verbs, so it needs no handle duplication. The caps land in three writes — the UI restrictions, the extended-limit block (memory + active-process + affinity), then the CPU rate block — so each prior block is captured (`QueryInformationJobObject`) up front and best-effort restored if a later write fails after it, keeping a failed live update on the previous set rather than a silent mix.

`ResourceLimits.WithIoMax` adds a fourth Job-specific control: `SetIoRateControlInformationJobObject` applies one aggregate bandwidth ceiling and one aggregate IOPS ceiling for an NT volume target. The API therefore requires matching read/write byte rates and matching read/write operation rates; `None` removes the policy. Live target or rate replacement is serialized under the same group lock, and a failed native write restores the previous Job I/O policy before returning `ProcessError.ResourceLimit`. If the platform lacks the Job I/O API, the backend returns `ProcessError.Unsupported` rather than claiming an unenforced limit.

The CPU-affinity pin (`ProcessGroupOptions.WithCpuAffinity`) needs no rollback machinery of its own: it is `JOBOBJECT_BASIC_LIMIT_INFORMATION.Affinity` plus the `JOB_OBJECT_LIMIT_AFFINITY` flag, so it rides inside the extended-limit block the kernel applies whole or not at all, and is already covered by that block's captured prior. Clearing it is likewise just the flag going unset in the next replacing write — there is no separate "disable" call, and so no analogue of the CPU rate control's `ERROR_INVALID_PARAMETER`-on-already-disabled case. What is specific to affinity is a representation ceiling: the mask is one pointer-sized word scoped to a single processor group, so `Native.Windows.windowsAffinityMask` refuses a core index at or beyond that width (64 on x64) rather than let the shift wrap onto a different core. Because that refusal must not leave a half-updated Job, the whole extended-limit block is now resolved as a pure value first (`extendedLimitBlockFor`), ahead of even the UI-restriction write, so an inexpressible pin errors with the Job untouched. Win32 also rejects a mask that is not a subset of the calling process's own affinity with a bare `ERROR_INVALID_PARAMETER`, so the apply path annotates that failure with the cores that were requested.

The UI restrictions (`ProcessGroupOptions.WithUiRestrictions`, a `JOBOBJECT_BASIC_UI_RESTRICTIONS` flags word denying the tree the clipboard, desktops, display/system parameters, global atoms, and `ExitWindows`) are deliberately written **first** and only when they differ from what the Job already carries: a failure has then changed nothing, so the honest "previous set still wholly in force" position holds without a rollback, and the overwhelmingly common case (no restrictions requested on a Job that has none) issues no native call at all. They are the one limit dimension with no cgroup v2 counterpart, so both POSIX backends refuse a limit set carrying them with `ProcessError.Unsupported` — not the `ResourceLimit` used when a cap that exists in principle merely cannot be enforced — and `ProcessGroup.Create` applies the same gate before it picks a non-Job mechanism. `ResourceLimits.Any` counts them, so a group asking only for restrictions still takes the limit-capable path.

### Windows token hardening

`Command.WindowsRestrictedToken` and `Command.WindowsIntegrityLevel` are the Windows counterpart of the POSIX `Uid`/`Gid`/`Groups` drop, and they live entirely inside `Native.Windows`. Instead of changing who the child runs as (Windows has no `setuid`), the spawn derives a weakened copy of *this process's own* primary token — `CreateRestrictedToken` with `DISABLE_MAX_PRIVILEGE` for the privilege strip, `SetTokenInformation(TokenIntegrityLevel, ...)` with a well-known `S-1-16-*` label for the integrity drop, or both on one token — and starts the child through `CreateProcessAsUserW` rather than `CreateProcessW`. Deriving from the caller's own token is what keeps this unprivileged; a host that refuses anyway surfaces as a typed `ProcessError.Spawn` naming the refusal, never a silent unhardened spawn.

Both Windows spawn paths (the contained `spawnWindowsCore` and the detached `spawnDetachedWindows`) go through one seam, `createChildProcess`, which owns the token's whole lifetime: built immediately before the create call and closed immediately after it, with no throwing code in between, so it can neither leak nor outlive the spawn that consumes it. That single seam is also why hardening cannot be honoured on one path and quietly dropped on the other. `CreateProcessAsUserW`'s `lpCommandLine` is bound as a `nativeint` into the same private unmanaged buffer the `CreateProcessW` path already allocates, for the reason T-198 established: the OS may patch that buffer in place, so it must never be a managed string. The ConPTY path is the deliberate exception — it spawns through `CreateProcessExtended`, which does not take a token — so `Pty` combined with either knob is rejected at the builder boundary and, defensively, again in `spawnWindows`. On POSIX all three spawn entry points refuse both knobs with `ProcessError.Unsupported` before any spawn work, the exact mirror of the Unix-knob gate `spawnWindows` applies.

`Signal.Kill` terminates the Job atomically. `Signal.Int` and `Signal.Term` are not Unix signals: they map to a best-effort soft stop built from two complementary, individually pid-targeted deliveries. For children explicitly started with `Command.WindowsCtrlSignals()`, ProcessKit sends `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` — the child has `CREATE_NEW_PROCESS_GROUP`, so its PID is its console group ID and only CTRL+BREAK (not CTRL+C) can be group-targeted; this reaches *console* children. ConPTY children receive that creation flag unconditionally to isolate the pseudoconsole child from stray shared-console CTRL+C broadcasts, but only an explicit `WindowsCtrlSignals()` registers the leader in `ctrlGroups` for this directed API. In addition, a `WM_CLOSE` is posted to the top-level windows of every member that has one (located by pid via `GetWindowThreadProcessId`, so no window outside the group is hit); this reaches *windowed* children (Electron/GUI tools) that have no console to signal. Delivery is a best-effort `Ok` when either mechanism reaches at least one member — delivery, not the child's compliance (a child may install a handler or a window may veto the close). It returns `Unsupported` only when the group has *neither* a CTRL-capable child *nor* any windowed member, and never silently becomes a hard kill. Other signals are unsupported.

Tracked process handles pin PID identity until release, preventing a stored console-group ID (or the pid used to locate a member's windows) from becoming a wrong target. Windows has no Job-wide soft signal, but `GracefulKillTree` still runs a best-effort soft phase around the same poll shape the Unix backends use: it posts a `WM_CLOSE` to every member's top-level windows, polls up to the grace period for the tree to drain, then *unconditionally* terminates the Job whatever survives. The hard kill is never removed or weakened — a child with no window, or one that vetoes the close, is force-killed exactly as before — and `grace = 0` skips the wait and hard-kills at once.

### POSIX process groups

`ProcessGroupBackend` is used on macOS/BSD and on Linux when whole-tree limits are not requested. Every `posix_spawn` child becomes leader of its own process group (`pgid = pid`); one ProcessKit group may therefore track several pgids. `killpg` reaches descendants that remain in each group. Signals, `SIGSTOP`, and `SIGCONT` are broadcast per tracked pgid. Tracking records each leader's start-time identity so a recycled pgid is never signalled; graceful shutdown snapshots both the pgids and those identity tokens before its off-lock configured-soft-signal → poll → `SIGKILL` sequence, preserving that guard if concurrent teardown removes the live tracking entry. One liveness + identity choke (`Native.Posix.trackedTarget`) answers *where* every operation goes rather than only whether the target is still ours, because a `Command.Pty` child is not yet a process-group leader when its spawn returns — its `setsid --ctty` helper calls `setsid()` after `exec`, so `killpg(pid, 0)` reports `ESRCH` for that window. An `ESRCH` from the group probe alone is therefore not proof the child is gone: the exact pid is probed too, and while it is still our identity-matched child the pgid stays tracked and the operation reaches that pid. Because the verdict is probed a moment *before* the delivery, the child may win the race to `setsid()` in between, so the delivery still considers the group as well: a hard kill in that window SIGKILLs the pid **and** sweeps `killpg` behind it (so a subtree forked in between cannot be orphaned by a teardown that erases the record immediately afterwards), while an observable signal tries the group first and falls back to the pid only on its `ESRCH`, so nothing is ever signalled twice. A group numbered `pid` can only be created by the process `pid` itself, which the probe just proved is our own live, identity-matched child, so that sweep cannot reach a stranger. Once the group exists every delivery goes back through `killpg` so the whole subtree is reached.

Three things drop the entry, not two: `ESRCH` from **both** probes, a positive recycle proof, and a live pid whose start-time token is missing or unreadable on either side. The exact-pid route accepts nothing weaker than a known, matching token on both sides — never a bare pid number — so it cannot widen the recycled-target window it sits inside; the price of that strictness is the third case, where a host that cannot read an identity at all keeps the pre-`setsid()` window's old behaviour and drops a live child from tracking. That is deliberate and fail-closed: delivering on the strength of a bare number would be the wrong-target kill itself.

A CPU-time-only limit also uses this backend and wraps each spawn with `RLIMIT_CPU`; it does not require a whole-tree container.

`Native.Posix` maintains a process-wide pending-wait registry keyed by PID. Linux 5.4+ registers per-child pidfds with one shared epoll reaper, while macOS registers `EVFILT_PROC` / `NOTE_EXIT` filters with one shared kqueue reaper. Older Linux and other POSIX hosts lazily install one managed `SIGCHLD` registration whose handler performs non-blocking `waitpid(..., WNOHANG)` scans. Every path resolves the exact pending-wait generation once; `reapLeader` uses a short bounded non-blocking retry loop for teardown, avoiding a permanently blocked teardown thread if a child is stuck in uninterruptible kernel sleep.

The fallback has no kernel tree resource limits and its `Stats` can report only live group count, not CPU or memory. Reaping a leader does not prove its backgrounded group is empty, so `Release` retains the pgid while group members remain.

### Linux cgroup v2

`CgroupBackend` is selected only on Linux when resource limits are requested. `Native.Cgroup` probes `/sys/fs/cgroup` and the hybrid `/sys/fs/cgroup/unified`, requiring a non-empty `cgroup.controllers`. Creation enables the required `memory`, `pids`, `cpu`, `cpuset`, and/or `io` controllers and writes `memory.max`, `pids.max`, `cpu.max`, `cpuset.cpus`, and/or `io.max`. `UpdateLimits` rewrites those same controller files in place (enabling any controller the new caps newly need, resetting a now-`None` dimension to that controller's own "unbounded" sentinel where its file already exists) to replace the caps on the live cgroup without recreating it. Each controller file's prior content is captured just before it is overwritten, and a mid-sequence write failure best-effort restores the files already changed, so a failed update leaves the live cgroup on the previous set rather than a silent mix.

`ResourceLimits.WithIoMax` uses a `major:minor` block-device key and the `rbps`, `wbps`, `riops`, and `wiops` fields of cgroup v2 `io.max`. A target replacement is two native writes — clear the old device key, then write the new key — because the file is keyed by device. Each write is recorded in the rollback ledger, so a failure on the second step restores the old device policy. A hierarchy that does not delegate the `io` controller is an honest `ProcessError.Unsupported` before `subtree_control` or `io.max` is touched; it never silently falls back to the unbounded POSIX process-group backend.

The CPU-affinity pin is the one dimension whose reset sentinel is *not* `max`: `cpuset.cpus` does not accept that word, and an **empty** cpuset is what means "every core the parent allows", so the apply plan carries a per-file sentinel and clears the pin by writing a blank line. That is also why both the clear and the rollback write a newline rather than zero bytes — a zero-length write never reaches the kernel's parser, so the old value would silently survive. `Native.Cgroup.formatCpuList` renders the core set in the grammar `cpuset.cpus` accepts and prints back (consecutive runs collapsed: `[0; 2; 3]` → `0,2-3`). `cpuset` is delegated exactly like the other controllers, but unlike them its availability is not implied by a mounted cgroup v2 hierarchy — where `cgroup.controllers` omits it, enabling it fails and the pin surfaces an honest `ProcessError.ResourceLimit`.

`posix_spawn` starts a child running immediately, so a bare parent-side write of the PID to `cgroup.procs` would land after the child had already executed — a spawn-to-migrate window where a descendant forked in that first instant is created in the parent cgroup and escapes the limits. ProcessKit closes it by launching the child through a small `/bin/sh` helper that writes its own PID into `cgroup.procs` and then `exec`s the real program in place (same PID): the target's first instruction already runs inside the cgroup, so any descendant it forks inherits the cgroup too. This mirrors the `setpriv` uid/gid helper — no managed code runs in a post-fork child — and a requested uid/gid drop is nested inside the launcher so the privileged cgroup join happens before the drop. The launcher is `/bin/sh` by absolute path, and the nested `setpriv` (and, under a PTY, `setsid --ctty`) is likewise the absolute path of a trusted-directory match, so no step of the chain is resolved through `PATH` — see [Trusted helper resolution](#trusted-helper-resolution) below. `Track` still writes the PID to `cgroup.procs` as an idempotent confirmation: a genuine open/write failure (missing or unwritable cgroup) means the launcher's own self-migrate failed too, so `Track` removes the PID from tracking, kills its process group, reaps the leader, and returns `ResourceLimit`; running unconstrained is not an accepted fallback. A write that races a fast target's exit (`ESRCH`) is treated as success — the target ran inside the cgroup and is gone.

Hard kill first writes `1` to `cgroup.kill` (kernel 5.14+). If unavailable, it best-effort freezes the cgroup, repeatedly SIGKILLs current members (up to 50 sweeps), then retries the thaw and reads `cgroup.freeze` back a bounded number of times. Each fallback SIGKILL goes through the same identity-safe choke as the per-member signal sweep (pin the task with a pidfd, re-read `cgroup.procs` to reconfirm membership, then send through the pinned handle), because freezing stops members forking but not exiting, so a raw by-number kill could still land on a process that recycled a member's pid; a pid that is gone or no longer a member is skipped, and the drain check decides whether the tree died. A delivery failure is reported as `ProcessError.Io` only when the cgroup is still populated (or unreadable) afterwards, and a kernel without pidfd (below 5.3) yields that honest error rather than a downgrade to the raw kill. A reusable `KillAll`/`Signal.Kill` reports `ProcessError.Io` when the freezer still reports `1` or cannot be read; an already-unfrozen or removed freezer remains best-effort success. `HardRelease` ignores that reusable-state error while it kills/reaps every directly spawned POSIX leader and removes the directory, so final disposal stays bounded. Resource-limit requests do not fall back to an unbounded process group when cgroup creation or delegation fails.

### Reaping on sudden parent death (`Command.KillOnParentDeath`)

Kill-on-drop covers the owner *disposing* the group; it cannot cover the owner process being killed outright (SIGKILL/crash/`TerminateProcess`), where no managed teardown runs. `Command.KillOnParentDeath` is the opt-in for that case, and its platform-fixed *scope* is reported honestly by `Command.KillOnParentDeathScope` (`KillOnParentDeathScope`, an honest-report union alongside `Mechanism`). On **Windows** it needs no code: every child already lives in a Job Object with `KILL_ON_JOB_CLOSE` whose sole handle the parent owns, so the kernel's handle rundown on parent death closes the last handle and terminates the whole Job tree — hence `KillOnParentDeathScope.WholeTree`. On **Linux** `Native.Posix` arms `PR_SET_PDEATHSIG(SIGKILL)` on the child through the existing `setpriv` helper (`setpriv --pdeathsig=SIGKILL`, composed with or standing in for a uid/gid drop via `needsSetpriv`/`setprivFlags`): the signal is set by a process that then `exec`s the target *in place* — no managed code in a post-fork child, the same safety reason as the uid/gid and cgroup helpers — and every POSIX helper chain here (`setpriv`, `setsid --ctty`, the `/bin/sh` cgroup launcher, and the parent-death guard below) `exec`s without an intervening `fork`, so the target stays the parent's *direct* child, which is what `PR_SET_PDEATHSIG` tracks. Because that arming runs inside the child, it cannot cover a parent that dies before it lands — the kernel reparents the orphan to the nearest subreaper (or init) first, and the `prctl` then binds the signal to *that* process — so `setpriv` does not `exec` the target directly: `parentDeathGuardedTarget` nests a small POSIX-sh guard (`/bin/sh -c`, pinned by absolute path) between the `setpriv` flags and the target, in all three chains and only when `KillOnParentDeath` is set. The guard compares its own `$PPID`, read after the arming, with the spawner pid captured in the parent *before* the spawn: equal, it `exec`s the target in place (same pid, so the pgid, cgroup membership, stdio, and controlling terminal the chain set up are unchanged); unequal, the parent already died inside that window, so the guard `SIGKILL`s itself and the target never runs — the outcome an armed signal would have produced. The comparison is against that captured pid and never the literal `1`, which would both kill the children of a spawner that legitimately is pid 1 (a container entrypoint) and miss a reparent to an ordinary subreaper. A host holding no `/bin/sh` to run the guard fails the spawn with a typed `ProcessError.Spawn` instead of arming with the window left open (see [Trusted helper resolution](#trusted-helper-resolution) below). Once the signal is armed the kernel owns the guarantee, which reaches the direct child only (`KillOnParentDeathScope.DirectChildOnly`): a grandchild forked after the signal is set does not inherit it, and the kernel resets the signal across an `execve` of a set-uid/set-gid image. On **macOS/BSD** there is no `PR_SET_PDEATHSIG` analog, so `spawnPosix` refuses the request with `ProcessError.Unsupported` (`KillOnParentDeathScope.Nothing`) rather than downgrading silently.

### Trusted helper resolution

The three trusted-directory POSIX helpers above (`setpriv` for a uid/gid drop and for `--pdeathsig`, `setsid --ctty` for a PTY's controlling terminal) are the code that *performs* the hardening, and on the drop path the first of them runs as root, before the credentials it exists to lower have been lowered. Launching either by bare name would resolve it through libc's `exec*p` `PATH` search, so a same-named binary planted in any directory ahead of `/usr/bin` would run with the parent's full privileges. `Native.Posix.trustedHelperPath` therefore resolves both helpers **only** against the fixed list `/usr/bin`, `/bin`, `/usr/sbin`, `/sbin` (reusing `Native.Common.probeDir`, so the "present and directly executable" rule is not duplicated) and the spawn runs the resolved **absolute path** — as `argv[0]` of the `posix_spawnp` on the plain and detached drop paths, and as the first word of the argv a pinned helper `exec`s in the PTY shim and the cgroup launcher. This is the POSIX counterpart of `Native.Windows.systemCmdExe`, which takes the batch wrapper's `cmd.exe` from `Environment.SystemDirectory` rather than `PATH`/`%ComSpec%` for the same reason. `Command.PreferLocal` substitutes only the caller's target program (`applyPreferLocal`), never a helper. When no trusted directory holds the helper, the request fails with the same typed error it already produced on a host missing the tool outright — `ProcessError.Spawn` naming the knob that needed `setpriv`, `ProcessError.Unsupported` for a PTY — never a `PATH` fallback and never a silently un-hardened child; the consumer-facing statement of that contract is in [Hardening untrusted children](../hardening.md#where-the-unix-helper-binaries-come-from). `/bin/sh` is the one program these chains also `exec` that is *not* resolved that way: both the cgroup launcher and the `KillOnParentDeath` guard take the absolute `/bin/sh` directly, because that path is fixed by POSIX instead of varying by distribution the way the util-linux tools do (the guard still checks it through the same `Native.Common.probeDir` before building its argv). Its absence is typed as well, never a downgrade — `ProcessError.Spawn` for the guard, because arming without it would leave the pre-arm window open while still reporting `DirectChildOnly`, and `ProcessError.ResourceLimit` for the cgroup launcher, the same answer the rest of that path gives when the limits cannot be enforced. (The `RLIMIT_CPU` shim, outside these chains, pins the same absolute path for the same reason.)

## Pump layer and output buffering

`Pump.LineBuffer` retains decoded lines as `(string, UTF-8 byte count + 1)` entries, charging every line one additional byte for the separator that `Text` reintroduces. `Add` always increments total line count and, when needed, total byte count before applying retention. This distinction lets diagnostics report the full stream even when retained content was truncated.

The modes are:

- `DropOldest`: append the new line, then evict from the front until both caps fit. A single line larger than the byte cap is itself evicted.
- `DropNewest`: when either cap would be exceeded, retain the existing prefix and discard the incoming line.
- `Error`: mark the capture too large when a cap is crossed, while the pump continues draining the pipe. With no caps set (`MaxLines = None` and `MaxBytes = None`) there is no ceiling to cross, so `Error` retains everything and never trips `OutputTooLarge`.
- Unbounded behavior is represented by `OutputBufferPolicy.Unbounded` (`MaxLines = None`, `MaxBytes = None`, with `DropOldest` irrelevant because no cap fills).

Line and byte caps solve different problems. A line cap bounds object/count overhead but permits a few enormous strings. A byte cap bounds retained UTF-8 payload and also supplies `readLines` with an in-flight line-length cap: newline-free output is force-flushed into segments rather than growing one `StringBuilder` indefinitely. It now genuinely bounds retained memory for empty-line floods as well, because every retained line is charged for its separator byte. The line cap remains independent so callers can place a direct bound on object/count overhead. Raw byte capture has no line structure and uses only `MaxBytes` through `RawBuffer`.

`readLines` reads 8192-byte chunks, tees raw bytes before decoding, strips a leading BOM from decoded text only, and applies the configured terminator rules across chunk boundaries. Its `onLine` callback returns `ValueTask`; awaiting it in the hot loop is what makes channel backpressure real. EOF resolves a pending carriage return and flushes a final unterminated line.

Which encoding that decode uses is whatever `CommandConfig.StdoutEncoding`/`StderrEncoding` hold — `Encoding.UTF8` unless the builder overrode it. `ConsoleEncoding.fs` supplies the one override the library can resolve for itself. On Windows it reads the live console output code page (`GetConsoleOutputCP`, falling back to the system OEM code page through `GetOEMCP` when the process has no console — both P/Invokes belong to `Native.Windows.fs` like every other Win32 entry point), registers `CodePagesEncodingProvider` exactly once behind a `lazy` so `Encoding.GetEncoding` knows the single-byte OEM/ANSI pages at all, and returns the matching `Encoding`; a code page with no data falls back to UTF-8 rather than throwing out of a builder call. Off Windows it returns the very `Encoding.UTF8` instance the default already uses, with no platform call on that path. The provider needs no package reference: `System.Text.Encoding.CodePages` ships in the shared framework and the targeting pack for both TFMs, and net10.0's framework package-override list supersedes the standalone package.

Nothing in the pump is aware of any of this — the resolved encoding arrives as an ordinary builder value. The verb that applies it, `Command.ConsoleEncoding()`, lives in `CommandVerbs.fs` rather than on `Command` itself for the compile-order reason above: it consumes a native-layer symbol, and `Command.fs` compiles before the native files.

## Stream channel machinery

`StreamChannel.create` uses an unbounded channel by default, with `SingleReader = true` and the caller-supplied writer count. An explicit `StreamBufferPolicy` creates a bounded channel with `SingleReader = false`, the correct `SingleWriter` value, and underlying `BoundedChannelFullMode.Wait` for every policy. ProcessKit implements full behavior itself because built-in drop modes make `TryWrite` appear successful and conceal whether a drop occurred.

`writeItem` implements:

- `Backpressure`: await `WriteAsync(item, disposalToken)`. Binding the wait to disposal prevents an abandoned full stream from leaving its pump alive forever.
- `DropNewest`: failed `TryWrite` drops the incoming item and increments the drop counter.
- `DropOldest`: on a full channel, `TryRead` evicts one item and `TryWrite` retries. The loop is required for the output-event channel's two writers: the sibling stdout/stderr pump can refill the slot between eviction and retry. If the channel completed, eviction and writing both fail; the loop counts the item dropped and stops instead of livelocking.
- `Error`: a failed `TryWrite` raises `ProcessException(OutputTooLarge ...)`, following the same pump-fault/channel-completion path as decoding, I/O, or line-handler failures.

`pumpLines` passes this asynchronous callback directly to `Pump.readLinesUntilDone`, keeping the read/decode/write path allocation-light and preserving ordering within each source stream. The combined event channel has concurrent stdout and stderr writers, so it does not promise a global ordering beyond arrival at the channel.

## Child lifecycle and kill-on-drop

The normal ownership sequence is:

```text
Spawn -> Track -> expose RunningProcess -> pump + Wait -> Release -> dispose streams/container
              \                         /
               `---- teardown owns ----'
```

`Command.LaunchDetached` is the one deliberate exception to that sequence, and it is structured so it can never dilute it. It has its own spawn path in each platform layer (`Native.Windows.spawnDetachedWindows`, `Native.Posix.spawnDetachedPosix`) which skips the whole transaction: no Job assignment (so no `CREATE_SUSPENDED`/resume dance), `POSIX_SPAWN_SETSID` instead of the tracked process group, no `Track`, no retained handle, and no `Release`. On POSIX, preparation starts one private process-wide reaper before `posix_spawn`; after the start-time snapshot, the direct leader is handed to that owner, which alone performs `waitpid` and retains the pid across temporary wait errors. A failed preparation prevents spawn, while a failed handoff synchronously kills and reaps the fresh session before returning `ProcessError.Spawn`. It returns only a `DetachedProcess` (pid + start time), never a `RunningProcess`, so the background wait owner does not become a public lifetime/control handle, and every builder knob that would require the parent-side machinery it does not create is refused up front with a typed `ProcessError.Unsupported` (`DetachedLaunch.incompatibleKnob` in `CommandVerbs.fs`). It shares the resolution, command-line, environment, stdio-opening, and privilege-drop helpers with the contained paths rather than re-deriving them, so the divergence is exactly the containment and private exit-status collection, nothing else. Keep it that way: a detached mode reachable as a *flag* on the ordinary path would make the kill-on-drop guarantee conditional everywhere, which is precisely what the separate verb avoids.

For a private per-run group, disposing `RunningProcess` disposes its `ProcessGroup`, so the whole tree dies. For a shared group, disposing a child handle detaches that run's I/O; the group remains the lifetime owner until `ShutdownAsync` or group disposal. `ProcessGroup` implements deterministic `IDisposable`/`IAsyncDisposable` teardown, and its finalizer is the last-resort safety net when callers fail to dispose.

For a detailed state machine, ownership matrix, and examples, see [Lifecycle state machine](lifecycle.md).

`ProcessGroup` guards its whole lifecycle with one lock (`sync`). A spawn+track (and the start of a run) and every control/accounting verb run their released-flag check *and* their native backend call inside that lock, so each either completes fully on the live container or observes the flag and returns `Unsupported` before touching native. The live→released transition also flips the flag under `sync`: acquiring the lock waits out any in-flight operation (each holds the lock for its whole native call), and once the flag is set every later operation bails. Whichever of `Dispose`/`DisposeAsync`/`ShutdownAsync`/finalizer wins that flip owns the one-shot `HardRelease`; the losers are no-ops, so teardown runs exactly once. `HardRelease` is bounded (SIGKILL + `waitpid` + close) and also runs under `sync`, but `ShutdownAsync` deliberately keeps its *unbounded* graceful-stop wait off the lock — the flag is already set, so no new operation can start during that wait.

This makes the spawn-versus-dispose race trivial: because the spawn+track transaction and the release transition are mutually exclusive, a child is either tracked before teardown (and then reaped exactly once by teardown's `Drain`) or never spawned (the start fails fast with a non-transient error). No separate escapee-reap fixup is needed, and a `RunningProcess` is never built over a container whose teardown has begun.

A normal run's shared-group `Release` — detaching one run's I/O without releasing the group — runs *under* `sync`, serialized against both the control/signal verbs and teardown. That serialization is what keeps the Windows `ctrlGroups` map non-stale: `Signal.Int`/`Signal.Term` deliver every `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` while holding `sync`, off a snapshot of the console-group ids, so a `Release` that drops an entry and closes its process handle — freeing the pid for OS reuse — can never interleave a delivery and misfire a CTRL+BREAK at a recycled pid on an unrelated console group (the wrong-target class T-084 closed for the POSIX kill and T-162 for the Windows Job handle, left open on this path until it too was moved under the lock).

PID reuse still matters for the backend primitive itself: after a successful `waitpid` the kernel may immediately reuse the numeric PID, so a second `killpg` on the same value could hit an unrelated process group, and a run's `Release` or the cgroup `Track` migration-failure cleanup could otherwise re-reap a PID teardown's `Drain` already took. `TrackedChildren.Drain` versus `Remove` makes exactly one path own each PID/pgid — the reap fires only when `Remove` returns true — a backend-level guard that holds even when a backend is driven concurrently in isolation (as the unit tests do), independent of the `ProcessGroup` lock above it. The same principle explains why Windows tracking uses open process handles rather than bare PIDs.

## Telemetry lifecycle

`Diag.fs` owns process-wide `ActivitySource` and `Meter` instances whose public names come from `ProcessKitDiagnostics`. It defines these instruments:

- counters `processkit.runs.started`, `processkit.runs.completed`, and `processkit.retries`;
- up/down counter `processkit.runs.active`;
- counters `processkit.supervisor.restarts` and `processkit.supervisor.storm_pauses`;
- histogram `processkit.run.duration`, recorded in seconds as required by OpenTelemetry conventions.

`Diag.newRunId` atomically increments a process-local `int64` and formats it as at least eight hexadecimal digits. It correlates spawn, timeout, retry, and exit logs; distributed uniqueness belongs to tracing. `RunTelemetryScope` arbitrates completion with `Interlocked.Exchange`: a terminal verb records completion, duration, span, and decrements active; an abandoned/disposed streaming run only decrements active. This prevents both double-counting and a permanently inflated active gauge.

Metric cardinality is deliberately bounded. Metrics carry the program name and, where relevant, a closed outcome label. Activities add run ID, outcome, optional exit code/signal, and PID. Neither telemetry nor logs record argv or environment values.

`Log.fs` uses cached `LoggerMessage.Define` delegates and stable event IDs for spawn, exit, timeout, retry, supervisor restart, and storm pause. Run-scoped events carry the correlation ID even if the logging provider does not preserve scopes. Lifecycle facts are safe to log; command arguments and environment variables may contain passwords or tokens and must never be added.

## Platform differences

| Capability | Windows Job Object | POSIX process group | Linux cgroup v2 |
|---|---|---|---|
| Selection | Windows, with or without limits | macOS/BSD; Linux without whole-tree limits (CPU-time-only is supported here) | Linux when whole-tree limits are requested |
| Containment timing | Child suspended, assigned to Job, then resumed | Group created atomically by `posix_spawn` attributes | POSIX group at spawn; `/bin/sh` launcher joins the cgroup before `exec`ing the target (already contained on its first instruction) |
| Adopt external process (`Adopt`) | `AssignProcessToJobObject` (opens a least-privilege handle, assigns, closes it — Job membership persists) | `ProcessError.Unsupported` — `setpgid` cannot relocate a foreign, already-`exec`ed process | Write the pid to `cgroup.procs` (limited groups only; the plain POSIX fallback is the middle column) |
| Detached launch (`LaunchDetached`) | Created running, assigned to no Job, both handles closed at once | `POSIX_SPAWN_SETSID`: own session, no pgid tracking; a private background reaper owns the direct leader's wait status | Same as the middle column — a detached child joins no cgroup; its direct leader uses the same private reaper |
| Whole-tree hard kill | Terminate Job or close kill-on-close Job handle | `killpg(SIGKILL)` for each tracked pgid | `cgroup.kill`; freeze-and-SIGKILL sweep fallback |
| Graceful tree stop | Best-effort `WM_CLOSE` to members' windows, poll to grace, then hard Job kill | configured soft signal, poll, then `SIGKILL` | identity-safe configured-signal sweep, poll, then cgroup hard kill |
| General signals | Kill; Int/Term as best-effort CTRL+BREAK (`WindowsCtrlSignals()` child) and/or `WM_CLOSE` (windowed member) | `killpg` with mapped/raw signal | per-current-member signal sweep; Kill is atomic cgroup kill |
| Suspend/resume | Best-effort per-thread; suspend counts stack | `SIGSTOP` / `SIGCONT` | `cgroup.freeze` best effort |
| Resource controls | Job memory, active-process, CPU quota/time, CPU-affinity, and per-volume I/O rate limits (live-updatable via `UpdateLimits`) | Per-spawn `RLIMIT_CPU`; whole-tree limits, I/O limits, and changing CPU-time live return `Unsupported`/`ResourceLimit` as applicable | `memory.max`, `pids.max`, `cpu.max`, `cpuset.cpus`, and per-device `io.max` plus per-spawn `RLIMIT_CPU` (controller limits live-updatable; CPU-time is spawn-time) |
| Desktop-session restrictions | `JOBOBJECT_BASIC_UI_RESTRICTIONS` (clipboard, desktop, display/system parameters, atoms, exit-Windows) | `ProcessError.Unsupported` — no analogue | `ProcessError.Unsupported` — no analogue |
| Child privilege reduction | Restricted token (`DISABLE_MAX_PRIVILEGE`) and/or a lowered integrity label, spawned via `CreateProcessAsUser` | `Uid`/`Gid`/`Groups`/`Umask` through the `setpriv` helper, loaded from a trusted system directory | same as the middle column |
| Membership snapshot | All Job PIDs | Tracked group leaders, not every descendant PID | Current `cgroup.procs` PIDs |
| Accounting | Active count, CPU, peak committed memory | Live group count only | Active members, CPU use, peak memory when files exist |
| Per-member resource sampling | Query-denied members remain with `None` metrics only after a fresh Job membership confirmation; inaccessible pids absent from that refresh are omitted | Tracked members require a known matching start-time token; vanished, recycled, or unknown-identity pids are omitted | Each `cgroup.procs` pid is bound to its snapshot start-time token and checked again after sampling; vanished, recycled, or unknown-identity pids are omitted |
| Reaping obligation | Handles close; Job kills on close | ProcessKit `waitpid`s direct leaders; other descendants reparent | cgroup kill plus `waitpid` of direct leaders |
| Main failure mode | Job creation/assignment or console delivery failure | No tree limits; PID/pgid reuse if ownership rules are broken | Missing/delegation-denied controllers, migration failure (honest `ResourceLimit`), older-kernel kill fallback |

On macOS, the POSIX backend is the only mechanism: whole-tree limits are rejected rather than approximated. `Native.Posix` also accounts for macOS `POSIX_SPAWN_CLOEXEC_DEFAULT` when preserving inherited standard descriptors, and current-directory support requires `posix_spawn_file_actions_addchdir_np` (macOS 10.15+). Signal and zombie-reaping semantics remain POSIX: `killpg` sends signals but never substitutes for reaping ProcessKit's direct child.

## Local verification boundary

`scripts/verify-all.ps1` is the repository's thin pre-push orchestrator. It owns
ordering, availability checks, the final pass/fail/skip table, and the aggregate
exit code; it does not reimplement any gate. Formatting stays in Fantomas, docs
snippets in `verify-doc-snippets.ps1`, spelling in `check-spelling.ps1`, rendered
sidebar structure in `check-sidebar-nav.py`, Linux execution in `test-linux.ps1`,
and fuzzing in `fuzz.ps1`.

Run `pwsh ./scripts/verify-all.ps1 -SkipLinux` for the ordinary host pass, or omit
the switch for every locally available stage. `-SkipSnippets` and `-SkipLinks`
are explicit fast-path opt-outs; `-LibFuzzer` enables the two fuzz smoke targets.
Optional executables that are absent are recorded as skipped, while any invoked
gate failure makes the aggregate command fail.

## Benchmarking hot paths

`benchmarks/ProcessKit.Benchmarks` (BenchmarkDotNet) covers the pump's decode/frame loop (`Pump.readLines`, all four `LineTerminator` modes, with and without a `MaxLineLength` force-flush cap), the no-op cost of a disabled lifecycle log call, and concurrent spawn/capture fan-out (`OutputStringAsync`, `StartAsync` + `WaitAllAsync`). It runs BenchmarkDotNet's default, statistically-rigorous job in-process against the already-built `ProcessKit` assembly — see `Program.fs`'s doc comment for why in-process, not BenchmarkDotNet's usual out-of-process toolchain, is required here (this repo's `Reference` + `AssemblySearchPaths` convention has no equivalent in a regenerated isolated project).

Run it locally with `dotnet run -c Release --project benchmarks/ProcessKit.Benchmarks -- --filter *Pump*` (or any BenchmarkDotNet `--filter` glob; omit it to run every benchmark class). Results land under `BenchmarkDotNet.Artifacts/results/` relative to the working directory the command was run from.

### Scheduled/manual CI run

`.github/workflows/benchmarks.yml` runs the same project on a weekly schedule and on `workflow_dispatch`, deliberately outside `ci.yml` and off the `pull_request`/`push` path entirely — a benchmark run adds real wall-clock time and its numbers are not something a PR gate should block on. It invokes the harness with `--ci`, which swaps in BenchmarkDotNet's reduced-iteration `Job.ShortRun` (see `Program.fs`) instead of the default job and attaches BenchmarkDotNet's full JSON exporter; the workflow uploads the whole `BenchmarkDotNet.Artifacts/results/` directory (JSON, Markdown, HTML, CSV reports) as the `benchmark-results` artifact.

**Reading the results honestly matters more than the numbers themselves.** GitHub-hosted runners are shared, variable-noise virtual machines, not dedicated benchmarking hardware — treat any single scheduled run's absolute numbers (ns/op, allocated bytes) as noisy, and never compare one run's mean directly against another run's mean as if it were a lab-grade measurement. What is worth eyeballing is a large, repeated shift in relative shape between benchmark methods/parameters across several runs (a hot path suddenly several times slower, or a benchmark that previously allocated nothing now allocating), not single-run deltas of a few percent. There is intentionally no automated trend-history page or regression alert (e.g. `github-action-benchmark` publishing to `gh-pages`): this repo's GitHub Pages deployment (`docs.yml`) already uses the artifact-based `actions/deploy-pages` mechanism for the API reference, and that tool's usual model of committing a running history to a `gh-pages` branch does not compose cleanly with it. The uploaded JSON artifact is the whole mechanism — download and diff it by hand (or feed it into a local trend tool) when investigating a suspected regression.

## Invariants to preserve when changing the stack

- Never expose a spawned child before successful tracking.
- Never stop draining a captured pipe merely because retention overflowed; only explicit streaming backpressure may pace it.
- Never convert a requested resource limit or signal into a silent weaker fallback.
- Keep the opt-out from containment a separate, loudly named verb with typed refusals — never a flag on a contained path.
- Keep teardown exactly once, but child cleanup owned by exactly one racer.
- Pair POSIX group killing with direct-child reaping.
- Treat handles/PIDs as identities only while tracking proves they have not been released and reused.
- Keep argv and environment values out of logs, metrics, and traces.
- Preserve the `.fsproj` declaration order whenever dependencies change.
