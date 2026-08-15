namespace ProcessKit

open System
open System.IO
open System.Threading
open System.Threading.Tasks

module internal ReadinessRace =

    let preferCancellation
        (program: string)
        (cancellationToken: CancellationToken)
        (result: Result<unit, ProcessError>)
        : Result<unit, ProcessError> =
        match result with
        | Ok() when cancellationToken.IsCancellationRequested -> Error(ProcessError.Cancelled program)
        | other -> other

    let preferCancellationAndDeadline
        (program: string)
        (cancellationToken: CancellationToken)
        (deadlineHasElapsed: unit -> bool)
        (notReady: ProcessError)
        (result: Result<unit, ProcessError>)
        : Result<unit, ProcessError> =
        match result with
        | Ok() ->
            if cancellationToken.IsCancellationRequested then
                Error(ProcessError.Cancelled program)
            elif deadlineHasElapsed () then
                Error notReady
            elif cancellationToken.IsCancellationRequested then
                // A caller cancellation can race the elapsed-time read; check again so the deadline gate
                // cannot let a success through after cancellation has taken effect.
                Error(ProcessError.Cancelled program)
            else
                result
        | other -> other

    // The ceiling on the single post-exit readiness re-check in `raceAgainstExit` below.
    // Deliberately much shorter than a typical readiness `timeout`: the re-check exists to observe a
    // condition the child published a moment ago — a local file, an open port/socket, a health endpoint
    // — all of which answer in milliseconds even on a loaded CI runner, so this window is generous for
    // an honest answer while keeping the guarantee that mattered before it existed. Without a ceiling, a
    // caller-owned predicate that answers slowly (or, like `TaskCompletionSource<bool>().Task`, never)
    // would turn "an exited child resolves promptly" back into "waits out the whole timeout". Reaching
    // the ceiling costs nothing beyond the delay: the verdict is then the same `NotReady` the exit
    // branch reported before this re-check was added.
    let postExitRecheckGrace = TimeSpan.FromMilliseconds 500.0

    // Race a readiness probe against the child's own exit so a probe on a child that has already
    // exited — or that dies early on startup — resolves to `NotReady` promptly instead of burning the
    // whole `timeout` polling a condition that can never come true. Shared by all readiness probes
    // (`WaitForHttpAsync`/`WaitForPortAsync`/`WaitForSocketAsync`/`WaitForAsync`) so their early-exit
    // behaviour cannot drift apart: `startProbe` builds the underlying `ReadinessProbe.*` task from the
    // snapshotted (still-`Fresh`) drain streams and a readiness token linked to the caller's `cancellationToken`;
    // everything else — the exit race, cancellation, and `NotReady`/`Cancelled` selection — lives here,
    // once.
    //
    // Early-exit detection MUST share the one reap-once exit wait every other verb on the handle uses —
    // that is what `sharedExitWait` is (`ConsumptionGate.EnsureBufferedWait`, memoized under the claim
    // lock) — rather than starting an independent `host.Wait()`: on POSIX, `host.Wait()`/`waitPosix`
    // REAPS the child and consumes its exit status, and is idempotent only while a wait stays in flight
    // — never after the pid has already been reaped (KB K-016). A second, unrelated `host.Wait()` here
    // would race the reap started by this one, so a later `WaitAsync`/`ProfileAsync` call (the common
    // "diagnose why the service died on startup" path) would either see the pid already gone (ECHILD →
    // fabricated `Outcome.Unobserved`) or, worse, risk observing a recycled pid. Joining that ONE shared
    // wait (instead of claiming the pipes through `ConsumptionGate.TryClaimBuffered`) starts it without
    // claiming the consumption, so the handle stays `Fresh` and a subsequent buffered verb can still
    // claim the pipes and reuse this exact same memoized wait — one `host.Wait()`, one reap, shared by
    // the probe and by whatever verb runs afterward.
    //
    // `probeDrainStreams` is the caller's still-`Fresh` snapshot of the handle's pipes
    // (`ConsumptionGate.IsFresh`): a probe drains them only while nothing else owns them, so it can
    // never start a second reader on a pipe an established consumer is already pumping.
    //
    // Observing the exit is NOT by itself proof that the condition never came true. The polling probe's
    // in-flight attempt may have observed a stale `false` and then yielded long enough for the child to
    // publish readiness (a sentinel file, an open port/socket, a health endpoint served by a surviving
    // grandchild) and exit: cancelling that run and reporting `NotReady` at once would erase a state
    // that genuinely exists. So the exit branch below gives the condition exactly ONE more observation
    // before concluding, and only when there is budget left for it — see the numbered contract there.
    let raceAgainstExit
        (config: CommandConfig)
        (probeDrainStreams: unit -> Stream option * Stream option)
        (sharedExitWait: unit -> Task<Outcome>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        (startProbe:
            ReadinessAttempts
                -> Stream option
                -> Stream option
                -> TimeSpan
                -> CancellationToken
                -> Task<Result<unit, ProcessError>>)
        : Task<Result<unit, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled config.Program)
            else
                // The whole probe's budget, clamped exactly as the readiness core clamps it, measured
                // from here — the post-exit re-check below spends what is left of THIS budget, never a
                // fresh copy of it, and a `NotReady` still reports this same clamped total (not the
                // shorter slice the re-check was given) so the reported budget matches what was enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                let startedTimestamp = config.TimeProvider.GetTimestamp()
                let stdout, stderr = probeDrainStreams ()

                use readinessCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                let readinessTask =
                    startProbe ReadinessAttempts.PollUntilDeadline stdout stderr timeout readinessCts.Token

                let childExitTask = sharedExitWait ()
                let! winner = Task.WhenAny(readinessTask :> Task, childExitTask :> Task)

                let classifyCompletedResult result =
                    preferCancellationAndDeadline
                        config.Program
                        cancellationToken
                        (fun () -> config.TimeProvider.GetElapsedTime startedTimestamp >= armedTimeout)
                        (ProcessError.NotReady(config.Program, armedTimeout))
                        result

                if obj.ReferenceEquals(winner, readinessTask) || readinessTask.IsCompleted then
                    let! completed = readinessTask
                    return classifyCompletedResult completed
                else
                    readinessCts.Cancel()
                    let! raced = readinessTask

                    match raced with
                    | Ok() ->
                        // (1) The polling run DID see the condition hold — it just finished in the window
                        // between the race above picking the exit and the cancel here. Its `Ok` was
                        // computed with the caller's token and the shared deadline both still clear, so it
                        // is an honest success; discarding it in favour of `NotReady` would lose exactly
                        // the readiness this branch exists to preserve.
                        return classifyCompletedResult (Ok())
                    | Error _ ->
                        if cancellationToken.IsCancellationRequested then
                            // (2) The caller cancelled: cancellation outranks readiness, and no further
                            // observation may be started on a token that has already fired.
                            return Error(ProcessError.Cancelled config.Program)
                        else
                            let remaining = armedTimeout - config.TimeProvider.GetElapsedTime startedTimestamp

                            if remaining <= TimeSpan.Zero then
                                // (3) The overall deadline is spent, so there is no budget to observe
                                // anything else with — `NotReady`, exactly as before this re-check existed.
                                // Stated here rather than left to the readiness core (which does refuse a
                                // non-positive budget without invoking the probe): the rule that a spent
                                // deadline buys no further observation belongs with the decision to make
                                // one, and it keeps the spent-budget path from starting a probe run at all.
                                return Error(ProcessError.NotReady(config.Program, armedTimeout))
                            else
                                // (4) One final observation of a state that can no longer change, bounded
                                // by `min(remaining budget, postExitRecheckGrace)` and by the caller's own
                                // token. `Once` (never a second poll loop) is what keeps this cheap: a
                                // "still not ready" answer returns at the first attempt, so the ordinary
                                // "child died on startup" path costs one probe invocation, not the rest of
                                // the timeout. The grace caps the OTHER direction — a probe that answers
                                // slowly (or never) must not turn prompt early-exit detection back into
                                // waiting out the deadline. No drain streams are handed over: an exited
                                // child cannot block on a full pipe, so there is nothing left to unblock.
                                let! recheck =
                                    startProbe
                                        ReadinessAttempts.Once
                                        None
                                        None
                                        (min remaining postExitRecheckGrace)
                                        cancellationToken

                                match recheck with
                                | Ok() ->
                                    // The caller can cancel, or the original absolute deadline can elapse,
                                    // after the bounded re-check reports success but before this branch
                                    // returns. Both checks must use the original budget, not the re-check's
                                    // relative slice.
                                    return classifyCompletedResult recheck
                                | Error(ProcessError.Cancelled _) ->
                                    // The caller's token fired while the one re-check was in flight; it
                                    // outranks the re-check's own verdict, as in (2).
                                    return Error(ProcessError.Cancelled config.Program)
                                | Error _ -> return Error(ProcessError.NotReady(config.Program, armedTimeout))
        }
