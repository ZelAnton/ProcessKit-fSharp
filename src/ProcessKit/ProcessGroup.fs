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

/// One stage's terminal state inside a pipeline run.
type internal PipelineStage =
    { Program: string
      Outcome: Outcome
      Unchecked: bool
      Stderr: string }

/// The captured result of running a whole pipeline: the last stage's stdout, every stage's
/// terminal state (left-to-right), the wall-clock duration, and whether the pipeline timed out.
type internal PipelineCapture =
    { LastStdout: byte[]
      Stages: PipelineStage list
      Duration: TimeSpan
      TimedOut: bool }

/// A **pull-based** periodic `ProcessGroupStats` series (the iterator behind `SampleStats`): the
/// first sample lands on the first `MoveNextAsync`, then one per `period`. It samples only when
/// pulled and runs no background task, so abandoning it does no work and — crucially — does not keep
/// the group alive, preserving kill-on-drop. The series ends on the first failing snapshot (e.g.
/// after the group is released) or when `cancellationToken` fires.
type internal StatsSampler
    (sample: unit -> Result<ProcessGroupStats, ProcessError>, period: TimeSpan, cancellationToken: CancellationToken) =

    let mutable current = Unchecked.defaultof<ProcessGroupStats>
    let mutable first = true
    let mutable finished = false

    interface IAsyncEnumerator<ProcessGroupStats> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            ValueTask<bool>(
                task {
                    if finished || cancellationToken.IsCancellationRequested then
                        finished <- true
                        return false
                    else
                        if first then
                            first <- false
                        else
                            try
                                do! Task.Delay(period, cancellationToken)
                            with :? OperationCanceledException ->
                                finished <- true

                        if finished then
                            return false
                        else
                            match sample () with
                            | Ok snapshot ->
                                current <- snapshot
                                return true
                            | Error _ ->
                                finished <- true
                                return false
                }
            )

        member _.DisposeAsync() = ValueTask.CompletedTask

type internal StatsSamplerSeq(sample: unit -> Result<ProcessGroupStats, ProcessError>, period: TimeSpan) =
    interface IAsyncEnumerable<ProcessGroupStats> with
        member _.GetAsyncEnumerator(cancellationToken) =
            StatsSampler(sample, period, cancellationToken) :> IAsyncEnumerator<ProcessGroupStats>

