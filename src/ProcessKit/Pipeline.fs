namespace ProcessKit

open System
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

/// Fail-fast validation of a `Command` being placed at a pipeline stage. A pipeline spawns stages
/// directly (bypassing the verb layer) and rewires each stage's stdin from the previous stage's
/// stdout, so a handful of per-stage `Command` settings can never take effect inside a chain. Rather
/// than silently dropping them at run time — the project's "no silent downgrades" rule — reject them
/// at *build* time, the earliest point the stage index is known (`Pipe`), with an `ArgumentException`
/// that names the offending field and the stage. Consistent with the other public builder-boundary
/// guards (`Command.Env`, `Command.Timeout`, `ResourceLimits.*`).
module internal PipelineStageGuard =

    /// Reject a per-stage `Command` setting a pipeline cannot honour. `stageIndex` is the stage's
    /// zero-based position and `paramName` the builder argument carrying it (so the thrown
    /// `ArgumentException` blames the right parameter):
    /// - a per-stage `Timeout` on **any** stage — only the chain-level `Pipeline.Timeout` bounds a
    ///   pipeline; a stage's own `Command.Timeout` never fires;
    /// - a per-stage `IdleTimeout` on **any** stage — a pipeline wires each stage's stdout into the next
    ///   stage's stdin and captures only the last stage's output, so it does not monitor per-stage
    ///   output activity; only the chain-level `Pipeline.Timeout` bounds a pipeline;
    /// - a per-stage `Retry` on **any** stage — retry is a verb-layer mechanism and stages spawn
    ///   directly, bypassing it;
    /// - a per-stage `CancelOn` on **any** stage — a stage's own `Command.CancelOn` is a verb-layer
    ///   token the direct stage spawn bypasses; only the chain-level `Pipeline.CancelOn` cancels a
    ///   pipeline;
    /// - a `Stdin` source on a stage **after the first** — every stage past stage 0 reads from the
    ///   previous stage's stdout, so only stage 0 may feed a source.
    let validate (paramName: string) (stageIndex: int) (command: Command) =
        let config = command.Config

        if config.Timeout.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a per-stage Timeout, which a pipeline cannot honour: only the chain-level Pipeline.Timeout bounds a pipeline. Set the deadline on the pipeline instead.",
                    paramName
                )
            )

        if config.IdleTimeout.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a per-stage IdleTimeout, which a pipeline cannot honour: a pipeline wires each stage's stdout into the next stage's stdin and captures only the last stage's output, so it does not monitor per-stage output activity — only the chain-level Pipeline.Timeout bounds a pipeline. Run the command on its own to use an idle timeout.",
                    paramName
                )
            )

        if config.Retry.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a per-stage Retry, which a pipeline cannot honour: retry is a verb-layer mechanism and pipeline stages are spawned directly, bypassing it. Retry the pipeline as a whole instead.",
                    paramName
                )
            )

        if config.CancelOn.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a per-stage CancelOn, which a pipeline cannot honour: a stage's own cancellation token is a verb-layer mechanism and pipeline stages are spawned directly, bypassing it. Cancel the pipeline as a whole with Pipeline.CancelOn instead.",
                    paramName
                )
            )

        if stageIndex > 0 && config.StdinSource.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a Stdin source, but a pipeline rewires every stage after the first to read the previous stage's stdout: only stage 0 may set a Stdin source.",
                    paramName
                )
            )

    /// Reject `MergeStderr` on a stage that is (or is about to become) a **non-last** pipeline stage. On
    /// an intermediate stage stdout is wired into the next stage's stdin, so an OS-level `2>&1` merge
    /// would inject that stage's stderr bytes into the downstream stage's input data — a silent change to
    /// what the next stage reads. Only the LAST stage may merge (its stdout is the pipeline's captured
    /// output, so a `2>&1` there is the meaningful "capture the final stage's merged output"). Enforced at
    /// build time the moment a stage stops being last — i.e. when another stage is appended after it.
    let rejectMergeOnNonLast (paramName: string) (stageIndex: int) (command: Command) =
        if command.Config.MergeStderr then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets MergeStderr but is not the last stage: a pipeline wires each stage's stdout into the next stage's stdin, so merging stderr into an intermediate stage's stdout would inject it into the downstream stage's input. Only the last stage of a pipeline may set MergeStderr (its stdout is the pipeline's captured output).",
                    paramName
                )
            )

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
/// Per-stage I/O config that applies inside a pipeline: each stage's `OkCodes` (pipefail) and
/// `UncheckedInPipe`, the last stage's `StdoutEncoding`, `StdoutTee`, and `OutputBuffer` **byte** cap
/// (`MaxBytes` + `Overflow`, applied to the captured stdout — its `MaxLines` never applies to a raw
/// byte capture), stage 0's `Stdin` source (feeding the whole chain), and the chain-level `Timeout` /
/// `CancelOn`. Every stage's stderr is likewise drained under its OWN `OutputBuffer`'s **byte** cap
/// (`MaxBytes` + `Overflow`; a stage without `MaxBytes` set keeps its stderr unbounded, as before), so
/// a chatty stage can never exhaust memory regardless of its position in the chain. Per-stage *stdout/
/// stderr observation* hooks are still **not** applied — intermediate stages' `StdoutTee`, every
/// stage's `StderrTee`, and `OnStdoutLine`/`OnStderrLine` — because the chain wires stdout into the
/// next stage's stdin and captures only the final stage's output. Observe an individual command by
/// running it on its own, not as a pipeline stage.
///
/// Per-stage config a pipeline cannot honour is **rejected when the stage is piped** (an
/// `ArgumentException` from `Pipe`, naming the field and stage index), rather than silently dropped:
/// a `Stdin` source on any stage *after the first* (its stdin is always rewired to the previous
/// stage's stdout — only stage 0 may set a source), a per-stage `Timeout` on any stage (only the
/// chain-level `Pipeline.Timeout` bounds a pipeline; `Command.Timeout` on a stage never fires), a
/// per-stage `IdleTimeout` on any stage (a pipeline captures only the last stage's output and does not
/// monitor per-stage output activity, so a stage's own idle deadline can never fire), a
/// per-stage `Retry` on any stage (retry is a verb-layer mechanism, and stages spawn directly,
/// bypassing it), and a per-stage `CancelOn` on any stage (a stage's own `Command.CancelOn` token is
/// likewise a verb-layer mechanism the direct stage spawn bypasses; only the chain-level
/// `Pipeline.CancelOn` cancels a pipeline). Set the deadline on the pipeline, cancel the whole chain
/// with `Pipeline.CancelOn`, feed stage 0, or run the command on its own.
///
/// `MergeStderr` (a shell `2>&1`) is allowed only on the **last** stage — its stdout is the pipeline's
/// captured output, so merging captures the final stage's combined stdout+stderr. On any earlier stage
/// it is rejected (`ArgumentException`) the moment the stage stops being last (another stage is appended
/// after it): a pipeline wires each stage's stdout into the next stage's stdin, so an OS-level merge on
/// an intermediate stage would inject its stderr into the downstream stage's input data.
///
/// Observability is whole-pipeline, not per-stage: running the chain emits one `Log.spawn`/`Log.exit`
/// pair (plus `Log.timeout` on a timeout) and one `Diag.runStarted`/`runCompleted`/`runEnded` triple,
/// all sharing a single run id — never one set per stage. Stage 0's `Logger` becomes the pipeline's
/// logger (a per-stage `Logger` on any *other* stage has no effect — set it on stage 0, or observe an
/// individual command by running it on its own); the `program` tag/label is a composite of every
/// stage's name, joined `"a | b | c"` (built only from `Command.Program`, never argv/env, so the
/// argv/env-never-logged invariant holds for a multi-stage run too).
///
/// Per-stage config that is simply **inapplicable** inside a pipeline and has no effect: `StreamBuffer`
/// (a policy for the streaming verbs, which a pipeline does not offer), and `KeepStdinOpen` /
/// `RunningProcess.TakeStdin` (a pipeline exposes no live handle to write stdin into — it wires each
/// stage after the first from the previous stage's stdout itself, and a `KeepStdinOpen` there is an
/// internal wiring detail, not user-reachable).
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
                not (stage.Outcome.IsAcceptedBy stage.OkCodes)

            let checkedFailures = checkedStages |> List.filter failed

            // The rightmost checked failure is where the pipeline failed — but a stage the chain's own
            // proactive teardown hard-killed (`TornDown`, killed after a *sibling* failed) is a victim,
            // not the culprit, so it must never steal the blame: prefer the rightmost NON-torn-down
            // checked failure, falling back to the rightmost failure only when every checked failure is a
            // teardown victim. With no proactive teardown in play (every `TornDown` is false) this is
            // exactly the previous "rightmost checked failure" rule, so the pipefail result is unchanged.
            let culprit =
                checkedFailures
                |> List.filter (fun stage -> not stage.TornDown)
                |> List.tryLast
                |> Option.orElseWith (fun () -> List.tryLast checkedFailures)

            match culprit with
            | Some stage -> stage // the checked failure the pipeline failed at
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

    // A genuine stage-0 stdin source failure surfaces as `ProcessError.Stdin` — uniformly with a single
    // command — but only on an otherwise-successful pipeline (the pipefail representative exited with an
    // accepted code); a pipefail failure or a timeout is the "realer" failure and passes through. The
    // program is stage 0's, whose source could not be read.
    static let stdinErrorOnSuccess (program: string) (capture: PipelineCapture) (representative: PipelineStage) =
        if representative.Outcome.IsAcceptedBy representative.OkCodes then
            capture.Stdin0Error
            |> Option.map (fun ex -> ProcessError.Stdin(program, ex.Message))
        else
            None

    // The pipeline's captured stdout tripped the last stage's fail-loud (`OverflowMode.Error`) byte
    // ceiling — surface it as `ProcessError.OutputTooLarge`, consistent with a single command's byte
    // verb. The overflow is on the raw byte capture, so it carries no lines (`TotalLines = 0`); the
    // limits are the last stage's configured caps. The program is the last stage's, whose output
    // overflowed. Checked before the pipefail/stdin classification, mirroring the single-command order.
    static let outputTooLargeError (commands: Command list) (capture: PipelineCapture) : ProcessError =
        let last = List.last commands
        let policy = last.Config.OutputBuffer
        ProcessError.OutputTooLarge(last.Program, policy.MaxLines, policy.MaxBytes, 0, capture.LastStdoutTotalBytes)

    /// Append another stage; its stdin is fed from the current last stage's stdout. Rejects
    /// (`ArgumentException`) a stage that sets a per-stage `Timeout`/`IdleTimeout`/`Retry`/`CancelOn` or
    /// a `Stdin` source — a pipeline cannot honour those (see the type doc); the appended stage is
    /// always after the first.
    member _.Pipe(command: Command) =
        ArgumentNullException.ThrowIfNull command
        PipelineStageGuard.validate (nameof command) commands.Length command
        // Appending demotes the current last stage to an intermediate one, where an OS-level MergeStderr
        // would leak its stderr into `command`'s stdin — reject it now (the moment it stops being last).
        PipelineStageGuard.rejectMergeOnNonLast (nameof command) (commands.Length - 1) (List.last commands)
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
            // A pipeline captures only the *last* stage's stdout, so the last command's `StdoutTee` and
            // `OutputBuffer` byte cap are applied to that captured stream — matching a single command's
            // byte capture. (Per-stage line handlers / stderr tees, and intermediate stages' output
            // buffers, are not applied within a pipeline; see the type doc.)
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
        ([<Optional>] cancellationToken: CancellationToken)
        : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        task {
            match! this.Execute cancellationToken with
            | Error error -> return Error error
            | Ok capture ->
                if capture.LastStdoutTooLarge then
                    return Error(outputTooLargeError commands capture)
                else
                    let stage = representative capture

                    match stdinErrorOnSuccess (List.head commands).Program capture stage with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<byte[]>(
                                    stage.Program,
                                    capture.LastStdout,
                                    stage.Stderr,
                                    stage.Outcome,
                                    capture.Duration,
                                    capture.LastStdoutTruncated,
                                    stage.OkCodes
                                )
                            )
        }

    /// Run the pipeline to completion, capturing the last stage's stdout as decoded text (using the
    /// last stage's stdout encoding). A non-zero pipefail exit is data here, not an error.
    member this.OutputStringAsync
        ([<Optional>] cancellationToken: CancellationToken)
        : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            match! this.Execute cancellationToken with
            | Error error -> return Error error
            | Ok capture ->
                if capture.LastStdoutTooLarge then
                    return Error(outputTooLargeError commands capture)
                else
                    let stage = representative capture

                    match stdinErrorOnSuccess (List.head commands).Program capture stage with
                    | Some err -> return Error err
                    | None ->
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
                                    capture.LastStdoutTruncated,
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
    member this.RunAsync([<Optional>] cancellationToken: CancellationToken) : Task<Result<string, ProcessError>> =
        CaptureVerbs.run (fun () -> this.OutputStringAsync cancellationToken)

    /// Like `RunAsync`, but discard the captured output.
    member this.RunUnitAsync([<Optional>] cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        CaptureVerbs.runUnit (fun () -> this.OutputStringAsync cancellationToken)

    /// The pipefail exit code. A signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCodeAsync([<Optional>] cancellationToken: CancellationToken) : Task<Result<int, ProcessError>> =
        CaptureVerbs.exitCode (fun () -> this.OutputStringAsync cancellationToken)

    /// Read the pipefail exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.ProbeAsync([<Optional>] cancellationToken: CancellationToken) : Task<Result<bool, ProcessError>> =
        CaptureVerbs.probe (fun () -> this.OutputStringAsync cancellationToken)

    /// Require a successful pipefail exit and parse the trimmed stdout into a `'T`; a thrown parser
    /// error becomes `ProcessError.Parse`.
    member this.ParseAsync
        (parser: Func<string, 'T>, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull parser

        CaptureVerbs.parse (List.last commands).Program parser.Invoke (fun () -> this.RunAsync cancellationToken)

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    member this.TryParseAsync
        (parser: TryParser<'T>, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull parser

        CaptureVerbs.tryParse (List.last commands).Program (TryParser.toResult parser) (fun () ->
            this.RunAsync cancellationToken)

/// `Command.Pipe` builds a two-stage `Pipeline`; further `Pipeline.Pipe` calls extend it.
[<Extension>]
type PipelineExtensions =

    /// Start a pipeline that feeds this command's stdout into `next`'s stdin. Rejects
    /// (`ArgumentException`) either stage setting a per-stage `Timeout`/`IdleTimeout`/`Retry`/`CancelOn`,
    /// or `next` (stage 1) setting a `Stdin` source — a pipeline cannot honour those (see the `Pipeline`
    /// type doc). `command` is stage 0, so a `Stdin` source on it (feeding the whole chain) is allowed.
    [<Extension>]
    static member Pipe(command: Command, next: Command) =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull next
        PipelineStageGuard.validate (nameof command) 0 command
        PipelineStageGuard.validate (nameof next) 1 next
        // `command` (stage 0) is not the last stage, so an OS-level MergeStderr on it would leak its
        // stderr into `next`'s stdin — reject it (only the last stage, `next`, may merge).
        PipelineStageGuard.rejectMergeOnNonLast (nameof command) 0 command
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
