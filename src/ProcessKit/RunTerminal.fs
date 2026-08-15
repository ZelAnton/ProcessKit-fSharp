namespace ProcessKit

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// The shared terminal machinery of ONE `RunningProcess` handle: the bounded waits every consumer on
/// the handle ends through, and the teardown they all end in.
///
/// Three bounds live here, and every verb on the handle inherits all three rather than re-deriving
/// any of them:
///
/// - the configured TIMEOUT race (`WaitWithTimeout`), anchored at spawn so `Command.Timeout` bounds
///   the RUN and not each individual wait;
/// - the bounded POST-KILL reap window (`ArmPostKillReap`, applied inside `WaitWithTimeout`), so a
///   kill-then-wait caller cannot block forever on a tree that will not be reaped;
/// - the bounded POST-EXIT output drain (`AwaitPumpsSettled`/`DrainPumpsBounded`), so something that
///   inherited the child's stdout/stderr cannot hold a verb open past the leader's own conclusion.
///
/// It also owns the handle's lifecycle tokens — the teardown marker every pump classifies its I/O
/// faults against, and the two backpressure tokens a terminal path releases an abandoned bounded
/// writer with — plus `ReapGuard`, the scope every terminal verb reaps in.
///
/// `sinceSpawn` is the LIVE monotonic clock (`Stopwatch.GetElapsedTime host.StartedTimestamp`), and
/// deliberately the only clock this type is given: every deadline here resolves against it. The
/// handle's COMPLETION clock (frozen at the recorded duration for a replayed cassette) feeds metadata
/// only and must never reach a deadline — see the two-clock note on `RunningProcess.elapsed`.
type internal RunTerminal
    (config: CommandConfig, host: RunningHost, runId: string, sinceSpawn: unit -> TimeSpan, markAbandoned: unit -> unit)
    =

    // Idle-timeout (`Command.IdleTimeout`, opt-in): a resettable "no output" watchdog, plus thin
    // activity-tracking wrappers around the stdout/stderr pipes that reset it on every non-empty read.
    // Byte granularity — honest and uniform across every verb (line pumps, byte drains, raw captures
    // all reset it), and independent of the handle's line counters. Unset (the default): no timer, and
    // the raw pipe streams pass straight through with zero overhead, keeping the idle path entirely
    // opt-in. Armed by `WaitWithTimeout` (via `Timeouts.raceTimeout`) when the exit wait begins;
    // disposed with this handle.
    let idleTimer: Timeouts.IdleTimer option =
        match config.IdleTimeout with
        | Some idle when Timeouts.isArmable idle -> Some(new Timeouts.IdleTimer(idle))
        | _ -> None

    // Fires once the bounded post-exit output drain gives up on a tail nobody is going to finish (see
    // `PostExitDrain` and `severOutputStreams` below). Every read of this handle's own stdout/stderr
    // goes through a `SeverableStream` carrying it, so severing ends each pump at a clean EOF rather
    // than at a fault anything has to classify. Like `disposalCts` it arms no timer and owns nothing to
    // release, so it is deliberately never disposed — nothing would be freed, and a `Cancel` racing a
    // `Dispose` would only add an `ObjectDisposedException` to a completion path.
    let severCts = new CancellationTokenSource()

    // Cancels a writer parked on a bounded stream's `StreamFullMode.Backpressure` (`WriteAsync`) once
    // this handle is torn down, so an abandoned bounded stream can't leave its pump running forever: a
    // `Command.Timeout` kills the CHILD but does not by itself free a writer waiting here if nothing
    // ever reads again (see the deadlock note in docs/streaming.md). No `CancelAfter` is ever armed on
    // it, so it owns no timer — there is nothing to release, and skipping `Dispose` is safe.
    let disposalCts = new CancellationTokenSource()

    // Bounded line/event/frame writers use a token separate from `disposalCts`: terminal verbs cancel
    // this token BEFORE awaiting their shared outcome, while `disposalCts` remains the marker that host
    // teardown has actually started for genuine-vs-teardown I/O classification. This distinction lets an
    // abandoned Backpressure writer wake without turning a real pump fault into a routine cancellation.
    let backpressureCts = new CancellationTokenSource()

    // Only the stdout chunk channel's bounded writer uses this token. StopAsync must be able to
    // release an abandoned chunk consumer before awaiting the chunk session's outcome, without
    // cancelling the general lifecycle token that line/event pumps use for teardown-fault classification.
    let chunkBackpressureCts = new CancellationTokenSource()

    // ---- the bounded post-kill reap window for THIS handle (see `PostKillReap`) ------------------
    //
    // Completed once the post-kill budget has elapsed after a hard kill delivered THROUGH this handle
    // — `Kill()` (which is what a cancelled run fires, via the token registration in
    // `CaptureVerbs.runToCompletion`), the pump-fault kill, and `StopAsync`'s soft->hard escalation.
    // Until one of those arms it, this task never completes and the exit wait behaves exactly as
    // before: an ordinary child is reaped synchronously and reports its REAL outcome, with no budget
    // and no artificial delay anywhere on the normal path.
    //
    // It bounds the waits that are IN FLIGHT when the kill lands. A wait that starts later cannot use
    // it — this is a one-shot latch that stays completed for the life of the handle, so racing it
    // would answer instantly and read nothing (see `boundedExitWait`, which gives such a wait its own
    // window instead).
    let postKillDeadline =
        // RunContinuationsAsynchronously: the arming timer's callback must not run the exit wait's
        // continuation inline on the timer thread.
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    // 0 until a hard kill armed the budget; `Interlocked.Exchange` flips it for the first one, so a
    // second kill (a `Kill()` after a `StopAsync`, a pump fault racing either) extends nothing and
    // cannot restart the window.
    let mutable postKillArmed = 0

    // 0 = no `StopAsync` has fired the soft-kill yet; `Interlocked.Exchange` flips it to 1 for the
    // first one. A repeat `StopAsync` (or one racing a `Dispose` that already reaped the container)
    // then skips re-entering the native graceful kill on an already-released container and only awaits
    // the same exit outcome — the once-guard that makes `StopAsync` idempotent.
    let mutable stopStarted = 0

    // Set by the winning total/idle deadline before teardown begins, then threaded into buffered
    // ProcessResult construction. Duration remains the complete wall-clock elapsed time.
    let mutable configuredTimeoutDuration: TimeSpan option = None

    // ---- the bounded post-exit output drain for THIS handle (see `PostExitDrain`) -----------------
    //
    // 0 until the drain had to SEVER this handle's parent read ends: the child's fate was already
    // settled, but something that inherited its stdout/stderr — a daemonized worker, a `setsid`
    // helper, a shell's background job — still held the pipe open when the window ran out. Read into
    // every capture's `Truncated`, so a capture cut short by the bound is never reported as complete.
    // Written from a pump-joining task, read from the verb building the result, hence `Volatile`.
    let mutable outputDrainSevered = 0

    // 0 until even the sever could not end a pump inside the window that follows it — a read the OS
    // will not let go of (a POSIX pty master's blocking `read`, which no token interrupts). Such a
    // pump is handed to `PostExitDrain.abandon`: never awaited again, its eventual fault observed.
    // A diagnostic, not a control input; the verb's answer is the same either way.
    let mutable outputPumpsAbandoned = 0

    let cancelBackpressureWriters () =
        backpressureCts.Cancel()
        chunkBackpressureCts.Cancel()

    // Start the one-shot post-kill reap window. Called at the exact points a hard kill has been
    // delivered, never at the points one is merely intended: arming it before the kill would cut a
    // still-legitimate graceful grace window short.
    let armPostKillReap () =
        if Interlocked.Exchange(&postKillArmed, 1) = 0 then
            Task
                .Delay(PostKillReap.budget ())
                .ContinueWith(
                    Action<Task>(fun _ -> postKillDeadline.TrySetResult() |> ignore),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
            |> ignore

    // True once THIS handle's own teardown has begun — `disposalCts` is cancelled (synchronously) by
    // `reapGuard`/`DisposeHandleAsync` immediately before `host.Teardown()` disposes the pipe streams
    // (the same happens-before the streaming pumps' `isTearingDown` relies on, see
    // `Pump.genuineReadFault`). The buffered pumps poll it before reclassifying a caught
    // `IOException`/`ObjectDisposedException`: one caught while this reports `true` is the routine
    // dispose/broken-pipe race a CONCURRENT `StopAsync`/`Dispose` sharing this handle triggers by
    // design — it disposes the pipes a still in-flight buffered verb's pumps are draining — not a
    // genuine OS read failure.
    let isTearingDown () =
        disposalCts.Token.IsCancellationRequested

    let markOutputDrainSevered () = Volatile.Write(&outputDrainSevered, 1)

    let markOutputPumpsAbandoned () =
        Volatile.Write(&outputPumpsAbandoned, 1)

    // Stop this handle's own parent read ends. Every pump reading them then ends at a clean EOF with
    // everything it had captured — no fault to classify, tees still flushed — so the verb can report
    // its already-decided `Outcome` instead of waiting on a writer that is no longer this run's child.
    //
    // Deliberately NOT a stream close, and deliberately no kill: closing the fd and deciding the fate
    // of the remaining tree both belong to the OWNER's teardown (`RunningHost.Teardown`, reached by
    // `reapGuard` moments later), which is the one place that knows whether this run owns a private
    // group — where reaping the descendants is right — or shares a group, where they belong to the
    // group and must outlive this handle untouched.
    let severOutputStreams () =
        try
            severCts.Cancel()
        with :? AggregateException ->
            // `Cancel()` aggregates whatever a registered cancellation callback raised. The only
            // registrations on this token are the runtime's own pending-read cancellations (CancelIoEx
            // on Windows, the socket engine's operation cancel on POSIX), and a failure to wake one is
            // not actionable here: the bound has already decided to stop waiting, and a pump it could
            // not end is abandoned-and-observed below either way.
            ()

    // The ONE bounded post-exit output drain, shared by every consumer that joins its own output
    // pumps once the child's fate is settled — the buffered captures, the discard drains, the
    // line/chunk/event streaming outcomes, the interactive sessions, and the shared `ExitTask`.
    //
    // Returns whether the pumps SETTLED. `true` is the answer on every ordinary run (they were already
    // at EOF, or reached it inside the window, or the sever ended them); `false` means they were
    // abandoned to `PostExitDrain` and must never be awaited again — the caller then reports what its
    // caller-owned capture holds rather than blocking on a task that may never complete.
    //
    // A pump fault is untouched by all of this: whenever the pumps settled, they are awaited here
    // exactly as an unbounded `Task.WhenAll` would await them, so a throwing
    // `OnStdoutLine`/tee/decoder before the bound stays the error it always was.
    let awaitPumpsSettled (pumps: Task[]) : Task<bool> =
        let all = Task.WhenAll pumps

        task {
            let! drained = PostExitDrain.settlesWithin (PostExitDrain.budget ()) all

            if drained then
                do! all
                return true
            else
                markOutputDrainSevered ()
                severOutputStreams ()
                // The sever's EOF still has to travel back through the pump's own read loop, line
                // framing, tee flush and channel completion, so give that unwinding the same window
                // rather than declaring the pumps lost the instant we cut them.
                let! settled = PostExitDrain.settlesWithin (PostExitDrain.budget ()) all

                if settled then
                    do! all
                    return true
                else
                    markOutputPumpsAbandoned ()
                    PostExitDrain.abandon all
                    return false
        }

    // `awaitPumpsSettled` for a consumer with no capture to salvage — the discard drains, and the
    // streaming sessions whose output has already reached its channel.
    let drainPumpsBounded (pumps: Task[]) : Task =
        task {
            let! _settled = awaitPumpsSettled pumps
            ()
        }
        :> Task

    // The reason an exit wait reports when a hard kill was delivered but the reap never landed inside
    // the post-kill budget. Deliberately `Unobserved` and not a fabricated `Exited`/`Signalled`: we
    // genuinely did not see how the tree concluded, and `Unobserved` is never accepted as success.
    let postKillUnobservedReason =
        "the tree was hard-killed, but its exit status was not observed within the bounded post-kill reap window; a background reaper owns the remaining wait"

    // The one `host.Wait()` for this handle (see `ConsumptionGate.EnsureBufferedWait`/`ExitTask` for
    // why there is exactly one), bounded once a hard kill has been delivered through this handle. A
    // delivered SIGKILL/`TerminateProcess` is not a promise that the child is reapable now — a child
    // wedged in uninterruptible sleep defers even SIGKILL — so a kill-then-wait caller (a cancelled
    // run's `Kill()`, the pump-fault kill) would otherwise wait forever on a tree it has already killed.
    // When the budget elapses the native wait keeps running as the SINGLE eventual reaper, adopted by
    // the `PostKillReap` ledger (which observes its fault; on POSIX it is the same shared `waitPosix`
    // group, so nothing starts a second reap — K-016), and this wait resolves to an honest `Unobserved`.
    //
    // The budget runs from whichever came LAST: the kill, or this wait's own start. That distinction is
    // the whole point of the two branches below — the window exists to stop a CALLER from blocking
    // unboundedly after the answer is decided, so a caller that only starts waiting later must get a
    // window of its own rather than inherit a spent one. Time between the kill and the first verb
    // (`Kill()`, then any work, then `WaitAsync`/`OutputStringAsync`/... — the exit wait is created
    // lazily by the first of them) would otherwise leave `postKillDeadline` already completed and
    // report `Unobserved` for a perfectly ordinary killed child whose status was there for the asking.
    // Either way the caller blocks at most one budget, and either way a wait that genuinely does not
    // land inside its window hands ownership over instead of being dropped. With no kill delivered at
    // all — the normal path — this is a straight pass-through, with no budget and no timer.
    let boundedExitWait () : Task<Outcome> =
        let wait = host.Wait()
        let waitBase = wait :> Task

        if Volatile.Read(&postKillArmed) = 1 then
            // The kill preceded this wait: give it a full budget measured from here. `awaitWithin` also
            // answers an already-completed wait without arming anything, and adopts on expiry.
            task {
                match! PostKillReap.awaitWithin (PostKillReap.budget ()) wait with
                | ValueSome outcome -> return outcome
                | ValueNone -> return Outcome.Unobserved postKillUnobservedReason
            }
        else
            // No kill yet: the handle-wide latch is what bounds this wait, one budget after a kill
            // delivered while it is in flight (and never at all if none is).
            task {
                let! winner = Task.WhenAny(waitBase, postKillDeadline.Task)

                if obj.ReferenceEquals(winner, waitBase) then
                    return! wait
                else
                    PostKillReap.adoptWait waitBase
                    return Outcome.Unobserved postKillUnobservedReason
            }

    // Wait for exit, applying the configured total and/or idle timeout: on whichever deadline fires,
    // kill the tree (gracefully if `TimeoutGrace` is set, else hard) — one shared kill for both, so no
    // double kill — and report `Outcome.TimedOut`. The idle watchdog is armed inside `raceTimeout` as
    // the wait begins and reset by each stdout/stderr read through the activity-tracking wrappers. The
    // exit wait underneath is `boundedExitWait`, so the reap after ANY hard kill on this handle stays
    // bounded (the timeout race bounds its own post-kill reap too, see `Timeouts.raceTimeoutWithCts`).
    //
    // The TOTAL deadline is anchored at SPAWN (`host.StartedTimestamp`, read through `sinceSpawn ()` —
    // the live monotonic clock, deliberately NOT the completion clock a replayed cassette handle
    // freezes at its recorded duration), not at this call: this exit wait is created LAZILY by
    // whichever consumer gets here first — a buffered verb through `ConsumptionGate.EnsureBufferedWait`,
    // a streaming/event session, a readiness probe, `WaitAnyAsync`/`WaitAllAsync` — and on a live
    // `StartAsync` handle that can be long after the child started. Re-issuing the full `config.Timeout`
    // here would hand the child `(time until the first consumer) + Timeout` to run in, so
    // `Command.Timeout(1s)` on a handle consumed five seconds later bounded nothing anyone asked for.
    // `Timeouts.totalDeadline` therefore arms only what is LEFT of the budget while keeping the
    // CONFIGURED duration for `ProcessError.Timeout`/`ProcessResult`/`Log.timeout`, and every wait
    // created on this handle — however many consumers reach it, and whenever — resolves to the same
    // single absolute deadline (KB K-149: a window that bounds later-created work must be computed when
    // that work is created, never fixed at an earlier event). A remainder too short for this wait to
    // have surfaced an exit the child had ALREADY made is topped up by the bounded
    // `Timeouts.exitSettleWindow` (capped by the configured duration, so it never defers a kill past
    // what the caller asked for) — the deadline bounds the RUN, and must not turn a late collect of an
    // already-finished child into a fabricated `TimedOut`.
    //
    // The IDLE deadline is deliberately NOT rewritten this way: it is an inactivity window, so it is
    // armed inside the race when output actually starts being consumed. Backdating it to spawn would
    // charge a handle for the quiet gap before anything was reading — the opposite of what it measures.
    let waitWithTimeout () : Task<Outcome> =
        let onTimeout (configuredDuration: TimeSpan) : Task =
            task {
                configuredTimeoutDuration <- Some configuredDuration

                match config.TimeoutGrace with
                | Some grace -> do! host.GracefulKill grace
                | None -> host.StartKill()
            }
            :> Task

        // Read the time since spawn BEFORE starting the wait, so the budget is never widened by the work
        // of starting it (the two are microseconds apart; erring towards the smaller remainder is the
        // honest direction for a deadline).
        let total = Timeouts.totalDeadline config.Timeout (sinceSpawn ())

        Timeouts.raceTimeout config.Logger config.Program runId total idleTimer onTimeout (boundedExitWait ())

    // Kill the tree the moment an output pump faults, so a still-producing child can't wedge the exit
    // wait — and the pump's siblings — by blocking on a full pipe that nobody drains once the pump
    // reading it has died. Fire-and-forget and best-effort: the kill only unblocks the child so the
    // exit wait can conclude and the child is reaped in bounded time even with no configured timeout;
    // the ORIGINAL pump fault is still surfaced by whoever awaits the pump (the session outcome, the
    // buffered verb's own join), so what propagates is that fault, not a secondary closed-pipe/channel
    // error. The continuation inspects only `IsFaulted` (never `Exception`), so the pump's exception
    // stays available for its real awaiter, and the continuation itself can't fault (the `StartKill`
    // call is guarded). Runs synchronously on the faulting pump's completion so the kill is prompt.
    let killTreeOnPumpFault (pump: Task) : unit =
        pump.ContinueWith(
            Action<Task>(fun completed ->
                if completed.IsFaulted then
                    try
                        host.StartKill()
                        // A hard kill was delivered: start the bounded post-kill reap window, so a
                        // child that cannot be reaped (wedged in uninterruptible sleep) cannot hold the
                        // exit wait this kill exists to unblock.
                        armPostKillReap ()
                    with _ ->
                        // Best-effort: `reapGuard`'s teardown still reaps the tree, and the pump fault
                        // is surfaced by its awaiter, so a hiccup in this early kill loses nothing.
                        ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    /// Wrap a pipe stream so every read resets the idle watchdog (a no-op passthrough when
    /// `Command.IdleTimeout` is unset, keeping the idle path entirely opt-in).
    member _.WatchActivity(stream: Stream option) : Stream option =
        match idleTimer with
        | Some timer ->
            stream
            |> Option.map (fun s -> new Timeouts.ActivityStream(s, timer.Reset) :> Stream)
        | None -> stream

    /// Wrap a pipe stream so the bounded post-exit drain can end its pump at a clean EOF (see
    /// `severOutputStreams`) rather than at a fault anything has to classify.
    member _.Severable(stream: Stream option) : Stream option =
        stream |> Option.map (fun s -> new SeverableStream(s, severCts.Token) :> Stream)

    /// Cancelled once this handle's own teardown begins — the marker a pump reads to tell a routine
    /// broken-pipe/close race caused by teardown from a genuine I/O failure.
    member _.DisposalToken = disposalCts.Token

    /// Cancelled when a terminal/shared-exit path takes ownership of ending a streaming session. It is
    /// separate from `DisposalToken`, because a bounded Backpressure writer must wake before the shared
    /// outcome is awaited while the pump's I/O fault classification still needs the actual teardown bit.
    member _.BackpressureToken = backpressureCts.Token

    /// The chunk session's own backpressure token: `StopAsync` must be able to release an abandoned
    /// chunk consumer without cancelling the token the line/event pumps classify teardown against.
    member _.ChunkBackpressureToken = chunkBackpressureCts.Token

    /// True once teardown of this handle has begun (see `isTearingDown`).
    member _.IsTearingDown() = isTearingDown ()

    /// Release every bounded writer parked on backpressure. Called by the terminal paths BEFORE they
    /// await a shared outcome, so an unread full channel can't keep that await waiting for the pump
    /// that is itself waiting for a reader.
    member _.CancelBackpressureWriters() = cancelBackpressureWriters ()

    /// Start this handle's one-shot post-kill reap window (see `postKillDeadline`). Called at the exact
    /// points a hard kill HAS been delivered, never where one is merely intended.
    member _.ArmPostKillReap() = armPostKillReap ()

    /// The configured timeout duration the winning deadline recorded, for the `ProcessResult` a
    /// buffered verb builds afterwards.
    member _.ConfiguredTimeoutDuration = configuredTimeoutDuration

    /// The exit wait with the configured total/idle timeouts applied (see `waitWithTimeout`). Every
    /// consumer that needs a fresh exit wait — the streaming sessions, and the memoized buffered wait —
    /// creates it through this, so all of them share one anchoring of the deadline at spawn.
    member _.WaitWithTimeout() = waitWithTimeout ()

    /// Kill the tree the moment `pump` faults (see `killTreeOnPumpFault`).
    member _.KillTreeOnPumpFault(pump: Task) = killTreeOnPumpFault pump

    /// Join `pumps` under the bounded post-exit output drain, reporting whether they SETTLED. `false`
    /// means they were abandoned and must never be awaited again — the caller reports what its own
    /// capture holds instead.
    member _.AwaitPumpsSettled(pumps: Task[]) = awaitPumpsSettled pumps

    /// `AwaitPumpsSettled` for a consumer with no capture to salvage.
    member _.DrainPumpsBounded(pumps: Task[]) = drainPumpsBounded pumps

    /// Whether this handle's captures were cut short by the bounded post-exit output drain. It is
    /// exactly the bit every capture ORs into its `Truncated`.
    member _.OutputDrainWasBounded = Volatile.Read(&outputDrainSevered) = 1

    /// Whether even the sever could not end a pump inside the window that follows it, so it was handed
    /// to `PostExitDrain.abandon` — observed, never awaited again. A diagnostic: the verb's answer is
    /// identical either way.
    member _.OutputPumpsWereAbandoned = Volatile.Read(&outputPumpsAbandoned) = 1

    /// Await a buffered verb's exit wait (`waitTask`, from the memoized buffered wait) together with its
    /// already-running `pumps`. Fault-aware in both directions:
    ///  - A pump fault kills the tree at once (see `killTreeOnPumpFault`), so the child can't wedge
    ///    `waitTask` by blocking on a pipe its dead pump no longer drains; `waitTask` then completes
    ///    (the killed child is reaped) and the ORIGINAL pump fault surfaces from the pump join.
    ///  - `backend.Wait` (the innermost primitive) is designed never to fault, but `waitWithTimeout`
    ///    layers a timeout race whose `onTimeout` hook calls native kill syscalls, so the composed wait
    ///    CAN throw. `ReapGuard`'s teardown disposes the streams the pumps read, so a pump still
    ///    in-flight when such a fault escaped this scope would race that dispose; awaiting the pumps
    ///    best-effort before re-raising closes that gap.
    /// A pump's own fault on the success path (thrown from the pump join) still propagates. Both joins
    /// go through the bounded drain, so neither can outlast the post-exit output drain — including the
    /// best-effort one on the fault path, which would otherwise turn a held-open inherited pipe into an
    /// unbounded wait on the way OUT of an already-failed verb.
    member _.AwaitBufferedOutcome(waitTask: Task<Outcome>, pumps: Task[]) : Task<Outcome> =
        pumps |> Array.iter killTreeOnPumpFault

        task {
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = waitTask
                do! drainPumpsBounded pumps
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault from `waitTask` before the pumps were awaited must not orphan them — observe
                // them best-effort. Their own fault, if any, is secondary to the error we surface.
                try
                    do! drainPumpsBounded pumps
                with _ ->
                    // best-effort drain; the original fault above is what we report.
                    ()

            match error with
            | Some ex -> return! Task.FromException<Outcome> ex
            | None -> return outcome
        }

    /// An async-disposable that reaps the tree on scope exit — normal OR exceptional. Every terminal
    /// verb opens one with `use` so the container is always torn down, even when a pump faults (e.g.
    /// a throwing line handler) before the verb would otherwise reach its teardown. `Teardown` is
    /// idempotent (the group's release runs once), so the redundant call on `RunningProcess` disposal
    /// is harmless.
    ///
    /// Load-bearing invariant: a verb must await ALL of its OWN pumps before this guard's scope exits,
    /// because `Teardown` disposes the pipe streams the pumps read — a pump still in-flight at teardown
    /// would race a stream `Dispose`. Every verb satisfies this (it awaits the pumps / the session
    /// outcome before returning); keep it that way when editing. A CONCURRENT verb's teardown is the
    /// exception the invariant can't cover: a `StopAsync`/`Dispose` on the same handle reaps as soon as
    /// the shared exit wait resolves, without waiting for an in-flight buffered verb's pumps — so it can
    /// dispose the pipes mid-drain. `disposalCts.Cancel()` below (before `Teardown`) is what the
    /// buffered pumps read via `IsTearingDown` to tell that routine race apart from a genuine read fault.
    member _.ReapGuard() : IAsyncDisposable =
        { new IAsyncDisposable with
            member _.DisposeAsync() =
                // Unblock bounded writers before/while tearing down, so they can't outlive this scope.
                // The general lifecycle token below remains the teardown marker for other pumps.
                cancelBackpressureWriters ()
                disposalCts.Cancel()
                // Clear `runs.active` for a verb that faults before reaching its own `conclude outcome`
                // (e.g. a throwing `OnStdoutLine`/`OnStderrLine` handler, or a faulted exit wait) — a
                // no-op (guarded by `RunTelemetryScope`'s once-guard) on the ordinary success path,
                // where `conclude outcome` already claimed it before this scope exits. Mirrors
                // `DisposeHandleAsync`'s own `markAbandoned()` call below, for the same reason.
                markAbandoned ()
                host.Teardown() }

    /// The once-guard behind `StopAsync`'s soft kill: `true` for the FIRST stop only, so a repeat
    /// `StopAsync` (or one racing a `Dispose` that already released the container) never re-enters the
    /// native graceful kill and only awaits the same shared outcome.
    member _.TryBeginStop() =
        Interlocked.Exchange(&stopStarted, 1) = 0

    /// The whole handle's disposal: release bounded writers, mark teardown, stop the idle watchdog,
    /// clear an abandoned run's in-flight mark, and reap the tree.
    member _.DisposeHandleAsync() : ValueTask =
        cancelBackpressureWriters ()
        disposalCts.Cancel()
        // Stop and release the idle-timeout watchdog (if any); a pump still resetting it races this
        // harmlessly (`Reset` after disposal is a no-op).
        idleTimer |> Option.iter (fun t -> (t :> IDisposable).Dispose())
        // Clear `runs.active` for a handle disposed without ever reaching a terminal verb — a no-op
        // (guarded by `RunTelemetryScope`'s once-guard, `telemetry.Abandon()` racing `Conclude`)
        // when a terminal verb already ran, so a normal verb-then-dispose sequence, or a repeated
        // dispose, cannot double-decrement.
        markAbandoned ()
        host.Teardown()

    /// The honesty `StopAsync` owes when its own window — the grace period plus the bounded post-kill
    /// reap budget — elapses before the tree's conclusion is observed.
    static member StopUnobservedReason =
        "the graceful stop hard-killed the tree, but its conclusion was not observed within the grace period plus the bounded post-kill reap window; a background reaper owns the remaining wait"

    /// Observe any fault on an otherwise fire-and-forget outcome task, so it can never surface as an
    /// unobserved task exception at finalization when nothing awaits it (a streaming-only consumer that
    /// abandons `FinishAsync`, or a readiness probe that races — and never awaits — the memoized
    /// buffered exit wait). A consumer that *does* await (`FinishAsync`/`WaitAnyAsync`/`WaitAllAsync`/
    /// `AwaitBufferedOutcome`) still re-throws it: the attach is purely observational and never
    /// reads/replaces the task's result or exception beyond marking it observed.
    ///
    /// Attach it exactly ONCE, at the moment such a task is created, inside the same guard that ensures
    /// single creation — never per-consumption and never not at all (KB K-084).
    static member ObserveFault(outcomeTask: Task<Outcome>) =
        outcomeTask.ContinueWith(Action<Task<Outcome>>(fun t -> t.Exception |> ignore))
        |> ignore
