namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization.Metadata
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
    /// - `KeepStdinOpen` on **any** stage — a pipeline exposes no per-stage `RunningProcess.TakeStdin`
    ///   handle through which the caller could use the kept-open pipe;
    /// - a per-stage `CapturePolicy` on **any** stage — every capture a pipeline makes (its final
    ///   stdout and each stage's stderr) is a raw byte capture, so there is no decoded line for that
    ///   seam to shape. Rejected rather than dropped for the same reason as the knobs above, and with
    ///   more at stake: a redaction hook that silently stops running would hand back the very secret it
    ///   was installed to remove;
    /// - a `Stdin` source on a stage **after the first** — every stage past stage 0 reads from the
    ///   previous stage's stdout, so only stage 0 may feed a source.
    /// - `StopSignal`, `CancelGrace`, or `CancelSignal` on a stage **after the first** — a graceful
    ///   teardown (timeout or cancellation) broadcasts ONE soft signal and ONE grace window to the whole
    ///   chain, so stage 0 owns the pipeline-wide control configuration.
    /// - `StdoutToFile` on **any** stage — every stage's stdout is owned by the pipeline: intermediate
    ///   output feeds the following stage and final output is captured by the pipeline itself.
    /// - `Stdout(StdioMode.Null|Inherit)` on **any** stage — the pipeline must use a pipe for that same
    ///   wiring and capture, so accepting another destination would silently replace it with `Piped`.
    let validate (paramName: string) (stageIndex: int) (command: Command) =
        let config = command.Config

        if config.ExtraFds.Count > 0 then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets ExtraFd, but a pipeline exposes no per-stage RunningProcess from which the parent-side channel could be claimed. Run the command on its own.",
                    paramName
                )
            )

        if config.StdoutFile.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets StdoutToFile, but a pipeline owns every stage's stdout: intermediate output feeds the next stage and final output is captured by the pipeline. Run the command on its own to redirect stdout to a file.",
                    paramName
                )
            )

        if config.StdoutMode <> StdioMode.Piped then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets Stdout to {config.StdoutMode}, but a pipeline owns every stage's stdout and requires a pipe for stage wiring and final output capture. Leave stdout at the default Piped mode, or run the command on its own.",
                    paramName
                )
            )

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
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a per-stage Retry/RetryBackoff policy, which a pipeline cannot honour: retry is a verb-layer mechanism and pipeline stages are spawned directly, bypassing it. Retry the pipeline as a whole instead.",
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

        if config.KeepStdinOpen then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets KeepStdinOpen, which a pipeline cannot honour: a pipeline exposes no per-stage RunningProcess.TakeStdin handle through which the caller could use the kept-open stdin pipe. Run the command on its own for interactive stdin.",
                    paramName
                )
            )

        // The one guard here whose failure direction is a leak rather than an ignored knob: a
        // `CapturePolicy` is installed to keep a secret out of the retained capture, and every capture a
        // pipeline makes is a raw byte capture with no decoded line to hand it. Accepting the stage would
        // return the raw secret from `OutputStringAsync` with nothing to signal that the redactor never
        // ran — so this is refused at build time, like every other per-stage knob a chain cannot honour.
        if config.CapturePolicy.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets a CapturePolicy, which a pipeline cannot honour: every capture a pipeline makes — its final stdout and each stage's stderr — is a raw byte capture, so there is no decoded line for the policy to shape. Run the command on its own to scrub its captured output.",
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

        if stageIndex > 0 && config.StopSignal <> Signal.Term then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets StopSignal, but graceful stop broadcasts one signal to the whole chain. Set StopSignal on stage 0, which owns the pipeline-wide control configuration.",
                    paramName
                )
            )

        // The cancellation ladder is chain-wide for the same reason `StopSignal` is: cancelling a
        // pipeline tears down ONE shared group with ONE soft signal and ONE grace window, so a per-stage
        // window/signal could not be honoured. Rejected on a later stage rather than silently ignored,
        // and read off stage 0 — the pipeline-wide control stage — when the chain is cancelled.
        if stageIndex > 0 && config.CancelGrace.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets CancelGrace, but a cancelled pipeline tears down the whole chain through one graceful window. Set CancelGrace on stage 0, which owns the pipeline-wide control configuration.",
                    paramName
                )
            )

        if stageIndex > 0 && config.CancelSignal.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets CancelSignal, but a graceful cancellation broadcasts one signal to the whole chain. Set CancelSignal on stage 0, which owns the pipeline-wide control configuration.",
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

    /// Reject `Pty` on a stage that is (or is about to become) a **non-last** pipeline stage. A PTY gives
    /// the child one merged terminal stream as its output; on an intermediate stage that stdout is wired
    /// into the next stage's stdin, so the tty stream (VT escapes and all) would become the downstream
    /// stage's input data — a silent change to what it reads. A PTY is allowed only as a standalone run or
    /// the LAST stage of a pipeline. Enforced at build time the moment a stage stops being last (another
    /// stage is appended after it), mirroring `rejectMergeOnNonLast`.
    let rejectPtyOnNonLast (paramName: string) (stageIndex: int) (command: Command) =
        if command.Config.Pty.IsSome then
            raise (
                ArgumentException(
                    $"pipeline stage {stageIndex} ('{command.Program}') sets Pty but is not the last stage: a pipeline wires each stage's stdout into the next stage's stdin, and a PTY stage's output is a single merged terminal stream, so placing it before another stage would inject the tty stream into that stage's input. A PTY is allowed only as a standalone run or the last stage of a pipeline.",
                    paramName
                )
            )

