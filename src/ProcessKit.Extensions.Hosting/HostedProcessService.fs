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
            task {
                // Register a kill-on-cancel backstop: without it, cancelling `cancellationToken`
                // (e.g. `lifetime.Cancel()` on host shutdown) would never reach an already-running child,
                // since `OutputStringAsync` itself does not watch any token. This mirrors the pattern
                // in `CaptureVerbs.runToCompletion` (src/ProcessKit/Runner.fs:123).
                match! (this :> IProcessRunner).SpawnAsync(command, cancellationToken) with
                | Error error -> return Error error
                | Ok running ->
                    use _registration = cancellationToken.Register(fun () -> running.Kill())

                    try
                        let! result = running.OutputStringAsync()
                        return result
                    finally
                        clearCurrent running
            }

        member this.CaptureBytesAsync(command, cancellationToken) =
            task {
                // See `CaptureStringAsync` above for why this registers a kill-on-cancel backstop.
                match! (this :> IProcessRunner).SpawnAsync(command, cancellationToken) with
                | Error error -> return Error error
                | Ok running ->
                    use _registration = cancellationToken.Register(fun () -> running.Kill())

                    try
                        let! result = running.OutputBytesAsync()
                        return result
                    finally
                        clearCurrent running
            }

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
                    let supervisor =
                        supervisor.StopWhen(Func<ProcessResult<string>, bool>(fun _ -> Volatile.Read(&stopping) <> 0))

                    let! outcome = supervisor.RunAsync(lifetime.Token)
                    publishOutcome outcome
            with
            | :? OperationCanceledException ->
                // Host shutdown cancelled the background supervision wait; the child stop path has
                // already observed the host token, so there is no further recovery to perform here.
                ()
            | ex -> logError ex
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
        /// task to actually finish (a plain `Dispose()` cannot await). Safe to call repeatedly, and
        /// safe to race against `StartAsync`/`StopAsync`: the `disposed` flag and `gate` lock ensure
        /// only the first `Dispose` call performs the teardown, and every access to `lifetime` elsewhere
        /// tolerates it being cancelled/disposed underneath a concurrent caller.
        member _.Dispose() =
            let shouldTearDown =
                lock gate (fun () ->
                    if disposed then
                        false
                    else
                        disposed <- true
                        true)

            if shouldTearDown then
                // Prevent a restart-policy decision racing this teardown from restarting the child: once
                // the active incarnation's kill below completes its run, `StopWhen` must end supervision
                // rather than schedule another restart.
                Volatile.Write(&stopping, 1)
                // Hard-kill the active child directly (belt-and-suspenders: independent of whether a
                // registration on `lifetime.Token` happens to be live right now) ...
                trackingRunner.KillActive()
                // ... and cancel the supervision loop itself — this also unblocks any in-flight backoff
                // sleep and the `TrackingRunner.CaptureStringAsync` kill-on-cancel registration, so a
                // child that starts running just as `Dispose` runs is still torn down.
                cancelLifetime ()
                lifetime.Dispose()
