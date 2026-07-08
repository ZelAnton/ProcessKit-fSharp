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
                match! (this :> IProcessRunner).SpawnAsync(command, cancellationToken) with
                | Error error -> return Error error
                | Ok running ->
                    try
                        let! result = running.OutputStringAsync()
                        return result
                    finally
                        clearCurrent running
            }

        member this.CaptureBytesAsync(command, cancellationToken) =
            task {
                match! (this :> IProcessRunner).SpawnAsync(command, cancellationToken) with
                | Error error -> return Error error
                | Ok running ->
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

    let gate = obj ()
    let trackingRunner = TrackingRunner runner
    let lifetime = new CancellationTokenSource()
    let mutable stopping = 0
    let mutable supervisionTask: Task = Task.CompletedTask
    let mutable lastOutcome: Result<SupervisionOutcome, ProcessError> option = None
    let mutable lastStopOutcome: Outcome option = None

    let logError (ex: exn) =
        logger.LogError(ex, "ProcessKit hosted process {Name} supervision failed.", name)

    let runSupervision () =
        task {
            try
                let supervisor =
                    configureSupervisor
                        .Invoke(Supervisor(command).WithRunner(trackingRunner))
                        .StopWhen(Func<ProcessResult<string>, bool>(fun _ -> Volatile.Read(&stopping) <> 0))

                let! outcome = supervisor.RunAsync(lifetime.Token)
                lastOutcome <- Some outcome
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
    member _.LastOutcome = lastOutcome

    /// The outcome returned by the child process stop operation during host shutdown, when one ran.
    member _.LastStopOutcome = lastStopOutcome

    interface IHostedService with
        member _.StartAsync(_cancellationToken) =
            lock gate (fun () ->
                if supervisionTask.IsCompleted then
                    supervisionTask <- runSupervision ()

                Task.CompletedTask)

        member _.StopAsync(cancellationToken) =
            task {
                Volatile.Write(&stopping, 1)

                let! stopped = trackingRunner.StopActiveAsync(options.ShutdownGracePeriod, cancellationToken)
                lastStopOutcome <- stopped
                lifetime.Cancel()

                try
                    do! supervisionTask.WaitAsync(cancellationToken)
                with :? OperationCanceledException ->
                    // The host stop token expired; the active child stop path already used the same
                    // token, so returning lets the host enforce its shutdown deadline.
                    ()
            }
            :> Task

    interface IDisposable with
        member _.Dispose() = lifetime.Dispose()
