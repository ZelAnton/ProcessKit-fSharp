namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

/// A captured run before it is shaped into a `ProcessResult` (stdout kept as raw bytes so
/// the text and bytes verbs share one capture path).
type internal CapturedRun =
    { Stdout: byte[]
      Stderr: string
      Outcome: Outcome
      Duration: TimeSpan }

/// A kill-on-dispose container for a process *tree*.
///
/// Every process started into the group — and everything those processes spawn — is reaped
/// when the group is disposed (deterministic under `use`/`use!`) or, failing that, when the
/// GC finalizes it. On Windows the container is a Job Object (`KILL_ON_JOB_CLOSE`); on
/// Linux/macOS a POSIX process group (`killpg` teardown). The active mechanism is reported
/// honestly by `Mechanism`.
[<Sealed>]
type ProcessGroup private (mechanism: Mechanism, jobHandle: nativeint) =

    // Windows uses `jobHandle`; POSIX records the spawned child's pgid here (0 until spawned).
    let mutable pgid = 0
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
    // only job handle triggers KILL_ON_JOB_CLOSE; a POSIX process group is not a closable object,
    // so SIGKILL reaps any survivors. (The pgid-reuse hazard is closed by cgroup v2 in the limits
    // feature.)
    let hardRelease () =
        match mechanism with
        | Mechanism.JobObject -> Native.closeWindowsHandle jobHandle
        | _ ->
            if pgid <> 0 then
                Native.killProcessGroup pgid

    let releaseContainer () =
        if Interlocked.Exchange(&releasedFlag, 1) = 0 then
            hardRelease ()

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

    /// Spawn `command` into the group, capture stdout/stderr to completion, and reap it.
    /// A cancelled token terminates the whole tree and resolves to `ProcessError.Cancelled`.
    member internal _.SpawnAndCapture
        (command: Command, cancellationToken: CancellationToken)
        : Task<Result<CapturedRun, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                let spawn =
                    match mechanism with
                    | Mechanism.JobObject -> Native.spawnWindows jobHandle command
                    | _ -> Native.spawnPosix command

                match spawn with
                | Error error -> return Error error
                | Ok spawned ->
                    // On POSIX the pgid equals the freshly spawned child's pid; record it before
                    // arming cancellation so a cancel targets the right group.
                    match mechanism with
                    | Mechanism.JobObject -> ()
                    | _ -> pgid <- int spawned.Handle

                    try
                        let startedAt = Stopwatch.GetTimestamp()
                        use stdout = spawned.Stdout
                        use stderr = spawned.Stderr
                        use outBuffer = new MemoryStream()
                        use errBuffer = new MemoryStream()
                        // Read both pipes concurrently with the run, or a full pipe buffer would
                        // deadlock the child.
                        let readStdout = stdout.CopyToAsync outBuffer
                        let readStderr = stderr.CopyToAsync errBuffer
                        use _registration = cancellationToken.Register(fun () -> killContainedTree ())

                        let! outcome =
                            match mechanism with
                            | Mechanism.JobObject -> Native.waitWindows spawned.Handle
                            | _ -> Native.waitPosix spawned.Handle

                        do! readStdout
                        do! readStderr
                        let duration = Stopwatch.GetElapsedTime startedAt

                        if cancellationToken.IsCancellationRequested then
                            return Error(ProcessError.Cancelled command.Program)
                        else
                            return
                                Ok
                                    { Stdout = outBuffer.ToArray()
                                      Stderr = Encoding.UTF8.GetString(errBuffer.ToArray())
                                      Outcome = outcome
                                      Duration = duration }
                    finally
                        // Close the Windows process handle on every path so a failed read cannot
                        // leak it. The job handle (owned by the group) reaps the tree separately;
                        // POSIX reaps the pid via waitPosix, so there is nothing to close there.
                        match mechanism with
                        | Mechanism.JobObject -> Native.closeWindowsHandle spawned.Handle
                        | _ -> ()
        }

    /// Tear the group down gracefully, then release it. On Unix: SIGTERM, then SIGKILL if the
    /// group is still alive after `gracePeriod`. On Windows: an atomic Job kill (there is no
    /// per-job graceful signal). Idempotent with `Dispose` — disposing instead skips the grace
    /// period and kills immediately.
    member this.Shutdown(gracePeriod: TimeSpan) : Task =
        task {
            // Win the single-teardown race; if Dispose or another Shutdown already released the
            // group, do nothing rather than signal a closed handle / reused pgid.
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

    // The finalizer is the GC-time safety net for a group that was never disposed: it reaps
    // the tree. Deterministic teardown still comes from `use`/`Dispose`.
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