/// A live streaming session over a whole pipeline — the multi-stage analogue of `RunningProcess`,
/// returned by `Pipeline.StartAsync`. It gives a pipeline the streaming layer a single command has long
/// had: stream the **final** stage's stdout line by line as it arrives (`StdoutLinesAsync` /
/// `StdoutJsonLinesAsync` / `OutputEventsAsync`), wait on a readiness line (`WaitForLineAsync`), wait for
/// the whole chain to finish with the SAME pipefail classification the buffering verbs use
/// (`FinishAsync`), or stop / reap the entire chain (`StopAsync` / `Kill` / dispose). Disposing it reaps
/// every stage's tree (kill-on-drop), just like disposing a `RunningProcess`.
///
/// **Single consumption** (the `RunningProcess` rule, [[K-031]]): the final stage's stdout is pumped
/// exactly once, so `StdoutLinesAsync` and `OutputEventsAsync` are mutually exclusive — a second,
/// different consumer throws `"already consumed by another verb"`. `FinishAsync` rejoins the SAME
/// stdout-streaming session `StdoutLinesAsync` started (so it is the natural "wait for the rest" after
/// streaming lines); after `OutputEventsAsync`, use `StopAsync` (or dispose) to reap. These hold because
/// the session delegates every streaming/consuming verb to one underlying `RunningProcess`.
///
/// **Whole-chain semantics.** The stream is the final stage's stdout, but `FinishAsync`/`StopAsync`
/// reap and classify the ENTIRE chain: the returned `Finished.Outcome` is the pipefail representative
/// (the rightmost checked stage that did not exit with an accepted code, or a `TimedOut`/`Cancelled`
/// for the whole chain); when no checked stage failed, it is the final stage's real outcome, including
/// an accepted unchecked non-zero exit. `Finished.Stderr` is that representative stage's stderr —
/// identical to what `Pipeline.RunAsync` would report. Stopping or disposing tears down
/// EVERY stage (including a partially started chain), never just the last. A genuine read failure in an
/// upstream inter-stage relay is returned by `FinishAsync` as `ProcessError.Io`, even if the downstream
/// stage observed the resulting EOF and exited successfully; a downstream broken pipe remains routine.
/// That relay failure also tears the whole chain down the moment it is seen — it never waits out a
/// still-running upstream stage that has simply stopped writing — so the streamed final-stage output
/// ends wherever that kill lands.
[<Sealed>]
type PipelineSession
    internal (inner: RunningProcess, commands: Command list, capture: Task<PipelineCapture>, wasCancelled: unit -> bool)
    =

    // Build the completion result from the stashed whole-chain capture, applying the EXACT same three
    // pipefail rules as the buffering verbs (`PipelineClassify`) — so a streamed run's outcome/error set
    // is never a truncated version of `RunAsync`'s. A whole-chain cancellation (the verb token or
    // `Pipeline.CancelOn`) wins over the killed chain's raw outcome, mirroring the buffered path's
    // `Cancelled`-takes-precedence classification.
    let classify (lastStdoutTruncated: bool) (capture: PipelineCapture) : Result<Finished, ProcessError> =
        if wasCancelled () then
            Error(ProcessError.Cancelled (List.last commands).Program)
        else
            match PipelineClassify.copyError capture with
            | Some error -> Error error
            | None ->
                match PipelineClassify.outputTooLargeError commands capture with
                | Some error -> Error error
                | None ->
                    let stage = PipelineClassify.representative capture

                    match PipelineClassify.stdinErrorOnSuccess (List.head commands).Program capture stage with
                    | Some error -> Error error
                    | None ->
                        Ok(
                            Finished(
                                stage.Outcome,
                                stage.Stderr,
                                PipelineClassify.resultTruncated lastStdoutTruncated stage
                            )
                        )

    /// Stream the FINAL stage's stdout line by line as it arrives — the pipeline analogue of
    /// `RunningProcess.StdoutLinesAsync`. Hands out its ONE enumerator exactly once; a second streaming
    /// consumer (this again, or `OutputEventsAsync`) throws. Call `FinishAsync` afterwards for the
    /// whole-chain outcome + the representative stage's stderr.
    member _.StdoutLinesAsync() : IAsyncEnumerable<string> = inner.StdoutLinesAsync()

    /// Stream the final stage's stdout as NDJSON / JSON Lines (reflection-based `System.Text.Json`), each
    /// non-empty line deserialized into a `'T` as it arrives — the pipeline analogue of
    /// `RunningProcess.StdoutJsonLinesAsync`. Not trim-/AOT-safe; prefer the `JsonTypeInfo<'T>` overload
    /// in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a NativeAOT app.">]
    member _.StdoutJsonLinesAsync<'T>([<Optional>] options: JsonSerializerOptions | null) : IAsyncEnumerable<'T> =
        inner.StdoutJsonLinesAsync<'T>(options)

    /// Like the overload above, but deserializes each line via a source-generated `JsonTypeInfo<'T>` —
    /// trim-/NativeAOT-safe.
    member _.StdoutJsonLinesAsync<'T>(typeInfo: JsonTypeInfo<'T>) : IAsyncEnumerable<'T> =
        inner.StdoutJsonLinesAsync<'T>(typeInfo)

    /// Stream merged final-stage stdout line events as they arrive, each tagged `OutputEvent.Stdout` — the
    /// pipeline analogue of `RunningProcess.OutputEventsAsync`. A pipeline captures only the final stage's
    /// stdout (each earlier stage's stdout is wired into the next stage's stdin, and every stage's stderr
    /// is drained under its own byte cap for the pipefail result), so — unlike a single command — no
    /// `OutputEvent.Stderr` is produced here. Mutually exclusive with `StdoutLinesAsync`; after it, reap
    /// with `StopAsync` (or dispose) rather than `FinishAsync`.
    member _.OutputEventsAsync() : IAsyncEnumerable<OutputEvent> = inner.OutputEventsAsync()

    /// Wait until a final-stage stdout line satisfies `predicate`, or fail with `NotReady` after
    /// `timeout` (or `Cancelled` if `cancellationToken` fires first) — the pipeline analogue of
    /// `RunningProcess.WaitForLineAsync`. Consumed lines are not re-delivered; a later `StdoutLinesAsync`/
    /// `FinishAsync` sees the rest.
    member _.WaitForLineAsync
        (predicate: Func<string, bool>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<string, ProcessError>> =
        inner.WaitForLineAsync(predicate, timeout, cancellationToken)

    /// Wait for the WHOLE chain to finish, then return how it concluded (the pipefail representative's
    /// `Outcome`) plus that stage's stderr and a truncation signal combining the final stdout stream with
    /// that representative stderr — with the same classification `Pipeline.RunAsync` applies:
    /// an `OutputTooLarge` on any stage's fail-loud stream, or a stage-0 stdin-source failure on an
    /// otherwise-successful run, surfaces as `Error`, and a whole-chain cancellation is `Cancelled`. A
    /// genuine upstream relay read fault also surfaces as `ProcessError.Io` — and, having torn the chain
    /// down when it happened, returns promptly rather than waiting out a silent upstream stage; a
    /// downstream broken pipe is routine early-consumer teardown. A non-zero pipefail exit is *data* in
    /// `Finished.Outcome`, not an `Error`. Reaps the whole tree.
    /// Pairs with `StdoutLinesAsync` (it rejoins that stdout-streaming session); called with no prior
    /// streaming it drains and discards the final stdout as it arrives — retaining nothing — then
    /// reports the outcome, after which `StdoutLinesAsync`/`StdoutJsonLinesAsync`/`WaitForLineAsync` are
    /// refused as already-consumed rather than answered with an empty stream (see
    /// `RunningProcess.FinishAsync`).
    member _.FinishAsync() : Task<Result<Finished, ProcessError>> =
        task {
            match! inner.FinishAsync() with
            | Error error ->
                // The inner handle drains no stderr of its own (the pipeline owns every stage's stderr),
                // so this branch is unreachable in practice; forward any inner error rather than mask it.
                return Error error
            | Ok finished ->
                let! settled = capture
                return classify finished.Truncated settled
        }

    /// Gracefully stop the WHOLE chain (soft signal, wait up to `gracePeriod`, then hard-kill the
    /// remainder — the same machinery `RunningProcess.StopAsync` drives), reap every stage, and return
    /// the pipefail representative's `Outcome`. Safe to call after any streaming verb (or none) and
    /// idempotent/race-safe with `Kill`/dispose — the tree is reaped exactly once.
    member _.StopAsync(gracePeriod: TimeSpan) : Task<Outcome> =
        task {
            let! _ = inner.StopAsync gracePeriod
            let! settled = capture
            return (PipelineClassify.representative settled).Outcome
        }

    /// `StopAsync` using the default grace window (2 seconds, matching `ProcessGroupOptions.ShutdownTimeout`).
    member this.StopAsync() : Task<Outcome> = this.StopAsync Limits.DefaultStopGrace

    /// Signal the whole chain to die without waiting (fire-and-forget, like `RunningProcess.Kill`); the
    /// tree is fully reaped when the session is disposed. For a blocking stop, use `StopAsync` or dispose.
    member _.Kill() = inner.Kill()

    interface IAsyncDisposable with
        /// Reap the whole chain's tree (kill-on-drop): disposes the underlying `RunningProcess`, whose
        /// teardown hard-kills and reaps every stage and releases the shared group.
        member _.DisposeAsync() =
            (inner :> IAsyncDisposable).DisposeAsync()

/// The overflow metadata a buffered pipeline result carries, alongside `PipelineClassify.resultTruncated`'s
/// flag: the volume the two captures that result presents actually SAW (retained plus dropped). Used by
/// `Pipeline`'s buffering verbs only — the streaming session reports its outcome as a `Finished`, which
/// carries no such totals — which is why it lives here rather than in the shared `PipelineClassify` fold.
[<RequireQualifiedAccess>]
module internal PipelineTotals =

    /// The captured totals for a result built from `capture` with `stage` as the pipefail representative:
    /// the last stage's captured stdout bytes plus that representative's stderr bytes, saturating at
    /// `Int32.MaxValue` like the buffers' own counters. Both captures are raw byte streams with no line
    /// structure, so the line total is unavailable while the byte total is measured independently.
    let forResult (capture: PipelineCapture) (stage: PipelineStage) : int option * int option =
        (None,
         Some(int (min (int64 capture.LastStdoutTotalBytes + int64 stage.StderrTotalBytes) (int64 Int32.MaxValue))))

/// An immutable left-to-right chain of commands wired stdout -> stdin, with **no shell** involved:
/// each stage's standard output feeds the next stage's standard input directly. The whole chain
/// runs inside one shared kill-on-dispose group, so cancelling, timing out, or disposing the run
/// reaps every stage together.
///
/// Build it by piping commands (`a.Pipe(b).Pipe(c)`), then run it to completion with the same
/// run-and-capture verbs a single command exposes (`RunAsync`/`OutputStringAsync`/`OutputBytesAsync`/`ExitCodeAsync`/
/// `ProbeAsync`/`ParseAsync`/`TryParseAsync`). To stream the final stage's stdout as it arrives instead
/// of buffering the whole chain, start a live session with `StartAsync` (→ `PipelineSession`), the
/// pipeline analogue of `Command.StartAsync` → `RunningProcess`. The exit
/// status follows shell **pipefail**: the rightmost stage that did not exit with an accepted code
/// (its `Command.OkCodes`, `{0}` by default) determines the result, unless that stage opted out with
/// `Command.UncheckedInPipe`. When no checked stage failed, the final stage's real outcome and diagnostics
/// are returned; an unchecked voluntary exit remains successful because its actual code is included in
/// the result's accepted codes, not because the outcome is rewritten.
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
/// running it on its own, not as a pipeline stage. A stage's `CapturePolicy` is bound by that same
/// boundary — every capture a pipeline makes (the final stdout, and each stage's stderr) is a RAW BYTE
/// capture with no line framing, which is also what leaves `OutputBuffer.MaxLines` inapplicable here —
/// but it is **rejected** rather than left unapplied, because a redaction hook that quietly stops
/// running hands back the secret it was installed to remove (see the rejected-config paragraph below).
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
/// `Pipeline.CancelOn` cancels a pipeline), `KeepStdinOpen` on any stage (the pipeline exposes no
/// per-stage `RunningProcess.TakeStdin` handle through which the kept-open pipe could be used), a
/// per-stage `CapturePolicy` on any stage (every capture a pipeline makes is a raw byte capture, so
/// the seam has no decoded line to shape — run such a command on its own to scrub what it captured),
/// `StdoutToFile` on any stage (stdout is instead pipeline wiring or final captured output), and
/// `Stdout(StdioMode.Null|Inherit)` on any stage (the pipeline must use a pipe rather than silently
/// override that destination). `StderrToFile` remains supported:
/// it directs that stage's diagnostics to the requested file while the chain continues to carry stdout.
/// Set the deadline on the pipeline, cancel the whole chain with `Pipeline.CancelOn`, feed stage 0, or
/// run the command on its own.
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
/// argv/env-never-logged invariant holds for a multi-stage run too). Stage 0 likewise owns
/// `StopSignal`, because graceful shutdown broadcasts one soft signal to the whole chain — and, for the
/// same reason, `CancelGrace`/`CancelSignal`: a cancelled chain (`Pipeline.CancelOn` or the verb's own
/// token) is torn down through ONE soft signal and ONE grace window over the shared group, so setting
/// either on stage 0 makes the whole chain's cancellation graceful, and setting it on a later stage is
/// rejected rather than ignored. The chain-level `Pipeline.Timeout` is untouched by that knob and keeps
/// its immediate hard kill.
///
/// `StreamBuffer` depends on how the pipeline is run. For buffered verbs (`RunAsync`, `OutputStringAsync`,
/// `OutputBytesAsync`, `ExitCodeAsync`, and similar verbs used without `StartAsync`), it is inapplicable because there is
/// no streaming consumer to receive the policy. `StartAsync`, however, returns a live
/// `PipelineSession`: the last stage's `StreamBuffer` policy is applied to the session's stdout channel,
/// including its bounded-buffer/backpressure behavior.
///
/// `KeepStdinOpen` is rejected with `ArgumentException`, not accepted as a silent no-op. Although
/// `PipelineSession` is a live handle, it does not expose interactive stdin: the pipeline wires each
/// stage after the first from the previous stage's stdout itself, and exposes no user-reachable
/// `RunningProcess.TakeStdin` handle.
[<Sealed>]
type Pipeline internal (commands: Command list, timeout: TimeSpan option, cancelOn: CancellationToken option) =

    /// Append another stage; its stdin is fed from the current last stage's stdout. Rejects
    /// (`ArgumentException`) a stage that sets a per-stage
    /// `Timeout`/`IdleTimeout`/`Retry`/`CancelOn`/`CapturePolicy`, `KeepStdinOpen`, `StdoutToFile`,
    /// non-piped `Stdout`, or a `Stdin` source — a pipeline cannot honour those (see the type doc); the
    /// appended stage is always after the first.
    member _.Pipe(command: Command) =
        ArgumentNullException.ThrowIfNull(command, nameof command)
        PipelineStageGuard.validate (nameof command) commands.Length command
        // Appending demotes the current last stage to an intermediate one, where an OS-level MergeStderr —
        // or a PTY's merged terminal output — would leak into `command`'s stdin; reject either now (the
        // moment the current last stage stops being last).
        PipelineStageGuard.rejectMergeOnNonLast (nameof command) (commands.Length - 1) (List.last commands)
        PipelineStageGuard.rejectPtyOnNonLast (nameof command) (commands.Length - 1) (List.last commands)
        Pipeline(commands @ [ command ], timeout, cancelOn)

    /// Kill the whole pipeline after `duration`, reporting the result as `Outcome.TimedOut`. A
    /// negative `duration` is rejected; one larger than ~24.8 days is treated as no timeout.
    member _.Timeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero, nameof duration)
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

    /// Start the pipeline as a live **streaming session** instead of running it to completion: spawn the
    /// whole chain into one shared kill-on-dispose group and hand back a `PipelineSession` that streams
    /// the FINAL stage's stdout line by line (`StdoutLinesAsync`/`OutputEventsAsync`), waits for the whole
    /// chain with the same pipefail classification the buffering verbs use (`FinishAsync`), and stops/reaps
    /// the entire chain (`StopAsync`/dispose). This is the pipeline analogue of `Command.StartAsync` →
    /// `RunningProcess`, for long-running or interactive pipelines (`journalctl -f | grep …`) whose final
    /// output must be read as it appears rather than buffered until the whole chain exits.
    ///
    /// `cancellationToken` is checked once, before spawning (an already-cancelled token reports
    /// `ProcessError.Cancelled` and starts nothing). The chain-level `Timeout`/`CancelOn` set on this
    /// pipeline still apply to the live session: either one firing hard-kills the whole tree, and a
    /// subsequent `FinishAsync` then reports the run as `TimedOut`/`Cancelled`. Otherwise the session is
    /// caller-driven — stop it with `StopAsync`/`Kill`/dispose. A stage that fails to spawn tears down the
    /// partially started chain and returns its error, orphaning nothing.
    member _.StartAsync
        ([<Optional>] cancellationToken: CancellationToken)
        : Task<Result<PipelineSession, ProcessError>> =
        task {
            // The whole-chain cancellation predicate the session uses to classify a killed run as
            // `Cancelled` (over the killed chain's raw outcome), matching the buffered path.
            let wasCancelled () =
                cancellationToken.IsCancellationRequested
                || (cancelOn |> Option.exists (fun t -> t.IsCancellationRequested))

            match! PipelineRunner.start commands timeout cancelOn cancellationToken with
            | Error error -> return Error error
            | Ok started ->
                // Reuse `RunningProcess`'s guarded construction (as `ProcessGroup.StartAsync` does): if the
                // handle ever faulted after spawn it reaps the tree via `host.Teardown` and re-raises.
                let! inner = RunningProcess.buildGuarded started.Host
                return Ok(PipelineSession(inner, commands, started.Capture, wasCancelled))
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
                match PipelineClassify.copyError capture with
                | Some error -> return Error error
                | None ->
                    match PipelineClassify.outputTooLargeError commands capture with
                    | Some error -> return Error error
                    | None ->
                        let stage = PipelineClassify.representative capture

                        match PipelineClassify.stdinErrorOnSuccess (List.head commands).Program capture stage with
                        | Some err -> return Error err
                        | None ->
                            let truncated = PipelineClassify.resultTruncated capture.LastStdoutTruncated stage

                            return
                                Ok(
                                    ProcessResult<byte[]>(
                                        stage.Program,
                                        capture.LastStdout,
                                        stage.Stderr,
                                        stage.Outcome,
                                        capture.Duration,
                                        truncated,
                                        stage.OkCodes,
                                        ?configuredTimeoutDuration = (if capture.TimedOut then timeout else None),
                                        stdoutEncoding = (List.last commands).Config.StdoutEncoding,
                                        overflowTotals = PipelineTotals.forResult capture stage
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
                match PipelineClassify.copyError capture with
                | Some error -> return Error error
                | None ->
                    match PipelineClassify.outputTooLargeError commands capture with
                    | Some error -> return Error error
                    | None ->
                        let stage = PipelineClassify.representative capture

                        match PipelineClassify.stdinErrorOnSuccess (List.head commands).Program capture stage with
                        | Some err -> return Error err
                        | None ->
                            let encoding = (List.last commands).Config.StdoutEncoding
                            let text = encoding.GetString capture.LastStdout
                            let truncated = PipelineClassify.resultTruncated capture.LastStdoutTruncated stage

                            return
                                Ok(
                                    ProcessResult<string>(
                                        stage.Program,
                                        text,
                                        stage.Stderr,
                                        stage.Outcome,
                                        capture.Duration,
                                        truncated,
                                        stage.OkCodes,
                                        ?configuredTimeoutDuration = (if capture.TimedOut then timeout else None),
                                        stdoutEncoding = encoding,
                                        overflowTotals = PipelineTotals.forResult capture stage
                                    )
                                )
        }

    // The capture-derived verbs share one implementation with the `Runner` module (`CaptureVerbs`),
    // over this pipeline's own stdout capture (`OutputStringAsync`, which carries the pipefail
    // representative stage), so trimming / success-checking / parse-error wrapping can't drift between
    // a single command and a pipeline.

    /// Require a successful pipefail exit and return the last stage's stdout, trailing whitespace
    /// trimmed. Any checked stage that did not exit 0 fails the pipeline. Output a bounded
    /// `OutputBuffer` byte cap truncated (`DropOldest`/`DropNewest` on the captured last-stage stdout,
    /// or on the stderr this result publishes) is refused with `ProcessError.OutputTooLarge` instead of
    /// being returned as if whole — `OutputStringAsync` is the lenient path, handing back the bounded
    /// payload with `ProcessResult.Truncated` set.
    ///
    /// That refusal always names the **last** stage and quotes that same stage's byte ceiling — the only
    /// ceiling a verb applied to the chain's captured stdout can name. The two cannot come apart here:
    /// an earlier stage is the pipefail representative only when it is a CHECKED FAILURE
    /// (`PipelineClassify.representative`), and the success check above has already turned such a chain
    /// into its own `Exit`/`Signalled`/`Timeout` error before truncation is looked at; with no checked
    /// failure the real last stage stands, whether or not it opted out of pipefail with `UncheckedInPipe`.
    /// So what this refusal reports is the last stage's own capture — its stdout, or the stderr published
    /// with it — while a clipped stderr on any other stage stays local to diagnostics the result does not
    /// publish (`PipelineClassify.resultTruncated`).
    member this.RunAsync([<Optional>] cancellationToken: CancellationToken) : Task<Result<string, ProcessError>> =
        // Only the byte ceiling is quoted: a pipeline captures raw bytes, so the last stage's `MaxLines`
        // never applies to it — the same convention `PipelineClassify.outputTooLargeError` follows for
        // the fail-loud overflow of these very captures.
        CaptureVerbs.run None (List.last commands).Config.OutputBuffer.MaxBytes (fun () ->
            this.OutputStringAsync cancellationToken)

    /// Like `RunAsync`, but discard the captured output — including when a bounded buffer truncated it:
    /// a side-effect run promises nothing about stdout, so a clean pipefail outcome stays `Ok`.
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
        ArgumentNullException.ThrowIfNull(parser, nameof parser)

        CaptureVerbs.parse (List.last commands).Program parser.Invoke (fun () -> this.RunAsync cancellationToken)

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    member this.TryParseAsync
        (parser: TryParser<'T>, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull(parser, nameof parser)

        CaptureVerbs.tryParse (List.last commands).Program (TryParser.toResult parser) (fun () ->
            this.RunAsync cancellationToken)

    /// Require a successful pipefail exit and deserialize the trimmed stdout as JSON into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults); invalid JSON becomes
    /// `ProcessError.Parse`, just like `ParseAsync`. Give an explicit type argument — there is no parser
    /// argument to infer `'T` from.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe —
    /// pass `options` with a source-generated `JsonSerializerContext`/`JsonTypeInfo&lt;'T&gt;` resolver, or
    /// avoid this verb, in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a NativeAOT app.">]
    member this.OutputJsonAsync<'T>
        ([<Optional>] options: JsonSerializerOptions | null, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        CaptureVerbs.outputJson (List.last commands).Program (Option.ofObj options) (fun () ->
            this.RunAsync cancellationToken)

    /// Require a successful pipefail exit and deserialize the trimmed stdout using source-generated
    /// `JsonTypeInfo<'T>` metadata. Invalid JSON becomes `ProcessError.Parse`; unlike the
    /// `JsonSerializerOptions` overload, this overload is safe for trimmed and NativeAOT applications.
    member this.OutputJsonAsync<'T>
        (typeInfo: JsonTypeInfo<'T>, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<'T, ProcessError>> =
        ArgumentNullException.ThrowIfNull(typeInfo, nameof typeInfo)

        CaptureVerbs.outputJsonTyped (List.last commands).Program typeInfo (fun () -> this.RunAsync cancellationToken)

/// `Command.Pipe` builds a two-stage `Pipeline`; further `Pipeline.Pipe` calls extend it.
[<Extension>]
type PipelineExtensions =

    /// Start a pipeline that feeds this command's stdout into `next`'s stdin. Rejects
    /// (`ArgumentException`) either stage setting a per-stage
    /// `Timeout`/`IdleTimeout`/`Retry`/`CancelOn`/`CapturePolicy`
    /// or `KeepStdinOpen`, or `next` (stage 1) setting a `Stdin` source — a pipeline cannot honour those
    /// (see the `Pipeline` type doc). `command` is stage 0, so a `Stdin` source on it (feeding the whole
    /// chain) is allowed.
    [<Extension>]
    static member Pipe(command: Command, next: Command) =
        ArgumentNullException.ThrowIfNull(command, nameof command)
        ArgumentNullException.ThrowIfNull(next, nameof next)
        PipelineStageGuard.validate (nameof command) 0 command
        PipelineStageGuard.validate (nameof next) 1 next
        // `command` (stage 0) is not the last stage, so an OS-level MergeStderr — or a PTY's merged
        // terminal output — on it would leak into `next`'s stdin; reject either (only the last stage,
        // `next`, may merge or be a PTY).
        PipelineStageGuard.rejectMergeOnNonLast (nameof command) 0 command
        PipelineStageGuard.rejectPtyOnNonLast (nameof command) 0 command
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
