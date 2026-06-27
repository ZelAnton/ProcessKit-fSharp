namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

/// A captured run before it is shaped into a `ProcessResult` (stdout kept as raw bytes so the
/// text and bytes verbs share one capture path).
type internal CapturedRun =
    { Stdout: byte[]
      Stderr: string
      Outcome: Outcome
      Duration: TimeSpan }

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
    static member Create(options: ProcessGroupOptions) : Result<ProcessGroup, ProcessError> =
        let limits = options.Limits

        let withBackend (backend: IContainmentBackend) = Ok(new ProcessGroup(backend, options))

        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            match Native.createWindowsJob () with
            | Error error -> Error error
            | Ok job ->
                if limits.Any then
                    match Native.applyWindowsJobLimits job limits with
                    | Ok() -> withBackend (JobObjectBackend job)
                    | Error message ->
                        Native.closeWindowsHandle job
                        Error(ProcessError.ResourceLimit message)
                else
                    withBackend (JobObjectBackend job)
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux && limits.Any then
            if Native.cgroupV2Available () then
                match Native.createCgroup limits with
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
    member internal _.SpawnInto(command: Command) : Result<Native.Spawned, ProcessError> =
        // Refuse to spawn into a released group: on Windows the child is created before it is assigned
        // to the (now-closed) Job, so spawning after teardown would leak an UNCONTAINED child. Fail
        // fast with a non-transient error instead. (Same released-flag guard as the control verbs.)
        if releasedFlag <> 0 then
            Error(ProcessError.Unsupported "the process group has been released")
        else
            match backend.Spawn command with
            | Error error -> Error error
            | Ok spawned ->
                backend.Track spawned
                Ok spawned

    /// Spawn `command` into the group and build a `RunningHost`. `ownsGroup` decides what disposing
    /// the resulting `RunningProcess` does: when **true** (a private per-run group) it reaps this
    /// whole group; when **false** (a shared group) it only detaches this run's I/O — the group owns
    /// the child's lifetime and reaps it on `Shutdown`/`Dispose`. That ownership choice is the only
    /// difference between the two start paths, so it lives here as one branch each on `StartKill`,
    /// `GracefulKill`, and `Teardown`.
    member private this.BuildHost(command: Command, ownsGroup: bool) : Result<RunningHost, ProcessError> =
        match this.SpawnInto command with
        | Error error -> Error error
        | Ok spawned ->
            Pump.feedStdinSource spawned.Stdin command.Config.StdinSource

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
    /// child's lifetime (reaped on `Shutdown`/`Dispose`). Internal — `Start` wraps it.
    member internal this.StartShared(command: Command) : Result<RunningHost, ProcessError> =
        this.BuildHost(command, ownsGroup = false)

    /// Start `command` into this shared group and return a live `RunningProcess`. The **group** owns
    /// the child's lifetime: disposing the returned process detaches its I/O but does not kill it —
    /// reap the tree with `Shutdown`/`Dispose`, or this one run with its `StartKill`.
    member this.Start(command: Command) : Task<Result<RunningProcess, ProcessError>> =
        task {
            match this.StartShared command with
            | Error error -> return Error error
            | Ok host -> return Ok(RunningProcess host)
        }

    // Tree-control / accounting verbs become an error once the group is released: after teardown the
    // backend's Job handle is closed (and POSIX pgids may have been recycled), so calling in would be
    // a use-after-close or a wrong-target kill. Guarding here (as `Stats` already did) closes the
    // window for a verb racing a concurrent `Dispose`/`Shutdown`.
    member private _.WhenLive(action: unit -> Result<'T, ProcessError>) : Result<'T, ProcessError> =
        // A released group is a permanent condition, not a transient I/O blip — use `Unsupported`
        // (which `ProcessError.isTransient` rejects) so a retry classifier never re-tries a dead group.
        if releasedFlag <> 0 then
            Error(ProcessError.Unsupported "the process group has been released")
        else
            action ()

    /// Immediately hard-kill every process currently in the group. Idempotent; the group stays
    /// usable for further spawns. Returns `Result` for parity with the other tree-control verbs (a
    /// future backend can report an undrained tree); the current backends always succeed.
    member this.TerminateAll() : Result<unit, ProcessError> =
        this.WhenLive(fun () ->
            backend.KillTree()
            Ok())

    /// The pids of the processes currently in the group — a point-in-time snapshot. On Windows the
    /// whole Job tree, on cgroup v2 the whole cgroup, on the POSIX fallback the tracked group leaders.
    member this.Members() : Result<IReadOnlyList<int>, ProcessError> =
        this.WhenLive(fun () -> backend.Members())
        |> Result.map (fun pids -> List.toArray pids :> IReadOnlyList<int>)

    /// Broadcast `signal` to every process in the group. Best-effort: an exited member is skipped
    /// and an empty group succeeds trivially. On **Windows** only `Signal.Kill` is deliverable (it
    /// maps to the Job terminate); any other signal returns `ProcessError.Unsupported`.
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
    member this.SampleStats(interval: TimeSpan) : IAsyncEnumerable<ProcessGroupStats> =
        let period =
            if interval <= TimeSpan.Zero then
                TimeSpan.FromMilliseconds 1.0
            else
                interval

        StatsSamplerSeq((fun () -> this.Stats()), period)

    /// Spawn `command` into the group, capture stdout/stderr to completion, and reap it. Does
    /// **not** release the group (the caller keeps it for `Shutdown`/`Dispose`). A cancelled token
    /// terminates the whole tree and resolves to `ProcessError.Cancelled`.
    member internal this.SpawnAndCapture
        (command: Command, cancellationToken: CancellationToken)
        : Task<Result<CapturedRun, ProcessError>> =
        task {
            // Honour the command's own CancelOn in addition to the verb's token, so a Command.CancelOn
            // (or a CliClient default) ties this run to its token even through a ProcessGroup runner.
            use linkedCts =
                match command.Config.CancelOn with
                | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            if effectiveToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match this.SpawnInto command with
                | Error error -> return Error error
                | Ok spawned ->
                    let startedAt = Stopwatch.GetTimestamp()
                    let registration = effectiveToken.Register(fun () -> backend.KillChild spawned)

                    Pump.feedStdinSource spawned.Stdin command.Config.StdinSource
                    let stdoutTask = Pump.drainRawOrEmpty spawned.Stdout None CancellationToken.None
                    let stderrTask = Pump.drainRawOrEmpty spawned.Stderr None CancellationToken.None
                    let waitTask = waitOutcome spawned.Handle

                    // Honour the command's `Timeout`: on the deadline, hard-kill just this child's
                    // subtree (a shared group may hold siblings, so the graceful whole-tree path is not
                    // used here) and report `TimedOut`. An over-long span is treated as no timeout.
                    let! outcome =
                        Timeouts.raceTimeout
                            command.Config.Logger
                            command.Program
                            command.Config.Timeout
                            (fun () -> task { backend.KillChild spawned } :> Task)
                            waitTask

                    let! stdoutBytes = stdoutTask
                    let! stderrBytes = stderrTask
                    // Stop reacting to cancellation before closing the child's handle, so a late
                    // cancellation can't fire KillChild on a since-reused handle value.
                    registration.Dispose()
                    Pump.closeSpawned spawned
                    // The child is reaped: stop tracking it (close its Windows handle) so a long-lived
                    // shared group does not accumulate handles/pgids across many captured runs.
                    backend.Release spawned
                    let duration = Stopwatch.GetElapsedTime startedAt

                    if effectiveToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        return
                            Ok
                                { Stdout = stdoutBytes
                                  Stderr = Encoding.UTF8.GetString stderrBytes
                                  Outcome = outcome
                                  Duration = duration }
        }

    /// Gracefully kill the contained tree (SIGTERM, then SIGKILL after `grace`) WITHOUT releasing
    /// the group — used by per-run timeouts (the run's own teardown releases the group). On Windows
    /// there is no per-job graceful signal, so this is the atomic Job kill.
    member internal _.GracefulKillTree(grace: TimeSpan) : Task = backend.GracefulKillTree grace

    /// Tear the group down gracefully, then release it. On Unix: SIGTERM, then SIGKILL if still
    /// alive after `gracePeriod`. On Windows: an atomic Job kill. Idempotent with `Dispose`.
    member this.Shutdown(gracePeriod: TimeSpan) : Task =
        task {
            if Interlocked.Exchange(&releasedFlag, 1) = 0 then
                do! backend.GracefulKillTree gracePeriod
                backend.HardRelease()
                GC.SuppressFinalize this
        }
        :> Task

    /// `Shutdown` using the group's configured `Options.ShutdownTimeout`.
    member this.Shutdown() : Task = this.Shutdown options.ShutdownTimeout

    // The finalizer is the GC-time safety net for a group that was never disposed: it reaps the
    // tree. Deterministic teardown still comes from `use`/`Dispose`.
    override _.Finalize() = releaseContainer ()

    // A `ProcessGroup` is itself an `IProcessRunner` — every run goes into THIS shared group rather
    // than a fresh private one, so a whole fleet shares one kill-on-dispose container (pass it to
    // `Supervisor.WithRunner`). Captured runs decode stdout with the command's encoding and honour the
    // per-run `Command.Timeout` (hard-kill of just that child's subtree → `TimedOut`) and `CancelOn`;
    // they do not apply the line-level `OutputBufferPolicy` or `TimeoutGrace` (a shared group has no
    // per-child graceful kill) — use `Start` + the `RunningProcess` verbs for those.
    interface IProcessRunner with
        member this.Start(command, cancellationToken) =
            task {
                if cancellationToken.IsCancellationRequested then
                    return Error(ProcessError.Cancelled command.Program)
                else
                    return! this.Start command
            }

        member this.OutputString(command, cancellationToken) =
            task {
                match! this.SpawnAndCapture(command, cancellationToken) with
                | Error error -> return Error error
                | Ok captured ->
                    let text = command.Config.StdoutEncoding.GetString captured.Stdout

                    return
                        Ok(
                            ProcessResult<string>(
                                command.Program,
                                text,
                                captured.Stderr,
                                captured.Outcome,
                                captured.Duration,
                                false,
                                command.Config.OkCodes
                            )
                        )
            }

        member this.OutputBytes(command, cancellationToken) =
            task {
                match! this.SpawnAndCapture(command, cancellationToken) with
                | Error error -> return Error error
                | Ok captured ->
                    return
                        Ok(
                            ProcessResult<byte[]>(
                                command.Program,
                                captured.Stdout,
                                captured.Stderr,
                                captured.Outcome,
                                captured.Duration,
                                false,
                                command.Config.OkCodes
                            )
                        )
            }

    interface IDisposable with
        member this.Dispose() =
            releaseContainer ()
            GC.SuppressFinalize this

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            releaseContainer ()
            GC.SuppressFinalize this
            ValueTask.CompletedTask
