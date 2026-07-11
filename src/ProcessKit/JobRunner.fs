namespace ProcessKit

open System
open System.Threading
open System.Threading.Tasks

/// The default `IProcessRunner`: spawns each command into a fresh kill-on-dispose `ProcessGroup`
/// (owned by the returned `RunningProcess`), then captures or streams its output and reaps the
/// whole tree on completion, failure, or cancellation.
[<Sealed>]
type JobRunner() =

    let start (command: Command) : Task<Result<RunningProcess, ProcessError>> =
        task {
            match ProcessGroup.Create() with
            | Error error -> return Error error
            | Ok group ->
                match group.StartInternal command with
                | Error error ->
                    (group :> IDisposable).Dispose()
                    return Error error
                | Ok host ->
                    // Ownership of the freshly-spawned tree transfers to the caller through the returned
                    // handle. `RunningProcess.buildGuarded` (shared with `ProcessGroup.StartAsync`) reaps
                    // the tree via `host.Teardown()` and re-raises should the constructor ever fault.
                    let! running = RunningProcess.buildGuarded host
                    return Ok running
        }

    // Run a started process to a completion result via the shared `CaptureVerbs.runToCompletion`,
    // supplying JobRunner's own `start` — a fresh owned kill-on-dispose group per run. Retry is applied
    // by the `Runner` verbs (it must wrap `ensureSuccess`, since a non-zero exit is data here, not an
    // error), so this path runs the command exactly once.
    let runToCompletion
        (command: Command)
        (cancellationToken: CancellationToken)
        (consume: RunningProcess -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        CaptureVerbs.runToCompletion command cancellationToken (fun () -> start command) consume

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            // An already-cancelled token must not spawn a tree the caller has to remember to dispose:
            // report it as an error up front, matching `ProcessGroup` so both runners honour the
            // contract that a cancelled run is always an error.
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
                start command

        member _.CaptureStringAsync(command, cancellationToken) =
            runToCompletion command cancellationToken (fun running -> running.OutputStringAsync())

        member _.CaptureBytesAsync(command, cancellationToken) =
            runToCompletion command cancellationToken (fun running -> running.OutputBytesAsync())
