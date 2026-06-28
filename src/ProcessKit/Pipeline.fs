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
/// Build it by piping commands (`a.Pipe(b).Pipe(c)`), then run it to completion with the same
/// run-and-capture verbs a single command exposes (`RunAsync`/`OutputStringAsync`/`OutputBytesAsync`/`ExitCodeAsync`/
/// `ProbeAsync`/`ParseAsync`/`TryParseAsync`). A pipeline runs as a whole, so the *streaming* verbs (`FirstLineAsync`,
/// `StdoutLinesAsync`) are deliberately not offered — capture the last stage's output instead. The exit
/// status follows shell **pipefail**: the rightmost stage that did not exit with an accepted code
/// (its `Command.OkCodes`, `{0}` by default) determines the result, unless that stage opted out with
/// `Command.UncheckedInPipe`.
///
/// Per-stage I/O config that applies inside a pipeline: each stage's `OkCodes` (pipefail), the last
/// stage's `StdoutEncoding` and `StdoutTee` (the captured stdout), and the chain-level `Timeout` /
/// `CancelOn`. Per-stage *stdout/stderr observation* hooks are **not** applied — intermediate stages'
/// `StdoutTee`, and every stage's `StderrTee`, `OnStdoutLine`/`OnStderrLine`, and `OutputBuffer`
/// policy — because the chain wires stdout into the next stage's stdin and captures only the final
/// stage's output. Observe an individual command by running it on its own, not as a pipeline stage.
[<Sealed>]
type Pipeline internal (commands: Command list, timeout: TimeSpan option, cancelOn: CancellationToken option) =

    // pipefail: the representative stage carries the program/outcome/stderr for the verb result.
    // It is the rightmost *checked* stage that did not exit with an accepted code; if every checked
    // stage succeeded, the last checked stage stands (so an unchecked, failed final stage does not
    // fail the pipeline). With no checked stages at all the pipeline is reported as a success. A
    // timed-out pipeline reports TimedOut against the last stage.
    static let representative (capture: PipelineCapture) : PipelineStage =
        let last = List.last capture.Stages

        if capture.TimedOut then
            { last with Outcome = Outcome.TimedOut }
        else
            let checkedStages = capture.Stages |> List.filter (fun stage -> not stage.Unchecked)

            let failed (stage: PipelineStage) =
                // A stage succeeds when it exits with one of *its own* accepted codes (`Command.OkCodes`,
                // default `{0}`); a signal/timeout is always a failure.
                match stage.Outcome with
                | Outcome.Exited code -> not (List.contains code stage.OkCodes)
                | _ -> true

            match checkedStages |> List.filter failed |> List.tryLast with
            | Some stage -> stage // rightmost checked failure -> the pipeline failed here
            | None ->
                // No checked stage failed: the pipeline succeeded. Report the last checked stage
                // (its accepted exit).
                match List.tryLast checkedStages with
                | Some stage -> stage
                | None ->
                    // Every stage opted out of pipefail (`UncheckedInPipe`): nothing can fail the
                    // pipeline, so report success against the last stage regardless of its raw exit
                    // (otherwise an all-unchecked chain with a failing last stage would wrongly fail
                    // `RunAsync`/`ExitCodeAsync`). Use the last stage's own first accepted code.
                    let okCode = last.OkCodes |> List.tryHead |> Option.defaultValue 0

                    { last with
                        Outcome = Outcome.Exited okCode }

    /// Append another stage; its stdin is fed from the current last stage's stdout.
    member _.Pipe(command: Command) =
        ArgumentNullException.ThrowIfNull command
        Pipeline(commands @ [ command ], timeout, cancelOn)

    /// Kill the whole pipeline after `duration`, reporting the result as `Outcome.TimedOut`. A
    /// negative `duration` is rejected; one larger than ~24.8 days is treated as no timeout.
    member _.Timeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero)
        Pipeline(commands, Some duration, cancelOn)

    /// Also cancel the whole pipeline when `cancellationToken` fires (in addition to any verb token).
    member _.CancelOn(cancellationToken: CancellationToken) =
        Pipeline(commands, timeout, Some cancellationToken)

    // Run the chain, honouring the pipeline-level `cancelOn` on top of the verb's token.
    member private _.Execute(cancellationToken: CancellationToken) : Task<Result<PipelineCapture, ProcessError>> =
        task {
            // A pipeline captures only the *last* stage's stdout, so the last command's `StdoutTee` is
            // applied to that captured stream — matching a single command's `StdoutTee`. (Per-stage line
            // handlers / stderr tees / output-buffer caps are not applied within a pipeline; see the
            // type doc.)
            let lastTee = (List.last commands).Config.StdoutTee

            match cancelOn with
            | Some extra ->
                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)

                return! PipelineRunner.run commands timeout lastTee linked.Token
            | None -> return! PipelineRunner.run commands timeout lastTee cancellationToken
        }

    /// Run the pipeline to completion, capturing the last stage's stdout as raw bytes. A non-zero
    /// pipefail exit is data here, not an error.
    member this.OutputBytesAsync
        (cancellationToken: CancellationToken)
        : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        task {
            match! this.Execute cancellationToken with
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
                            false,
                            stage.OkCodes
                        )
                    )
        }

    /// Run the pipeline to completion, capturing the last stage's stdout as decoded text (using the
    /// last stage's stdout encoding). A non-zero pipefail exit is data here, not an error.
    member this.OutputStringAsync
        (cancellationToken: CancellationToken)
        : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            match! this.Execute cancellationToken with
            | Error error -> return Error error
            | Ok capture ->
                let stage = representative capture
                let encoding = (List.last commands).Config.StdoutEncoding
                let text = encoding.GetString capture.LastStdout

                return
                    Ok(
                        ProcessResult<string>(
                            stage.Program,
                            text,
                            stage.Stderr,
                            stage.Outcome,
                            capture.Duration,
                            false,
                            stage.OkCodes
                        )
                    )
        }

    // The capture-derived verbs share one implementation with the `Runner` module (`CaptureVerbs`),
    // over this pipeline's own stdout capture (`OutputStringAsync`, which carries the pipefail
    // representative stage), so trimming / success-checking / parse-error wrapping can't drift between
    // a single command and a pipeline.

    /// Require a successful pipefail exit and return the last stage's stdout, trailing whitespace
    /// trimmed. Any checked stage that did not exit 0 fails the pipeline.
    member this.RunAsync(cancellationToken: CancellationToken) : Task<Result<string, ProcessError>> =
        CaptureVerbs.run (fun () -> this.OutputStringAsync cancellationToken)

    /// Like `RunAsync`, but discard the captured output.
    member this.RunUnitAsync(cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        CaptureVerbs.runUnit (fun () -> this.OutputStringAsync cancellationToken)

    /// The pipefail exit code. A signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCodeAsync(cancellationToken: CancellationToken) : Task<Result<int, ProcessError>> =
        CaptureVerbs.exitCode (fun () -> this.OutputStringAsync cancellationToken)

    /// Read the pipefail exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.ProbeAsync(cancellationToken: CancellationToken) : Task<Result<bool, ProcessError>> =
        CaptureVerbs.probe (fun () -> this.OutputStringAsync cancellationToken)

    /// `OutputBytesAsync` against `CancellationToken.None`.
    member this.OutputBytesAsync() =
        this.OutputBytesAsync CancellationToken.None

    /// `OutputStringAsync` against `CancellationToken.None`.
    member this.OutputStringAsync() =
        this.OutputStringAsync CancellationToken.None

    /// `RunAsync` against `CancellationToken.None`.
    member this.RunAsync() = this.RunAsync CancellationToken.None

    /// `RunUnitAsync` against `CancellationToken.None`.
    member this.RunUnitAsync() =
        this.RunUnitAsync CancellationToken.None

    /// `ExitCodeAsync` against `CancellationToken.None`.
    member this.ExitCodeAsync() =
        this.ExitCodeAsync CancellationToken.None

    /// `ProbeAsync` against `CancellationToken.None`.
    member this.ProbeAsync() = this.ProbeAsync CancellationToken.None

    /// Require a successful pipefail exit and parse the trimmed stdout into a `'T`; a thrown parser
    /// error becomes `ProcessError.Parse`.
    member this.ParseAsync
        (parser: Func<string, 'T>, cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull parser

        CaptureVerbs.parse (List.last commands).Program parser.Invoke (fun () -> this.RunAsync cancellationToken)

    /// `ParseAsync` against `CancellationToken.None`.
    member this.ParseAsync(parser: Func<string, 'T>) =
        this.ParseAsync(parser, CancellationToken.None)

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    member this.TryParseAsync
        (parser: TryParser<'T>, cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull parser

        CaptureVerbs.tryParse (List.last commands).Program (TryParser.toResult parser) (fun () ->
            this.RunAsync cancellationToken)

    /// `TryParseAsync` against `CancellationToken.None`.
    member this.TryParseAsync(parser: TryParser<'T>) =
        this.TryParseAsync(parser, CancellationToken.None)

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

// Verbs (Run/OutputString/ExitCode/Probe/Parse/…) are instance methods on `Pipeline` — call them
// directly (`pipeline.RunAsync()`); the module forwards only the pipe-friendly *builders* above.
