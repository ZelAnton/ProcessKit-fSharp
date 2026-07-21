namespace ProcessKit.Extensions.Hosting

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open ProcessKit

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
///
/// **Why a `SupervisionSession`, not a hand-rolled active-child tracker.** Supervision is driven through
/// `Supervisor.StartAsync` → `SupervisionSession`, the interactive supervision primitive, rather than
/// `Supervisor.RunAsync` plus a bespoke tracking runner. The session is the single, already-correct owner
/// of "which incarnation is live right now": it publishes each freshly-spawned child and clears it the
/// instant that incarnation ends (naturally, between incarnations during a backoff/storm pause, or once
/// supervision concludes), and its graceful stop interrupts an in-flight backoff sleep so a stop taken
/// *between* incarnations ends supervision promptly instead of waiting the delay out and launching another
/// child. This service delegates its own graceful stop, live telemetry, and hard teardown to that one
/// source of truth (`SupervisionSession.StopActiveAsync`/`Status`/`Completion`), so there is no second,
/// divergent copy of the incarnation-tracking contract to drift out of step with the loop.
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

    // Serializes every state transition below: `disposed`, `supervisionTask`/`sessionReady`/`currentSession`
    // (check + reassign), and the publication of `lastOutcome`/`lastStopOutcome` — so `StartAsync`/
    // `StopAsync`/`Dispose`, called concurrently or repeatedly, resolve to one unambiguous transition apiece
    // rather than racing on the same mutable fields (e.g. two concurrent `StartAsync` calls starting two
    // supervision tasks, or a `StopAsync` awaiting a `supervisionTask` reference a concurrent `StartAsync`
    // has already replaced). Distinct from — and never held across — the `SupervisionSession`'s own gate
    // (`Status` reads take that lock, off this one), so the two locks never nest.
    let gate = obj ()
    // Cancelled by hard teardown (`Dispose`/`DisposeAsync`). Cancelling it ends the session's supervision
    // loop and hard-kills the live incarnation through the session's own per-incarnation kill-on-cancel
    // registration (`effectiveToken.Register(running.Kill)`), so a plain `Dispose()` initiates a full hard
    // teardown even though it cannot await the loop's unwind.
    let lifetime = new CancellationTokenSource()
    let mutable disposed = false
    let mutable supervisionTask: Task = Task.CompletedTask
    // Resolves the instant the background wrapper has created this run's `SupervisionSession` (or decided it
    // never will — a `configureSupervisor` failure yields `None`). A `StopAsync` racing session creation
    // awaits this rather than reading a possibly-not-yet-published `currentSession`, so it can never miss a
    // child that is about to spawn and hard-cancel it instead of stopping it gracefully.
    let mutable sessionReady: Task<SupervisionSession option> = Task.FromResult None
    // The current run's session, published by the wrapper for synchronous, non-blocking telemetry reads
    // (`RestartCount`/`IsStormPaused`). `None` before the first `StartAsync`, in the brief window between a
    // fresh `StartAsync` and the wrapper publishing the session, and after a `configureSupervisor` failure.
    let mutable currentSession: SupervisionSession option = None
    let mutable lastOutcome: Result<SupervisionOutcome, ProcessError> option = None
    let mutable lastStopOutcome: Outcome option = None

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

    // Hard teardown, shared by `Dispose` and `DisposeAsync`: cancel `lifetime` so the session's supervision
    // loop observes the cancelled token and ends without starting another incarnation, and its
    // per-incarnation kill-on-cancel registration hard-kills whatever child is currently live. Only ever
    // runs once `claimTeardown` has granted the transition. `disposed` (set by `claimTeardown`) already
    // forbids new starts, so no restart-policy decision racing this teardown can spawn a fresh child.
    let beginHardTeardown () = cancelLifetime ()

    // `configureSupervisor` is caller-supplied and may throw, or — since it is a `Func` callable from
    // C# — return null. Either would otherwise be silently swallowed by the catch-all below, leaving
    // the hosted service looking "started" while nothing actually supervises the child. Surface it the
    // same way a real supervision failure is surfaced: as an observable `LastOutcome`, not only a log.
    let buildSupervisor () : Result<Supervisor, ProcessError> =
        try
            let built = configureSupervisor.Invoke(Supervisor(command).WithRunner runner)

            if obj.ReferenceEquals(built, null) then
                Error(ProcessError.Io $"configureSupervisor for hosted process '{name}' returned null")
            else
                Ok built
        with ex ->
            Error(ProcessError.Io $"configureSupervisor for hosted process '{name}' threw: {ex.Message}")

    // The background supervision run, published by `StartAsync` under `gate`. `signal` is this run's own
    // readiness `TaskCompletionSource` (passed in, not read from the field, so a concurrent restart's fresh
    // signal can never be completed by a stale run).
    let runSupervision (signal: TaskCompletionSource<SupervisionSession option>) : Task =
        task {
            // Force the async boundary *before* any real work, so the whole synchronous supervision prefix
            // runs off the caller's thread — i.e. off the `gate` that `StartAsync` holds while it publishes
            // this task. Without this yield, the caller-supplied `configureSupervisor` (inside
            // `buildSupervisor`) and the native first-incarnation spawn (reached synchronously inside the
            // session's loop start) would execute inline under that lock: they would block every `gate`
            // reader (`IsSupervisionActive`/`RestartCount`/`IsStormPaused`/`LastOutcome`, and transitively
            // `HostedProcessHealthCheck`), run caller code under the service's own lock (a deadlock risk if
            // the callback touches this service from another thread), and make `StartAsync` slow against the
            // spirit of `IHostedService`. Publishing the `Task` handle stays synchronous under the lock (so
            // `StopAsync`/`Dispose`/`DisposeAsync` snapshot exactly this task); only the *run* is deferred
            // past this point.
            do! Task.Yield()

            try
                match buildSupervisor () with
                | Error err ->
                    logger.LogError(
                        "ProcessKit hosted process {Name} failed to configure its supervisor: {Message}",
                        name,
                        err.Message
                    )

                    signal.TrySetResult None |> ignore
                    publishOutcome (Error err)
                | Ok supervisor ->
                    // Start the interactive session and publish it — both as the synchronous telemetry
                    // snapshot (`currentSession`) and as the awaited readiness signal (`sessionReady`) —
                    // BEFORE awaiting its completion, so a concurrent `StopAsync` racing session creation
                    // still reaches the real session rather than missing a child about to spawn.
                    // `Supervisor.StartAsync` returns synchronously (the session's loop yields before its
                    // first spawn), so the session exists the instant this resumes.
                    let! session = supervisor.StartAsync lifetime.Token
                    lock gate (fun () -> currentSession <- Some session)
                    signal.TrySetResult(Some session) |> ignore

                    // `SupervisionSession.Completion` never faults for an *expected* end (policy/predicate/
                    // budget/stop/cancellation are all folded into its `Result`); it faults only for an
                    // exception that escaped the run itself — e.g. the injected `IProcessRunner` threw
                    // rather than returning a `Result` error — which the catch-all below turns into an
                    // observable `LastOutcome` failure.
                    let! outcome = session.Completion
                    publishOutcome outcome
            with
            | :? OperationCanceledException ->
                // Host shutdown cancelled the background supervision start; the child stop path has already
                // observed the host token, so there is no further recovery to perform here.
                signal.TrySetResult None |> ignore
            | ex ->
                // An exception here escaped the already-built supervisor's own run (e.g. the injected
                // `IProcessRunner` threw instead of returning a `Result` error). Publish it the same way any
                // other supervision failure is published: as an observable `LastOutcome`, not only a log —
                // otherwise a caller reading `LastOutcome` sees `None` and believes supervision is still
                // active when it has in fact crashed.
                signal.TrySetResult None |> ignore
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

    /// The last result the supervision session reported through its `Completion`, when supervision has
    /// ended.
    member _.LastOutcome = lock gate (fun () -> lastOutcome)

    /// The outcome returned by the child process stop operation during host shutdown, when one ran.
    /// Only overwritten when a `StopAsync` call actually stopped an active child — a `StopAsync`
    /// call that finds no active child (supervision mid-backoff-sleep, or already ended) leaves the
    /// previously published value untouched, rather than resetting it to `None`.
    member _.LastStopOutcome = lock gate (fun () -> lastStopOutcome)

    /// Whether the background supervision loop is currently running: `true` from a successful
    /// `StartAsync` until supervision ends (naturally, via `StopAsync`, or via `Dispose`/`DisposeAsync`)
    /// — i.e. teardown has not begun and the supervision `Task` has not yet completed. `false` before the
    /// first `StartAsync`, once supervision has ended, or once `Dispose`/`DisposeAsync` has claimed
    /// teardown (even if the loop has not quite finished unwinding yet — see `IDisposable.Dispose`). The
    /// primary "is it alive" signal for a health check.
    member _.IsSupervisionActive =
        lock gate (fun () -> not disposed && not supervisionTask.IsCompleted)

    /// How many restarts the current (or most recent) supervision run has made so far, live — mirrors
    /// `SupervisionOutcome.Restarts` but updates as each restart happens, read straight from the
    /// supervision session's live `Status`. `0` before the current run's session has been published (a
    /// fresh `StartAsync` before the first spawn), and reset naturally by each fresh session.
    member _.RestartCount =
        match lock gate (fun () -> currentSession) with
        | Some session -> session.Status.Restarts
        | None -> 0

    /// Whether the supervised child is currently paused by the failure-storm guard
    /// (`Supervisor.StormPause`), live — read straight from the supervision session's `Status`. Always
    /// `false` when `StormPause` was never configured, or before the current run's session is published.
    /// A reasonable "Degraded" signal for a health check: supervision is still alive, but restarts are
    /// being throttled because failures are clustering.
    member _.IsStormPaused =
        match lock gate (fun () -> currentSession) with
        | Some session -> session.Status.IsStormPaused
        | None -> false

    interface IHostedService with
        /// Starts (or restarts, once a prior supervision has ended) the background supervision loop.
        /// Idempotent while supervision is already running — a concurrent or repeated call never starts
        /// a second supervision task. A `cancellationToken` that is already cancelled honors the
        /// standard `IHostedService` cancellation contract: this returns a cancelled task without
        /// starting supervision or spawning a child. A no-op once `Dispose` has run — `Dispose`
        /// permanently forbids new starts.
        ///
        /// Fast and non-blocking: only the state transition (forget the prior run's session snapshot,
        /// mint a fresh readiness signal, publish the supervision `Task` handle) runs under `gate`. The
        /// actual supervision start — the caller's `configureSupervisor` and the native spawn of the first
        /// incarnation — is forced off the lock by the leading `Task.Yield()` in `runSupervision`, so gate
        /// readers (`IsSupervisionActive` et al.) are never blocked for the spawn and caller code never runs
        /// under the service's lock.
        member _.StartAsync(cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromCanceled cancellationToken
            else
                lock gate (fun () ->
                    if not disposed && supervisionTask.IsCompleted then
                        // A fresh run is a new lifetime, not a continuation: forget the previous run's
                        // session so telemetry (`RestartCount`/`IsStormPaused`) reads its defaults until the
                        // new session publishes, and hand the wrapper a fresh readiness signal that a racing
                        // `StopAsync` can await. The session itself resets its own restart count / storm-pause
                        // flag, so nothing carries over.
                        currentSession <- None

                        let signal =
                            TaskCompletionSource<SupervisionSession option>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        sessionReady <- signal.Task
                        // `runSupervision` yields before any real work (see its leading `Task.Yield()`), so
                        // this call returns an already-suspended, not-yet-completed task immediately: the
                        // handle is published under `gate` here (so `StopAsync`/`Dispose`/`DisposeAsync`
                        // snapshot exactly it, with no "Stop saw a stale/null task" window), while the
                        // configure-and-spawn prefix runs off the lock. `IsCompleted` is therefore `false`
                        // the instant the handle is assigned, so a racing second `StartAsync` sees an active
                        // task and does not start a second supervision loop.
                        supervisionTask <- runSupervision signal

                    Task.CompletedTask)

        /// Requests a graceful stop: stops the current live incarnation through its own graceful path
        /// (`RunningProcess.StopAsync`, honouring `HostedProcessOptions.ShutdownGracePeriod`) and interrupts
        /// any in-flight backoff sleep, so the supervision loop ends promptly — with the current child's
        /// honest result when one was live, or between incarnations with the last incarnation's — and
        /// launches no further child. Then awaits the supervision loop's own end. Race-safe with a
        /// concurrent `Dispose` and with session creation: the run's session is reached through the
        /// `sessionReady` signal (so a stop that predates the first spawn still finds the real session),
        /// and the supervision task awaited here is snapshotted once so a concurrent `StartAsync`
        /// reassigning it cannot be raced.
        member _.StopAsync(cancellationToken) =
            task {
                let ready, toAwait = lock gate (fun () -> sessionReady, supervisionTask)

                // Reach this run's session (if any) and stop it gracefully.
                try
                    let! session = ready.WaitAsync cancellationToken

                    match session with
                    | Some session ->
                        let! stopped = session.StopActiveAsync(options.ShutdownGracePeriod, cancellationToken)

                        // Only publish when a live child was actually stopped (`stopped.IsSome`) — see
                        // `LastStopOutcome`'s doc-comment. A stop that found no active child (a repeat/lone
                        // stop, or one taken between incarnations during a backoff sleep) must not stomp a
                        // previously published real outcome; the session's own `stopping` flag + interrupted
                        // backoff still end supervision promptly without launching another incarnation.
                        if stopped.IsSome then
                            lock gate (fun () -> lastStopOutcome <- stopped)
                    | None ->
                        // No session for this run: supervision was never started, or `configureSupervisor`
                        // failed and supervision already ended — nothing to stop.
                        ()
                with :? OperationCanceledException ->
                    // The host stop token expired before the session was reachable / its child stop
                    // completed; the child stop path already observed the same token, so returning lets the
                    // host enforce its shutdown deadline.
                    ()

                try
                    do! toAwait.WaitAsync cancellationToken
                with :? OperationCanceledException ->
                    // As above: the host stop token expired while awaiting the loop's end.
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
