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
10. `Signal.fs` — portable signal model.
11. `Limits.fs` — resource and process-group options.
12. `ProcessResult.fs` — captured result.
13. `TryParser.fs` — .NET try-parse adapter.
14. `OutputPolicy.fs` — buffered and streaming overflow policies.
15. `OutputEvent.fs` — stdout/stderr event model.
16. `Finished.fs` — finish result.
17. `Stats.fs` — group/run statistics and samplers.
18. `Stdin.fs` — stdin source model.
19. `Timeouts.fs` — timeout normalization.
20. `Priority.fs` — priority model and native mapping.
21. `LineTerminator.fs` — line-ending rules.
22. `Command.fs` — immutable command configuration and builder API.

### Native and platform layer

23. `Native.Common.fs` — shared spawned-process representation and signal-delivery result.
24. `Native.Windows.fs` — Win32 process, pipe, Job Object, console-control, limits, and accounting calls.
25. `Native.Posix.fs` — `posix_spawn`, process groups, signals, and `waitpid` registry.
26. `Native.Cgroup.fs` — Linux cgroup v2 discovery, controls, membership, and accounting.

### Backend, pump, and channels

27. `Backend.fs` — containment interface and its three implementations.
28. `Pump.fs` — pipe decoding, line/raw buffering, tees, and stdin pumping.
29. `StreamChannel.fs` — streaming channel construction and full-mode behavior.
30. `ProcessStdin.fs` — interactive stdin handle.
31. `ReadinessProbe.fs` — readiness polling.
32. `RunningProcess.fs` — live-process state, streams, completion, and disposal.

### Runner and verbs

33. `IProcessRunner.fs` — injectable runner seam.
34. `Runner.fs` — capture primitives and reusable verbs.
35. `ProcessRunnerExtensions.fs` — .NET extensions for custom runners.
36. `DelegatingProcessRunner.fs` — runner decorator base.
37. `ProcessGroup.fs` — containment owner and shared-group runner.
38. `JobRunner.fs` — default private-group runner.
39. `CommandVerbs.fs` — default-runner `Command` extensions.
40. `PipelineRunner.fs` — internal pipeline execution.
41. `Pipeline.fs` — pipeline public API.
42. `Supervisor.fs` — restart supervision.
43. `CliClient.fs` — configured command client.
44. `Exec.fs` — concise execution entry points.

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

Natural exit, explicit kill, timeout, cancellation, and disposal converge on waiting/reaping and teardown. A verb may transform the resulting `Outcome`, but it must not bypass ownership cleanup.

## Containment backend contract

`Backend.fs` defines the complete internal contract as follows (comments omitted here; signatures are unchanged):

```fsharp
type internal IContainmentBackend =
    abstract Mechanism: Mechanism
    abstract Spawn: Command -> Result<Native.Common.Spawned, ProcessError>
    abstract Track: Native.Common.Spawned -> Result<unit, ProcessError>
    abstract Release: Native.Common.Spawned -> unit
    abstract Wait: nativeint -> Task<Outcome>
    abstract PidOf: Native.Common.Spawned -> int option
    abstract KillChild: Native.Common.Spawned -> unit
    abstract KillTree: unit -> unit
    abstract GracefulKillTree: TimeSpan -> Task
    abstract Members: unit -> Result<int list, ProcessError>
    abstract Signal: Signal -> Result<unit, ProcessError>
    abstract Suspend: unit -> Result<unit, ProcessError>
    abstract Resume: unit -> Result<unit, ProcessError>
    abstract Stats: unit -> Result<ProcessGroupStats, ProcessError>
    abstract HardRelease: unit -> unit
```

The current interface has 15 abstract members:

- `Mechanism` identifies the primitive honestly.
- `Spawn` starts a child, initially not in the backend's tracking collection.
- `Track` completes containment/tracking; on error it is responsible for killing and reaping the child.
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
- `HardRelease` performs the once-only hard teardown and frees the container.

