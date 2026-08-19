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
/// Object (`KILL_ON_JOB_CLOSE`), a Linux cgroup v2 (when resource limits are requested), a FreeBSD
/// `procctl(2)` process reaper (whole-tree, `setsid` escapees included), or a POSIX process group
/// (`killpg` teardown). All of that lives behind an `IContainmentBackend`; this type only orchestrates
/// once-only teardown, the stdin/stream wiring, and the runner/disposable seams.
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

    // The group's live options snapshot. `options` is the CREATE-time value; `UpdateLimits` swaps in a
    // new set here (under `sync`, only AFTER the backend applied the new limits) so `member _.Options`
    // reads back the caps actually in force. A plain reference field: a read is atomic (a consumer sees
    // either the old or the new immutable snapshot, never a torn one), and every write is serialized
    // under the lifecycle lock alongside the backend call it reflects.
    let mutable currentOptions = options

    // The STICKY record of which resource-limit axes this group has ever configured — recorded here at
    // construction and again at every `UpdateLimits` call (see that member: an axis it NAMES joins this
    // record whether the apply then succeeds or fails), so `LimitEvidence` stays honest even after a cap
    // is later lifted. Read only under `sync`, alongside the backend call it feeds.
    let mutable cappedAxes = CappedAxes.None.Record options.Limits

    // The evidence captured exactly once, from the still-live backend, in `hardRelease` — the instant
    // before its counters (and, for a Linux cgroup v2 group, the cgroup directory itself) are torn down.
    // `None` until teardown has run; `Some` forever after, so any number of later `LimitEvidence()` reads
    // return this same immutable snapshot rather than a stale or re-derived one.
    let mutable capturedLimitEvidence: LimitEvidence option = None

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
        lock sync (fun () ->
            // Capture the limit evidence from the STILL-LIVE backend before it tears the container down —
            // the cgroup v2 counters this reads (and the directory itself) do not survive `HardRelease`.
            // Runs inside the same critical section as every other backend call here, so it can never
            // interleave with a concurrent `LimitEvidence()` read (which also takes `sync`).
            capturedLimitEvidence <- Some(backend.LimitEvidence cappedAxes)
            backend.HardRelease())

    let releaseContainer () =
        if claimRelease () then
            hardRelease ()

    /// The OS primitive containing this group on the current platform.
    member _.Mechanism = backend.Mechanism

    /// The options the group is currently configured with (shutdown grace, resource limits). The
    /// resource limits reflect the latest successful `UpdateLimits`, not only the create-time set.
    member _.Options = currentOptions

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

        // WHICH mechanism these options select here — and the typed refusal when they cannot be honoured
        // at all — is decided by `MechanismSelection`, the one place that owns that decision. It is shared
        // with `ProcessGroup.Capabilities` so a snapshot can never predict a mechanism this call would not
        // actually pick, or call a refusal survivable. Everything BELOW the decision stays here: creating
        // the native container is exactly the part that has side effects and its own failures, which is
        // why a capability probe cannot run it.
        match MechanismSelection.choose options with
        | MechanismChoice.Refused error -> Error error
        | MechanismChoice.Selected Mechanism.JobObject ->
            match Native.Windows.createWindowsJob () with
            | Error error -> Error error
            | Ok job ->
                if limits.Any then
                    match Native.Windows.applyWindowsJobLimits job limits with
                    | Ok() -> withBackend (JobObjectBackend(job, limits))
                    | Error message ->
                        Native.Windows.closeWindowsHandle job

                        if limits.IoMax.IsSome && Native.Windows.isIoRateControlUnsupported message then
                            Error(ProcessError.Unsupported message)
                        else
                            Error(ProcessError.ResourceLimit message)
                else
                    withBackend (JobObjectBackend(job, limits))
        | MechanismChoice.Selected Mechanism.CgroupV2 ->
            match Native.Cgroup.createCgroup limits with
            | Ok path -> withBackend (CgroupBackend(path, limits))
            | Error message -> Error(ProcessError.ResourceLimit message)
        | MechanismChoice.Selected Mechanism.ProcessGroup ->
            // No whole-tree limits: the POSIX group forms when children are spawned (each becomes its own
            // pgid). A CPU-time-only cap rides along per child through `RLIMIT_CPU`.
            withBackend (ProcessGroupBackend limits)
        | MechanismChoice.Selected Mechanism.ProcessReaper ->
            // FreeBSD: take reaper status for this process — once, permanently, process-wide — and layer
            // the whole-tree `procctl(2)` reaper over the very same POSIX process group the branch above
            // builds. This is the only place the acquisition is ever attempted: it is a real side effect,
            // so the capability snapshot predicts this mechanism without performing it.
            //
            // A refused acquisition is NOT a creation failure and NOT a silent claim: the group is built on
            // the plain process group instead and reports `Mechanism.ProcessGroup`, `setsid` hole included,
            // rather than telling a caller it holds a containment guarantee this process could not take.
            let posix = ProcessGroupBackend limits

            if Native.FreeBsd.acquireReaperStatus () then
                withBackend (ProcessReaperBackend(posix, limits))
            else
                withBackend posix

    /// What process containment can actually do on **this** host, for the default options — a
    /// side-effect-free snapshot taken WITHOUT creating a group or spawning anything. See the
    /// `ProcessGroupOptions` overload.
    static member Capabilities() : ContainmentCapabilities =
        ProcessGroup.Capabilities(ProcessGroupOptions())

    /// What process containment can actually do on **this** host for `options` — the mechanism
    /// `Create(options)` would select, and, per axis, either a real availability or a typed
    /// `Capability.Unsupported` naming the precondition that is missing. Taken WITHOUT creating a group,
    /// spawning a process, or touching any container, so a long-lived orchestrator can choose a portable
    /// policy *before* the first spawn instead of creating a group and probing each operation.
    ///
    /// The mechanism it reports comes from the very decision `Create` dispatches on, and each capability
    /// from the very probe the corresponding spawn path consults, so the snapshot cannot drift from what
    /// the real calls do. It is a point-in-time report and deliberately not cached — see
    /// `ContainmentCapabilities` for what that does and does not promise.
    static member Capabilities(options: ProcessGroupOptions) : ContainmentCapabilities =
        ArgumentNullException.ThrowIfNull options
        CapabilityProbe.current options

    /// Test-only seam: wrap an arbitrary containment backend so the lifecycle guard (spawn / control /
    /// stat versus release) can be exercised against a synthetic backend, with no real OS handles.
    /// Internal — production code always goes through `Create`, which picks the platform backend.
    static member internal FromBackend(backend: IContainmentBackend, options: ProcessGroupOptions) : ProcessGroup =
        new ProcessGroup(backend, options)

    /// Wait for one contained process handle to conclude. Internal — used by pipeline staging.
    member internal _.WaitHandle(handle: nativeint) : Task<Outcome> = waitOutcome handle

    /// Hard-kill the contained tree now (no grace) without releasing the group. Internal — used by
    /// pipeline cancellation/timeout, which are fire-and-forget sweeps with no result channel of their
    /// own: the pipeline's honest answer is the `Cancelled`/`TimedOut` outcome its stages resolve to, so
    /// a refused kill shows up as a stage that did not conclude, never as a fabricated success. The public
    /// `KillAll` is the verb that reports the kill's real outcome.
    member internal _.KillTree() = backend.KillTree() |> ignore

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
    member private this.BuildHost
        (command: Command, ownsGroup: bool)
        : Result<RunningHost * (int * Stream) list, ProcessError> =
        // THE launch boundary for a one-shot stdin payload (`FromStream`/`FromLines`/`FromAsyncLines`).
        // Every real child ProcessKit starts on its own — `StartAsync`, the capture verbs, streaming,
        // supervision, whichever runner drives them — is spawned right here, so taking the payload here
        // is what makes "at most one incarnation ever reads it" true for all of them at once, instead of
        // for the handful of high-level paths that used to guard it individually.
        //
        // Reserved BEFORE the spawn: a second run — concurrent, or merely later — is refused with a
        // typed `Unsupported` while it still has no child of its own, rather than creating one and then
        // feeding it the exhausted remains of someone else's payload. A run that already owns the
        // payload (`Runner.withRetry`, whose hold rides on the command) is lending it to its own
        // attempt and passes straight through.
        let launchResult =
            OneShotStdin.reserveLaunch command.Program command.Config.StdinSource command.Config.StdinReservation

        let spawnResult =
            match launchResult with
            | Error error -> Error error
            | Ok launch ->
                // Spawn+track AND read the child's pid atomically with the release transition, all under
                // `sync` (via `WhenLive`): a `RunningProcess` is therefore never built over a backend whose
                // teardown has already begun, and reading the pid can't race the Job handle being closed.
                // Once this returns Ok the child is tracked, so a later Dispose/ShutdownAsync reaps it
                // exactly once.
                //
                // The commit rides INSIDE that same critical section, on the success arm: it is the very
                // step that makes the payload spent, so it belongs with the spawn+track transaction it
                // witnesses rather than alongside it — and it lands well before `stdinFeeder` (built
                // below, off the lock) can read a byte. Deliberately no new synchronization of its own
                // (K-025): the claim's state is a lock-free `Interlocked`/`Volatile` cell owned by
                // `OneShotStdinClaim`, and this call is a single bounded write, exactly like the other
                // bounded native work the group runs under `sync`.
                let spawnOutcome =
                    try
                        this.WhenLive(fun () ->
                            match spawnAndTrack command with
                            | Error error -> Error error
                            | Ok spawned ->
                                launch.Commit()
                                Ok(spawned, backend.PidOf spawned))
                    with _ ->
                        // The spawn RAISED instead of reporting a typed failure. Not swallowed — the
                        // exception is re-raised unchanged — but the launch must still be settled before
                        // it leaves this boundary, or the hold outlives the launch that took it: an
                        // owned one would strand the payload for the life of the source, a lent one
                        // would lock the run out of its own next attempt. `Rollback` only ever moves a
                        // still-*reserved* payload back, so on the (equally possible) throw from AFTER
                        // `Commit` — `backend.PidOf` — it correctly does nothing to a spent payload.
                        launch.Rollback()
                        reraise ()

                match spawnOutcome with
                | Ok tracked -> Ok tracked
                | Error error ->
                    // No child exists — the program was never found, the spawn or the track failed, an
                    // up-front capability was refused, or the group was already released — so nothing can
                    // have read the payload: hand it back for the next attempt or run. Committing only on
                    // the success arm and releasing here is what keeps "a failed launch leaves the source
                    // intact" true while "a launched child spends it" stays irreversible, even if the feed
                    // itself fails afterwards.
                    launch.Rollback()
                    Error error

        spawnResult
        |> Result.map (fun (spawned, pid) ->
            // The rest of the host is closures and stream references, built outside the lock. They do not
            // touch the container's release state at construction; the kill closures below route each LATER
            // native kill through the lifecycle gate (see `killWhenLive`). The background stdin feed writes
            // to the child's own stdin pipe (not the container), so kicking it off here is race-free.
            let stdinFeeder =
                Pump.feedStdinSourceWithEncoding
                    command.Config.StdinEncoding
                    spawned.Stdin
                    command.Config.StdinSource
                    command.Config.KeepStdinOpen

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

            let signalWhenLive (signal: Signal) : Result<unit, ProcessError> =
                lock sync (fun () ->
                    if releasedFlag = 0 && not runTornDown then
                        if ownsGroup then
                            // A private per-run group is this run's exact containment unit. Use the
                            // backend's whole-container path (notably cgroup pidfd delivery) so the full
                            // descendant tree receives the signal with the strongest identity guarantee.
                            backend.Signal signal
                        else
                            backend.SignalChild(spawned, signal)
                    else
                        Error(ProcessError.Unsupported "the run has already been torn down"))

            // The graceful analogue for the owned tree kill (`StopAsync`/timeout-grace): begin the
            // bounded-by-grace graceful kill only on a live, not-torn-down run; otherwise a no-op. Its
            // soft-signal prefix runs under `sync` (safe against the release transition); the bounded
            // poll+hard-kill that follow run off the lock, exactly as `ShutdownAsync`'s own graceful wait is
            // deliberately kept off it.
            let gracefulKillWhenLive (nativeGracefulKill: unit -> Task) : Task =
                lock sync (fun () ->
                    if releasedFlag = 0 && not runTornDown then
                        nativeGracefulKill ()
                    else
                        Task.CompletedTask)

            // The pty-resize closure, built ONLY for a `Command.Pty` run — a retained pseudoconsole
            // handle / pty master fd survives in `spawned.PtyControl`; a non-PTY run leaves it `None`, so
            // `RunningProcess.ResizeAsync` reports a typed `Unsupported` (D6). `spawned.Handle` is the child
            // pid on POSIX (the `SIGWINCH` target); on Windows the resize goes through the pseudoconsole
            // handle alone.
            //
            // Gated through the SAME `sync`/`releasedFlag` + `runTornDown` lifecycle gate as the kill
            // closures above (T-203, mirroring T-093). `RunningProcess.ResizeAsync` is a fire-and-forget
            // verb a caller can invoke at ANY time — after a terminal verb's `reapGuard` has concluded and
            // reaped this run, or after the handle was disposed. Either teardown's `closeStreams` closes the
            // pty master fd (POSIX) / pseudoconsole handle (Windows) held in `spawned.PtyControl`. Unlike a
            // recycled *pid*, an fd NUMBER is reused IMMEDIATELY by the same process, so a late UNGATED
            // resize would `ioctl(TIOCSWINSZ)` a STRANGER's fd — a concurrent run's socketpair/pty/redirect
            // that inherited the number (a wrong-target mutation, or a misleading `Io` failure) — and
            // deliver `SIGWINCH` to a possibly-recycled pid: exactly the wrong-target class T-083/T-084/
            // T-093/T-097 closed for the kill/signal/metrics paths. On Windows the `hPC` can likewise be
            // closed and its value recycled onto an unrelated object (a use-after-close). Running the
            // released/torn-down check AND the native resize in ONE critical section refuses a post-teardown
            // resize with a typed, non-transient `Unsupported` (as `WhenLive` does for a released group)
            // BEFORE touching native. The gate is only as tight as the ORDER in which teardown raises the
            // flag versus releasing the resource: `Teardown` sets `runTornDown` UNDER `sync` BEFORE
            // `closeStreams` closes the POSIX master fd (T-203, R-01), so on POSIX a concurrent resize either
            // fires fully on the live pty or observes the flag and no-ops — never half of each. On WINDOWS
            // the `hPC` is instead closed by the child-exit callback (`closePseudoConsoleOnChildExit`), a
            // different lifecycle event this gate cannot lead; that leaves a documented, strictly WEAKER
            // residual window (an `HPCON` is not recycled with an fd's immediacy, and `ResizePseudoConsole`
            // returns a typed `Io` failure on a stale handle, never a silent wrong-target) — see `Teardown`.
            // A live run resizes exactly as before, and this keeps `ResizeAsync` non-consuming (K-031).
            // Bounded native work under the lock — an `ioctl` + best-effort `SIGWINCH`, or one
            // `ResizePseudoConsole` — exactly like `killWhenLive`/`Signal`.
            let resizePty =
                spawned.PtyControl
                |> Option.map (fun control ->
                    let childHandle = spawned.Handle

                    fun (cols: int, rows: int) ->
                        lock sync (fun () ->
                            if releasedFlag = 0 && not runTornDown then
                                if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                                    Native.Windows.resizePseudoConsole control cols rows
                                else
                                    Native.Posix.resizePty control childHandle cols rows
                            else
                                Error(ProcessError.Unsupported "the run has been torn down")))

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
              // The bounded FINAL observation of the stashed source failure, made by a Result-producing
              // verb once its child has exited with an accepted code and its output drains have finished
              // (`RunningProcess.stdinErrorOnSuccess`). Race-free — `feedStdin` never faults, so the feed's
              // completion establishes the happens-before for the stashed value. A feed that has already
              // finished (the common case: a missing `FromFile` faults at spawn in `File.OpenRead` and then
              // only has to end the child's stdin) answers with no wait; a source still reading when the
              // child exited gets a short bounded window to conclude, and is STOPPED — never awaited — once
              // it runs out, so a hung `FromStream`/`FromLines`/`FromAsyncLines` can delay the verb by at
              // most that budget and can never hang it. Peeking once here (as this did before) instead
              // dropped a genuine source failure that lost the race with a fast child's own exit, turning
              // it into a silent success; ProcessKit-rs closed the same race the same way
              // (`87d6ca498696`/`bc2e43efab45`).
              StdinError = (fun () -> stdinFeeder.ObserveFaultAsync())
              // Block until the background feeder has drained the source, so `TakeStdin` (on a
              // `Stdin(source)` + `KeepStdinOpen` run) never hands the caller a pipe the feeder is still
              // writing to. `feedStdin` never faults (every exception is swallowed into a stashed fault) and
              // never leaves its returned task faulted/cancelled, so awaiting it can't throw; a stopped feed
              // (teardown) also completes, so this can never hang past teardown. The no-source /
              // nothing-to-feed feeder's task is already completed, so this returns immediately there.
              // The blocking `GetResult` is deadlock-safe even when `TakeStdin` is called from a
              // single-threaded `SynchronizationContext` (WPF/WinForms/classic ASP.NET): the feed runs
              // detached on the thread pool (`feedStdin` is a `backgroundTask`), so it keeps making progress
              // while this thread is parked here rather than waiting to post a continuation back to it.
              StdinFeedComplete = (fun () -> stdinFeeder.Task.GetAwaiter().GetResult() |> ignore)
              StartKill =
                (if ownsGroup then
                     (fun () -> killWhenLive (fun () -> backend.KillTree() |> ignore))
                 else
                     (fun () -> killWhenLive (fun () -> backend.KillChild spawned)))
              Signal = signalWhenLive
              GracefulKill =
                (if ownsGroup then
                     (fun grace ->
                         gracefulKillWhenLive (fun () -> this.GracefulKillTree(command.Config.StopSignal, grace)))
                 else
                     (fun _ ->
                         // Shared group: no per-child graceful signal, so this is the immediate child kill
                         // (documented `StopAsync`/`TimeoutGrace` degradation), gated the same way.
                         killWhenLive (fun () -> backend.KillChild spawned)
                         Task.CompletedTask))
              ResizePty = resizePty
              TreeStats =
                if ownsGroup then
                    Some(fun () ->
                        match this.Stats() with
                        | Ok stats -> Some stats
                        | Error _ -> None)
                else
                    None
              Teardown =
                fun () ->
                    // Stop the stdin feeder first: cancelling its lifecycle token unblocks a feed parked
                    // in the user's source (a hung `FromAsyncLines`) so it unwinds and disposes the
                    // user's enumerator/stream, instead of leaking it past teardown. Idempotent, so the
                    // redundant call on a second Teardown (verb reapGuard, then handle disposal) is
                    // harmless. Closing the stdin stream below still covers a write-parked feed.
                    stdinFeeder.Stop()

                    // Mark this run torn down (under `sync`) BEFORE `closeStreams` releases the pipe fds —
                    // "flag first, then release the resource", mirroring the kill path (T-093, whose flag
                    // precedes `Dispose`) and the shared-group `Release` below (T-204). `closeStreams` closes
                    // the pty master fd held in `spawned.PtyControl` (POSIX: `closeSpawned` disposes
                    // `spawned.Stdout`, the sole owner that closes the master fd), and an fd NUMBER is recycled
                    // IMMEDIATELY by a concurrent spawn of an UNRELATED run — unlike a pid. Were the flag raised
                    // only AFTER `closeStreams` (as before this fix), a `ResizeAsync` that took `sync` in the
                    // gap between the fd close and the flag would see `releasedFlag = 0 && not runTornDown` and
                    // `ioctl(TIOCSWINSZ)` + `kill(pid, SIGWINCH)` a closed-and-recycled fd/pid: the wrong-target
                    // mutation of a STRANGER's run this task exists to close, merely NARROWED but not shut. With
                    // the flag first, a concurrent resize either fires fully on the still-live pty (flag not yet
                    // observed) or sees `runTornDown` and returns `Unsupported` — never half of each. Hoisting
                    // it is safe for both arms: reaping (`Dispose` / `backend.Release`) does not depend on the
                    // flag's value — the flag gates only the fire-and-forget kill/resize closures, which are a
                    // no-op during teardown regardless.
                    //
                    // WINDOWS residual window (documented, not closed — R-01). The pseudoconsole `hPC` is NOT
                    // closed by `closeStreams`; it is closed by `closePseudoConsoleOnChildExit`'s thread-pool
                    // wait callback the instant the CHILD EXITS (Native.Windows.fs), which MUST fire to flush
                    // conhost and let the parent's merged-output pump reach EOF — it cannot be deferred to this
                    // gate without hanging capture/streaming. That close is driven by child exit, a DIFFERENT
                    // lifecycle event than teardown, so it races `runTornDown` and a concurrent `ResizeAsync`
                    // can still reach `ResizePseudoConsole` on a just-closed `hPC`. This is a strictly WEAKER
                    // exposure than the POSIX fd case: an `HPCON` value is not recycled with the immediacy of an
                    // fd number, and `ResizePseudoConsole` type-checks its handle and surfaces a non-zero
                    // HRESULT as a typed `Io` failure (Native.Windows.fs) — a bounded typed error, never a
                    // silent wrong-target success. Fully closing it would require threading `sync`/the flag into
                    // that native child-exit callback — a second, independent lifecycle primitive (K-016 warns
                    // against exactly that) disproportionate to the residual risk.
                    lock sync (fun () -> runTornDown <- true)
                    closeStreams ()

                    if ownsGroup then
                        // Owned group: closing the run reaps the whole tree. `runTornDown` is already set
                        // (above), so a `Kill()`/`StopAsync()` that races or follows this teardown observes the
                        // flag and no-ops rather than signalling a child this run no longer owns.
                        //
                        // This is also where the remaining tree of a leader that spawned something which
                        // inherited its stdout/stderr is reaped. Nothing extra is needed for that case: the
                        // whole point of `RunningProcess`'s bounded post-exit output drain (`PostExitDrain`)
                        // is that the verb REACHES this teardown instead of waiting on the pipe that
                        // descendant holds open — an unbounded pump join used to leave the private group
                        // this call releases alive, and its survivors with it.
                        (this :> IDisposable).Dispose()
                    else
                        // Shared group: detach this run's I/O only — the GROUP owns the child's lifetime
                        // (Shutdown/Dispose reaps it), and so does any descendant of it that outlived the
                        // leader. That asymmetry with the owned arm above is deliberate and is what the
                        // bounded post-exit output drain relies on: severing this run's read ends is a
                        // per-HANDLE act with no effect on anyone else's run in the same container, while
                        // killing the remainder of a tree here would reach into a group this handle does not
                        // own. `runTornDown` was set above (before `closeStreams`); here
                        // we only stop tracking the child, still under `sync` and still AFTER the flag (T-204's
                        // required order is preserved — the flag now merely leads by a wider margin). Both must
                        // serialize against the control verbs: `runTornDown` fends off a later `Kill()`/
                        // `StopAsync()`, and `backend.Release` must not race `Signal`. On Windows `Release` drops
                        // the child's `ctrlGroups` entry AND closes its process handle, freeing its pid for OS
                        // reuse; the console process-group id in that entry is exactly what `Signal(Int/Term)`
                        // targets. `Signal` snapshots `ctrlGroups.Values` and delivers every
                        // `GenerateConsoleCtrlEvent` CTRL+BREAK while holding `sync` — so running this `Release`
                        // OFF the lock (as the original pre-T-204 code did) let it drop the entry and close the
                        // handle midway through that delivery loop, after which a CTRL+BREAK could land on a pid
                        // the OS had already recycled onto an unrelated console process group (the wrong-target
                        // class T-084 closed for POSIX kill and T-162 for the Windows Job handle, left open on
                        // this path). Serializing `Release` under `sync` restores the documented `ctrlGroups`
                        // invariant: an entry's handle stays open for the whole of any concurrent `Signal`
                        // delivery. It is bounded native work — a handle close on Windows, a pgid liveness probe
                        // on POSIX — exactly the release work it already did, now merely under the lock like
                        // `KillChild`/`hardRelease`, so teardown is not lengthened beyond it. POSIX stops
                        // tracking only once the child's group is empty, so a still-live child stays reapable by
                        // the group.
                        lock sync (fun () -> backend.Release spawned)

                    ValueTask.CompletedTask },
            spawned.ExtraFds)

    /// Spawn `command` into the group and build a `RunningHost`. Disposing the resulting
    /// `RunningProcess` reaps this whole group (the group is owned by the running process).
    member internal this.StartInternal(command: Command) : Result<RunningHost * (int * Stream) list, ProcessError> =
        this.BuildHost(command, ownsGroup = true)

    /// Spawn `command` into this *shared* group and build a `RunningHost`. Unlike `StartInternal`,
    /// disposing the resulting `RunningProcess` does **not** reap the group — the group owns the
    /// child's lifetime (reaped on `ShutdownAsync`/`Dispose`). Internal — `StartAsync` wraps it.
    member internal this.StartShared(command: Command) : Result<RunningHost * (int * Stream) list, ProcessError> =
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
                | Ok(host, extraFds) ->
                    // `RunningProcess.buildGuarded` (shared with `JobRunner.start`) reaps the tree via
                    // `host.Teardown()` and re-raises should the constructor ever fault, so a shared
                    // group's spawn gets the same defence-in-depth as a private, per-run group.
                    let! running = RunningProcess.buildGuardedWithExtraFds host extraFds
                    return Ok running
        }

    /// Adopt an already-running **external** process — one this group did not start (created by other
    /// code, inherited from another layer, or located by pid) — into the container, so from now on it
    /// obeys the same whole-tree rules as a process started with `StartAsync`: kill-on-dispose, and
    /// participation in `Signal`/`Suspend`/`Resume`/`Members`/`MembersInfo`/`Stats` and any resource
    /// limits. This restores the "kill the whole tree" guarantee for a wrapper whose child was not
    /// launched through ProcessKit.
    ///
    /// **The argument is a `System.Diagnostics.Process`, not a bare pid — deliberately.** A raw pid is
    /// subject to number reuse: between the caller obtaining it and the adopt landing, the OS may have
    /// recycled it onto an unrelated process, which the adopt would then contain by mistake. A live
    /// `Process` holds an open OS handle to the target, which on **Windows** pins the pid (the OS will not
    /// reuse it while a handle is open), so the adopt cannot race a recycle; this call keeps the `Process`
    /// alive across the native adopt for exactly that reason. On **Linux** there is no handle that pins a
    /// pid, so the `Process` gives the pre-adopt `HasExited` guard and a narrower window, but a pid
    /// recycled in the residual window cannot be fully ruled out by number alone — the honest limitation
    /// is documented, not hidden.
    ///
    /// A caller who has only a number — from a pidfile, a registry, an FFI or IPC boundary — cannot use
    /// this overload at all, and `AdoptByPid` is the door for that case: it takes an identity anchor of
    /// its own for whatever the number currently names (the process object on Windows, cgroup membership
    /// on cgroup v2, a re-verified start-time token on the POSIX process group) rather than trusting the
    /// number. It is a strictly weaker guarantee than the pinning handle THIS overload gets on Windows,
    /// because nothing can close the window before the call — so where a live `Process` is available, this
    /// one stays the better choice.
    ///
    /// **Per-platform behaviour** (honest and typed, never a silent no-op):
    ///  * **Windows (Job Object)** — `AssignProcessToJobObject`; supported with or without limits.
    ///  * **Linux cgroup v2** — writes the pid to the group's `cgroup.procs`; available only on a group
    ///    created **with** resource limits (which is what selects the cgroup mechanism). A plain,
    ///    limit-free Linux group uses the POSIX process-group mechanism and cannot adopt (below).
    ///  * **POSIX process group (macOS/BSD, or Linux without limits)** — `ProcessError.Unsupported`:
    ///    `setpgid` only relocates our own children before they `exec`, so a foreign process cannot be
    ///    moved into our group at all.
    ///
    /// **Edge cases**, each a distinct typed failure rather than a fabricated success:
    ///  * a `null` argument throws `ArgumentNullException` (a programming error, surfaced eagerly);
    ///  * a process that has already exited, or a pid that no longer exists (a TOCTOU race lost to the
    ///    target's own exit), returns `ProcessError.Adopt`;
    ///  * missing rights to the foreign process returns `ProcessError.Adopt` (a typed error, not an
    ///    escaping exception);
    ///  * a Windows process already assigned to a Job that does not permit nesting on this OS
    ///    configuration returns `ProcessError.Adopt` — never a "looks like it worked" no-op.
    ///
    /// **The adopted process is not our child**, so ProcessKit does **not** reap it via `waitpid` and does
    /// **not** signal its process group (which it does not own): the container primitive alone contains and
    /// kills it — the Job's `KILL_ON_JOB_CLOSE` / a cgroup `cgroup.kill` at teardown — while its real
    /// parent (or `init`, once reparented) reaps it. The **exit-observation (wait) path is the caller's
    /// own `Process`** (`Process.WaitForExitAsync` / `HasExited`); this method adds containment, and returns
    /// `Result<unit, _>` rather than a `RunningProcess`, because the external process's stdio is not ours
    /// to stream.
    ///
    /// Routed through the same lifecycle gate as the other control verbs: adopting into a released group
    /// returns a non-transient `ProcessError.Unsupported` before touching the closed/removed native
    /// container, never a use-after-teardown.
    member this.Adopt(externalProcess: Process) : Result<unit, ProcessError> =
        ArgumentNullException.ThrowIfNull externalProcess

        // Read the pid + liveness OFF the lifecycle lock (they touch the caller's Process, not our
        // container). Both throw for a Process with no associated OS process (never started / already
        // disposed); such a caller gets an honest typed refusal, not an escaping exception. A live
        // external process reports HasExited = false.
        let pidResult =
            try
                let pid = externalProcess.Id

                if externalProcess.HasExited then
                    Error(
                        ProcessError.Adopt(
                            pid,
                            "the process has already exited; a concluded process cannot be adopted (adopt it while it is live)"
                        )
                    )
                else
                    Ok pid
            with
            | :? InvalidOperationException ->
                Error(
                    ProcessError.Adopt(
                        0,
                        "the argument is not a started, live process (no OS process is associated with it)"
                    )
                )
            | :? System.ComponentModel.Win32Exception as ex ->
                Error(ProcessError.Adopt(0, $"could not determine the process's liveness: {ex.Message}"))

        match pidResult with
        | Error error -> Error error
        | Ok pid ->
            // Adopt on the LIVE backend, serialized against teardown exactly like every other control verb.
            // Keep the caller's Process alive across the whole native adopt: on Windows its open OS handle
            // pins the pid, so `adoptIntoJob`'s OpenProcess(pid) cannot race a recycle onto a stranger.
            let result = this.WhenLive(fun () -> backend.Adopt pid)
            GC.KeepAlive externalProcess
            result

    /// Adopt an already-running **external** process into the container from a **bare pid** — the door for
    /// a process this library did not start and for which the caller holds no `System.Diagnostics.Process`
    /// at all: one an outside supervisor launched, one whose id came from a pidfile, a registry or an
    /// FFI/IPC caller. `Adopt(Process)` covers only the case where a live `Process` object is in hand; this
    /// takes the one identifier every such caller does have. Both are unchanged in what containment means:
    /// kill-on-dispose, and participation in `Signal`/`Suspend`/`Resume`/`Members`/`MembersInfo`/`Stats`
    /// and any resource limits.
    ///
    /// ### A pid is an address, not a handle
    ///
    /// The number is used to *find* the process; it is not what the group keeps afterwards. Once a process
    /// is reaped the OS may give its number to an unrelated one, so this call captures an **identity
    /// anchor of its own** for whatever the number names at the moment it runs, and binds the group to
    /// that:
    ///
    ///  * **Windows (Job Object)** — the process **object**. The number is used exactly once, by this
    ///    call's `OpenProcess`; `AssignProcessToJobObject` then puts that object into the Job, and the
    ///    kernel keeps membership per object. Nothing later resolves the number again.
    ///  * **Linux cgroup v2** — kernel-maintained **cgroup membership**, per task. A `/proc/<pid>/stat`
    ///    start-time read on either side of the `cgroup.procs` write *detects* a number that changed hands
    ///    across it (see the failure list below for what the call then does about it) — detection, not
    ///    prevention.
    ///  * **POSIX process group (macOS, or Linux without limits)** — the tracked pid **plus** the
    ///    start-time token read here, **re-read before every later probe, signal, suspend/resume and
    ///    teardown kill**, never once at adoption and then trusted. A number whose token no longer matches
    ///    is dropped from the group and receives nothing.
    ///
    /// So a process that recycles the number *after* this call is rejected rather than signalled. What no
    /// library can check for you is the window *before* it — whether `pid` still named the process you
    /// meant by the time you passed it here. Look the number up as late as you can; where you can hold a
    /// `Process`, `Adopt(Process)` closes even that window on Windows, because the caller's own open handle
    /// pins the pid. The start-time token also carries one residual the other two anchors do not: its
    /// resolution (a clock tick on Linux, a microsecond on macOS) cannot tell apart two processes that
    /// occupied the number within the same tick.
    ///
    /// ### What the group covers
    ///
    /// Processes the adopted one had **already** spawned keep their original containment. What happens to
    /// the ones it spawns *afterwards* follows the mechanism (`Mechanism`): on the **Job Object** and
    /// **cgroup v2** a later fork joins the container with its parent, so the subtree grown from here is
    /// contained; on the **POSIX process group** the process is tracked **individually** — signalled and
    /// killed with the group, but its future forks are not, because POSIX has no primitive that moves a
    /// foreign, already-`exec`ed process into another process group.
    ///
    /// ### Refusals — each typed, never a silent success
    ///
    ///  * `pid <= 0` and this process's **own** pid are refused with `ProcessError.Adopt` before any
    ///    mechanism is consulted. Neither is an adoptable target and both are actively dangerous as a
    ///    number: `0` means "the caller's own process group" to `kill`, a negative number addresses a
    ///    process group rather than a process, and adopting this very process would enlist the caller in
    ///    its own group's teardown.
    ///  * a platform with **no start-time identity reader** on the POSIX process-group mechanism (the
    ///    BSDs) returns `ProcessError.Unsupported`: there is no anchor to capture, and tracking a bare
    ///    number would mean SIGKILLing whatever holds it at teardown. Never a silent downgrade to raw
    ///    by-number containment.
    ///  * a process this caller may **not signal** — another user's, or a protected/system one — is
    ///    refused with `ProcessError.Adopt` on the POSIX process-group mechanism, which checks it with a
    ///    `kill(pid, 0)` probe at adoption. Reading a start-time anchor proves only that the process can be
    ///    *identified* (on Linux `/proc/<pid>/stat` is world-readable), never that it can be controlled,
    ///    and a member this group could not signal or SIGKILL would be containment reported but not held.
    ///    The Job Object and cgroup v2 mechanisms refuse the same case by construction, through the denied
    ///    `OpenProcess`/`cgroup.procs` write below. The check is about the moment of adoption: a process
    ///    that changes credentials afterwards is no more foreseeable here than one that exits afterwards.
    ///  * a pid that names nothing, an identity that cannot be read (a `hidepid` `/proc` mount, another
    ///    user's process on macOS), a denied `OpenProcess`/`cgroup.procs` write, a Windows assign the
    ///    kernel refuses, or a number that changed hands *while this call ran*, all return
    ///    `ProcessError.Adopt` with the specific cause. On cgroup v2 that last case has already written the
    ///    stranger into this group's cgroup, so the call moves it back out into the parent cgroup and says
    ///    so; where even that is refused, the message says the process stays a member of this group and
    ///    will be killed by its teardown.
    ///
    /// ### Ownership: this group never reaps it
    ///
    /// Exactly as with `Adopt(Process)`, an adopted process is **not** this library's child: nothing here
    /// `waitpid`s it, and no exit status for it is ever reported through this API — there is no
    /// `RunningProcess` and no result to await, which is why this returns `Result<unit, _>`. The group can
    /// *contain*, *signal*, *list* and *kill* it; its exit belongs to its real parent (an outside
    /// supervisor, or `init` once it is reparented), and observing that exit is the caller's own business
    /// — `Process.WaitForExitAsync()`/`HasExited` where a `Process` is available. One consequence to plan
    /// for on the POSIX mechanism: a process that exits and is not reaped by its own parent becomes a
    /// zombie, and a zombie still answers the identity probe, so a graceful `ShutdownAsync` waits out its
    /// full grace on it and no kill can clear it — only its parent's `wait` can.
    ///
    /// Routed through the same lifecycle gate as the other control verbs: adopting into a released group
    /// returns a non-transient `ProcessError.Unsupported` before touching the closed/removed native
    /// container, never a use-after-teardown.
    member this.AdoptByPid(pid: int) : Result<unit, ProcessError> =
        // The two numbers that are never adoptable are refused HERE, before any backend sees them, so the
        // guarantee is one rule for every mechanism rather than three implementations of it. Typed, not
        // thrown: unlike a `null` argument (a programming error `Adopt` surfaces eagerly), a pid usually
        // arrives from outside the program — a pidfile, a registry, an IPC message — so a bad one is data
        // to report, not a contract violation to raise on.
        if pid <= 0 then
            Error(
                ProcessError.Adopt(
                    pid,
                    "pid 0 and negative pids are not adoptable targets: 0 means 'the caller's own process group' to kill and 'self' to setpgid, and a negative number addresses a process group rather than a process — either would point this group's teardown somewhere it was never asked to reach"
                )
            )
        elif pid = Environment.ProcessId then
            Error(
                ProcessError.Adopt(
                    pid,
                    "this process's own pid is not an adoptable target: it would enlist the caller in its own group's teardown, so disposing the group would kill the process that owns it"
                )
            )
        else
            this.WhenLive(fun () -> backend.AdoptByPid pid)

    /// Immediately hard-kill every process currently in the group (the honest name for the tree kill —
    /// no graceful signal). Idempotent; the group stays usable for further spawns. Returns `Result` for
    /// parity with the other tree-control verbs.
    ///
    /// Linux cgroup v2's legacy fallback — a kernel without `cgroup.kill` (< 5.14) — reports
    /// `ProcessError.Io` in two independent cases. **Thaw**: it cannot verify that the reusable cgroup
    /// was thawed (`cgroup.freeze` still reports frozen, or cannot be read); an already-unfrozen or
    /// removed freezer stays a best-effort success. **Delivery**: a member could not be signalled AND
    /// the cgroup is still populated — or its membership unreadable — once the sweep ends; a cgroup that
    /// did drain is not reported as a kill failure, because the drain check is the authority on whether
    /// the tree died. That second case includes a kernel without pidfd (< 5.3), where the identity-safe
    /// sweep kills nothing at all rather than downgrading to a raw `kill(pid, ...)` that a recycled pid
    /// could land on an unrelated process. Final disposal remains best-effort — it removes the cgroup
    /// regardless of either error.
    ///
    /// **Windows** reports `ProcessError.Io` when `TerminateJobObject` is REFUSED and the Job still holds
    /// live members (or its accounting cannot be read at all, which is never read as "drained"). A refusal
    /// on an already-empty Job stays a successful no-op, so repeating `KillAll` on a reaped tree — or
    /// racing the tree's own exit — is idempotent as before. Closing the Job handle (`Dispose`) still kills
    /// the tree through `KILL_ON_JOB_CLOSE`, but that backstop fires at teardown, not now, so it is
    /// deliberately NOT reported as a completed kill here.
    member this.KillAll() : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.KillTree())

    /// The pids of the processes currently in the group — a point-in-time snapshot. On Windows the
    /// whole Job tree, on cgroup v2 the whole cgroup, on the POSIX fallback the tracked group leaders plus
    /// any process adopted by pid (`AdoptByPid`) whose identity anchor still matches.
    member this.Members() : Result<IReadOnlyList<int>, ProcessError> =
        this.WhenLive(fun () -> backend.Members())
        |> Result.map (fun pids -> List.toArray pids :> IReadOnlyList<int>)

    /// An enriched, point-in-time snapshot of the group's members: the same pids `Members` reports, in
    /// the same platform matrix (the whole Job tree on Windows, the whole cgroup on cgroup v2, the tracked
    /// group leaders and any anchor-matching `AdoptByPid` member on the POSIX fallback), each carrying its parent pid, executable image name, and
    /// OS-reported start time **where the platform can honestly report them** — every enriching field is
    /// `option` and is `None` otherwise, never a fabricated value. A member that exits between the
    /// enumeration and its metadata read is **omitted** rather than filled with invented fields. The
    /// member's command line and environment are **never** included on any platform — argv routinely
    /// carries secrets and redaction is the consumer's policy — the same exclusion the logging / tracing /
    /// metrics paths enforce. Enumeration errors the same typed way as `Members` once the group is released.
    member this.MembersInfo() : Result<IReadOnlyList<MemberInfo>, ProcessError> =
        // Take the pid snapshot under the lifecycle lock exactly like `Members` does, but ENRICH off the
        // lock (in `Result.map`, after `WhenLive` has returned): the per-pid metadata reads (`/proc`, a
        // Toolhelp process snapshot, `proc_pidinfo`) touch no container handle, so they must not hold
        // `sync` across potentially many reads. Each pid is read point-in-time and dropped if it has since
        // vanished. The platform split mirrors `Create`'s: Windows enriches from ONE system snapshot,
        // every POSIX target (cgroup v2 and the process-group fallback alike) reads per pid.
        this.WhenLive(fun () -> backend.Members())
        |> Result.map (fun pids ->
            let infos =
                if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                    Native.Windows.readMembersInfo pids
                else
                    pids |> List.choose Native.Posix.readMemberInfo

            List.toArray infos :> IReadOnlyList<MemberInfo>)

    /// Return a point-in-time resource snapshot for each current member. CPU time and resident memory
    /// are optional per member, and I/O counters are optional on platforms without a per-process
    /// counter API. The native backend owns both membership enumeration and metric reads under this
    /// lifecycle gate: a member that vanishes during sampling is omitted, and a recycled pid cannot
    /// contribute metrics from a foreign process.
    member this.MemberStats() : Result<IReadOnlyList<MemberStats>, ProcessError> =
        this.WhenLive(fun () -> backend.MemberStats())
        |> Result.map (fun stats -> List.toArray stats :> IReadOnlyList<MemberStats>)

    /// Broadcast `signal` to every process in the group. A member that has already exited (or an
    /// already-empty group) is a best-effort success — that races the target's own exit, not a
    /// caller error. `Signal.Other 0` (a liveness *probe* that delivers nothing) and a negative
    /// number are not deliverable signals at all: they are refused up front with
    /// `ProcessError.Unsupported`, never a false success, even on an empty group. A number that IS a
    /// signal but the platform rejects (e.g. an out-of-range `Signal.Other 999`, EINVAL) is a genuine
    /// delivery failure and returns `ProcessError.Io` with the errno detail; when the group has several
    /// members, the first genuine failure is reported but every member still receives the signal.
    ///
    /// On **Windows** `Signal.Kill` maps to the atomic Job terminate, and reports a refusal on exactly the
    /// terms `KillAll` documents — `ProcessError.Io` while the Job still holds live members, an idempotent
    /// success once it is empty. `Signal.Int` and `Signal.Term`
    /// are a best-effort soft stop combining two individually-targeted deliveries: a console
    /// **CTRL+BREAK** to each child started with `Command.WindowsCtrlSignals()` (spawned in its own
    /// console process group) AND a **`WM_CLOSE`** posted to the top-level windows of every member that
    /// has one (a windowed Electron/GUI child), targeted strictly by process id so no foreign window is
    /// touched. Either mechanism reaching a member is a best-effort `Ok`; the call returns
    /// `ProcessError.Unsupported` ONLY when the group has neither a CTRL-capable child nor a windowed
    /// member — never a silent downgrade to a kill. Delivery is not compliance even on success — a child
    /// may install its own handler or a window may prompt/veto the close. Every other Windows signal
    /// returns `ProcessError.Unsupported`.
    member this.Signal(signal: Signal) : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Signal signal)

    /// Suspend (freeze) every process in the group. POSIX: `SIGSTOP` (level-triggered, idempotent).
    /// Cgroup v2: `cgroup.freeze`. Windows: suspend every thread of every member; suspend counts stack,
    /// so N `Suspend`s need N `Resume`s. A failed native delivery or cgroup write returns
    /// `ProcessError.Io` — including a Windows member that was confirmed to be in the job and still could
    /// not be suspended, which is reported even when the other members were frozen (a partial freeze is
    /// reported, not rolled back). A member that exited concurrently is a successful no-op on every
    /// platform, as is a Windows pid that was recycled onto an unrelated process.
    member this.Suspend() : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Suspend())

    /// Resume a tree suspended by `Suspend`. Failed native delivery or cgroup thaw writes return
    /// `ProcessError.Io`, on the same terms as `Suspend`; a member that exited concurrently is a
    /// successful no-op.
    member this.Resume() : Result<unit, ProcessError> =
        this.WhenLive(fun () -> backend.Resume())

    /// Apply a new whole-tree resource-limit set to this **live** group, without recreating it or
    /// restarting its children — adaptive resource control (tighten memory on a sagging batch, widen a
    /// long-lived worker pool's CPU quota) at runtime. The `limits` are a full REPLACEMENT of the caps
    /// in force: a dimension left `None` becomes unbounded again, not left at its previous cap.
    ///
    /// A limit-capable mechanism re-applies the caps to its live container — **Windows** re-issues
    /// `SetInformationJobObject` on the Job (the caps, the CPU-affinity mask, and the UI restrictions),
    /// **Linux cgroup v2** rewrites `memory.max`/`memory.oom.group`/`pids.max`/`cpu.max`/`cpuset.cpus`
    /// in place — while the
    /// **POSIX process-group** mechanism (macOS/BSD, or Linux without cgroup v2) has no whole-tree limit
    /// primitive and returns `ProcessError.ResourceLimit`, the same honest, typed refusal `Create` gives
    /// for a limited group there — never a silent no-op. On Linux the CPU-affinity pin additionally needs
    /// the `cpuset` controller: a hierarchy that does not carry it at all cannot enforce a pin, so an
    /// update requesting one is refused with that same typed `ProcessError.ResourceLimit` — and refused
    /// before any controller file is written, so the previous caps stay in force — never a dropped pin.
    ///
    /// Routed through the same lifecycle gate as the other control verbs: the released-flag check AND
    /// the native re-apply run in one critical section, so a call racing (or following) teardown either
    /// applies fully on the live container or observes the flag and returns a non-transient
    /// `ProcessError.Unsupported` BEFORE touching the closed/recycled native handle — never a
    /// use-after-teardown. Only on a successful apply is the `Options` snapshot swapped to the new set,
    /// under the same lock, so `Options.Limits` a consumer reads back matches what is actually enforced.
    ///
    /// The caps land through several sequential native writes, so a later write can fail after an earlier
    /// one applied. A failed apply is honest about that: a limit-capable mechanism best-effort **restores
    /// the previous set** to its live container before returning the error, so a failed `UpdateLimits`
    /// leaves BOTH the container and the `Options` snapshot on the previous set (nothing net changed) —
    /// the container and `Options.Limits` never silently diverge. In the rare case the restore itself also
    /// fails, the returned `ProcessError.ResourceLimit` says so explicitly (its message notes the limits
    /// may be partially applied) and `Options.Limits` keeps reporting the previous set: the error is the
    /// explicit signal that the container's state is uncertain, so a consumer must not treat the snapshot
    /// as authoritative there — never a silent divergence.
    member this.UpdateLimits(limits: ResourceLimits) : Result<unit, ProcessError> =
        this.WhenLive(fun () ->
            // Record every axis THIS request names on the sticky evidence record BEFORE attempting the
            // apply, whether it then succeeds or fails (see `LimitEvidence()`'s own doc comment): neither
            // backend applies its caps atomically, so a failure part-way through can still have reached
            // the OS for one axis of this request, and recording only on success would let a later
            // `LimitEvidence()` answer `NotTripped` for it without ever reading a counter.
            cappedAxes <- cappedAxes.Record limits

            match backend.UpdateLimits limits with
            | Ok() ->
                // Reflect the new caps only after the backend confirms they fully applied.
                currentOptions <- currentOptions.WithLimits limits
                Ok()
            | Error error ->
                // The backend failed to apply the new set and best-effort restored the previous one, so
                // the snapshot must stay on the previous set — leaving `currentOptions` untouched keeps
                // the container and `Options.Limits` consistent (or, in the rare unrecoverable case, the
                // error itself is the explicit signal that the snapshot is no longer authoritative).
                Error error)

    /// A snapshot of the group's resource usage. On Windows this reads the Job Object's accounting
    /// (CPU + peak committed memory + I/O + active count); on cgroup v2 it reads the cgroup accounting;
    /// on the POSIX fallback only the live count (its tracked groups plus any anchor-matching `AdoptByPid`
    /// member) is available. Errors once the group is released.
    member this.Stats() : Result<ProcessGroupStats, ProcessError> =
        this.WhenLive(fun () -> backend.Stats())

    /// Post-run, per-axis evidence of whether a resource cap this group ever configured actually **fired**
    /// — the question a plain exit code or signal cannot answer, and the other side of
    /// `ProcessError.ResourceLimit` (which answers "could the cap be applied at all", never "did it
    /// fire"). One `LimitVerdict` (`Tripped`/`NotTripped`/`Unknown`) per axis — `Memory`/`Processes`/`Cpu`
    /// — read from the container's own authoritative post-mortem counters, never re-derived from the
    /// `ResourceLimits` that requested the cap and never inferred from the run's exit code or signal (a
    /// cap-driven kill and a self-inflicted crash can look identical from the outside — see
    /// `LimitVerdict`'s own doc comment).
    ///
    /// **Available only after the group has been torn down** — `ShutdownAsync`/`Dispose`/`DisposeAsync`,
    /// or the finalizer — deliberately the OPPOSITE lifetime rule `Stats()` follows. The evidence is
    /// captured exactly once, from the still-live container, in the instant immediately before its
    /// counters (and, for a Linux cgroup v2 group, the cgroup directory itself) are torn down, and is then
    /// cached: any number of later reads — including well after teardown, on any thread — return that
    /// same immutable snapshot rather than a re-derived or stale one. Calling this **before** teardown has
    /// completed returns a non-transient `ProcessError.Unsupported`: an honest "not yet available", never
    /// a fabricated verdict read off counters that are still changing.
    ///
    /// Per axis, deliberately never folded into one whole-group verdict — see `LimitVerdict` for what each
    /// value means, and `LimitEvidence` for why folding would misrepresent "no evidence" as "no" (and for
    /// why `IoMax`/`CpuAffinity` have no axis here at all). Only the Linux cgroup v2 mechanism can ever
    /// answer `Tripped`/`NotTripped` from real evidence (see `Native.Cgroup.limitEvidence` for exactly
    /// which kernel counters back each axis, and `LimitEvidence.Cpu` for the further `CpuTimeMax`
    /// refinement that applies on every mechanism). The two mechanisms differ in what they say about an
    /// axis this group never capped: a Windows Job Object keeps no post-mortem record that any of these
    /// caps fired, so every axis it ever capped reads `Unknown` — not an oversight, a measured conclusion
    /// about what it actually preserves after the fact — but an axis it never capped still reads
    /// `NotTripped` (nothing was capped, so nothing could fire) without touching native at all. The POSIX
    /// process-group fallback has no whole-tree resource-accounting apparatus whatsoever — the same reason
    /// `Create`/`UpdateLimits` refuse any whole-tree cap on it — so it answers `Unknown` on every axis
    /// UNCONDITIONALLY, including one this group never capped: there is no "nothing was capped" case that
    /// lets it read `NotTripped` the way a Job Object can, because it never had any evidence apparatus to
    /// begin with.
    member this.LimitEvidence() : Result<LimitEvidence, ProcessError> =
        lock sync (fun () ->
            match capturedLimitEvidence with
            | Some evidence -> Ok evidence
            | None ->
                Error(
                    ProcessError.Unsupported
                        "limit evidence is captured from the container's own post-mortem counters at teardown; call ShutdownAsync/Dispose/DisposeAsync first, then read LimitEvidence() — it is not available on a still-live group"
                ))

    /// A periodic `ProcessGroupStats` series: the first sample immediately, then one per `interval`.
    /// **Pull-based** — it samples only as the enumeration is pulled and runs no background task, so
    /// it neither keeps the group alive nor leaks if abandoned. The series ends on the first snapshot
    /// the group fails to report (notably after it is torn down) or when the enumerator's token fires.
    /// A non-positive `interval` (`<= TimeSpan.Zero`) is rejected with `ArgumentOutOfRangeException`,
    /// thrown eagerly by this call rather than deferred to enumeration — a sampling cadence must be a
    /// positive duration.
    member this.SampleStatsAsync(interval: TimeSpan) : IAsyncEnumerable<ProcessGroupStats> =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero)

        // Cap an over-long interval into the armable range so the sampler's `Task.Delay` can't throw
        // synchronously (it then samples at the max ~24.8-day cadence instead of faulting).
        let period = Timeouts.clampArmable interval

        StatsSamplerSeq((fun () -> this.Stats()), period)

    /// Capture a run through this *shared* group, exactly as `JobRunner` captures through a private one:
    /// build a `RunningProcess` over the shared group and run it to completion via the `RunningProcess`
    /// verb, so encoding, line-ending/BOM/trailing-newline normalization, `OkCodes`, and
    /// `OutputBufferPolicy` all match every other runner. If the (CancelOn-linked) token fires, just this
    /// child is torn down and the run resolves to `ProcessError.Cancelled` — an immediate hard kill unless
    /// the command sets `CancelGrace`, which soft-signals this child first and escalates to the same kill
    /// after the grace (a shared group has no per-child graceful path beyond that soft signal, the
    /// documented shared-group scope). The child's I/O is detached on
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

    /// Gracefully kill the contained tree (the supplied soft signal, then a hard kill after `grace`)
    /// WITHOUT releasing the group — used by per-run timeouts (the run's own teardown releases the group).
    /// On Windows only the default soft-stop contract is representable.
    member internal _.GracefulKillTree(signal: Signal, grace: TimeSpan) : Task = backend.GracefulKillTree signal grace

    // Deliberate divergence from ProcessKit-rs (`shutdown(escalate_to_kill = false)` / `stop(grace,
    // escalate)`): we do NOT offer a graceful shutdown that *keeps the group usable* by sparing processes
    // that ignore the soft signal. That mode makes kill-on-drop conditional (spared survivors outlive a
    // drop), and this port keeps the kill-on-drop tree guarantee UNCONDITIONAL. The four teardown needs are
    // still covered without it: immediate+terminal = `Dispose`; immediate+keep-usable = `KillAll`;
    // graceful+terminal = `ShutdownAsync` / `ShutdownReportAsync` (T-384's reporting sibling, same
    // unconditional teardown, richer observation — see its own doc comment).

    /// Tear the group down gracefully, then release it. On Unix: the configured `Options.StopSignal`,
    /// then SIGKILL if still alive after `gracePeriod`. On Windows: best-effort `WM_CLOSE`, then the
    /// atomic Job kill. A negative `gracePeriod` is
    /// rejected with `ArgumentOutOfRangeException`; `TimeSpan.Zero` escalates immediately.
    /// Idempotent with `Dispose` in the sense that matters — the one-shot teardown (`hardRelease`)
    /// still runs exactly once no matter how many callers race `ShutdownAsync`/`Dispose`/`DisposeAsync`,
    /// and every `GracefulKillTree` failure still leaves the container released, never leaked. It is
    /// NOT idempotent in the sense of "every call observes the same completion": the caller that loses
    /// the `claimRelease` race returns immediately without waiting for the winner's (possibly still
    /// in-flight, up-to-`gracePeriod`) teardown to finish — unlike `RunningProcess.StopAsync`, which
    /// funnels concurrent callers onto one shared conclusion. A loser that needs "the tree is fully torn
    /// down" must await the SAME `Task` the winner returned, or otherwise synchronize with it itself.
    member this.ShutdownAsync(gracePeriod: TimeSpan) : Task =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        task {
            // Win the transition first (flag flipped under `sync`), so from here no StartAsync/Signal/
            // Stats/... touches the backend. The graceful wait is UNBOUNDED (up to `gracePeriod`), so it
            // runs with NO lock held — the flag already fends off every new op. Only the winner reaches
            // the wait, so the one-shot teardown that follows runs exactly once.
            if claimRelease () then
                try
                    // `GracefulKillTree` now returns `Task<GracefulOutcome>` (the observed-facts core
                    // `ShutdownReportAsync` reads); `ShutdownAsync` discards the outcome exactly as it
                    // discarded the bare `Task` before, hence the explicit upcast — a plain `do!` cannot
                    // bind a non-unit result to `()`.
                    do! (backend.GracefulKillTree currentOptions.StopSignal gracePeriod :> Task)
                finally
                    // Guarantee `hardRelease`/`GC.SuppressFinalize` even if `GracefulKillTree` throws
                    // synchronously or its `Task` faults — mirroring how `RunTelemetryScope.Conclude`
                    // guards `Diag.runEnded` in a `finally`. `claimRelease()` already consumed the
                    // live->released transition above, so without this `finally` a faulting graceful
                    // stage would leave `hardRelease()` never called: every later Dispose/ShutdownAsync/
                    // finalizer sees `releasedFlag <> 0` and no-ops, permanently leaking the Job handle /
                    // cgroup / process group this container owns. The original exception (if any) is
                    // NOT swallowed — `finally` only guarantees the cleanup runs alongside it, and the
                    // exception still propagates to this call's `Task` once the `finally` completes.
                    hardRelease ()
                    GC.SuppressFinalize this
        }
        :> Task

    /// `ShutdownAsync` using the group's configured `Options.ShutdownTimeout`.
    member this.ShutdownAsync() : Task =
        this.ShutdownAsync currentOptions.ShutdownTimeout

    /// Gracefully tear the group down, additionally reporting what the teardown **actually observed** —
    /// the introspective sibling of `ShutdownAsync(gracePeriod)`.
    ///
    /// Drives the EXACT SAME teardown as `ShutdownAsync(gracePeriod)` — the configured
    /// `Options.StopSignal` (a best-effort `WM_CLOSE` on Windows), then up to `gracePeriod` for the tree
    /// to drain, then an UNCONDITIONAL hard kill of any survivor, then release — and is purely additive:
    /// it changes nothing about `ShutdownAsync`'s own behaviour or signature. What it adds is the
    /// `ShutdownReport`: the soft signal's fate, the member counts before/after, whether the tree drained
    /// within the grace or needed the hard kill, and how long it actually took — so a caller that owns
    /// its own end-of-run race (a deadline that is not a fixed timeout, but a timeout x Ctrl-C x
    /// control-socket race) can report the OBSERVED tier instead of re-deriving it from `ShutdownAsync`'s
    /// bare success.
    ///
    /// # Deliberately NOT a "keep the group usable, spare survivors" mode
    ///
    /// ProcessKit-rs's `ProcessGroup::stop` additionally takes an `escalate: bool` that, when `false`,
    /// leaves any survivor running and keeps the group usable — seemingly the Rust source this method
    /// otherwise mirrors closely. This port deliberately does not offer that combination — see the
    /// "Deliberate divergence" note above `GracefulKillTree` for why: this port's kill-on-drop guarantee
    /// is unconditional, and there is no honest way to offer "spare the survivors" on a teardown that
    /// still releases the container the way `ShutdownAsync`/this method both document. On Windows in
    /// particular, closing the Job handle unconditionally kills whatever the Job still holds
    /// (`KILL_ON_JOB_CLOSE`), so a "spared" survivor would be silently killed anyway by the very release
    /// this method still performs at the end — reporting `Escalated = false` there would misrepresent a
    /// kill this call itself causes as a spare. `Escalated` instead answers an honest, narrower question:
    /// did THIS teardown's grace window need the hard kill, or did the tree drain on the soft signal
    /// alone.
    ///
    /// See `ShutdownAsync(gracePeriod)`'s own doc comment for the full teardown/idempotency contract this
    /// shares (negative-grace rejection, `Dispose`/`ShutdownAsync` idempotency, the "only the winner
    /// awaits the in-flight teardown" rule). A caller that loses the underlying release race — a
    /// concurrent `Dispose`/`ShutdownAsync`/`ShutdownReportAsync`/finalizer already claimed it — gets
    /// `ProcessError.Unsupported` here rather than a fabricated report, since there is no teardown left
    /// for THIS call to have observed.
    member this.ShutdownReportAsync(gracePeriod: TimeSpan) : Task<Result<ShutdownReport, ProcessError>> =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        task {
            if claimRelease () then
                let stopwatch = Stopwatch.StartNew()

                // Read BEFORE the soft signal, directly on the still-live backend (not through `WhenLive`):
                // `claimRelease` already won the release transition above, so — exactly like
                // `hardRelease`'s `LimitEvidence` capture — no other op can reach the backend concurrently
                // from here.
                let membersBefore =
                    match backend.Members() with
                    | Ok members -> Some(List.length members)
                    | Error _ -> None

                let mutable outcome = Unchecked.defaultof<GracefulOutcome>
                let mutable membersAfter = None

                try
                    let! result = backend.GracefulKillTree currentOptions.StopSignal gracePeriod
                    outcome <- result

                    // Read AFTER the grace/kill decision but BEFORE `hardRelease` below closes the native
                    // container — the container is still live here (see `hardRelease`'s own comment on why
                    // `LimitEvidence` must likewise read before it runs), the same window
                    // ProcessKit-rs's `GracefulOutcome::members_after` is read in.
                    membersAfter <-
                        match backend.Members() with
                        | Ok members -> Some(List.length members)
                        | Error _ -> None
                finally
                    hardRelease ()
                    GC.SuppressFinalize this

                let softSignal =
                    match outcome.Soft with
                    | SoftDelivery.Sent -> SoftSignalDelivery.Sent currentOptions.StopSignal
                    | SoftDelivery.Unsupported -> SoftSignalDelivery.Unsupported
                    | SoftDelivery.Failed -> SoftSignalDelivery.Failed currentOptions.StopSignal

                return
                    Ok(
                        ShutdownReport(
                            softSignal,
                            membersBefore,
                            membersAfter,
                            outcome.Drained,
                            outcome.Escalated,
                            stopwatch.Elapsed
                        )
                    )
            else
                return
                    Error(
                        ProcessError.Unsupported
                            "the process group has already been released by another Dispose/ShutdownAsync/ShutdownReportAsync/finalizer call, which owns that teardown (or already discarded its report); this call observed no teardown of its own"
                    )
        }

    /// `ShutdownReportAsync` using the group's configured `Options.ShutdownTimeout`.
    member this.ShutdownReportAsync() : Task<Result<ShutdownReport, ProcessError>> =
        this.ShutdownReportAsync currentOptions.ShutdownTimeout

    /// How far a soft stop (`Signal.Int`/`Signal.Term`) reaches on this group's CURRENT live membership,
    /// right now — see `SoftStopScope` for the full per-mechanism table. A capability report, not an
    /// action: side-effect-free, delivering no signal and mutating nothing, so asking never changes the
    /// answer a later `Signal`/`ShutdownAsync`/`ShutdownReportAsync` soft stop gets. Errors the same
    /// honest way every other control verb does once the group is released
    /// (`ProcessError.Unsupported`) — there is no live membership left to read.
    member this.SoftStopScope() : Result<SoftStopScope, ProcessError> =
        this.WhenLive(fun () -> Ok(backend.SoftStopScope()))

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
    // teardown) and `CancelOn`. `TimeoutGrace` has no per-child graceful kill in a shared group, so it
    // falls back to the immediate kill.
    //
    // A descendant that inherited the output pipe and outlives the leader therefore keeps that pipe
    // open here in a way a private group would not — but it no longer delays the capture: the run's
    // bounded post-exit output drain (`PostExitDrain`) severs THIS run's own read ends once the
    // leader's outcome is settled, and the capture returns marked `Truncated`. Severing is all it
    // does: the descendant belongs to the shared group, and this library does not kill one run's
    // survivors out from under a container it shares with everyone else's.
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
