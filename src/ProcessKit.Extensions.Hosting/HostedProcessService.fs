namespace ProcessKit.Extensions.Hosting

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open ProcessKit

type internal TrackingRunner(inner: IProcessRunner) =
    let gate = obj ()
    let mutable current: RunningProcess option = None

    let setCurrent value = lock gate (fun () -> current <- value)

    let clearCurrent running =
        lock gate (fun () ->
            match current with
            | Some active when Object.ReferenceEquals(active, running) -> current <- None
            | _ -> ())

    member _.StopActiveAsync(gracePeriod: TimeSpan, cancellationToken: CancellationToken) : Task<Outcome option> =
        let active = lock gate (fun () -> current)

        match active with
        | None -> Task.FromResult None
        | Some running ->
            task {
                try
                    let! outcome = running.StopAsync(gracePeriod).WaitAsync(cancellationToken)
                    return Some outcome
                with :? OperationCanceledException ->
                    return None
            }

    /// Fire-and-forget hard-kill of whatever child is currently active, without waiting for it to
    /// exit — the synchronous half of `Dispose`'s hard teardown (a plain `IDisposable.Dispose()`
    /// cannot await). A no-op when no child is running. `RunningProcess.Kill()` is itself idempotent
    /// and race-safe with a concurrent `StopAsync`/`Dispose`/repeat `Kill()`.
    member _.KillActive() =
        let active = lock gate (fun () -> current)

        match active with
        | Some running -> running.Kill()
        | None -> ()

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            task {
                match! inner.SpawnAsync(command, cancellationToken) with
                | Ok running ->
                    setCurrent (Some running)
                    return Ok running
                | Error error -> return Error error
            }

        member this.CaptureStringAsync(command, cancellationToken) =
            // Reuse `CaptureVerbs.runToCompletion` rather than a hand-rolled spawn->consume->cancel-map
            // loop, so hosted commands get the exact same contract every other runner gets: a `Command`
            // `CancelOn` is linked into the effective token (not silently ignored), and a run cancelled
            // through that effective token always resolves to `Error(ProcessError.Cancelled ...)` — never
            // an `Ok` carrying the killed child's `Signalled`/non-zero result. `start` reuses this
            // runner's own `SpawnAsync` so the active-child tracking (`setCurrent`) still registers the
            // handle `StopActiveAsync`/`KillActive` rely on; `consume` clears it again in `finally`, once
            // `OutputStringAsync` has actually finished, independent of the outcome.
            CaptureVerbs.runToCompletion
                command
                cancellationToken
                (fun () -> (this :> IProcessRunner).SpawnAsync(command, cancellationToken))
                (fun running ->
                    task {
                        try
                            return! running.OutputStringAsync()
                        finally
                            clearCurrent running
                    })

        member this.CaptureBytesAsync(command, cancellationToken) =
            // See `CaptureStringAsync` above for the rationale.
            CaptureVerbs.runToCompletion
                command
                cancellationToken
                (fun () -> (this :> IProcessRunner).SpawnAsync(command, cancellationToken))
                (fun running ->
                    task {
                        try
                            return! running.OutputBytesAsync()
                        finally
                            clearCurrent running
                    })

/// Options for a ProcessKit hosted process registered with `AddProcessKitHostedProcess`.
[<Sealed>]
type HostedProcessOptions() =
    let mutable shutdownGracePeriod = TimeSpan.FromSeconds 2.0

    /// The grace period passed to `RunningProcess.StopAsync` during host shutdown.
    member _.ShutdownGracePeriod
        with get () = shutdownGracePeriod
        and set value =
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero)
            shutdownGracePeriod <- value

