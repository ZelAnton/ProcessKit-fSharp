namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

/// A kill-on-dispose container for a process *tree*.
///
/// Every process started into the group — and everything those processes spawn — is reaped when
/// the group is disposed (deterministic under `use`) or, failing that, when the GC finalizes it.
/// The OS primitive is chosen at creation and reported honestly by `Mechanism` — a Windows Job
/// Object (`KILL_ON_JOB_CLOSE`), a Linux cgroup v2 (when resource limits are requested), or a POSIX
/// process group (`killpg` teardown). All of that lives behind an `IContainmentBackend`; this type
/// only orchestrates once-only teardown, the stdin/stream wiring, and the runner/disposable seams.
[<Sealed>]
type ProcessGroup private (backend: IContainmentBackend, options: ProcessGroupOptions) =

    // 0 = live, 1 = released. Interlocked so Dispose/DisposeAsync/Shutdown/finalizer run the
    // teardown exactly once. All containment behaviour lives in `backend`; this type only
    // orchestrates the once-only teardown, the stdin/stream wiring, and the runner/disposable seams.
    let mutable releasedFlag = 0

    let waitOutcome (handle: nativeint) : Task<Outcome> = backend.Wait handle

    let releaseContainer () =
        if Interlocked.Exchange(&releasedFlag, 1) = 0 then
            backend.HardRelease()

    /// The OS primitive containing this group on the current platform.
    member _.Mechanism = backend.Mechanism

    /// The options the group was created with (shutdown grace, resource limits).
    member _.Options = options

    /// Create a new, empty kill-on-dispose group on the current platform (no resource limits).
    static member Create() : Result<ProcessGroup, ProcessError> =
        ProcessGroup.Create(ProcessGroupOptions())

    /// Create a new kill-on-dispose group with `options` (graceful-shutdown window and whole-tree
    /// resource limits). When `options.Limits` is set, the group needs a limit-capable mechanism —
    /// a Windows Job Object or a Linux cgroup v2 at the real cgroup root; otherwise creation fails
    /// fast with `ProcessError.ResourceLimit` rather than leaving the tree unbounded. Without limits
    /// the group uses the platform's default mechanism (Job Object / POSIX process group).
    ///
    /// On the Linux cgroup v2 mechanism, a spawned child is migrated into the cgroup right after it
    /// starts, and the limits then apply to it and every descendant it forks *afterwards*. A grandchild
    /// forked in the brief spawn→migrate window is created in the parent cgroup and stays there — still
    /// reaped by kill-on-drop teardown, but outside the resource limits. If the child cannot be migrated
    /// at all (e.g. the cgroup was torn down underneath the spawn), it is killed and reaped and the
    /// spawn fails with `ProcessError.ResourceLimit` — never left running unconstrained.
    static member Create(options: ProcessGroupOptions) : Result<ProcessGroup, ProcessError> =
        let limits = options.Limits

        let withBackend (backend: IContainmentBackend) = Ok(new ProcessGroup(backend, options))

        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            match Native.Windows.createWindowsJob () with
            | Error error -> Error error
            | Ok job ->
                if limits.Any then
                    match Native.Windows.applyWindowsJobLimits job limits with
                    | Ok() -> withBackend (JobObjectBackend job)
                    | Error message ->
                        Native.Windows.closeWindowsHandle job
                        Error(ProcessError.ResourceLimit message)
                else
                    withBackend (JobObjectBackend job)
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux && limits.Any then
            if Native.Cgroup.cgroupV2Available () then
                match Native.Cgroup.createCgroup limits with
                | Ok path -> withBackend (CgroupBackend path)
                | Error message -> Error(ProcessError.ResourceLimit message)
            else
                Error(
                    ProcessError.ResourceLimit
                        "cgroup v2 is not mounted; whole-tree resource limits need a Windows Job Object or Linux cgroup v2"
                )
        elif limits.Any then
            // macOS / BSD, or Linux without cgroup v2 — no whole-tree limit primitive.
            Error(
                ProcessError.ResourceLimit
                    "this platform has no whole-tree resource-limit primitive (needs a Windows Job Object or Linux cgroup v2)"
            )
        else
            // No limits: the POSIX group forms when children are spawned (each becomes its own pgid).
            withBackend (ProcessGroupBackend())

    /// Wait for one contained process handle to conclude. Internal — used by pipeline staging.
    member internal _.WaitHandle(handle: nativeint) : Task<Outcome> = waitOutcome handle

    /// Hard-kill the contained tree now (no grace) without releasing the group. Internal — used by
    /// pipeline cancellation/timeout.
    member internal _.KillTree() = backend.KillTree()

    /// Spawn `command` into the group, tracking the child for signalling / teardown. Internal.
    member internal _.SpawnInto(command: Command) : Result<Native.Common.Spawned, ProcessError> =
        // Refuse to spawn into a released group: on Windows the child is created before it is assigned
        // to the (now-closed) Job, so spawning after teardown would leak an UNCONTAINED child. Fail
        // fast with a non-transient error instead. (Same released-flag guard as the control verbs.)
        if releasedFlag <> 0 then
            Error(ProcessError.Unsupported "the process group has been released")
        else
            match backend.Spawn command with
            | Error error -> Error error
            | Ok spawned ->
                match backend.Track spawned with
                | Error trackError ->
                    // The backend could not actually contain the child (e.g. the cgroup backend failed
                    // to migrate it into the cgroup). It has already killed and reaped the child on this
                    // path, so there is no live child to clean up here — just surface the honest error
                    // rather than let an uncontained child run.
                    Error trackError
                | Ok() ->
                    // Re-check after tracking: if the group was released concurrently between the guard
                    // above and `Track`, `HardRelease`'s kill/reap snapshot may have missed this child (it
                    // would then run uncontained on the POSIX/cgroup backends). The release flag is set
                    // before that snapshot runs, so observing it set here means the child may have escaped —
                    // reap it ourselves rather than leak it, preserving the kill-on-drop guarantee.
                    if releasedFlag <> 0 then
                        backend.ReapEscapee spawned
                        Error(ProcessError.Unsupported "the process group has been released")
                    else
                        Ok spawned

    /// Spawn `command` into the group and build a `RunningHost`. `ownsGroup` decides what disposing
    /// the resulting `RunningProcess` does: when **true** (a private per-run group) it reaps this
    /// whole group; when **false** (a shared group) it only detaches this run's I/O — the group owns
    /// the child's lifetime and reaps it on `ShutdownAsync`/`Dispose`. That ownership choice is the only
    /// difference between the two start paths, so it lives here as one branch each on `StartKill`,
    /// `GracefulKill`, and `Teardown`.
    member private this.BuildHost(command: Command, ownsGroup: bool) : Result<RunningHost, ProcessError> =
        match this.SpawnInto command with
        | Error error -> Error error
        | Ok spawned ->
            let stdinFeed = Pump.feedStdinSource spawned.Stdin command.Config.StdinSource

            let closeStreams () =
                // Close the pipe streams (OS handles/fds) before releasing/detaching.
                Pump.closeSpawned spawned

            Ok
                { Config = command.Config
                  Pid = backend.PidOf spawned
                  Stdout = spawned.Stdout
                  Stderr = spawned.Stderr
                  Stdin =
                    (if command.Config.StdinSource.IsSome then
                         None
                     else
                         spawned.Stdin)
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  Wait = (fun () -> waitOutcome spawned.Handle)
                  // Observe the stashed source failure without blocking: read it only once the feed has
                  // finished, else `None`. `IsCompletedSuccessfully` + `.Result` is race-free — `feedStdin`
                  // never faults, so completion establishes the happens-before for the stashed value. We
                  // deliberately do NOT await the feed (a source parked mid-read would hang the verb), so
                  // an async-manifesting source fault that loses the race with the child's own exit is not
                  // surfaced — matching ProcessKit-rs. The common case, a missing `FromFile`, faults
                  // synchronously at spawn (`File.OpenRead`), so it is always observed.
                  StdinError =
                    (fun () ->
                        if stdinFeed.IsCompletedSuccessfully then
                            stdinFeed.Result
                        else
                            None)
                  StartKill =
                    (if ownsGroup then
                         backend.KillTree
                     else
                         (fun () -> backend.KillChild spawned))
                  GracefulKill =
                    (if ownsGroup then
                         (fun grace -> this.GracefulKillTree grace)
                     else
                         (fun _ -> task { backend.KillChild spawned } :> Task))
                  Teardown =
                    fun () ->
                        closeStreams ()

                        if ownsGroup then
                            // Owned group: closing the run reaps the whole tree.
                            (this :> IDisposable).Dispose()
                        else
                            // Shared group: detach this run's I/O only — the GROUP owns the child's
                            // lifetime (Shutdown/Dispose reaps it). Stop tracking it — POSIX only
                            // once its group is empty, so a still-live child stays reapable by the
                            // group; Windows closes the handle (the Job still contains the tree).
                            backend.Release spawned

                        ValueTask.CompletedTask }

    /// Spawn `command` into the group and build a `RunningHost`. Disposing the resulting
    /// `RunningProcess` reaps this whole group (the group is owned by the running process).
    member internal this.StartInternal(command: Command) : Result<RunningHost, ProcessError> =
        this.BuildHost(command, ownsGroup = true)

    /// Spawn `command` into this *shared* group and build a `RunningHost`. Unlike `StartInternal`,
    /// disposing the resulting `RunningProcess` does **not** reap the group — the group owns the
    /// child's lifetime (reaped on `ShutdownAsync`/`Dispose`). Internal — `StartAsync` wraps it.
    member internal this.StartShared(command: Command) : Result<RunningHost, ProcessError> =
        this.BuildHost(command, ownsGroup = false)

    /// Start `command` into this shared group and return a live `RunningProcess`. The **group** owns
    /// the child's lifetime: disposing the returned process detaches its I/O but does not kill it —
    /// reap the tree with `ShutdownAsync`/`Dispose`, or this one run with its `Kill`.
    member this.StartAsync
        (command: Command, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<RunningProcess, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match this.StartShared command with
                | Error error -> return Error error
                | Ok host -> return Ok(RunningProcess host)
        }

    // Tree-control / accounting verbs become an error once the group is released: after teardown the
    // backend's Job handle is closed (and POSIX pgids may have been recycled), so calling in would be
    // a use-after-close or a wrong-target kill. Guarding here (as `Stats` already did) closes the
    // window for a verb racing a concurrent `Dispose`/`ShutdownAsync`.
    member private _.WhenLive(action: unit -> Result<'T, ProcessError>) : Result<'T, ProcessError> =
        // A released group is a permanent condition, not a transient I/O blip — use `Unsupported`
        // (which `ProcessError.isTransient` rejects) so a retry classifier never re-tries a dead group.
        if releasedFlag <> 0 then
            Error(ProcessError.Unsupported "the process group has been released")
        else
            action ()

    /// Immediately hard-kill every process currently in the group (the honest name for the tree kill —
    /// no graceful signal). Idempotent; the group stays usable for further spawns. Returns `Result` for
    /// parity with the other tree-control verbs (a future backend can report an undrained tree); the
    /// current backends always succeed.
    member this.KillAll() : Result<unit, ProcessError> =
        this.WhenLive(fun () ->
            backend.KillTree()
            Ok())

    /// The pids of the processes currently in the group — a point-in-time snapshot. On Windows the
    /// whole Job tree, on cgroup v2 the whole cgroup, on the POSIX fallback the tracked group leaders.
    member this.Members() : Result<IReadOnlyList<int>, ProcessError> =
        this.WhenLive(fun () -> backend.Members())
        |> Result.map (fun pids -> List.toArray pids :> IReadOnlyList<int>)

    /// Broadcast `signal` to every process in the group. A member that has already exited (or an
    /// already-empty group) is a best-effort success — that races the target's own exit, not a
    /// caller error. An invalid delivery — most notably `Signal.Other` with a signal number the
    /// platform rejects — is a genuine failure and returns `ProcessError.Io` with the errno detail;
    /// when the group has several members, the first genuine failure is reported but every member
    /// still receives the signal. On **Windows** only `Signal.Kill` is deliverable (it maps to the
    /// Job terminate); any other signal returns `ProcessError.Unsupported`.
    member this.Signal(signal: Signal) : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Signal signal)

    /// Suspend (freeze) every process in the group. POSIX: `SIGSTOP` (level-triggered, idempotent).
    /// Cgroup v2: `cgroup.freeze`. Windows: suspend every thread of every member — best-effort, and
    /// suspend counts stack, so N `Suspend`s need N `Resume`s.
    member this.Suspend() : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Suspend())

    /// Resume a tree suspended by `Suspend`.
    member this.Resume() : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Resume())

    /// A snapshot of the group's resource usage. On Windows this reads the Job Object's accounting
    /// (CPU + peak committed memory + active count); on cgroup v2 the cgroup accounting; on the POSIX
    /// fallback only the live group count (CPU/memory are `None`). Errors once the group is released.
    member this.Stats() : Result<ProcessGroupStats, ProcessError> =
        this.WhenLive(fun () -> backend.Stats())

    /// A periodic `ProcessGroupStats` series: the first sample immediately, then one per `interval`.
    /// **Pull-based** — it samples only as the enumeration is pulled and runs no background task, so
    /// it neither keeps the group alive nor leaks if abandoned. The series ends on the first snapshot
    /// the group fails to report (notably after it is torn down) or when the enumerator's token fires.
    member this.SampleStatsAsync(interval: TimeSpan) : IAsyncEnumerable<ProcessGroupStats> =
        let period =
            if interval <= TimeSpan.Zero then
                TimeSpan.FromMilliseconds 1.0
            else
                // Cap an over-long interval into the armable range so the sampler's `Task.Delay` can't
                // throw synchronously (it then samples at the max ~24.8-day cadence instead of faulting).
                Timeouts.clampArmable interval

        StatsSamplerSeq((fun () -> this.Stats()), period)

    /// Capture a run through this *shared* group, exactly as `JobRunner` captures through a private one:
    /// build a `RunningProcess` over the shared group and run it to completion via the `RunningProcess`
    /// verb, so encoding, line-ending/BOM/trailing-newline normalization, `OkCodes`, and
    /// `OutputBufferPolicy` all match every other runner. If the (CancelOn-linked) token fires, just this
    /// child is killed and the run resolves to `ProcessError.Cancelled`. The child's I/O is detached on
    /// completion (via the run's teardown); the group keeps owning the child until `ShutdownAsync`/`Dispose`.
    member private this.CaptureShared
        (command: Command)
        (cancellationToken: CancellationToken)
        (consume: RunningProcess -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        // Run into THIS shared group — `StartAsync` is `StartShared` + wrap, so the run's teardown
        // detaches only its own I/O and the group keeps owning the child until `Shutdown`/`Dispose`. The
        // register → consume → cancel-map loop is single-sourced with `JobRunner` via `CaptureVerbs`.
        CaptureVerbs.runToCompletion
            command
            cancellationToken
            (fun () -> this.StartAsync(command, cancellationToken))
            consume

    /// Gracefully kill the contained tree (SIGTERM, then SIGKILL after `grace`) WITHOUT releasing
    /// the group — used by per-run timeouts (the run's own teardown releases the group). On Windows
    /// there is no per-job graceful signal, so this is the atomic Job kill.
    member internal _.GracefulKillTree(grace: TimeSpan) : Task = backend.GracefulKillTree grace

    // Deliberate divergence from ProcessKit-rs (`shutdown(escalate_to_kill = false)`): we do NOT offer a
    // graceful shutdown that *keeps the group usable* by sparing the processes that ignore SIGTERM. That
    // mode makes kill-on-drop conditional (spared survivors outlive a drop), and this port keeps the
    // kill-on-drop tree guarantee UNCONDITIONAL. The four teardown needs are still covered without it:
    // immediate+terminal = `Dispose`; immediate+keep-usable = `KillAll`; graceful+terminal = `ShutdownAsync`.

    /// Tear the group down gracefully, then release it. On Unix: SIGTERM, then SIGKILL if still
    /// alive after `gracePeriod`. On Windows: an atomic Job kill. Idempotent with `Dispose`.
    member this.ShutdownAsync(gracePeriod: TimeSpan) : Task =
        task {
            if Interlocked.Exchange(&releasedFlag, 1) = 0 then
                do! backend.GracefulKillTree gracePeriod
                backend.HardRelease()
                GC.SuppressFinalize this
        }
        :> Task

    /// `ShutdownAsync` using the group's configured `Options.ShutdownTimeout`.
    member this.ShutdownAsync() : Task =
        this.ShutdownAsync options.ShutdownTimeout

    // The finalizer is the GC-time safety net for a group that was never disposed: it reaps the
    // tree. Deterministic teardown still comes from `use`/`Dispose`.
    override _.Finalize() = releaseContainer ()

    // A `ProcessGroup` is itself an `IProcessRunner` — every run goes into THIS shared group rather
    // than a fresh private one, so a whole fleet shares one kill-on-dispose container (pass it to
    // `Supervisor.WithRunner`). Captured runs go through the same `RunningProcess` verbs as the default
    // `JobRunner`, so encoding, line-ending/BOM/trailing-newline normalization, `OkCodes`, and the
    // line-level `OutputBufferPolicy` all match every other runner. They honour the per-run
    // `Command.Timeout` (hard-kill of just that child → `TimedOut`: its process group/subtree on POSIX,
    // only the leader process on Windows — descendants stay in the shared Job and are reaped at group
    // teardown, so a Windows descendant that inherited the output pipe and outlives the leader can delay
    // the capture's completion until it too exits) and `CancelOn`. `TimeoutGrace` has no per-child
    // graceful kill in a shared group, so it falls back to the immediate kill.
    interface IProcessRunner with
        member this.SpawnAsync(command, cancellationToken) =
            this.StartAsync(command, cancellationToken)

        member this.CaptureStringAsync(command, cancellationToken) =
            this.CaptureShared command cancellationToken (fun running -> running.OutputStringAsync())

        member this.CaptureBytesAsync(command, cancellationToken) =
            this.CaptureShared command cancellationToken (fun running -> running.OutputBytesAsync())

    interface IDisposable with
        member this.Dispose() =
            releaseContainer ()
            GC.SuppressFinalize this

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            releaseContainer ()
            GC.SuppressFinalize this
            ValueTask.CompletedTask
