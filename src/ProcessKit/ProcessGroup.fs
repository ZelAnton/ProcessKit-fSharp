namespace ProcessKit

open System
open System.Diagnostics
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
/// On Windows the container is a Job Object (`KILL_ON_JOB_CLOSE`); on Linux/macOS a POSIX process
/// group (`killpg` teardown). The active mechanism is reported honestly by `Mechanism`.
[<Sealed>]
type ProcessGroup private (mechanism: Mechanism, jobHandle: nativeint) =

    // Windows: pgid unused, processHandle is the (single) child's process handle.
    // POSIX: jobHandle/processHandle unused, pgid records the spawned child's pgid (0 until spawned).
    let mutable pgid = 0
    let mutable processHandle = IntPtr.Zero
    // 0 = live, 1 = released. Interlocked so Dispose/DisposeAsync/Shutdown/finalizer run the
    // teardown exactly once and never signal a handle or pgid after it has been released.
    let mutable releasedFlag = 0

    let killContainedTree () =
        match mechanism with
        | Mechanism.JobObject -> Native.terminateWindowsJob jobHandle
        | _ ->
            if pgid <> 0 then
                Native.killProcessGroup pgid

    // The hard teardown, run exactly once by whoever wins `releasedFlag`. On Windows, closing the
    // job handle triggers KILL_ON_JOB_CLOSE; a POSIX process group is not a closable object, so
    // SIGKILL reaps any survivors. (The pgid-reuse hazard is closed by cgroup v2 in the limits
    // feature.) The child's process handle (Windows) is closed here too.
    let hardRelease () =
        match mechanism with
        | Mechanism.JobObject ->
            if processHandle <> IntPtr.Zero then
                Native.closeWindowsHandle processHandle

            Native.closeWindowsHandle jobHandle
        | _ ->
            if pgid <> 0 then
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

    /// Create a new, empty kill-on-dispose group on the current platform.
    static member Create() : Result<ProcessGroup, ProcessError> =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            match Native.createWindowsJob () with
            | Ok job -> Ok(new ProcessGroup(Mechanism.JobObject, job))
            | Error error -> Error error
        else
            // The POSIX group forms when the first child is spawned (its pid becomes the pgid).
            Ok(new ProcessGroup(Mechanism.ProcessGroup, IntPtr.Zero))

    /// Spawn `command` into the group, recording the pgid / process handle. Internal.
    member internal _.SpawnInto(command: Command) : Result<Native.Spawned, ProcessError> =
        let spawn =
            match mechanism with
            | Mechanism.JobObject -> Native.spawnWindows jobHandle command
            | _ -> Native.spawnPosix command

        match spawn with
        | Error error -> Error error
        | Ok spawned ->
            match mechanism with
            | Mechanism.JobObject -> processHandle <- spawned.Handle
            | _ -> pgid <- int spawned.Handle

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
                    use _registration = cancellationToken.Register(fun () -> killContainedTree ())

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
            | _ ->
                if pgid <> 0 then
                    Native.terminateProcessGroup pgid
                    let stopwatch = Stopwatch.StartNew()

                    while Native.processGroupAlive pgid && stopwatch.Elapsed < grace do
                        do! Task.Delay 50

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
                | _ ->
                    if pgid <> 0 then
                        Native.terminateProcessGroup pgid
                        let stopwatch = Stopwatch.StartNew()

                        while Native.processGroupAlive pgid && stopwatch.Elapsed < gracePeriod do
                            do! Task.Delay 50

                        if Native.processGroupAlive pgid then
                            Native.killProcessGroup pgid

                hardRelease ()
                GC.SuppressFinalize this
        }
        :> Task

    // The finalizer is the GC-time safety net for a group that was never disposed: it reaps the
    // tree. Deterministic teardown still comes from `use`/`Dispose`.
    override _.Finalize() = releaseContainer ()

    interface IDisposable with
        member this.Dispose() =
            releaseContainer ()
            GC.SuppressFinalize this

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            releaseContainer ()
            GC.SuppressFinalize this
            ValueTask.CompletedTask
