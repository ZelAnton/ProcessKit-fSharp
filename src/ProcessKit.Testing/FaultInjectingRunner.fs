namespace ProcessKit.Testing

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit

[<RequireQualifiedAccess; NoComparison>]
type internal FaultInjectionKind =
    | Error of ProcessError
    | Outcome of Outcome
    | Delegate

/// One deterministic action for `FaultInjectingRunner`: return a typed error, synthesize a terminal
/// process outcome, or delegate to the wrapped runner. `WithLatency` delays that action through the
/// command's `TimeProvider`, so virtual-time tests do not sleep in real time.
[<Sealed>]
type FaultInjection private (kind: FaultInjectionKind, latency: TimeSpan) =
    do
        ArgumentOutOfRangeException.ThrowIfLessThan(latency, TimeSpan.Zero, nameof latency)

        if not (Timeouts.isArmable latency) then
            raise (ArgumentOutOfRangeException(nameof latency, latency, "latency exceeds the BCL timer range"))

    /// Return `error` instead of invoking the wrapped runner.
    static member Error(error: ProcessError) =
        ArgumentNullException.ThrowIfNull(error, nameof error)
        FaultInjection(FaultInjectionKind.Error error, TimeSpan.Zero)

    /// Return a fake run with `outcome` (for example `Exited 7`, `Signalled`, or `TimedOut`).
    static member Outcome(outcome: Outcome) =
        ArgumentNullException.ThrowIfNull(outcome, nameof outcome)
        FaultInjection(FaultInjectionKind.Outcome outcome, TimeSpan.Zero)

    /// Invoke the wrapped runner. Useful in a scripted sequence that needs latency without a failure.
    static member Delegate() =
        FaultInjection(FaultInjectionKind.Delegate, TimeSpan.Zero)

    /// A copy that waits for `value` through `Command.TimeProvider` before applying this action.
    member _.WithLatency(value: TimeSpan) = FaultInjection(kind, value)

    /// The configured injected latency.
    member _.Latency = latency

    member internal _.Kind = kind

[<RequireQualifiedAccess; NoComparison>]
type internal FaultInjectionMode =
    | Sequence of FaultInjection[]
    | First of Count: int * Injection: FaultInjection
    | Seeded of Seed: int * Probability: float * Injection: FaultInjection

