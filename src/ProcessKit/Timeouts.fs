namespace ProcessKit

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Bounds for arming an OS timer from a user `TimeSpan`. `Task.Delay`,
/// `CancellationTokenSource.CancelAfter`, and `System.Threading.Timer` all reject a delay outside
/// `[-1ms, Int32.MaxValue ms]` — feeding them an out-of-range span throws synchronously, which would
/// break the honest-result contract and orphan the in-flight output pumps. ProcessKit rejects a
/// negative timeout when it is configured and treats an over-long one as "no timeout".
module internal Timeouts =

    /// The largest delay the BCL timers accept (`Int32.MaxValue` ms ≈ 24.8 days).
    let maxArmable = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// True when `duration` can be armed on a BCL timer (non-negative and within range). A larger
    /// positive span is "effectively never", so the run proceeds as if no timeout were set.
    let isArmable (duration: TimeSpan) =
        duration >= TimeSpan.Zero && duration <= maxArmable

    /// Clamp `duration` into the armable range so a BCL timer can be constructed without throwing:
    /// a negative span becomes zero (the timer fires immediately); an over-long one is capped at the
    /// max (~24.8 days). Used where a deadline must always be armed (e.g. a readiness probe).
    let clampArmable (duration: TimeSpan) =
        if duration < TimeSpan.Zero then TimeSpan.Zero
        elif duration > maxArmable then maxArmable
        else duration

    /// The total-run deadline (`Command.Timeout`) as the exit-wait race has to see it: the duration the
    /// caller CONFIGURED — the one every result, error, and log reports — together with the delay this
    /// particular wait actually arms, which is only what is LEFT of that budget, and the settle window
    /// that delay is topped up to when the wait is created too late to have any of it left.
    ///
    /// `Configured` and `Armed` differ because the budget is anchored at **spawn**, while the exit wait
    /// it bounds is created lazily by the first consumer (`RunningProcess.ensureBufferedWait`, or a
    /// streaming session's own `waitWithTimeout`) — which on a live `StartAsync` handle can be long
    /// after the child started. Arming the whole configured duration there would silently grant the
    /// child `delay + Timeout` to run in; arming the remainder gives every consumer of one run the same
    /// single absolute deadline, while `Configured` keeps `ProcessError.Timeout`, `ProcessResult`, and
    /// `Log.timeout` reporting the deadline that was set rather than whatever slice of it was left when
    /// someone got around to waiting.
    type internal TotalDeadline =
        {
            /// The duration `Command.Timeout` was set to — what a fired deadline always reports.
            Configured: TimeSpan
            /// The delay this wait arms: what remains of `Configured`, measured from spawn. Always
            /// armable, and never negative (see `totalDeadline`).
            Armed: TimeSpan
            /// How much longer the child's own exit is still given to become observable once `Armed`
            /// has elapsed, before the deadline may fire (`exitSettleWindow`). Zero unless `Armed` was
            /// shorter than that window — i.e. only for a wait created so late that it could not have
            /// seen an already-published exit in the time it was armed for. See `exitSettleWindow`.
            Settle: TimeSpan
        }

    /// How long an exit wait is given, in total, to surface an exit that has ALREADY happened before a
    /// fired deadline is allowed to answer `TimedOut` and kill the tree.
    ///
    /// This is not extra budget for the run. A child that finished on its own well inside its deadline,
    /// on a handle whose first consuming verb only arrives afterwards, must still report its real
    /// outcome: answering `TimedOut` for a run that never exceeded anything would fabricate a failure
    /// out of the caller's scheduling. That exit is never visible synchronously — `host.Wait()` resolves
    /// through a pidfd/kqueue readiness callback or a `RegisterWaitForSingleObject` hop onto the thread
    /// pool — so a wait armed for a mere sliver of leftover budget could lose the race to its own
    /// `Task.Delay` just as surely as one armed for nothing at all. The window is therefore applied to
    /// BOTH: what matters is how long the wait has been in flight when the deadline fires, not whether
    /// the remainder it was armed with happened to be zero or five milliseconds.
    ///
    /// The plumbing it covers answers in well under a millisecond, so a quarter second is generous
    /// headroom even on a loaded CI runner, and it stays bounded — a child that genuinely IS still
    /// running past its deadline pays this fixed, negligible delay before its (already overdue) kill
    /// instead of a whole freshly counted timeout. It is also capped by the configured duration itself
    /// (see `totalDeadline`), so it can never defer a kill past what the caller asked for.
    let exitSettleWindow = TimeSpan.FromMilliseconds 250.0

    /// Build the total deadline for an exit wait created `elapsedSinceSpawn` after the child started.
    ///
    /// `isArmable` screens the CONFIGURED value alone, so the builder's contract is decided by what the
    /// caller set and cannot be changed by how late the wait is created: a negative timeout is already
    /// rejected at the builder, and one longer than a BCL timer can hold (~24.8 days) is "effectively
    /// never", i.e. no timeout at all. What is left of an armable budget is what gets armed, and a
    /// budget already spent arms nothing.
    ///
    /// `Settle` then tops that up so every wait has been in flight for the same `exitSettleWindow`
    /// before its deadline may fire — one rule for a remainder of zero and for a remainder of five
    /// milliseconds alike (see `exitSettleWindow`) — and never a moment longer than needed: a wait that
    /// was armed for at least the window already had it. The window is capped by the configured
    /// duration, so the deadline fires at `Configured` after spawn for a prompt consumer and at most
    /// `min(exitSettleWindow, Configured)` after a late one, never later than the caller's own timeout
    /// would have put it. A zero-length budget is the degenerate case of that cap: it fires at once,
    /// because no exit can have happened inside it that a settle window could reveal.
    ///
    /// The arithmetic only ever subtracts a smaller span from a larger one inside the armable range, so
    /// it cannot overflow, and the armed delay is clamped anyway — a `Task.Delay`/timer here must never
    /// throw synchronously and orphan the in-flight pumps.
    let totalDeadline (configured: TimeSpan option) (elapsedSinceSpawn: TimeSpan) : TotalDeadline option =
        match configured with
        | Some total when isArmable total ->
            // A monotonic clock cannot run backwards, but a synthetic host can hand us a spawn stamp
            // from the future; treat that as "no time has been spent" rather than widening the budget.
            let spent =
                if elapsedSinceSpawn > TimeSpan.Zero then
                    elapsedSinceSpawn
                else
                    TimeSpan.Zero

            let armed =
                if spent < total then
                    clampArmable (total - spent)
                else
                    TimeSpan.Zero

            // Never settle for longer than the caller configured: for a `Timeout` shorter than the
            // window, the window IS the timeout.
            let settleWindow = min exitSettleWindow total

            Some
                { Configured = total
                  Armed = armed
                  Settle =
                    if armed < settleWindow then
                        settleWindow - armed
                    else
                        TimeSpan.Zero }
        | _ -> None

    /// A resettable "no output" watchdog behind `Command.IdleTimeout`. `Expired` completes once the
    /// idle window elapses with no intervening `Reset`; each stdout/stderr read `Reset`s it (through the
    /// `ActivityStream` wrapper), pushing the deadline out. Created stopped: `Arm` starts the countdown
    /// when the exit wait begins, so the window is measured from when output is actually being
    /// consumed, not from an earlier construction. The underlying timer is disposed with the run
    /// (`IDisposable`); a `Reset` after disposal — or after the deadline already fired — is a harmless
    /// no-op. `idle` is assumed already screened by `isArmable` at construction.
    type IdleTimer(idle: TimeSpan) =
        // RunContinuationsAsynchronously: completing the TCS from the timer's thread-pool callback must
        // not run the race's continuation inline on the timer thread.
        let expiry =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        // Created in the stopped state (infinite due time); `Arm`/`Reset` schedule the one-shot fire.
        let timer = new Timer(TimerCallback(fun _ -> expiry.TrySetResult() |> ignore))

        /// The configured idle window (carried into the timeout log).
        member _.Idle = idle

        /// Completes once `idle` elapses with no `Reset`.
        member _.Expired: Task = expiry.Task

        /// (Re)start the countdown from now. Safe from any thread and after expiry/disposal (a no-op
        /// then); on the hot per-read path it is a single `Timer.Change`.
        member _.Reset() =
            try
                timer.Change(idle, Timeout.InfiniteTimeSpan) |> ignore
            with :? ObjectDisposedException ->
                // The run concluded and the timer was disposed; the deadline no longer matters.
                ()

        /// Start the countdown when the exit wait begins (an alias of `Reset`, named for intent).
        member this.Arm() = this.Reset()

        interface IDisposable with
            member _.Dispose() = timer.Dispose()

    /// A transparent read-only wrapper that resets an `IdleTimer` on every non-empty read from `inner`
    /// — how `Command.IdleTimeout` sees stdout/stderr activity at byte granularity, uniformly across
    /// every pump (line splitting, byte drains, raw captures). Everything else forwards straight to
    /// `inner`; the wrapper owns no resources of its own (the underlying pipe stream is disposed by the
    /// run's teardown).
    type ActivityStream(inner: Stream, onActivity: unit -> unit) =
        inherit Stream()

        // Reset the idle deadline whenever a read actually produced bytes; a zero-length read (EOF, or
        // a spurious empty completion) is not activity. Returns the count so it threads through a read.
        let note (n: int) =
            if n > 0 then
                onActivity ()

            n

        override _.CanRead = inner.CanRead
        override _.CanSeek = inner.CanSeek
        override _.CanWrite = inner.CanWrite
        override _.Length = inner.Length

        override _.Position
            with get () = inner.Position
            and set value = inner.Position <- value

        override _.Flush() = inner.Flush()
        override _.Seek(offset, origin) = inner.Seek(offset, origin)
        override _.SetLength value = inner.SetLength value
        override _.Write(buffer, offset, count) = inner.Write(buffer, offset, count)

        override _.Read(buffer, offset, count) =
            note (inner.Read(buffer, offset, count))

        override _.ReadAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) =
            task {
                let! n = inner.ReadAsync(buffer, offset, count, cancellationToken)
                return note n
            }

        override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
            ValueTask<int>(
                task {
                    let! n = inner.ReadAsync(buffer, cancellationToken)
                    return note n
                }
            )

        override _.Dispose(disposing: bool) =
            if disposing then
                inner.Dispose()

    /// Race a process `wait` against a total deadline (`total`) and/or a resettable idle deadline
    /// (`idle`, armed here as the exit wait begins), using an externally supplied `timeoutCts` to arm
    /// the total-timeout timer. With neither deadline armed, just returns `wait`. Otherwise: if the
    /// wait wins, cancel `timeoutCts` and return its outcome; on whichever deadline fires first, cancel
    /// `timeoutCts` immediately (so the losing `Task.Delay` never outlives the race, including through
    /// a grace-period-bounded kill), pass its configured duration to `onTimeout` (the kill — shared by
    /// both, so there is never a double kill), reap the child within `postKillBudget`, log the cause
    /// that fired (total vs idle), and report `TimedOut`.
    ///
    /// The total deadline arrives already resolved (`TotalDeadline`, built by `totalDeadline`): the
    /// delay armed here is what is LEFT of the spawn-anchored budget, while the duration handed to
    /// `onTimeout` and to the log is the one the caller CONFIGURED. When that remainder was shorter
    /// than `exitSettleWindow`, the deadline does not answer the moment it wins — it first gives the
    /// exit wait the rest of that window (`TotalDeadline.Settle`) to surface an exit the child had
    /// ALREADY made, and reports that real outcome if one arrives. So every wait, however late it was
    /// created, has been in flight for the same bounded window before a `TimedOut` can be fabricated
    /// out of the caller's scheduling. The idle deadline needs no such top-up (its window is measured
    /// from this very wait, so it has always been in flight for all of it) and is deliberately
    /// unaffected in the other direction too — it is an inactivity window, so it starts when output
    /// actually begins to be consumed (`timer.Arm()` below), never backdated to spawn.
    ///
    /// The post-kill reap is BOUNDED by `postKillBudget` (`PostKillReap`): the kill has already been
    /// delivered and the disposition is already `TimedOut`, so a child wedged in uninterruptible sleep
    /// — which defers even SIGKILL — must not hang the very timeout that was supposed to bound it. If
    /// the reap does not land inside the budget, the single remaining right to await it passes to the
    /// `PostKillReap` ledger (fault-observed, no second waiter) and `TimedOut` is reported anyway.
    ///
    /// Split out from `raceTimeout` as a test seam: it lets `ProcessKit.Tests` (via
    /// `InternalsVisibleTo`) observe the total-timeout CTS directly instead of reflecting into the
    /// `task {}` state machine's private fields, and drive the post-kill budget without waiting out the
    /// production one. Ownership/disposal of `timeoutCts` stays with the caller — this function only
    /// cancels it, never disposes it.
    let raceTimeoutWithCts
        (timeoutCts: CancellationTokenSource)
        (postKillBudget: TimeSpan)
        (logger: ILogger option)
        (program: string)
        (runId: string)
        (total: TotalDeadline option)
        (idle: IdleTimer option)
        (onTimeout: TimeSpan -> Task)
        (wait: Task<Outcome>)
        : Task<Outcome> =
        match total, idle with
        | None, None -> wait
        | _ ->
            task {
                let waitBase = wait :> Task

                // Each deadline task is paired with the log to emit if it is the one that fired, so a
                // single race can carry both the total-timeout and the idle deadline yet still name the
                // right cause — and with the settle window it must honour before it may answer (zero
                // for the idle deadline, which is never armed for less than its own window).
                let deadlines: (Task * TimeSpan * TimeSpan * (unit -> unit)) list =
                    [ match total with
                      | Some deadline ->
                          // Arm the REMAINDER of the spawn-anchored budget, but carry the CONFIGURED
                          // duration into the kill hook and the log — a late first consumer must not be
                          // told the deadline was the leftover slice it happened to arrive with.
                          yield
                              (Task.Delay(clampArmable deadline.Armed, timeoutCts.Token),
                               deadline.Configured,
                               deadline.Settle,
                               (fun () -> Log.timeout logger program deadline.Configured runId))
                      | None -> ()

                      match idle with
                      | Some timer ->
                          // Start the idle countdown now (the exit wait has begun); each stdout/stderr
                          // read resets it via the activity-tracking stream wrapper.
                          timer.Arm()

                          yield
                              (timer.Expired,
                               timer.Idle,
                               TimeSpan.Zero,
                               (fun () -> Log.idleTimeout logger program timer.Idle runId))
                      | None -> () ]

                let deadlineTasks = deadlines |> List.map (fun (deadline, _, _, _) -> deadline)
                let! winner = Task.WhenAny(waitBase :: deadlineTasks)

                if obj.ReferenceEquals(winner, waitBase) then
                    // The child exited first: cancel the total-timeout timer. The idle timer is stopped
                    // when the handle is disposed — a late fire is a harmless no-op on a decided race.
                    timeoutCts.Cancel()
                    return! wait
                else
                    // A deadline won: carry its configured duration into teardown/result construction,
                    // kill (once), reap the child, then log whichever deadline fired.
                    let _, configuredDuration, settle, emit =
                        deadlines
                        |> List.find (fun (deadline, _, _, _) -> obj.ReferenceEquals(deadline, winner))

                    // ... unless this wait was created too late to have been armed for the settle window
                    // (`totalDeadline`): the exit wait then gets the rest of that window before anything
                    // is fabricated, because an exit the child had ALREADY made cannot surface
                    // synchronously — it arrives on a pidfd/kqueue callback or a thread-pool hop. If it
                    // lands here, it is the honest answer to report: nothing exceeded anything, and there
                    // is no tree left to kill. The prompt-consumer path arms nothing extra (`Settle` is
                    // zero) and is untouched, so no run pays this delay for someone else's late collect.
                    let! exitObserved =
                        if settle > TimeSpan.Zero then
                            task {
                                let! _ = Task.WhenAny(waitBase, Task.Delay(settle, timeoutCts.Token))
                                return waitBase.IsCompleted
                            }
                        else
                            Task.FromResult false

                    if exitObserved then
                        // The run concluded on its own after all; the deadline it outran by a hair of
                        // the caller's scheduling has nothing to report.
                        timeoutCts.Cancel()
                        return! wait
                    else
                        // Cancel the total-timeout timer immediately once the winner is decided — before
                        // the (possibly grace-period-bounded) kill — so the losing Task.Delay never
                        // outlives the race resolution, including on the graceful-teardown path.
                        timeoutCts.Cancel()
                        do! onTimeout configuredDuration
                        // Reap the child within the bounded post-kill window. The kill above has already
                        // been delivered and this race has already decided `TimedOut`, so a reap that
                        // does not land inside the budget changes nothing about the answer — it is
                        // handed to the `PostKillReap` ledger (which observes its eventual fault and
                        // stays its only owner) rather than blocking the timeout it was meant to
                        // enforce. A wait that completes in time still surfaces its fault here, exactly
                        // as before.
                        let! _ = PostKillReap.awaitWithin postKillBudget wait

                        emit ()

                        return Outcome.TimedOut
            }

    /// Race a process `wait` against a total deadline (`total`) and/or a resettable idle deadline
    /// (`idle`). Owns the total-timeout `CancellationTokenSource` for the duration of the race and
    /// disposes it on the way out. See `raceTimeoutWithCts` for the shared behaviour.
    /// One home for the subtle CTS-cancel + reference-equality-winner logic shared by the run verbs and
    /// the group runner. (Negatives are rejected by the builder; `totalDeadline` screens the configured
    /// duration and `clampArmable` re-screens the armed one, so `Task.Delay` here can never throw
    /// synchronously; the idle timer was screened at construction.)
    let raceTimeout
        (logger: ILogger option)
        (program: string)
        (runId: string)
        (total: TotalDeadline option)
        (idle: IdleTimer option)
        (onTimeout: TimeSpan -> Task)
        (wait: Task<Outcome>)
        : Task<Outcome> =
        task {
            use timeoutCts = new CancellationTokenSource()

            return!
                raceTimeoutWithCts timeoutCts (PostKillReap.budget ()) logger program runId total idle onTimeout wait
        }
