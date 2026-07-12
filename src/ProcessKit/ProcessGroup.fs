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

    // The single lifecycle gate. Every operation that must not race teardown — a spawn+track, and each
    // public control/stat verb — runs its released-flag check AND its native backend call inside
    // `lock sync`. So it either completes fully on the live backend, or observes the flag and returns
    // `Unsupported` BEFORE touching native — never half of each. The live->released transition also
    // flips the flag under `sync`: acquiring the lock drains any in-flight op (an op holds `sync` for
    // its whole native call), and once the flag is set every later op bails, so the (bounded) teardown
    // runs with no concurrent backend op. This is the one critical section that used to be an
    // unsynchronized flag re-check plus a per-child escapee-reap dance.
    let sync = obj ()

    // 0 = live, 1 = released. Read and written only under `sync`. Set to 1 exactly once, by whichever
    // of Dispose/DisposeAsync/ShutdownAsync/finalizer wins the transition; that winner then owns the
    // one-shot teardown.
    let mutable releasedFlag = 0

    let waitOutcome (handle: nativeint) : Task<Outcome> = backend.Wait handle

    // Spawn + track as one transaction. The caller runs this under `sync` (via `WhenLive`) so the whole
    // thing is atomic with the release transition: either it tracks the child on a live backend (a later
    // teardown then reaps it exactly once via its drain), or it never runs because the group was already
    // released. A `Track` failure means the backend already killed and reaped the child it could not
    // contain, so there is no live, uncontained child left to clean up here — just surface the error.
    let spawnAndTrack (command: Command) : Result<Native.Common.Spawned, ProcessError> =
        match backend.Spawn command with
        | Error error -> Error error
        | Ok spawned ->
            match backend.Track spawned with
            | Error trackError -> Error trackError
            | Ok() -> Ok spawned

    // Flip live->released exactly once, under `sync`. Returns `true` for the single caller that won the
    // transition; that caller then owns running the teardown. Acquiring `sync` here waits out any
    // in-flight backend op, and setting the flag makes every later op bail — so the winner's teardown
    // has exclusive access without holding the lock across it.
    let claimRelease () : bool =
        lock sync (fun () ->
            if releasedFlag = 0 then
                releasedFlag <- 1
                true
            else
                false)

    // The hard teardown, always under `sync` so it can never interleave with a control/stat/spawn op
    // that raced the release. Only the `claimRelease` winner calls it, so it runs exactly once. Bounded
    // work (SIGKILL + waitpid + close), so holding the monitor across it is fine — unlike ShutdownAsync's
    // unbounded graceful wait, which is deliberately kept OFF the lock.
    let hardRelease () =
        lock sync (fun () -> backend.HardRelease())

    let releaseContainer () =
        if claimRelease () then
            hardRelease ()

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

    /// Test-only seam: wrap an arbitrary containment backend so the lifecycle guard (spawn / control /
    /// stat versus release) can be exercised against a synthetic backend, with no real OS handles.
    /// Internal — production code always goes through `Create`, which picks the platform backend.
    static member internal FromBackend(backend: IContainmentBackend, options: ProcessGroupOptions) : ProcessGroup =
        new ProcessGroup(backend, options)

    /// Wait for one contained process handle to conclude. Internal — used by pipeline staging.
    member internal _.WaitHandle(handle: nativeint) : Task<Outcome> = waitOutcome handle

    /// Hard-kill the contained tree now (no grace) without releasing the group. Internal — used by
    /// pipeline cancellation/timeout.
    member internal _.KillTree() = backend.KillTree()

    // Tree-control / accounting verbs, spawn+track, and the release transition all serialize on `sync`.
    // A verb runs its live check AND its native backend call under the lock, so a concurrent
    // Dispose/ShutdownAsync either waits for it to finish on the live backend or flips the flag first and
    // the verb then returns `Unsupported` before any native call. After teardown the backend's Job handle
    // is closed (and POSIX pgids may have been recycled), so calling in would otherwise be a
    // use-after-close or a wrong-target kill.
    member private _.WhenLive(action: unit -> Result<'T, ProcessError>) : Result<'T, ProcessError> =
        // A released group is a permanent condition, not a transient I/O blip — use `Unsupported`
        // (which `ProcessError.isTransient` rejects) so a retry classifier never re-tries a dead group.
        lock sync (fun () ->
            if releasedFlag <> 0 then
                Error(ProcessError.Unsupported "the process group has been released")
            else
                action ())

    /// Spawn `command` into the group, tracking the child for signalling / teardown. Internal.
    member internal this.SpawnInto(command: Command) : Result<Native.Common.Spawned, ProcessError> =
        // Refuse to spawn into a released group: on Windows the child is created before it is assigned
        // to the (now-closed) Job, so spawning after teardown would leak an UNCONTAINED child. Running
        // the whole spawn+track under `sync` makes this atomic with the release transition — either it
        // completes with the child tracked on the live backend (teardown then reaps it), or the group is
        // already released and it fails fast with a non-transient error BEFORE the native spawn. No child
        // can be tracked in a window teardown has already drained, so no escapee-reap fixup is needed.
        this.WhenLive(fun () -> spawnAndTrack command)

    /// Spawn `command` into the group and build a `RunningHost`. `ownsGroup` decides what disposing
    /// the resulting `RunningProcess` does: when **true** (a private per-run group) it reaps this
    /// whole group; when **false** (a shared group) it only detaches this run's I/O — the group owns
    /// the child's lifetime and reaps it on `ShutdownAsync`/`Dispose`. That ownership choice is the only
    /// difference between the two start paths, so it lives here as one branch each on `StartKill`,
    /// `GracefulKill`, and `Teardown`.
    ///
    /// The run's kill verbs (`StartKill`/`GracefulKill`, driving `RunningProcess.Kill()`/`StopAsync()`
    /// and the timeout/pump-fault kills) route every native kill through the same `sync`/`releasedFlag`
    /// lifecycle gate the tree-control/stat verbs use, plus a per-run `runTornDown` flag — see the
    /// `killWhenLive` comment below for why they must not reach the backend directly.
    member private this.BuildHost(command: Command, ownsGroup: bool) : Result<RunningHost, ProcessError> =
        // Spawn+track AND read the child's pid atomically with the release transition, all under `sync`
        // (via `SpawnInto`/`WhenLive`): a `RunningProcess` is therefore never built over a backend whose
        // teardown has already begun, and reading the pid can't race the Job handle being closed. Once
        // this returns Ok the child is tracked, so a later Dispose/ShutdownAsync reaps it exactly once.
        this.WhenLive(fun () ->
            match spawnAndTrack command with
            | Error error -> Error error
            | Ok spawned -> Ok(spawned, backend.PidOf spawned))
        |> Result.map (fun (spawned, pid) ->
            // The rest of the host is closures and stream references, built outside the lock. They do not
            // touch the container's release state at construction; the kill closures below route each LATER
            // native kill through the lifecycle gate (see `killWhenLive`). The background stdin feed writes
            // to the child's own stdin pipe (not the container), so kicking it off here is race-free.
            let stdinFeeder =
                Pump.feedStdinSource spawned.Stdin command.Config.StdinSource command.Config.KeepStdinOpen

            let closeStreams () =
                // Close the pipe streams (OS handles/fds) before releasing/detaching.
                Pump.closeSpawned spawned

            // Capture this child's OS-reported creation time once, right here at spawn — the identity
            // token `RunningProcess.processMetrics` (T-097) later re-checks before trusting a
            // `Process.GetProcessById pid` read, so a pid recycled for an unrelated process after this
            // child is reaped can't be mistaken for it. `None` when there is no pid, or the read raced a
            // child that had already exited (or is otherwise inaccessible) by the time we asked — the
            // gate then defers to the raw read rather than treating that as proof of anything.
            let startTimeIdentity =
                pid
                |> Option.bind (fun p ->
                    try
                        use proc = Process.GetProcessById p
                        Some proc.StartTime
                    with _ ->
                        None)

            // The lifecycle gate for THIS run's kill verbs. `RunningProcess.Kill()`/`StopAsync()` — and the
            // timeout/pump-fault kills that reuse the same closures — are fire-and-forget entry points a
            // caller can invoke at ANY time: after the (shared) group was released underneath us, or after
            // this run's own teardown. The closures used to call `backend.KillTree`/`backend.KillChild`
            // DIRECTLY, bypassing `sync`/`releasedFlag` — the single lifecycle gate every OTHER mutating
            // verb funnels through. That let a post-release kill `TerminateProcess`/`TerminateJobObject` a
            // Job handle `Release`/`HardRelease` had already closed (use-after-close: the value can be
            // recycled to an unrelated object), or `kill`/`killpg` a pid/pgid the OS has since recycled
            // (wrong-target kill). Routing them through `killWhenLive` runs the released/torn-down check AND
            // the native call in ONE critical section: the kill either fires fully on the live backend or
            // observes the flag and no-ops BEFORE touching native — never half of each.
            //
            // `runTornDown` is this run's per-host analogue of the group's `releasedFlag`: set by `Teardown`
            // under `sync`, it stops the closures touching native after THIS handle is torn down even while a
            // SHARED group stays live for other runs (the wrong-target/use-after-close window when `Kill()`/
            // `StopAsync()` is called after this run's own `Release` dropped its identity token / closed its
            // handle). The group's `releasedFlag` covers the other direction — the shared group released
            // out from under a still-undisposed handle.
            let mutable runTornDown = false

            // A bounded, fire-and-forget native kill (`StartKill`, or a shared run's soft-kill), gated: it
            // reaches the backend only while the group is live AND this run has not been torn down. Bounded
            // native work (terminate / SIGKILL / handle close), so holding the monitor across it is fine —
            // exactly like `KillAll`/`Signal`.
            let killWhenLive (nativeKill: unit -> unit) : unit =
                lock sync (fun () ->
                    if releasedFlag = 0 && not runTornDown then
                        nativeKill ())

            // The graceful analogue for the owned tree kill (`StopAsync`/timeout-grace): begin the
            // bounded-by-grace graceful kill only on a live, not-torn-down run; otherwise a no-op. Its
            // SIGTERM prefix runs under `sync` (safe against the release transition); the bounded
            // poll+SIGKILL that follow run off the lock, exactly as `ShutdownAsync`'s own graceful wait is
            // deliberately kept off it.
            let gracefulKillWhenLive (nativeGracefulKill: unit -> Task) : Task =
                lock sync (fun () ->
                    if releasedFlag = 0 && not runTornDown then
                        nativeGracefulKill ()
                    else
                        Task.CompletedTask)

            { Config = command.Config
              Pid = pid
              Stdout = spawned.Stdout
              Stderr = spawned.Stderr
              Stdin =
                // Hand `TakeStdin` a live pipe only when one is kept open past the feeder: `KeepStdinOpen`
                // (with or without a source) leaves the parent write-end open for interactive writing. A
                // source WITHOUT `KeepStdinOpen` is the child's complete input — the feeder closes the pipe
                // once it is drained — so there is no interactive stream to hand out (`None`). No source and
                // no `KeepStdinOpen` keeps no pipe at all, so `spawned.Stdin` is already `None` there.
                (match command.Config.StdinSource with
                 | Some _ when not command.Config.KeepStdinOpen -> None
                 | _ -> spawned.Stdin)
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = startTimeIdentity
              Wait = (fun () -> waitOutcome spawned.Handle)
              // Observe the stashed source failure without blocking: `StdinFeeder.Fault` reads it only
              // once the feed has finished, else `None`. Race-free — `feedStdin` never faults, so
              // completion establishes the happens-before for the stashed value. We deliberately do NOT
              // await the feed (a source parked mid-read would hang the verb), so an async-manifesting
              // source fault that loses the race with the child's own exit is not surfaced — matching
              // ProcessKit-rs. The common case, a missing `FromFile`, faults synchronously at spawn
              // (`File.OpenRead`), so it is always observed.
              StdinError = (fun () -> stdinFeeder.Fault)
              // Block until the background feeder has drained the source, so `TakeStdin` (on a
              // `Stdin(source)` + `KeepStdinOpen` run) never hands the caller a pipe the feeder is still
              // writing to. `feedStdin` never faults (every exception is swallowed into a stashed fault) and
              // never leaves its returned task faulted/cancelled, so awaiting it can't throw; a stopped feed
              // (teardown) also completes, so this can never hang past teardown. The no-source /
              // nothing-to-feed feeder's task is already completed, so this returns immediately there.
              StdinFeedComplete = (fun () -> stdinFeeder.Task.GetAwaiter().GetResult() |> ignore)
              StartKill =
                (if ownsGroup then
                     (fun () -> killWhenLive backend.KillTree)
                 else
                     (fun () -> killWhenLive (fun () -> backend.KillChild spawned)))
              GracefulKill =
                (if ownsGroup then
                     (fun grace -> gracefulKillWhenLive (fun () -> this.GracefulKillTree grace))
                 else
                     (fun _ ->
                         // Shared group: no per-child graceful signal, so this is the immediate child kill
                         // (documented `StopAsync`/`TimeoutGrace` degradation), gated the same way.
                         killWhenLive (fun () -> backend.KillChild spawned)
                         Task.CompletedTask))
              Teardown =
                fun () ->
                    // Stop the stdin feeder first: cancelling its lifecycle token unblocks a feed parked
                    // in the user's source (a hung `FromAsyncLines`) so it unwinds and disposes the
                    // user's enumerator/stream, instead of leaking it past teardown. Idempotent, so the
                    // redundant call on a second Teardown (verb reapGuard, then handle disposal) is
                    // harmless. Closing the stdin stream below still covers a write-parked feed.
                    stdinFeeder.Stop()
                    closeStreams ()

                    // Mark this run torn down (under `sync`) BEFORE detaching/reaping, so a `Kill()`/
                    // `StopAsync()` that races or follows this teardown observes the flag and no-ops rather
                    // than signalling a child this run no longer owns (see `runTornDown`).
                    lock sync (fun () -> runTornDown <- true)

                    if ownsGroup then
                        // Owned group: closing the run reaps the whole tree.
                        (this :> IDisposable).Dispose()
                    else
                        // Shared group: detach this run's I/O only — the GROUP owns the child's
                        // lifetime (Shutdown/Dispose reaps it). Stop tracking it — POSIX only
                        // once its group is empty, so a still-live child stays reapable by the
                        // group; Windows closes the handle (the Job still contains the tree).
                        backend.Release spawned

                    ValueTask.CompletedTask })

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
    ///
    /// `cancellationToken` is checked once, before the spawn (an already-cancelled token reports
    /// `ProcessError.Cancelled` and starts nothing); once the child is running the token is not tracked —
    /// this live handle is caller-driven, so kill or reap it yourself. (The capture/completion verbs on a
    /// `ProcessGroup` do watch the token — and the command's `CancelOn` — for the whole run.)
    member this.StartAsync
        (command: Command, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<RunningProcess, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match this.StartShared command with
                | Error error -> return Error error
                | Ok host ->
                    // `RunningProcess.buildGuarded` (shared with `JobRunner.start`) reaps the tree via
                    // `host.Teardown()` and re-raises should the constructor ever fault, so a shared
                    // group's spawn gets the same defence-in-depth as a private, per-run group.
                    let! running = RunningProcess.buildGuarded host
                    return Ok running
        }

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
    /// still receives the signal.
    ///
    /// On **Windows** `Signal.Kill` maps to the atomic Job terminate. `Signal.Int` and `Signal.Term`
    /// are delivered as a best-effort console **CTRL+BREAK**, but ONLY to children started with
    /// `Command.WindowsCtrlSignals()` (spawned in their own console process group): if the group has
    /// no such child, or the caller has no console to share with it, the call returns
    /// `ProcessError.Unsupported` rather than silently downgrading to a kill. Delivery is not
    /// guaranteed even on success — a console child may install its own handler. Every other Windows
    /// signal returns `ProcessError.Unsupported`.
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
            // Win the transition first (flag flipped under `sync`), so from here no StartAsync/Signal/
            // Stats/... touches the backend. The graceful wait is UNBOUNDED (up to `gracePeriod`), so it
            // runs with NO lock held — the flag already fends off every new op. Only the winner reaches
            // the wait, so the one-shot teardown that follows runs exactly once.
            if claimRelease () then
                do! backend.GracefulKillTree gracePeriod
                hardRelease ()
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