/// A kill-on-dispose container for a process *tree*.
///
/// Every process started into the group — and everything those processes spawn — is reaped when
/// the group is disposed (deterministic under `use`) or, failing that, when the GC finalizes it.
/// On Windows the container is a Job Object (`KILL_ON_JOB_CLOSE`); on Linux/macOS a POSIX process
/// group (`killpg` teardown). The active mechanism is reported honestly by `Mechanism`.
[<Sealed>]
type ProcessGroup private (mechanism: Mechanism, jobHandle: nativeint, cgroupPath: string, options: ProcessGroupOptions)
    =

    // The contained children, tracked so the whole tree can be signalled / killed. Windows: each
    // child's process handle (closed on reap or group teardown). POSIX process group: each child's
    // pgid — every `posix_spawn` makes its *own* process group, so a multi-child group (e.g. a
    // pipeline) holds several. Cgroup v2: membership lives in the cgroup itself (`cgroup.procs`), so
    // neither list is used — `cgroupPath` is the container. Guarded by `childLock` because
    // shared-group runs spawn and reap concurrently.
    let childLock = obj ()
    let processHandles = System.Collections.Generic.List<nativeint>()
    let childPgids = System.Collections.Generic.List<int>()
    // 0 = live, 1 = released. Interlocked so Dispose/DisposeAsync/Shutdown/finalizer run the
    // teardown exactly once.
    let mutable releasedFlag = 0

    let pgidSnapshot () =
        lock childLock (fun () -> List.ofSeq childPgids)

    let trackChild (spawned: Native.Spawned) =
        match mechanism with
        | Mechanism.JobObject -> lock childLock (fun () -> processHandles.Add spawned.Handle)
        // Place the child in the cgroup; membership is tracked by the kernel, not a list.
        | Mechanism.CgroupV2 -> Native.migrateToCgroup cgroupPath (int spawned.Handle)
        | Mechanism.ProcessGroup -> lock childLock (fun () -> childPgids.Add(int spawned.Handle))

    // Stop tracking a *reaped* child. Windows: close its process handle (removing it first so the
    // group teardown will not double-close a possibly-reused handle value); the Job still contains
    // the tree. POSIX process group: a pgid is a whole *group*, and the reaped leader may have left
    // backgrounded members behind — only stop tracking once the group is actually empty, so
    // Shutdown/Dispose can still reap lingering members. Cgroup: nothing to untrack (the kernel
    // removes an exited process from `cgroup.procs`).
    let releaseChild (spawned: Native.Spawned) =
        match mechanism with
        | Mechanism.JobObject ->
            let wasTracked = lock childLock (fun () -> processHandles.Remove spawned.Handle)

            if wasTracked then
                Native.closeWindowsHandle spawned.Handle
        | Mechanism.CgroupV2 -> ()
        | Mechanism.ProcessGroup ->
            let pgid = int spawned.Handle

            if not (Native.processGroupAlive pgid) then
                lock childLock (fun () -> childPgids.Remove pgid |> ignore)

    // Hard-kill a single contained child (not the whole group): the lone process on Windows / cgroup
    // (its descendants stay in the shared container), its own pgid (subtree) on a POSIX group.
    let killChild (spawned: Native.Spawned) =
        match mechanism with
        | Mechanism.JobObject -> Native.terminateWindowsProcess spawned.Handle
        | Mechanism.CgroupV2 -> Native.killProcess (int spawned.Handle)
        | Mechanism.ProcessGroup -> Native.killProcessGroup (int spawned.Handle)

    let anyChildAlive () =
        match mechanism with
        | Mechanism.CgroupV2 -> Native.cgroupAlive cgroupPath
        | _ -> pgidSnapshot () |> List.exists Native.processGroupAlive

    let killContainedTree () =
        match mechanism with
        | Mechanism.JobObject -> Native.terminateWindowsJob jobHandle
        | Mechanism.CgroupV2 -> Native.killCgroup cgroupPath
        | Mechanism.ProcessGroup ->
            for pgid in pgidSnapshot () do
                Native.killProcessGroup pgid

    // The hard teardown, run exactly once by whoever wins `releasedFlag`. Windows: closing the job
    // handle triggers KILL_ON_JOB_CLOSE. Cgroup v2: `cgroup.kill` SIGKILLs the subtree atomically,
    // then the (drained) directory is removed. POSIX process group: not a closable object, so SIGKILL
    // reaps survivors. Any still-tracked child process handles (Windows) are closed here too.
    let hardRelease () =
        match mechanism with
        | Mechanism.JobObject ->
            let handles =
                lock childLock (fun () ->
                    let copy = List.ofSeq processHandles
                    processHandles.Clear()
                    copy)

            for handle in handles do
                Native.closeWindowsHandle handle

            Native.closeWindowsHandle jobHandle
        | Mechanism.CgroupV2 ->
            Native.killCgroup cgroupPath
            Native.removeCgroup cgroupPath
        | Mechanism.ProcessGroup ->
            for pgid in pgidSnapshot () do
                Native.killProcessGroup pgid

    let releaseContainer () =
        if Interlocked.Exchange(&releasedFlag, 1) = 0 then
            hardRelease ()

    let waitOutcome (handle: nativeint) : Task<Outcome> =
        match mechanism with
        | Mechanism.JobObject -> Native.waitWindows handle
        | _ -> Native.waitPosix handle

    /// The OS primitive containing this group on the current platform.
    member _.Mechanism = mechanism

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

        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            match Native.createWindowsJob () with
            | Error error -> Error error
            | Ok job ->
                if limits.Any then
                    match Native.applyWindowsJobLimits job limits with
                    | Ok() -> Ok(new ProcessGroup(Mechanism.JobObject, job, "", options))
                    | Error message ->
                        Native.closeWindowsHandle job
                        Error(ProcessError.ResourceLimit message)
                else
                    Ok(new ProcessGroup(Mechanism.JobObject, job, "", options))
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux && limits.Any then
            if Native.cgroupV2Available () then
                match Native.createCgroup limits with
                | Ok path -> Ok(new ProcessGroup(Mechanism.CgroupV2, IntPtr.Zero, path, options))
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
            Ok(new ProcessGroup(Mechanism.ProcessGroup, IntPtr.Zero, "", options))

    /// Wait for one contained process handle to conclude. Internal — used by pipeline staging.
    member internal _.WaitHandle(handle: nativeint) : Task<Outcome> = waitOutcome handle

    /// Hard-kill the contained tree now (no grace) without releasing the group. Internal — used by
    /// pipeline cancellation/timeout.
    member internal _.KillTree() = killContainedTree ()

    /// Spawn `command` into the group, tracking the child for signalling / teardown. Internal.
    member internal _.SpawnInto(command: Command) : Result<Native.Spawned, ProcessError> =
        let spawn =
            match mechanism with
            | Mechanism.JobObject -> Native.spawnWindows jobHandle command
            | _ -> Native.spawnPosix command

        match spawn with
        | Error error -> Error error
        | Ok spawned ->
            trackChild spawned
            Ok spawned

    /// Spawn `command` into the group and build a `RunningHost`. Disposing the resulting
    /// `RunningProcess` reaps this whole group (the group is owned by the running process).
    member internal this.StartInternal(command: Command) : Result<RunningHost, ProcessError> =
        match this.SpawnInto command with
        | Error error -> Error error
        | Ok spawned ->
            // Feed a stdin source in the background, then close stdin (EOF): a source is the
            // child's complete input. `KeepStdinOpen` is only for the no-source interactive case,
            // where the stream is handed to the caller via `TakeStdin` instead of being fed here.
            match spawned.Stdin, command.Config.StdinSource with
            | Some stdinStream, Some source ->
                Pump.feedStdin source.Source stdinStream true CancellationToken.None |> ignore
            | _ -> ()

            let pid =
                match mechanism with
                | Mechanism.JobObject -> Some(Native.processIdWindows spawned.Handle)
                | _ -> Some(int spawned.Handle)

            Ok
                { Config = command.Config
                  Pid = pid
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
                  StartKill = killContainedTree
                  GracefulKill = (fun grace -> this.GracefulKillTree grace)
                  Teardown =
                    fun () ->
                        // Close the pipe streams (OS handles/fds) before releasing the container.
                        spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                        spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                        spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                        (this :> IDisposable).Dispose()
                        ValueTask.CompletedTask }

    /// Spawn `command` into this *shared* group and build a `RunningHost`. Unlike `StartInternal`,
    /// disposing the resulting `RunningProcess` does **not** reap the group — the group owns the
    /// child's lifetime (reaped on `Shutdown`/`Dispose`). Internal — `Start` wraps it.
    member internal this.StartShared(command: Command) : Result<RunningHost, ProcessError> =
        match this.SpawnInto command with
        | Error error -> Error error
        | Ok spawned ->
            match spawned.Stdin, command.Config.StdinSource with
            | Some stdinStream, Some source ->
                Pump.feedStdin source.Source stdinStream true CancellationToken.None |> ignore
            | _ -> ()

            let pid =
                match mechanism with
                | Mechanism.JobObject -> Some(Native.processIdWindows spawned.Handle)
                | _ -> Some(int spawned.Handle)

            Ok
                { Config = command.Config
                  Pid = pid
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
                  StartKill = (fun () -> killChild spawned)
                  GracefulKill = (fun _ -> task { killChild spawned } :> Task)
                  Teardown =
                    fun () ->
                        // Shared group: closing this run's `RunningProcess` detaches its I/O only —
                        // the GROUP owns the child's lifetime (group Shutdown/Dispose reaps it).
                        spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                        spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                        spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                        // Stop tracking it — POSIX only once its group is empty, so a still-live
                        // child stays reapable by the group; Windows closes the handle (the Job
                        // still contains and will kill the tree).
                        releaseChild spawned
                        ValueTask.CompletedTask }

    /// Start `command` into this shared group and return a live `RunningProcess`. The **group** owns
    /// the child's lifetime: disposing the returned process detaches its I/O but does not kill it —
    /// reap the tree with `Shutdown`/`Dispose`, or this one run with its `StartKill`.
    member this.Start(command: Command) : Task<Result<RunningProcess, ProcessError>> =
        task {
            match this.StartShared command with
            | Error error -> return Error error
            | Ok host -> return Ok(RunningProcess host)
        }

    /// Immediately hard-kill every process currently in the group. Idempotent; the group stays
    /// usable for further spawns. Returns `Result` for parity with the other tree-control verbs (a
    /// future cgroup backend can report an undrained tree); the current backends always succeed.
    member _.TerminateAll() : Result<unit, ProcessError> =
        killContainedTree ()
        Ok()

    /// The pids of the processes currently in the group — a point-in-time snapshot. On Windows the
    /// whole Job tree; on the POSIX fallback the tracked group leaders (one per spawned child).
    /// Returns `Result` because a future cgroup backend's `cgroup.procs` read can fail; the current
    /// backends always succeed.
    member _.Members() : Result<int list, ProcessError> =
        match mechanism with
        | Mechanism.JobObject -> Ok(Native.membersWindows jobHandle)
        | Mechanism.CgroupV2 -> Ok(Native.cgroupMembers cgroupPath)
        | Mechanism.ProcessGroup -> Ok(pgidSnapshot ())

    /// Broadcast `signal` to every process in the group. Best-effort: an exited member is skipped
    /// and an empty group succeeds trivially. On **Windows** only `Signal.Kill` is deliverable (it
    /// maps to the Job terminate); any other signal returns `ProcessError.Unsupported`.
    member _.Signal(signal: Signal) : Result<unit, ProcessError> =
        match mechanism with
        | Mechanism.JobObject ->
            match signal with
            | Signal.Kill ->
                Native.terminateWindowsJob jobHandle
                Ok()
            | _ -> Error(ProcessError.Unsupported $"signal {signal} on Windows (only Signal.Kill is deliverable)")
        | Mechanism.CgroupV2 ->
            match signal with
            | Signal.Kill -> Native.killCgroup cgroupPath // atomic whole-subtree SIGKILL
            | _ -> Native.signalCgroup cgroupPath (Native.signalNumber signal)

            Ok()
        | Mechanism.ProcessGroup ->
            let signalNum = Native.signalNumber signal

            for pgid in pgidSnapshot () do
                Native.signalProcessGroup pgid signalNum |> ignore

            Ok()

    /// Suspend (freeze) every process in the group. POSIX: `SIGSTOP` (level-triggered, idempotent).
    /// Windows: suspend every thread of every member — best-effort, and suspend counts stack, so N
    /// `Suspend`s need N `Resume`s.
    member _.Suspend() : Result<unit, ProcessError> =
        match mechanism with
        | Mechanism.JobObject ->
            Native.suspendWindows jobHandle
            Ok()
        | Mechanism.CgroupV2 ->
            Native.freezeCgroup cgroupPath true
            Ok()
        | Mechanism.ProcessGroup ->
            for pgid in pgidSnapshot () do
                Native.suspendProcessGroup pgid

            Ok()

    /// Resume a tree suspended by `Suspend`.
    member _.Resume() : Result<unit, ProcessError> =
        match mechanism with
        | Mechanism.JobObject ->
            Native.resumeWindows jobHandle
            Ok()
        | Mechanism.CgroupV2 ->
            Native.freezeCgroup cgroupPath false
            Ok()
        | Mechanism.ProcessGroup ->
            for pgid in pgidSnapshot () do
                Native.resumeProcessGroup pgid

            Ok()

    /// A snapshot of the group's resource usage. On Windows this reads the Job Object's accounting
    /// (CPU + peak committed memory + active count); on the POSIX fallback only the live group count
    /// is available (CPU/memory are `None` until the cgroup v2 backend). Errors once the group is
    /// released.
    member _.Stats() : Result<ProcessGroupStats, ProcessError> =
        if releasedFlag <> 0 then
            Error(ProcessError.Io "the process group has been released")
        else
            match mechanism with
            | Mechanism.JobObject ->
                match Native.jobStatsWindows jobHandle with
                | Some(active, cpu, peak) -> Ok(ProcessGroupStats(active, Some cpu, Some peak))
                | None -> Error(ProcessError.Io "failed to query Job Object accounting")
            | Mechanism.CgroupV2 ->
                let active = List.length (Native.cgroupMembers cgroupPath)
                let cpu, peak = Native.cgroupStats cgroupPath
                Ok(ProcessGroupStats(active, cpu, peak))
            | Mechanism.ProcessGroup ->
                let active = pgidSnapshot () |> List.filter Native.processGroupAlive |> List.length
                Ok(ProcessGroupStats(active, None, None))

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
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match this.SpawnInto command with
                | Error error -> return Error error
                | Ok spawned ->
                    let startedAt = Stopwatch.GetTimestamp()
                    use _registration = cancellationToken.Register(fun () -> killChild spawned)

                    // Feed a stdin source, then close stdin (EOF) — a source is the child's full input.
                    match spawned.Stdin, command.Config.StdinSource with
                    | Some stdinStream, Some source ->
                        Pump.feedStdin source.Source stdinStream true CancellationToken.None |> ignore
                    | _ -> ()

                    let stdoutTask =
                        match spawned.Stdout with
                        | Some s -> Pump.drainRaw s None CancellationToken.None
                        | None -> Task.FromResult Array.empty<byte>

                    let stderrTask =
                        match spawned.Stderr with
                        | Some s -> Pump.drainRaw s None CancellationToken.None
                        | None -> Task.FromResult Array.empty<byte>

                    let! outcome = waitOutcome spawned.Handle
                    let! stdoutBytes = stdoutTask
                    let! stderrBytes = stderrTask
                    spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                    spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                    spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                    // The child is reaped: stop tracking it (close its Windows handle) so a long-lived
                    // shared group does not accumulate handles/pgids across many captured runs.
                    releaseChild spawned
                    let duration = Stopwatch.GetElapsedTime startedAt

                    if cancellationToken.IsCancellationRequested then
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
    member internal _.GracefulKillTree(grace: TimeSpan) : Task =
        task {
            match mechanism with
            | Mechanism.JobObject -> Native.terminateWindowsJob jobHandle
            | Mechanism.CgroupV2 ->
                Native.terminateCgroup cgroupPath
                let stopwatch = Stopwatch.StartNew()

                while Native.cgroupAlive cgroupPath && stopwatch.Elapsed < grace do
                    do! Task.Delay 50

                if Native.cgroupAlive cgroupPath then
                    Native.killCgroup cgroupPath
            | Mechanism.ProcessGroup ->
                let pgids = pgidSnapshot ()

                if not (List.isEmpty pgids) then
                    for pgid in pgids do
                        Native.terminateProcessGroup pgid

                    let stopwatch = Stopwatch.StartNew()

                    while anyChildAlive () && stopwatch.Elapsed < grace do
                        do! Task.Delay 50

                    for pgid in pgids do
                        if Native.processGroupAlive pgid then
                            Native.killProcessGroup pgid
        }
        :> Task

    /// Tear the group down gracefully, then release it. On Unix: SIGTERM, then SIGKILL if still
    /// alive after `gracePeriod`. On Windows: an atomic Job kill. Idempotent with `Dispose`.
    member this.Shutdown(gracePeriod: TimeSpan) : Task =
        task {
            if Interlocked.Exchange(&releasedFlag, 1) = 0 then
                match mechanism with
                | Mechanism.JobObject -> Native.terminateWindowsJob jobHandle
                | Mechanism.CgroupV2 ->
                    Native.terminateCgroup cgroupPath
                    let stopwatch = Stopwatch.StartNew()

                    while Native.cgroupAlive cgroupPath && stopwatch.Elapsed < gracePeriod do
                        do! Task.Delay 50
                    // hardRelease below does the cgroup.kill + rmdir.
                    ()
                | Mechanism.ProcessGroup ->
                    let pgids = pgidSnapshot ()

                    if not (List.isEmpty pgids) then
                        for pgid in pgids do
                            Native.terminateProcessGroup pgid

                        let stopwatch = Stopwatch.StartNew()

                        while anyChildAlive () && stopwatch.Elapsed < gracePeriod do
                            do! Task.Delay 50

                        for pgid in pgids do
                            if Native.processGroupAlive pgid then
                                Native.killProcessGroup pgid

                hardRelease ()
                GC.SuppressFinalize this
        }
        :> Task

    /// `Shutdown` using the group's configured `Options.ShutdownTimeout`.
    member this.Shutdown() : Task = this.Shutdown options.ShutdownTimeout

    /// Run a multi-stage pipeline: spawn every stage into one fresh shared group, wire each stage's
    /// stdout to the next stage's stdin (no shell involved), capture the last stage's stdout, and
    /// reap the whole tree on exit. Cancellation or the optional `timeout` hard-kill the tree.
    /// Internal — `Pipeline` shapes the capture into the verb results.
    static member internal RunPipeline
        (commands: Command list)
        (timeout: TimeSpan option)
        (lastTee: Stream option)
        (cancellationToken: CancellationToken)
        : Task<Result<PipelineCapture, ProcessError>> =
        task {
            let stages = List.toArray commands

            if stages.Length = 0 then
                return Error(ProcessError.Spawn("<pipeline>", "a pipeline needs at least one stage"))
            elif cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
            else
                match ProcessGroup.Create() with
                | Error error -> return Error error
                | Ok group ->
                    use group = group
                    let startedAt = Stopwatch.GetTimestamp()
                    use timeoutCts = new CancellationTokenSource()

                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

                    match timeout with
                    | Some duration -> timeoutCts.CancelAfter duration
                    | None -> ()

                    use _registration = linkedCts.Token.Register(fun () -> group.KillTree())

                    // Dispose a pipe stream, swallowing the teardown-race exceptions (double close,
                    // or a broken pipe surfaced while flushing on dispose because the peer is gone).
                    let closeQuietly (stream: Stream) =
                        try
                            stream.Dispose()
                        with
                        | :? ObjectDisposedException ->
                            // Already disposed (double close during teardown); nothing to do.
                            ()
                        | :? IOException ->
                            // The pipe broke while flushing on dispose (peer end already gone). The
                            // run's outcome already reflects it; closing is best-effort teardown.
                            ()

                    let spawned = ResizeArray<Native.Spawned>()
                    let copyTasks = ResizeArray<Task>()
                    let stderrTasks = ResizeArray<Task<byte[]>>()
                    let mutable prevStdout: Stream option = None
                    let mutable spawnError = None
                    let mutable index = 0

                    while index < stages.Length && spawnError.IsNone do
                        // Stages after the first take their stdin from the previous stage's stdout, so
                        // they need a stdin pipe; `KeepStdinOpen` creates one without an auto-fed
                        // source (we copy `prevStdout` into it below).
                        let stage =
                            if index > 0 then
                                stages[index].KeepStdinOpen()
                            else
                                stages[index]

                        match group.SpawnInto stage with
                        | Error error -> spawnError <- Some error
                        | Ok sp ->
                            spawned.Add sp

                            if index = 0 then
                                // Only the first stage may carry its own stdin source; feed it.
                                match sp.Stdin, stages[0].Config.StdinSource with
                                | Some stdinStream, Some source ->
                                    Pump.feedStdin source.Source stdinStream true CancellationToken.None |> ignore
                                | _ -> ()
                            else
                                match prevStdout, sp.Stdin with
                                | Some upstream, Some downstream ->
                                    copyTasks.Add(
                                        task {
                                            try
                                                do! upstream.CopyToAsync downstream
                                            with _ ->
                                                // A downstream stage that exits early stops reading
                                                // (broken pipe), or the stream is torn down during
                                                // teardown. Fall through to close both ends.
                                                ()

                                            // Close the write end (EOF to the downstream stage) AND the
                                            // upstream read end: if the downstream exited early, closing
                                            // the read end propagates a broken pipe up to the producing
                                            // stage (SIGPIPE / failed write) so it stops instead of
                                            // blocking forever on a full stdout pipe.
                                            closeQuietly downstream
                                            closeQuietly upstream
                                        }
                                        :> Task
                                    )
                                | _ -> ()

                            // Drain every stage's stderr so a full stderr pipe never blocks a stage.
                            match sp.Stderr with
                            | Some s -> stderrTasks.Add(Pump.drainRaw s None CancellationToken.None)
                            | None -> stderrTasks.Add(Task.FromResult Array.empty<byte>)

                            prevStdout <- sp.Stdout

                        index <- index + 1

                    match spawnError with
                    | Some error ->
                        // The group's `use` dispose reaps any stages that did start.
                        return Error error
                    | None ->
                        let lastSpawned = spawned[spawned.Count - 1]

                        let captureTask =
                            match lastSpawned.Stdout with
                            | Some s -> Pump.drainRaw s lastTee CancellationToken.None
                            | None -> Task.FromResult Array.empty<byte>

                        let waitTasks =
                            spawned |> Seq.map (fun sp -> group.WaitHandle sp.Handle) |> Seq.toArray

                        let! outcomes = Task.WhenAll waitTasks
                        do! Task.WhenAll(copyTasks.ToArray())
                        let! lastStdout = captureTask
                        let! stderrBytes = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            sp.Stdout |> Option.iter closeQuietly
                            sp.Stderr |> Option.iter closeQuietly
                            sp.Stdin |> Option.iter closeQuietly

                        let duration = Stopwatch.GetElapsedTime startedAt

                        if cancellationToken.IsCancellationRequested then
                            return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
                        else
                            let stageResults =
                                [ for i in 0 .. stages.Length - 1 ->
                                      { Program = stages[i].Program
                                        Outcome = outcomes[i]
                                        Unchecked = stages[i].Config.UncheckedInPipe
                                        Stderr = stages[i].Config.StderrEncoding.GetString stderrBytes[i] } ]

                            return
                                Ok
                                    { LastStdout = lastStdout
                                      Stages = stageResults
                                      Duration = duration
                                      TimedOut = timeoutCts.IsCancellationRequested }
        }

    // The finalizer is the GC-time safety net for a group that was never disposed: it reaps the
    // tree. Deterministic teardown still comes from `use`/`Dispose`.
    override _.Finalize() = releaseContainer ()

    // A `ProcessGroup` is itself an `IProcessRunner` — every run goes into THIS shared group rather
    // than a fresh private one, so a whole fleet shares one kill-on-dispose container (pass it to
    // `Supervisor.WithRunner`). Captured runs decode stdout with the command's encoding; they do not
    // apply the line-level `OutputBufferPolicy` or the per-run `Command.Timeout` (cancellation still
    // works) — use `Start` + the `RunningProcess` verbs, or the default `JobRunner`, for those.
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
