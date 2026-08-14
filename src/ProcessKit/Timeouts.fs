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

    /// Race a process `wait` against a total deadline (`timeout`) and/or a resettable idle deadline
    /// (`idle`, armed here as the exit wait begins), using an externally supplied `timeoutCts` to arm
    /// the total-timeout timer. With neither deadline armed, just returns `wait`. Otherwise: if the
    /// wait wins, cancel `timeoutCts` and return its outcome; on whichever deadline fires first, cancel
    /// `timeoutCts` immediately (so the losing `Task.Delay` never outlives the race, including through
    /// a grace-period-bounded kill), pass its configured duration to `onTimeout` (the kill — shared by
    /// both, so there is never a double kill), reap the child within `postKillBudget`, log the cause
    /// that fired (total vs idle), and report `TimedOut`.
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
        (timeout: TimeSpan option)
        (idle: IdleTimer option)
        (onTimeout: TimeSpan -> Task)
        (wait: Task<Outcome>)
        : Task<Outcome> =
        let total =
            match timeout with
            | Some t when isArmable t -> Some t
            | _ -> None

        match total, idle with
        | None, None -> wait
        | _ ->
            task {
                let waitBase = wait :> Task

                // Each deadline task is paired with the log to emit if it is the one that fired, so a
                // single race can carry both the total-timeout and the idle deadline yet still name the
                // right cause.
                let deadlines: (Task * TimeSpan * (unit -> unit)) list =
                    [ match total with
                      | Some t ->
                          yield (Task.Delay(t, timeoutCts.Token), t, (fun () -> Log.timeout logger program t runId))
                      | None -> ()

                      match idle with
                      | Some timer ->
                          // Start the idle countdown now (the exit wait has begun); each stdout/stderr
                          // read resets it via the activity-tracking stream wrapper.
                          timer.Arm()
                          yield (timer.Expired, timer.Idle, (fun () -> Log.idleTimeout logger program timer.Idle runId))
                      | None -> () ]

                let deadlineTasks = deadlines |> List.map (fun (deadline, _, _) -> deadline)
                let! winner = Task.WhenAny(waitBase :: deadlineTasks)

                if obj.ReferenceEquals(winner, waitBase) then
                    // The child exited first: cancel the total-timeout timer. The idle timer is stopped
                    // when the handle is disposed — a late fire is a harmless no-op on a decided race.
                    timeoutCts.Cancel()
                    return! wait
                else
                    // A deadline won: carry its configured duration into teardown/result construction,
                    // kill (once), reap the child, then log whichever deadline fired.
                    let _, configuredDuration, emit =
                        deadlines
                        |> List.find (fun (deadline, _, _) -> obj.ReferenceEquals(deadline, winner))

                    // Cancel the total-timeout timer immediately once the winner is decided — before
                    // the (possibly grace-period-bounded) kill — so the losing Task.Delay never outlives
                    // the race resolution, including on the graceful-teardown path.
                    timeoutCts.Cancel()
                    do! onTimeout configuredDuration
                    // Reap the child within the bounded post-kill window. The kill above has already
                    // been delivered and this race has already decided `TimedOut`, so a reap that does
                    // not land inside the budget changes nothing about the answer — it is handed to the
                    // `PostKillReap` ledger (which observes its eventual fault and stays its only
                    // owner) rather than blocking the timeout it was meant to enforce. A wait that
                    // completes in time still surfaces its fault here, exactly as before.
                    let! _ = PostKillReap.awaitWithin postKillBudget wait

                    emit ()

                    return Outcome.TimedOut
            }

    /// Race a process `wait` against a total deadline (`timeout`) and/or a resettable idle deadline
    /// (`idle`). Owns the total-timeout `CancellationTokenSource` for the duration of the race and
    /// disposes it on the way out. See `raceTimeoutWithCts` for the shared behaviour.
    /// One home for the subtle CTS-cancel + reference-equality-winner logic shared by the run verbs and
    /// the group runner. (Negatives are rejected by the builder; `isArmable` screens the total defensively,
    /// so `Task.Delay` here can never throw synchronously; the idle timer was screened at construction.)
    let raceTimeout
        (logger: ILogger option)
        (program: string)
        (runId: string)
        (timeout: TimeSpan option)
        (idle: IdleTimer option)
        (onTimeout: TimeSpan -> Task)
        (wait: Task<Outcome>)
        : Task<Outcome> =
        task {
            use timeoutCts = new CancellationTokenSource()

            return!
                raceTimeoutWithCts timeoutCts (PostKillReap.budget ()) logger program runId timeout idle onTimeout wait
        }
