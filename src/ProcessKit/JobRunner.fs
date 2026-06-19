namespace ProcessKit

open System.Text
open System.Threading
open System.Threading.Tasks

/// The default `IProcessRunner`: spawns each command into a fresh kill-on-dispose
/// `ProcessGroup`, captures its output to completion, and reaps the whole tree when the run
/// ends (success, failure, or cancellation).
[<Sealed>]
type JobRunner() =

    let capture (command: Command) (cancellationToken: CancellationToken) : Task<Result<CapturedRun, ProcessError>> =
        task {
            match ProcessGroup.Create() with
            | Error error -> return Error error
            | Ok group ->
                use grp = group
                return! grp.SpawnAndCapture(command, cancellationToken)
        }

    interface IProcessRunner with
        member _.OutputString(command, cancellationToken) =
            task {
                match! capture command cancellationToken with
                | Error error -> return Error error
                | Ok run ->
                    let result =
                        ProcessResult<string>(
                            command.Program,
                            Encoding.UTF8.GetString run.Stdout,
                            run.Stderr,
                            run.Outcome,
                            run.Duration,
                            false
                        )

                    return Ok result
            }

        member _.OutputBytes(command, cancellationToken) =
            task {
                match! capture command cancellationToken with
                | Error error -> return Error error
                | Ok run ->
                    let result =
                        ProcessResult<byte[]>(command.Program, run.Stdout, run.Stderr, run.Outcome, run.Duration, false)

                    return Ok result
            }