/// An `IHostedService` wrapper over `Supervisor` for one long-lived child process.
[<Sealed>]
type HostedProcessService
    internal
    (
        name: string,
        command: Command,
        configureSupervisor: Func<Supervisor, Supervisor>,
        runner: IProcessRunner,
        options: HostedProcessOptions,
        logger: ILogger
    ) =

    // Serializes every state transition below: `disposed`, `supervisionTask` (check + reassign), and
    // the publication of `lastOutcome`/`lastStopOutcome` — so `StartAsync`/`StopAsync`/`Dispose`, called
    // concurrently or repeatedly, resolve to one unambiguous transition apiece rather than racing on the
    // same mutable fields (e.g. two concurrent `StartAsync` calls starting two supervision tasks, or a
    // `StopAsync` awaiting a `supervisionTask` reference a concurrent `StartAsync` has already replaced).
    let gate = obj ()
    let trackingRunner = TrackingRunner runner
    let lifetime = new CancellationTokenSource()
    let mutable stopping = 0
    let mutable disposed = false
    let mutable supervisionTask: Task = Task.CompletedTask
    let mutable lastOutcome: Result<SupervisionOutcome, ProcessError> option = None
    let mutable lastStopOutcome: Outcome option = None
    // Live supervision telemetry, mirrored from `Supervisor.OnRestart`/`OnStormPause` (see
    // `runSupervision` below) so a health check (or any other observer) can read "is it storm-paused
    // right now" / "how many restarts so far" without waiting for the final `SupervisionOutcome` —
    // which, for a long-lived service, may never arrive. Reset at the start of each fresh supervision
    // run (see `StartAsync`), same as `stopping`, so a restarted hosted service does not carry over a
    // prior run's counts.
    let mutable restartCount = 0
    let mutable stormPaused = false

    let logError (ex: exn) =
        logger.LogError(ex, "ProcessKit hosted process {Name} supervision failed.", name)

    let publishOutcome (outcome: Result<SupervisionOutcome, ProcessError>) =
        lock gate (fun () -> lastOutcome <- Some outcome)

    // Cancelling `lifetime` after `Dispose` has already disposed it would throw
    // `ObjectDisposedException` (e.g. a `StopAsync` racing a concurrent `Dispose`) — expected under
    // concurrent teardown, and there is nothing further to cancel once the source is gone.
    let cancelLifetime () =
        try
            lifetime.Cancel()
        with :? ObjectDisposedException ->
            ()

    // Claims the one-and-only teardown transition, shared by `Dispose` and `DisposeAsync`: returns
    // `true` for exactly the first call across any race between the two (or repeats of either),
    // `false` for every call after. Concurrent-safe with `StartAsync`/`StopAsync` via the same `gate`.
    let claimTeardown () =
        lock gate (fun () ->
            if disposed then
                false
            else
                disposed <- true
                true)

    // The synchronous half of hard teardown, shared by `Dispose` and `DisposeAsync`: forbids new
    // starts, hard-kills whatever child is currently active, and cancels the supervision loop (and any
    // in-flight backoff sleep). Only ever runs once `claimTeardown` has granted the transition.
    let beginHardTeardown () =
        // Prevent a restart-policy decision racing this teardown from restarting the child: once
        // the active incarnation's kill below completes its run, `StopWhen` must end supervision
        // rather than schedule another restart.
        Volatile.Write(&stopping, 1)
        // Hard-kill the active child directly (belt-and-suspenders: independent of whether a
        // registration on `lifetime.Token` happens to be live right now) ...
        trackingRunner.KillActive()
        // ... and cancel the supervision loop itself — this also unblocks any in-flight backoff
        // sleep and the `TrackingRunner.CaptureStringAsync` kill-on-cancel registration, so a
        // child that starts running just as teardown runs is still torn down.
        cancelLifetime ()

    // `configureSupervisor` is caller-supplied and may throw, or — since it is a `Func` callable from
    // C# — return null. Either would otherwise be silently swallowed by the catch-all below, leaving
    // the hosted service looking "started" while nothing actually supervises the child. Surface it the
    // same way a real supervision failure is surfaced: as an observable `LastOutcome`, not only a log.
    let buildSupervisor () : Result<Supervisor, ProcessError> =
        try
            let built =
                configureSupervisor.Invoke(Supervisor(command).WithRunner(trackingRunner))

            if obj.ReferenceEquals(built, null) then
                Error(ProcessError.Io $"configureSupervisor for hosted process '{name}' returned null")
            else
                Ok built
        with ex ->
            Error(ProcessError.Io $"configureSupervisor for hosted process '{name}' threw: {ex.Message}")

    let runSupervision () =
        task {
            try
                match buildSupervisor () with
                | Error err ->
                    logger.LogError(
                        "ProcessKit hosted process {Name} failed to configure its supervisor: {Message}",
                        name,
                        err.Message
                    )

                    publishOutcome (Error err)
                | Ok supervisor ->
                    // Combine the host's own stop flag with whatever `StopWhen` predicate the caller
                    // already configured via `configureSupervisor` — `Supervisor.StopWhen` *replaces*
                    // `config.StopWhen`, so calling it again here without reading `CurrentStopWhen`
                    // first would silently drop the caller's predicate.
                    let userStopWhen = supervisor.CurrentStopWhen

                    let supervisor =
                        supervisor.StopWhen(
                            Func<ProcessResult<string>, bool>(fun result ->
                                Volatile.Read(&stopping) <> 0
                                || (match userStopWhen with
                                    | Some predicate -> predicate result
                                    | None -> false))
                        )

                    // Same combine-don't-replace treatment for `OnRestart`/`OnStormPause`: track the
                    // live restart count and storm-pause flag for `RestartCount`/`IsStormPaused`
                    // without dropping whatever handler the caller already installed via
                    // `configureSupervisor`. `OnStormPause` is always immediately followed — in the
                    // same loop iteration, before the next incarnation — by an `OnRestart` call (a
                    // storm pause only ever precedes that restart's own backoff), so clearing
                    // `stormPaused` from the `OnRestart` handler exactly brackets the real pause
                    // window without needing a dedicated "pause ended" event.
                    let userOnRestart = supervisor.CurrentOnRestart
                    let userOnStormPause = supervisor.CurrentOnStormPause

                    let supervisor =
                        supervisor
                            .OnRestart(
                                Action<SupervisorRestartEvent>(fun event ->
                                    lock gate (fun () ->
                                        restartCount <- event.Restart
                                        stormPaused <- false)

                                    match userOnRestart with
                                    | Some handler -> handler event
                                    | None -> ())
                            )
                            .OnStormPause(
                                Action<SupervisorStormPauseEvent>(fun event ->
                                    lock gate (fun () -> stormPaused <- true)

                                    match userOnStormPause with
                                    | Some handler -> handler event
                                    | None -> ())
                            )

                    let! outcome = supervisor.RunAsync(lifetime.Token)
                    publishOutcome outcome
            with
            | :? OperationCanceledException ->
                // Host shutdown cancelled the background supervision wait; the child stop path has
                // already observed the host token, so there is no further recovery to perform here.
                ()
            | ex ->
                // An exception here is not a `configureSupervisor` failure (that is already handled
                // above, via `buildSupervisor`) — it escaped the already-built `Supervisor.RunAsync`
                // itself (e.g. the injected `IProcessRunner` threw instead of returning a `Result`
                // error). Publish it the same way any other supervision failure is published: as an
                // observable `LastOutcome`, not only a log — otherwise a caller reading `LastOutcome`
                // sees `None` and believes supervision is still active when it has in fact crashed.
                logError ex
                publishOutcome (Error(ProcessError.Io $"hosted process '{name}' supervision failed: {ex.Message}"))
        }
        :> Task

    new
        (
            name: string,
            command: Command,
            configureSupervisor: Func<Supervisor, Supervisor>,
            runner: IProcessRunner,
            options: HostedProcessOptions
        ) =
        new HostedProcessService(name, command, configureSupervisor, runner, options, NullLogger.Instance)

    /// The logical service name supplied during registration.
    member _.Name = name

    /// The last result returned by `Supervisor.RunAsync`, when supervision has ended.
    member _.LastOutcome = lock gate (fun () -> lastOutcome)

    /// The outcome returned by the child process stop operation during host shutdown, when one ran.
    member _.LastStopOutcome = lock gate (fun () -> lastStopOutcome)

    /// Whether the background supervision loop is currently running: `true` from a successful
    /// `StartAsync` until supervision ends (naturally, via `StopAsync`, or via `Dispose`/`DisposeAsync`)
    /// — i.e. `LastOutcome` is still `None` and teardown has not begun. `false` before the first
    /// `StartAsync`, once supervision has ended (`LastOutcome` is `Some`), or once `Dispose`/
    /// `DisposeAsync` has claimed teardown (even if the loop has not quite finished unwinding yet — see
    /// `IDisposable.Dispose`). The primary "is it alive" signal for a health check.
    member _.IsSupervisionActive =
        lock gate (fun () -> not disposed && not supervisionTask.IsCompleted)

    /// How many restarts the current (or most recent) supervision run has made so far, live —
    /// mirrors `SupervisionOutcome.Restarts` but updates as each restart happens (via
    /// `Supervisor.OnRestart`), rather than only once supervision ends. Reset to `0` at the start of
    /// each fresh `StartAsync`.
    member _.RestartCount = lock gate (fun () -> restartCount)

    /// Whether the supervised child is currently paused by the failure-storm guard
    /// (`Supervisor.StormPause`), live — set from `Supervisor.OnStormPause` and cleared once the
    /// paused restart actually happens (`Supervisor.OnRestart`). Always `false` when `StormPause` was
    /// never configured. A reasonable "Degraded" signal for a health check: supervision is still
    /// alive, but restarts are being throttled because failures are clustering.
    member _.IsStormPaused = lock gate (fun () -> stormPaused)

    interface IHostedService with
        /// Starts (or restarts, once a prior supervision has ended) the background supervision loop.
        /// Idempotent while supervision is already running — a concurrent or repeated call never starts
        /// a second supervision task. A `cancellationToken` that is already cancelled honors the
        /// standard `IHostedService` cancellation contract: this returns a cancelled task without
        /// starting supervision or spawning a child. A no-op once `Dispose` has run — `Dispose`
        /// permanently forbids new starts.
        member _.StartAsync(cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromCanceled cancellationToken
            else
                lock gate (fun () ->
                    if not disposed && supervisionTask.IsCompleted then
                        // A restart after a prior `StopAsync` must not inherit that stop's request —
                        // otherwise the fresh supervision loop's `StopWhen` predicate would be true from
                        // its very first run and it would immediately end supervision again.
                        Interlocked.Exchange(&stopping, 0) |> ignore
                        // Likewise, a fresh supervision run must not carry over a prior run's restart
                        // count / storm-pause flag — this is a new lifetime, not a continuation.
                        restartCount <- 0
                        stormPaused <- false
                        supervisionTask <- runSupervision ()

                    Task.CompletedTask)

        /// Requests a graceful stop: signals the restart policy to end supervision after the active
        /// child concludes, stops that active child (honouring `HostedProcessOptions.ShutdownGracePeriod`),
        /// then awaits the supervision loop's own end. Race-safe with a concurrent `Dispose` — cancelling
        /// an already-disposed `lifetime` is expected and swallowed, and the supervision task awaited
        /// here is snapshotted once so a concurrent `StartAsync` reassigning it cannot be raced.
        member _.StopAsync(cancellationToken) =
            task {
                Volatile.Write(&stopping, 1)

                let! stopped = trackingRunner.StopActiveAsync(options.ShutdownGracePeriod, cancellationToken)
                lock gate (fun () -> lastStopOutcome <- stopped)

                // Only cancel `lifetime` here when there was no active child to gracefully stop above
                // (`stopped = None`): with an active child, `stopping` (set just above) already makes
                // the combined `StopWhen` end the supervision loop on THAT child's own honest capture
                // result once it returns (`CaptureVerbs.runToCompletion`'s post-consume check watches
                // this very `lifetime.Token`) — cancelling it here as well would race that still-finishing
                // capture and risk overwriting a legitimate graceful outcome with a bare `Cancelled`
                // error, discarding the real exit/stdout/stderr the caller expects via `LastOutcome`.
                // With no active child (e.g. supervision is mid-backoff-sleep, or hasn't spawned yet),
                // there is nothing else to unblock the loop, so cancel `lifetime` to interrupt any
                // in-flight backoff sleep and end supervision promptly.
                if stopped.IsNone then
                    cancelLifetime ()

                let toAwait = lock gate (fun () -> supervisionTask)

                try
                    do! toAwait.WaitAsync(cancellationToken)
                with :? OperationCanceledException ->
                    // The host stop token expired; the active child stop path already used the same
                    // token, so returning lets the host enforce its shutdown deadline.
                    ()
            }
            :> Task

    interface IDisposable with
        /// Idempotent hard teardown: permanently forbids new starts, cancels the supervision loop (and
        /// any in-flight backoff sleep), hard-kills whatever child is currently active, and releases the
        /// lifetime token source — all without waiting for a graceful shutdown or for the supervision
        /// task to actually finish (a plain `Dispose()` cannot await, so this only *initiates* teardown;
        /// it does not itself guarantee the child has already exited by the time it returns). Safe to
        /// call repeatedly, and safe to race against `StartAsync`/`StopAsync`: the `disposed` flag and
        /// `gate` lock ensure only the first `Dispose`/`DisposeAsync` call across either performs the
        /// teardown, and every access to `lifetime` elsewhere tolerates it being cancelled/disposed
        /// underneath a concurrent caller. Prefer `DisposeAsync` (e.g. `await using`) when the caller
        /// needs the stronger guarantee that supervision has actually finished before teardown returns.
        member _.Dispose() =
            if claimTeardown () then
                beginHardTeardown ()
                lifetime.Dispose()

    interface IAsyncDisposable with
        /// Deterministic counterpart to `Dispose()`: performs the same idempotent hard teardown
        /// (forbid new starts, hard-kill the active child, cancel the supervision loop) — but,
        /// unlike `Dispose()`, unconditionally awaits the supervision loop itself to actually finish
        /// before returning, so by the time this completes, there is genuinely no live background
        /// supervision task. This holds whether or not *this* call wins the `claimTeardown` race
        /// against a concurrent/prior `Dispose()`/`DisposeAsync()`: `supervisionTask` is stable once
        /// `disposed` is `true` (`StartAsync` never reassigns it after that), so every caller — the one
        /// that claimed the teardown transition and every one that lost the race — reads the same task
        /// and awaits it to completion. Only the call that actually claims the transition performs the
        /// teardown itself (hard-kill + cancel + `lifetime.Dispose()`), so teardown remains exactly
        /// once; every other call still gets the documented "supervision has finished" guarantee.
        member _.DisposeAsync() : ValueTask =
            ValueTask(
                task {
                    let claimedTeardown = claimTeardown ()

                    if claimedTeardown then
                        beginHardTeardown ()

                    // Snapshotted under `gate` after `disposed` is (by now, one way or another)
                    // already `true`, so no concurrent `StartAsync` can reassign it out from under
                    // this await (same pattern as `StopAsync`). `runSupervision` itself never lets an
                    // exception or cancellation escape its own task (see its `with` handlers above),
                    // so this await cannot fault or be cancelled.
                    let toAwait = lock gate (fun () -> supervisionTask)
                    do! toAwait

                    if claimedTeardown then
                        lifetime.Dispose()
                }
            )
