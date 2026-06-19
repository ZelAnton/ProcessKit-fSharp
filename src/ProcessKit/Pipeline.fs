namespace ProcessKit

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// An immutable left-to-right chain of commands wired stdout -> stdin, with **no shell** involved:
/// each stage's standard output feeds the next stage's standard input directly. The whole chain
/// runs inside one shared kill-on-dispose group, so cancelling, timing out, or disposing the run
/// reaps every stage together.
///
/// Build it by piping commands (`a.Pipe(b).Pipe(c)`), then run it with the same verbs a single
/// command exposes. The exit status follows shell **pipefail**: the rightmost stage that did not
/// exit 0 determines the result, unless that stage opted out with `Command.UncheckedInPipe`.
[<Sealed>]
type Pipeline internal (commands: Command list, timeout: TimeSpan option, cancelOn: CancellationToken option) =

    // pipefail: the representative stage carries the program/outcome/stderr for the verb result.
    // It is the rightmost *checked* stage that did not exit 0; if every checked stage exited 0, the
    // last checked stage stands (so an unchecked, failed final stage does not fail the pipeline).
    // With no checked stages at all, the last stage stands. A timed-out pipeline reports TimedOut
    // against the last stage.
    static let representative (capture: PipelineCapture) : PipelineStage =
        let last = List.last capture.Stages

        if capture.TimedOut then
            { last with Outcome = Outcome.TimedOut }
        else
            let checkedStages = capture.Stages |> List.filter (fun stage -> not stage.Unchecked)

            let failed (stage: PipelineStage) =
                match stage.Outcome with
                | Outcome.Exited 0 -> false
                | _ -> true

            match checkedStages |> List.filter failed |> List.tryLast with
            | Some stage -> stage // rightmost checked failure -> the pipeline failed here
            | None ->
                // No checked stage failed: the pipeline succeeded. Report the last checked stage
                // (its exit 0), falling back to the last stage when every stage is unchecked.
                match List.tryLast checkedStages with
                | Some stage -> stage
                | None -> last

    member internal _.Commands = commands

    /// Append another stage; its stdin is fed from the current last stage's stdout.
    member _.Pipe(command: Command) =
        ArgumentNullException.ThrowIfNull command
        Pipeline(commands @ [ command ], timeout, cancelOn)

    /// Kill the whole pipeline after `duration`, reporting the result as `Outcome.TimedOut`.
    member _.Timeout(duration: TimeSpan) =
        Pipeline(commands, Some duration, cancelOn)

    /// Also cancel the whole pipeline when `cancellationToken` fires (in addition to any verb token).
    member _.CancelOn(cancellationToken: CancellationToken) =
        Pipeline(commands, timeout, Some cancellationToken)

    // Run the chain, honouring the pipeline-level `cancelOn` on top of the verb's token.
    member private _.Execute
        (lastTee: Stream option)
        (cancellationToken: CancellationToken)
        : Task<Result<PipelineCapture, ProcessError>> =
        task {
            match cancelOn with
            | Some extra ->
                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)

                return! ProcessGroup.RunPipeline commands timeout lastTee linked.Token
            | None -> return! ProcessGroup.RunPipeline commands timeout lastTee cancellationToken
        }

    /// Run the pipeline to completion, capturing the last stage's stdout as raw bytes. A non-zero
    /// pipefail exit is data here, not an error.
    member this.OutputBytes(cancellationToken: CancellationToken) : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        task {
            match! this.Execute None cancellationToken with
            | Error error -> return Error error
            | Ok capture ->
                let stage = representative capture

                return
                    Ok(
                        ProcessResult<byte[]>(
                            stage.Program,
                            capture.LastStdout,
                            stage.Stderr,
                            stage.Outcome,
                            capture.Duration,
                            false
                        )
                    )
        }

    /// Run the pipeline to completion, capturing the last stage's stdout as decoded text (using the
    /// last stage's stdout encoding). A non-zero pipefail exit is data here, not an error.
    member this.OutputString(cancellationToken: CancellationToken) : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            match! this.Execute None cancellationToken with
            | Error error -> return Error error
            | Ok capture ->
                let stage = representative capture
                let encoding = (List.last commands).Config.StdoutEncoding
                let text = encoding.GetString capture.LastStdout

                return
                    Ok(ProcessResult<string>(stage.Program, text, stage.Stderr, stage.Outcome, capture.Duration, false))
        }

    /// Require a successful pipefail exit and return the last stage's stdout, trailing whitespace
    /// trimmed. Any checked stage that did not exit 0 fails the pipeline.
    member this.Run(cancellationToken: CancellationToken) : Task<Result<string, ProcessError>> =
        task {
            match! this.OutputString cancellationToken with
            | Error error -> return Error error
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Error error -> return Error error
                | Ok ok -> return Ok(ok.Stdout.TrimEnd())
        }

    /// Like `Run`, but discard the captured output.
    member this.RunUnit(cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        task {
            match! this.Run cancellationToken with
            | Error error -> return Error error
            | Ok _ -> return Ok()
        }

    /// The pipefail exit code. A signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCode(cancellationToken: CancellationToken) : Task<Result<int, ProcessError>> =
        task {
            match! this.OutputString cancellationToken with
            | Error error -> return Error error
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited code -> return Ok code
                | Outcome.Signalled signal ->
                    return Error(ProcessError.Signalled(result.Program, signal, result.Stdout, result.Stderr))
                | Outcome.TimedOut ->
                    return Error(ProcessError.Timeout(result.Program, result.Duration, result.Stdout, result.Stderr))
        }

    /// Read the pipefail exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.Probe(cancellationToken: CancellationToken) : Task<Result<bool, ProcessError>> =
        task {
            match! this.OutputString cancellationToken with
            | Error error -> return Error error
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 0 -> return Ok true
                | Outcome.Exited 1 -> return Ok false
                | Outcome.Exited code ->
                    return Error(ProcessError.Exit(result.Program, code, result.Stdout, result.Stderr))
                | Outcome.Signalled signal ->
                    return Error(ProcessError.Signalled(result.Program, signal, result.Stdout, result.Stderr))
                | Outcome.TimedOut ->
                    return Error(ProcessError.Timeout(result.Program, result.Duration, result.Stdout, result.Stderr))
        }

    /// `OutputBytes` against `CancellationToken.None`.
    member this.OutputBytes() = this.OutputBytes CancellationToken.None

    /// `OutputString` against `CancellationToken.None`.
    member this.OutputString() =
        this.OutputString CancellationToken.None

    /// `Run` against `CancellationToken.None`.
    member this.Run() = this.Run CancellationToken.None

    /// `RunUnit` against `CancellationToken.None`.
    member this.RunUnit() = this.RunUnit CancellationToken.None

    /// `ExitCode` against `CancellationToken.None`.
    member this.ExitCode() = this.ExitCode CancellationToken.None

    /// `Probe` against `CancellationToken.None`.
    member this.Probe() = this.Probe CancellationToken.None

/// `Command.Pipe` builds a two-stage `Pipeline`; further `Pipeline.Pipe` calls extend it.
[<Extension>]
type PipelineExtensions =

    /// Start a pipeline that feeds this command's stdout into `next`'s stdin.
    [<Extension>]
    static member Pipe(command: Command, next: Command) =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull next
        Pipeline([ command; next ], None, None)

/// Pipe-friendly functions over `Pipeline`, mirroring the instance methods.
[<RequireQualifiedAccess>]
module Pipeline =

    /// Begin a pipeline from `first` feeding into `second`.
    let create (first: Command) (second: Command) = first.Pipe second

    /// Append another stage to the pipeline.
    let pipe (next: Command) (pipeline: Pipeline) = pipeline.Pipe next

    /// Kill the whole pipeline after `duration`.
    let timeout (duration: TimeSpan) (pipeline: Pipeline) = pipeline.Timeout duration

    /// Also cancel the whole pipeline when `cancellationToken` fires.
    let cancelOn (cancellationToken: CancellationToken) (pipeline: Pipeline) = pipeline.CancelOn cancellationToken

    /// Require a successful pipefail exit and return the last stage's trimmed stdout.
    let run (pipeline: Pipeline) = pipeline.Run()

    /// Like `run`, discarding the captured output.
    let runUnit (pipeline: Pipeline) = pipeline.RunUnit()

    /// Capture the last stage's stdout as decoded text (a non-zero pipefail exit is data).
    let outputString (pipeline: Pipeline) = pipeline.OutputString()

    /// Capture the last stage's stdout as raw bytes (a non-zero pipefail exit is data).
    let outputBytes (pipeline: Pipeline) = pipeline.OutputBytes()

    /// The pipefail exit code.
    let exitCode (pipeline: Pipeline) = pipeline.ExitCode()

    /// Read the pipefail exit code as a yes/no answer.
    let probe (pipeline: Pipeline) = pipeline.Probe()
