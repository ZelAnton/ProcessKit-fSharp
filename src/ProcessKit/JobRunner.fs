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
                | Ok host -> return Ok(RunningProcess host)
        }

    // Run a started process to a completion result, killing the tree if the (possibly
    // CancelOn-linked) token fires and reporting the cancellation as an error.
    let runToCompletion
        (command: Command)
        (cancellationToken: CancellationToken)
        (consume: RunningProcess -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            let config = command.Config

            use linkedCts =
                match config.CancelOn with
                | Some cancelOn -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancelOn)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            // Retry is applied by the `Runner` verbs (it must wrap `ensureSuccess`, since a
            // non-zero exit is data here, not an error). This path runs the command exactly once.
            if effectiveToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match! start command with
                | Error error -> return Error error
                | Ok running ->
                    use _registration = effectiveToken.Register(fun () -> running.Kill())
                    let! result = consume running

                    if effectiveToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        return result
        }

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