`TrackedChildren<'T>` serializes `Add`, `Remove`, `Snapshot`, and `Drain` behind one lock. `Drain` is essential during teardown: it atomically transfers ownership of every recorded child to the teardown path. A mere snapshot would allow a racing cleanup to act twice on a recycled PID or handle.

`GracefulTeardown.poll` supplies the POSIX/cgroup shape: send `SIGTERM`, poll every 50 ms until empty or the grace period expires, then force-kill survivors. `PosixReap.leader` pairs `killpg` with `waitpid`; killing a process group does not reap the direct child that ProcessKit owns.

The implementation is split across four files. `Native.Windows.fs`, `Native.Posix.fs`, and `Native.Cgroup.fs` provide the OS-specific operations; `Backend.fs` composes them into `JobObjectBackend`, `ProcessGroupBackend`, and `CgroupBackend`, respectively, behind `IContainmentBackend`.

### Windows Job Object

`JobObjectBackend` owns one Job handle plus child process handles. `Native.Windows.spawnWindows` creates the process suspended, assigns it to the Job, and only then resumes it. This prevents a child from escaping by forking before assignment. The Job has `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; closing the Job is the final kill-on-drop guarantee. Requested memory, process-count, and CPU limits are applied through Job Object limit APIs.

`Signal.Kill` terminates the Job atomically. `Signal.Int` and `Signal.Term` are not Unix signals: for children explicitly started with `Command.WindowsCtrlSignals()`, they map to `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)`. The child is created with `CREATE_NEW_PROCESS_GROUP`, and its PID is its console group ID. Without an eligible child or a shared console, delivery returns `Unsupported`; it never silently becomes a hard kill. Other signals are unsupported.

Tracked process handles pin PID identity until release, preventing the stored console-group ID from becoming a wrong target. Windows graceful tree shutdown has no Job-wide soft primitive, so `GracefulKillTree` performs the atomic hard Job termination.

### POSIX process groups

`ProcessGroupBackend` is used on macOS/BSD and on Linux when limits are not requested. Every `posix_spawn` child becomes leader of its own process group (`pgid = pid`); one ProcessKit group may therefore track several pgids. `killpg` reaches descendants that remain in each group. Signals, `SIGSTOP`, and `SIGCONT` are broadcast per tracked pgid.

`Native.Posix` maintains a process-wide pending-wait registry keyed by PID and lazily installs one managed `SIGCHLD` registration. The handler performs non-blocking `waitpid(..., WNOHANG)` scans and completes the corresponding waiter. `reapLeader` uses a short bounded non-blocking retry loop for teardown, avoiding a permanently blocked teardown thread if a child is stuck in uninterruptible kernel sleep.

The fallback has no kernel tree resource limits and its `Stats` can report only live group count, not CPU or memory. Reaping a leader does not prove its backgrounded group is empty, so `Release` retains the pgid while group members remain.

### Linux cgroup v2

`CgroupBackend` is selected only on Linux when resource limits are requested. `Native.Cgroup` probes `/sys/fs/cgroup` and the hybrid `/sys/fs/cgroup/unified`, requiring a non-empty `cgroup.controllers`. Creation enables the required `memory`, `pids`, and/or `cpu` controllers and writes `memory.max`, `pids.max`, and `cpu.max`.

`posix_spawn` starts a child running immediately, so a bare parent-side write of the PID to `cgroup.procs` would land after the child had already executed — a spawn-to-migrate window where a descendant forked in that first instant is created in the parent cgroup and escapes the limits. ProcessKit closes it by launching the child through a small `/bin/sh` helper that writes its own PID into `cgroup.procs` and then `exec`s the real program in place (same PID): the target's first instruction already runs inside the cgroup, so any descendant it forks inherits the cgroup too. This mirrors the `setpriv` uid/gid helper — no managed code runs in a post-fork child — and a requested uid/gid drop is nested inside the launcher so the privileged cgroup join happens before the drop. `Track` still writes the PID to `cgroup.procs` as an idempotent confirmation: a genuine open/write failure (missing or unwritable cgroup) means the launcher's own self-migrate failed too, so `Track` removes the PID from tracking, kills its process group, reaps the leader, and returns `ResourceLimit`; running unconstrained is not an accepted fallback. A write that races a fast target's exit (`ESRCH`) is treated as success — the target ran inside the cgroup and is gone.

Hard kill first writes `1` to `cgroup.kill` (kernel 5.14+). If unavailable, it best-effort freezes the cgroup, repeatedly SIGKILLs current members (up to 50 sweeps), then thaws survivors. `HardRelease` also kills/reaps every directly spawned POSIX leader because cgroup kill does not perform `waitpid`, then removes the directory. Resource-limit requests do not fall back to an unbounded process group when cgroup creation or delegation fails.

## Pump layer and output buffering

`Pump.LineBuffer` retains decoded lines as `(string, UTF-8 byte count + 1)` entries, charging every line one additional byte for the separator that `Text` reintroduces. `Add` always increments total line count and, when needed, total byte count before applying retention. This distinction lets diagnostics report the full stream even when retained content was truncated.

The modes are:

- `DropOldest`: append the new line, then evict from the front until both caps fit. A single line larger than the byte cap is itself evicted.
- `DropNewest`: when either cap would be exceeded, retain the existing prefix and discard the incoming line.
- `Error`: mark the capture too large when a cap is crossed, while the pump continues draining the pipe. With no caps set (`MaxLines = None` and `MaxBytes = None`) there is no ceiling to cross, so `Error` retains everything and never trips `OutputTooLarge`.
- Unbounded behavior is represented by `OutputBufferPolicy.Unbounded` (`MaxLines = None`, `MaxBytes = None`, with `DropOldest` irrelevant because no cap fills).

Line and byte caps solve different problems. A line cap bounds object/count overhead but permits a few enormous strings. A byte cap bounds retained UTF-8 payload and also supplies `readLines` with an in-flight line-length cap: newline-free output is force-flushed into segments rather than growing one `StringBuilder` indefinitely. It now genuinely bounds retained memory for empty-line floods as well, because every retained line is charged for its separator byte. The line cap remains independent so callers can place a direct bound on object/count overhead. Raw byte capture has no line structure and uses only `MaxBytes` through `RawBuffer`.

`readLines` reads 8192-byte chunks, tees raw bytes before decoding, strips a leading BOM from decoded text only, and applies the configured terminator rules across chunk boundaries. Its `onLine` callback returns `ValueTask`; awaiting it in the hot loop is what makes channel backpressure real. EOF resolves a pending carriage return and flushes a final unterminated line.

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

For a private per-run group, disposing `RunningProcess` disposes its `ProcessGroup`, so the whole tree dies. For a shared group, disposing a child handle detaches that run's I/O; the group remains the lifetime owner until `ShutdownAsync` or group disposal. `ProcessGroup` implements deterministic `IDisposable`/`IAsyncDisposable` teardown, and its finalizer is the last-resort safety net when callers fail to dispose.

`ProcessGroup` guards its whole lifecycle with one lock (`sync`). A spawn+track (and the start of a run) and every control/accounting verb run their released-flag check *and* their native backend call inside that lock, so each either completes fully on the live container or observes the flag and returns `Unsupported` before touching native. The live→released transition also flips the flag under `sync`: acquiring the lock waits out any in-flight operation (each holds the lock for its whole native call), and once the flag is set every later operation bails. Whichever of `Dispose`/`DisposeAsync`/`ShutdownAsync`/finalizer wins that flip owns the one-shot `HardRelease`; the losers are no-ops, so teardown runs exactly once. `HardRelease` is bounded (SIGKILL + `waitpid` + close) and also runs under `sync`, but `ShutdownAsync` deliberately keeps its *unbounded* graceful-stop wait off the lock — the flag is already set, so no new operation can start during that wait.

This makes the spawn-versus-dispose race trivial: because the spawn+track transaction and the release transition are mutually exclusive, a child is either tracked before teardown (and then reaped exactly once by teardown's `Drain`) or never spawned (the start fails fast with a non-transient error). No separate escapee-reap fixup is needed, and a `RunningProcess` is never built over a container whose teardown has begun.

PID reuse still matters for the paths that reap *outside* that lock — a normal run's `Release` and the cgroup `Track` migration-failure cleanup can each race teardown's `Drain`. After a successful `waitpid` the kernel may immediately reuse the numeric PID, so a second `killpg` on the same value could hit an unrelated process group. `TrackedChildren.Drain` versus `Remove` makes exactly one path own each PID/pgid: the reap fires only when `Remove` returns true. The same principle explains why Windows tracking uses open process handles rather than bare PIDs.

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
| Selection | Windows, with or without limits | macOS/BSD; Linux without requested limits | Linux when limits are requested |
| Containment timing | Child suspended, assigned to Job, then resumed | Group created atomically by `posix_spawn` attributes | POSIX group at spawn; `/bin/sh` launcher joins the cgroup before `exec`ing the target (already contained on its first instruction) |
| Whole-tree hard kill | Terminate Job or close kill-on-close Job handle | `killpg(SIGKILL)` for each tracked pgid | `cgroup.kill`; freeze-and-SIGKILL sweep fallback |
| Graceful tree stop | No Job-wide soft stop; hard kill | `SIGTERM`, poll, then `SIGKILL` | signal members with `SIGTERM`, poll, then cgroup hard kill |
| General signals | Kill; Int/Term as opt-in CTRL+BREAK only | `killpg` with mapped/raw signal | per-current-member signal sweep; Kill is atomic cgroup kill |
| Suspend/resume | Best-effort per-thread; suspend counts stack | `SIGSTOP` / `SIGCONT` | `cgroup.freeze` best effort |
| Resource controls | Job memory, active-process, CPU limits | None | `memory.max`, `pids.max`, `cpu.max` |
| Membership snapshot | All Job PIDs | Tracked group leaders, not every descendant PID | Current `cgroup.procs` PIDs |
| Accounting | Active count, CPU, peak committed memory | Live group count only | Active members, CPU use, peak memory when files exist |
| Reaping obligation | Handles close; Job kills on close | ProcessKit `waitpid`s direct leaders; other descendants reparent | cgroup kill plus `waitpid` of direct leaders |
| Main failure mode | Job creation/assignment or console delivery failure | No tree limits; PID/pgid reuse if ownership rules are broken | Missing/delegation-denied controllers, migration failure (honest `ResourceLimit`), older-kernel kill fallback |

On macOS, the POSIX backend is the only mechanism: whole-tree limits are rejected rather than approximated. `Native.Posix` also accounts for macOS `POSIX_SPAWN_CLOEXEC_DEFAULT` when preserving inherited standard descriptors, and current-directory support requires `posix_spawn_file_actions_addchdir_np` (macOS 10.15+). Signal and zombie-reaping semantics remain POSIX: `killpg` sends signals but never substitutes for reaping ProcessKit's direct child.

## Invariants to preserve when changing the stack

- Never expose a spawned child before successful tracking.
- Never stop draining a captured pipe merely because retention overflowed; only explicit streaming backpressure may pace it.
- Never convert a requested resource limit or signal into a silent weaker fallback.
- Keep teardown exactly once, but child cleanup owned by exactly one racer.
- Pair POSIX group killing with direct-child reaping.
- Treat handles/PIDs as identities only while tracking proves they have not been released and reused.
- Keep argv and environment values out of logs, metrics, and traces.
- Preserve the `.fsproj` declaration order whenever dependencies change.