/// A deterministic `IProcessRunner` decorator for resilience tests. It can consume a scripted
/// injection sequence, inject the first N calls, or select calls with a stable seeded probability;
/// calls not selected by the policy are forwarded to `Inner`.
[<Sealed>]
type FaultInjectingRunner private (inner: IProcessRunner, mode: FaultInjectionMode) =
    inherit DelegatingProcessRunner(inner)

    let mutable invocationCount = 0

    let stableSample (seed: int) (index: int) =
        let mutable value = uint64 (uint32 seed) + uint64 index * 0x9E3779B97F4A7C15UL
        value <- (value ^^^ (value >>> 30)) * 0xBF58476D1CE4E5B9UL
        value <- (value ^^^ (value >>> 27)) * 0x94D049BB133111EBUL
        value <- value ^^^ (value >>> 31)
        float (value >>> 11) / 9007199254740992.0

    let nextInjection () =
        let index = Interlocked.Increment(&invocationCount)

        match mode with
        | FaultInjectionMode.Sequence injections ->
            if index <= injections.Length then
                Some injections[index - 1]
            else
                None
        | FaultInjectionMode.First(count, injection) -> if index <= count then Some injection else None
        | FaultInjectionMode.Seeded(seed, probability, injection) ->
            if stableSample seed index < probability then
                Some injection
            else
                None

    let waitLatency
        (injection: FaultInjection)
        (command: Command)
        (completion: bool)
        (cancellationToken: CancellationToken)
        : Task<bool> =
        task {
            use linked =
                if completion then
                    match command.Config.CancelOn with
                    | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                    | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                else
                    CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            try
                if injection.Latency > TimeSpan.Zero then
                    do! Task.Delay(injection.Latency, command.Config.TimeProvider, linked.Token)

                return not linked.IsCancellationRequested
            with :? OperationCanceledException ->
                return false
        }

    let fake (command: Command) (outcome: Outcome) =
        FakeProcess.OfCommand(command).WithOutcome(outcome).Build()

    /// Number of intercepted primitive invocations so far, including delegated calls.
    member _.InvocationCount = Volatile.Read(&invocationCount)

    /// Consume `injections` in order, one per primitive invocation, then delegate all later calls.
    new(inner: IProcessRunner, injections: seq<FaultInjection | null>) =
        ArgumentNullException.ThrowIfNull(inner, nameof inner)
        ArgumentNullException.ThrowIfNull(injections, nameof injections)

        let values =
            injections
            |> Seq.map (function
                | null -> raise (ArgumentException("injections must not contain null", nameof injections))
                | injection -> injection)
            |> Seq.toArray

        FaultInjectingRunner(inner, FaultInjectionMode.Sequence values)

    /// Apply `injection` to the first `count` primitive invocations, then delegate later calls.
    new(inner: IProcessRunner, count: int, injection: FaultInjection) =
        ArgumentNullException.ThrowIfNull(inner, nameof inner)
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof count)
        ArgumentNullException.ThrowIfNull(injection, nameof injection)
        FaultInjectingRunner(inner, FaultInjectionMode.First(count, injection))

    /// Create a stable seeded policy. For a fixed seed and invocation order, the same calls are injected.
    static member Seeded(inner: IProcessRunner, seed: int, probability: float, injection: FaultInjection) =
        ArgumentNullException.ThrowIfNull(inner, nameof inner)
        ArgumentNullException.ThrowIfNull(injection, nameof injection)

        if Double.IsNaN probability || probability < 0.0 || probability > 1.0 then
            raise (ArgumentOutOfRangeException(nameof probability, probability, "probability must be between 0 and 1"))

        FaultInjectingRunner(inner, FaultInjectionMode.Seeded(seed, probability, injection))

    override this.CaptureStringAsync(command, cancellationToken) =
        Seam.validate command

        match nextInjection () with
        | None -> inner.CaptureStringAsync(command, cancellationToken)
        | Some injection ->
            task {
                let! active = waitLatency injection command true cancellationToken

                if not active then
                    return Error(ProcessError.Cancelled command.Program)
                else
                    match injection.Kind with
                    | FaultInjectionKind.Error error -> return Error error
                    | FaultInjectionKind.Outcome outcome ->
                        return!
                            Seam.complete
                                (fun _ -> Ok(fake command outcome))
                                (fun running -> running.OutputStringAsync())
                                command
                                cancellationToken
                    | FaultInjectionKind.Delegate -> return! inner.CaptureStringAsync(command, cancellationToken)
            }

    override this.CaptureBytesAsync(command, cancellationToken) =
        Seam.validate command

        match nextInjection () with
        | None -> inner.CaptureBytesAsync(command, cancellationToken)
        | Some injection ->
            task {
                let! active = waitLatency injection command true cancellationToken

                if not active then
                    return Error(ProcessError.Cancelled command.Program)
                else
                    match injection.Kind with
                    | FaultInjectionKind.Error error -> return Error error
                    | FaultInjectionKind.Outcome outcome ->
                        return!
                            Seam.complete
                                (fun _ -> Ok(fake command outcome))
                                (fun running -> running.OutputBytesAsync())
                                command
                                cancellationToken
                    | FaultInjectionKind.Delegate -> return! inner.CaptureBytesAsync(command, cancellationToken)
            }

    override this.SpawnAsync(command, cancellationToken) =
        Seam.validate command

        match nextInjection () with
        | None -> inner.SpawnAsync(command, cancellationToken)
        | Some injection ->
            task {
                let! active = waitLatency injection command false cancellationToken

                if not active then
                    return Error(ProcessError.Cancelled command.Program)
                else
                    match injection.Kind with
                    | FaultInjectionKind.Error error -> return Error error
                    | FaultInjectionKind.Outcome outcome -> return Ok(fake command outcome)
                    | FaultInjectionKind.Delegate -> return! inner.SpawnAsync(command, cancellationToken)
            }
