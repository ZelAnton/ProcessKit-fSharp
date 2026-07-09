namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

/// One stage's terminal state inside a pipeline run.
type internal PipelineStage =
    {
        Program: string
        Outcome: Outcome
        Unchecked: bool
        Stderr: string
        OkCodes: int list
        /// This stage's stderr capture tripped its OWN `OutputBuffer` fail-loud (`OverflowMode.Error`)
        /// byte ceiling. Carried (with `StderrTotalBytes`) so `Pipeline` can surface a deterministic
        /// `ProcessError.OutputTooLarge` naming this stage — the per-stage stderr analogue of the last
        /// stage's `PipelineCapture.LastStdoutTooLarge`, so a fail-loud stderr overflow on ANY stage is
        /// honoured, not silently dropped. Always false for the dropping modes (`DropOldest`/`DropNewest`,
        /// which stay lossy but non-erroring) and for a stage without a `MaxBytes` cap.
        StderrTooLarge: bool
        /// Cumulative stderr bytes this stage produced (saturating at `Int32.MaxValue`), carried into the
        /// `OutputTooLarge` diagnostics when `StderrTooLarge` is set.
        StderrTotalBytes: int
        /// The stage was ended by the chain's *proactive teardown* — the shared group was hard-killed
        /// after some *other* (checked) stage failed — rather than by a failure of its own. Such a
        /// victim is de-prioritized in the pipefail fold (`Pipeline.representative`) so the stage that
        /// actually triggered the teardown keeps the blame, never a sibling the kill happened to catch.
        TornDown: bool
    }

/// The captured result of running a whole pipeline: the last stage's stdout (with its byte-cap
/// truncation / fail-loud / total signals from the last stage's `OutputBuffer`), every stage's
/// terminal state (left-to-right, each carrying its own stderr fail-loud / total signals so an
/// `OverflowMode.Error` stderr overflow on any stage — not just the final stdout — is surfaced), the
/// wall-clock duration, whether the pipeline timed out, and a genuine stage-0 stdin source-acquisition
/// failure observed by the feeder (surfaced by `Pipeline` as `ProcessError.Stdin` on an
/// otherwise-successful run, like a single command).
type internal PipelineCapture =
    { LastStdout: byte[]
      LastStdoutTruncated: bool
      LastStdoutTooLarge: bool
      LastStdoutTotalBytes: int
      Stages: PipelineStage list
      Duration: TimeSpan
      TimedOut: bool
      Stdin0Error: exn option }

/// Runs a multi-stage pipeline inside one fresh shared `ProcessGroup` — the staging/wiring/teardown
/// for the `Pipeline` type. Kept next to `Pipeline` (its only consumer) rather than inside
/// `ProcessGroup`, which it drives purely through that group's public/internal surface.
module internal PipelineRunner =

    /// Internal test seam (T-061): invoked on the staging thread immediately after each stage
    /// successfully spawns, with that stage's zero-based index. It lets a deterministic regression test
    /// fire cancellation in the exact window between two spawns — proving that once cancellation/timeout
    /// is observed no later stage is started, and any stage that raced the sweep is reaped before `run`
    /// returns. `None` (the default) in every production run; nothing but a test ever sets it.
    let mutable stageSpawnedTestHook: (int -> unit) option = None

    /// Spawn every stage into one fresh shared group, wire each stage's stdout to the next stage's
    /// stdin (no shell involved), capture the last stage's stdout, and reap the whole tree on exit.
    /// Cancellation or the optional `timeout` hard-kill the tree.
    let run
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
                // The pipeline's observability identity, chosen once no matter how the run ends:
                // stage 0's `Logger` becomes the pipeline's logger (a pipeline emits one whole-run
                // event pair, not per-stage events — see the `Pipeline` type doc), one `runId` ties
                // every event of this run together, and the `program` label is a composite of stage
                // *names* only (`a | b | c`) — never argv/env, keeping the security invariant that
                // holds for a single command.
                let logger = stages[0].Config.Logger
                let programLabel = stages |> Array.map (fun c -> c.Program) |> String.concat " | "
                let runId = Diag.newRunId ()

                match ProcessGroup.Create() with
                | Error error -> return Error error
                | Ok group ->
                    use group = group
                    let startedAt = Stopwatch.GetTimestamp()
                    use timeoutCts = new CancellationTokenSource()

                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

                    match timeout with
                    // Only arm the deadline when it fits a BCL timer; an over-long span is "no timeout"
                    // (a negative one was already rejected by `Pipeline.Timeout`). Guards CancelAfter
                    // against a synchronous out-of-range throw that would abort the run mid-spawn.
                    | Some duration when Timeouts.isArmable duration -> timeoutCts.CancelAfter duration
                    | _ -> ()

                    // Serialize each stage spawn against the cancellation/timeout sweep. `KillTree` leaves
                    // the group usable for further spawns, so the one-shot callback (which kills the stages
                    // running when it fires) would otherwise MISS a stage the sequential loop starts right
                    // afterwards — that stage would outlive the cancellation it should have been swept into.
                    // The gate makes "spawn a stage" and "mark cancelled, then sweep" mutually exclusive:
                    // `cancellationFired` is set UNDER the gate before the callback's `KillTree`, so a spawn
                    // taking the same gate either observes it and starts nothing, or has already made the
                    // child a group member the sweep then reaps. No stage can start after the sweep (T-061).
                    let stagingGate = obj ()
                    let mutable cancellationFired = false

                    use _registration =
                        linkedCts.Token.Register(fun () ->
                            lock stagingGate (fun () -> cancellationFired <- true)
                            group.KillTree())

                    // Dispose a pipe stream, swallowing the teardown-race exceptions (double close, or
                    // a broken pipe surfaced while flushing on dispose because the peer is gone).
                    let closeQuietly = Pump.disposeQuietly

                    let spawned = ResizeArray<Native.Common.Spawned>()
                    let copyTasks = ResizeArray<Task>()
                    let stderrTasks = ResizeArray<Task<Pump.RawCapture>>()
                    let mutable prevStdout: Stream option = None
                    let mutable spawnError = None
                    // Set when a spawn is skipped because cancellation/timeout already fired (the gate
                    // short-circuited it): the loop stops, and the post-loop match reaps the partial set and
                    // returns a prompt Cancelled/timed-out result instead of the normal full-chain path.
                    let mutable stagingHalted = false
                    let mutable stage0Feed: Task<exn option> option = None
                    let mutable index = 0

                    // Set exactly once, the moment stage 0 actually spawns — mirroring the
                    // single-command rule that a spawn failure is never counted as a run
                    // (`RunningProcess` — and its `Diag.runStarted` — only exists after a successful
                    // native spawn). Guards the `spawnError` branch below: a stage-0 spawn failure
                    // never starts a `RunTelemetryScope` (so there is nothing to close there); a later
                    // stage's spawn failure does, and must be closed via `Abandon()`.
                    let mutable telemetry: RunTelemetryScope option = None
                    let mutable startTimeUtc = DateTime.UtcNow

                    while index < stages.Length && spawnError.IsNone && not stagingHalted do
                        // The pipeline owns stdout wiring: every stage's stdout must be a pipe —
                        // intermediate stages feed the next stage's stdin and the last stage's stdout is
                        // captured. Force `Piped` over any user-set `Null`/`Inherit` so an unwired
                        // downstream stdin can't deadlock the chain. Stages after the first also take
                        // their stdin from the previous stage's stdout, so they need a stdin pipe;
                        // `KeepStdinOpen` creates one without an auto-fed source (we copy `prevStdout`
                        // into it below).
                        let piped = stages[index] |> Command.stdout StdioMode.Piped

                        let stage = if index > 0 then piped.KeepStdinOpen() else piped

                        // Take `stagingGate` for the spawn itself, mutually exclusive with the cancellation
                        // callback's "mark fired + KillTree" (above): either we observe `cancellationFired`
                        // and start nothing (the loop halts), or the child becomes a group member BEFORE the
                        // callback's sweep, which then reaps it. Either way no stage outlives the sweep.
                        let spawnOutcome =
                            lock stagingGate (fun () ->
                                if cancellationFired then
                                    ValueNone
                                else
                                    ValueSome(group.SpawnInto stage))

                        match spawnOutcome with
                        | ValueNone ->
                            // Cancellation/timeout already fired: do not start this — or any later — stage.
                            stagingHalted <- true
                        | ValueSome(Error error) -> spawnError <- Some error
                        | ValueSome(Ok sp) ->
                            spawned.Add sp

                            if index = 0 then
                                // The chain has actually launched: count + mark the whole run in
                                // flight and log the spawn now — matching `RunningProcess`, which does
                                // the same at construction, right after its own successful native
                                // spawn. No single pid represents a multi-process chain, so `None`
                                // rides here (a pipeline never claims one process's pid as its own).
                                startTimeUtc <- DateTime.UtcNow
                                telemetry <- Some(RunTelemetryScope.Start(programLabel, runId, startTimeUtc))
                                Log.spawn logger programLabel None runId

                                // Only the first stage may carry its own stdin source; feed it and keep the
                                // feed task so a genuine source-acquisition failure (a missing `FromFile`
                                // path, say) can surface as `ProcessError.Stdin` on an otherwise-successful
                                // pipeline — uniformly with a single command — instead of silently feeding
                                // the stage empty input.
                                stage0Feed <- Some(Pump.feedStdinSource sp.Stdin stages[0].Config.StdinSource)
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

                            // Drain every stage's stderr so a full stderr pipe never blocks a stage,
                            // bounding retained memory by that stage's own `OutputBuffer` byte cap +
                            // `Overflow` mode — the same path (`Pump.captureRawOrEmpty`) the last
                            // stage's stdout capture uses below. A stage without `MaxBytes` set keeps
                            // its previous unbounded behaviour (`captureRawOrEmpty` falls back to
                            // `drainRawOrEmpty` in that case).
                            stderrTasks.Add(
                                Pump.captureRawOrEmpty
                                    sp.Stderr
                                    None
                                    stages[index].Config.OutputBuffer
                                    CancellationToken.None
                            )

                            prevStdout <- sp.Stdout

                            // Test seam only (never set in production): let a regression test act in the
                            // exact window between this spawn and the next — see `stageSpawnedTestHook`.
                            stageSpawnedTestHook |> Option.iter (fun hook -> hook index)

                        index <- index + 1

                    match spawnError with
                    | Some error ->
                        // Some stages started before the failure. Hard-kill them, then reap (waitpid)
                        // each, drain/observe the relay + stderr tasks, and close every pipe — the
                        // group's `use` dispose alone hard-kills but does not waitpid POSIX leaders or
                        // close these parent-side streams, so without this they would leak.
                        group.KillTree()

                        let! _ =
                            spawned
                            |> Seq.map (fun sp -> group.WaitHandle sp.Handle)
                            |> Seq.toArray
                            |> Task.WhenAll

                        for t in copyTasks do
                            do! t

                        let! _ = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            Pump.closeSpawned sp

                        // A spawn failure is not a completed run — no duration/outcome to report,
                        // mirroring a single command's own spawn failure, which never reaches
                        // `RunningProcess`/`conclude` at all (no `runCompleted`, ever, on this path).
                        // But if stage 0 already spawned before a LATER stage failed, `telemetry`
                        // above already marked this run in flight — that mark must be cleared here so
                        // `runs.active` returns to zero instead of leaking (`Abandon`, not `Conclude`:
                        // this is not a completed run). A stage-0 spawn failure never started a
                        // `telemetry` scope, so this is then a no-op.
                        telemetry |> Option.iter (fun t -> t.Abandon())

                        return Error error
                    | None when stagingHalted ->
                        // Cancellation or the timeout fired mid-staging, so the gate short-circuited the loop
                        // and only `spawned.Count` of `stages.Length` stages ever started. The callback's
                        // `KillTree` already covered exactly this set (the gate guarantees none escaped); reap
                        // and drain them here — like the spawn-failure path above — so the caller gets a
                        // prompt Cancelled/timed-out result with no straggling stage and no process/pipe
                        // handle leak. The explicit `KillTree` makes the kill happen-before the waits, so the
                        // reap can never block on a raced stage the callback had not reached yet.
                        group.KillTree()

                        let! killedOutcomes =
                            spawned
                            |> Seq.map (fun sp -> group.WaitHandle sp.Handle)
                            |> Seq.toArray
                            |> Task.WhenAll

                        for t in copyTasks do
                            do! t

                        let! stderrCaptures = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            Pump.closeSpawned sp

                        let duration = Stopwatch.GetElapsedTime startedAt
                        let timedOut = timeoutCts.IsCancellationRequested

                        // The chain reached a terminal state (deliberately torn down), so — exactly like the
                        // full-spawn cancel/timeout path below — Conclude the run's telemetry before the
                        // returned Result is decided, so `runs.active` never leaks. `telemetry` is `Some` iff
                        // stage 0 spawned; a cancellation that beat even stage 0 leaves it `None` (nothing to
                        // close), matching a stage-0 spawn failure. A timed-out chain reports `TimedOut` (and
                        // logs its deadline); an externally cancelled one carries the stages' own kill outcome.
                        let overallOutcome =
                            if timedOut then
                                Outcome.TimedOut
                            elif killedOutcomes.Length > 0 then
                                killedOutcomes[killedOutcomes.Length - 1]
                            else
                                Outcome.Signalled None

                        let timeoutForLog = if timedOut then timeout else None

                        telemetry
                        |> Option.iter (fun t ->
                            t.Conclude(logger, overallOutcome, None, duration, ?timeout = timeoutForLog))

                        if cancellationToken.IsCancellationRequested then
                            // External cancellation takes precedence over a co-firing timeout (mirroring the
                            // full-spawn path's `cancellationToken`-first check): report `Cancelled`.
                            return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
                        else
                            // A pure timeout that landed during staging. Report `TimedOut` against the whole
                            // chain's last stage — the same observable contract the full-spawn timeout yields
                            // (`OutputStringAsync` → `IsTimedOut`, `RunAsync` → `Error`), so a timeout is never
                            // silently downgraded to `Cancelled` just because it raced stage startup. Every
                            // command is represented so `Pipeline.representative` reports the timeout against
                            // the chain's last stage; stages past `spawned.Count` never started (they carry a
                            // `TimedOut`/empty-stderr placeholder, which a `TimedOut` capture ignores anyway).
                            let stageResults =
                                [ for i in 0 .. stages.Length - 1 ->
                                      let outcome =
                                          if i < killedOutcomes.Length then
                                              killedOutcomes[i]
                                          else
                                              Outcome.TimedOut

                                      let stderr =
                                          if i < stderrCaptures.Length then
                                              stages[i].Config.StderrEncoding.GetString stderrCaptures[i].Bytes
                                          else
                                              ""

                                      // A stage that never started (staging halted mid-chain) has no stderr
                                      // capture, so it never overflowed — default its fail-loud state off.
                                      let stderrTooLarge = i < stderrCaptures.Length && stderrCaptures[i].TooLarge

                                      let stderrTotalBytes =
                                          if i < stderrCaptures.Length then
                                              stderrCaptures[i].TotalBytes
                                          else
                                              0

                                      { Program = stages[i].Program
                                        Outcome = outcome
                                        Unchecked = stages[i].Config.UncheckedInPipe
                                        Stderr = stderr
                                        OkCodes = stages[i].Config.OkCodes
                                        StderrTooLarge = stderrTooLarge
                                        StderrTotalBytes = stderrTotalBytes
                                        TornDown = false } ]

                            return
                                Ok
                                    { LastStdout = Array.empty
                                      LastStdoutTruncated = false
                                      LastStdoutTooLarge = false
                                      LastStdoutTotalBytes = 0
                                      Stages = stageResults
                                      Duration = duration
                                      TimedOut = true
                                      Stdin0Error = None }
                    | None ->
                        let lastSpawned = spawned[spawned.Count - 1]

                        // The last stage's stdout is the pipeline's captured output, so its `OutputBuffer`
                        // byte cap + overflow apply to that capture — matching a single command's byte
                        // verb. (`MaxLines` never applies to a raw byte capture; intermediate stages'
                        // policies are not applied — see the `Pipeline` type doc.)
                        let captureTask =
                            Pump.captureRawOrEmpty
                                lastSpawned.Stdout
                                lastTee
                                stages[stages.Length - 1].Config.OutputBuffer
                                CancellationToken.None

                        // Proactive teardown (ProcessKit-rs 2.1 robustness): the first *checked* stage to
                        // finish with a non-accepted outcome hard-kills the whole shared tree at once,
                        // instead of leaving the chain to wait for a pipe EOF that a quiet, still-running
                        // sibling may never deliver — classically an upstream producer that never writes
                        // and so never dies of a broken pipe, holding the relay's read (and thus the whole
                        // run) open indefinitely. The stages this kill catches are recorded `TornDown` and
                        // de-prioritized by the pipefail fold (`Pipeline.representative`), so the stage that
                        // actually failed keeps the blame and the reported result is unchanged. Only a
                        // genuine stage failure fires it: an `UncheckedInPipe` stage's unclean exit is
                        // forgiven (never a pipefail culprit, so never a teardown trigger), and a tree
                        // already being torn down by the whole-chain timeout or cancellation (`linkedCts`)
                        // suppresses it — that path is killing the tree itself.
                        use teardownCts = new CancellationTokenSource()
                        let tornDown = Array.zeroCreate<bool> spawned.Count

                        let observeStage (index: int) : Task<Outcome> =
                            task {
                                let! outcome = group.WaitHandle spawned[index].Handle

                                // Snapshot before possibly firing: a teardown already in flight when this
                                // stage ends marks it a victim (`TornDown`); the first genuine checked
                                // failure sees no teardown yet, fires it, and stays the (non-torn) culprit.
                                let victim = teardownCts.IsCancellationRequested
                                tornDown[index] <- victim

                                let checkedFailure =
                                    not stages[index].Config.UncheckedInPipe
                                    && not (outcome.IsAcceptedBy stages[index].Config.OkCodes)

                                if not victim && not linkedCts.IsCancellationRequested && checkedFailure then
                                    // `Cancel` marks the teardown fired (so later-finishing siblings read
                                    // themselves as victims); `KillTree` reaps the still-running rest of the
                                    // chain. Both are idempotent, so a benign race between two near-
                                    // simultaneous genuine failures (each a legitimate culprit) is harmless.
                                    teardownCts.Cancel()
                                    group.KillTree()

                                return outcome
                            }

                        let waitTasks = [| for index in 0 .. spawned.Count - 1 -> observeStage index |]

                        let! outcomes = Task.WhenAll waitTasks
                        do! Task.WhenAll(copyTasks.ToArray())
                        let! lastCapture = captureTask
                        let! stderrCaptures = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            Pump.closeSpawned sp

                        let duration = Stopwatch.GetElapsedTime startedAt
                        let timedOut = timeoutCts.IsCancellationRequested

                        // The whole chain reaped, its stdout captured: the run reached a terminal
                        // state. Report it exactly once here — before the cancellation check below can
                        // override the *returned* Result to `Cancelled` — mirroring `Runner.runToCompletion`,
                        // which likewise lets a verb's own completion (and its `Conclude`) fire before
                        // the outer cancel-map can replace its result. A timed-out chain additionally
                        // gets `Log.timeout` (the explicit deadline-kill event), exactly like a single
                        // command's own `Timeouts.raceTimeout`; the outcome fed to `Log.exit`/`Diag.runCompleted`
                        // is `Outcome.TimedOut` for that case, else the last stage's raw outcome — the
                        // same stage whose stdout/duration the pipeline reports as its own.
                        let overallOutcome =
                            if timedOut then
                                Outcome.TimedOut
                            else
                                outcomes[outcomes.Length - 1]

                        // `telemetry` is always `Some` here: reaching this branch means every stage
                        // spawned, including stage 0, which is the only place it is set.
                        let timeoutForLog = if timedOut then timeout else None

                        telemetry
                        |> Option.iter (fun t ->
                            t.Conclude(logger, overallOutcome, None, duration, ?timeout = timeoutForLog))

                        if cancellationToken.IsCancellationRequested then
                            return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
                        else
                            let stageResults =
                                [ for i in 0 .. stages.Length - 1 ->
                                      { Program = stages[i].Program
                                        Outcome = outcomes[i]
                                        Unchecked = stages[i].Config.UncheckedInPipe
                                        Stderr = stages[i].Config.StderrEncoding.GetString stderrCaptures[i].Bytes
                                        OkCodes = stages[i].Config.OkCodes
                                        // Carry this stage's stderr fail-loud state so `Pipeline` can
                                        // surface a stderr overflow on ANY stage, not just the final
                                        // stdout. Every stage spawned on this path, so `stderrCaptures[i]`
                                        // exists for each `i`.
                                        StderrTooLarge = stderrCaptures[i].TooLarge
                                        StderrTotalBytes = stderrCaptures[i].TotalBytes
                                        TornDown = tornDown[i] } ]

                            // Observe the stage-0 stdin fault without blocking: only a feed that has
                            // finished (a missing `FromFile` faults synchronously at spawn) yields its
                            // stashed source failure — matching the single-command observer.
                            let stdin0Error =
                                match stage0Feed with
                                | Some feed when feed.IsCompletedSuccessfully -> feed.Result
                                | _ -> None

                            return
                                Ok
                                    { LastStdout = lastCapture.Bytes
                                      LastStdoutTruncated = lastCapture.Truncated
                                      LastStdoutTooLarge = lastCapture.TooLarge
                                      LastStdoutTotalBytes = lastCapture.TotalBytes
                                      Stages = stageResults
                                      Duration = duration
                                      TimedOut = timeoutCts.IsCancellationRequested
                                      Stdin0Error = stdin0Error }
        }
