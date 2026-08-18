namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Pulling items out of a completion-ordered batch stream (`Exec.outputStream` /
/// `outputStreamBytes`). Every read is bounded, so a fan-out that stalls fails its test with a
/// timeout instead of hanging the whole run.
module private BatchStreaming =

    let private readTimeout = TimeSpan.FromSeconds 5.0

    /// The next item, or `None` once the stream has ended.
    let next (enumerator: IAsyncEnumerator<BatchItem<'T>>) : Task<BatchItem<'T> option> =
        task {
            let! moved = enumerator.MoveNextAsync().AsTask().WaitAsync readTimeout
            return (if moved then Some enumerator.Current else None)
        }

    /// The next item, failing the test if the stream ended instead.
    let expect (enumerator: IAsyncEnumerator<BatchItem<'T>>) : Task<BatchItem<'T>> =
        task {
            match! next enumerator with
            | Some item -> return item
            | None -> return failwith "the batch stream ended before the expected item"
        }

    /// Assert the stream is finished (no further item).
    let expectEnd (enumerator: IAsyncEnumerator<BatchItem<'T>>) : Task<unit> =
        task {
            match! next enumerator with
            | None -> ()
            | Some item -> failwith $"expected the batch stream to be finished, got index {item.Index}"
        }

    /// Every remaining item, in the order the stream yields them.
    let drain (enumerator: IAsyncEnumerator<BatchItem<'T>>) : Task<ResizeArray<BatchItem<'T>>> =
        task {
            let collected = ResizeArray<BatchItem<'T>>()
            let mutable more = true

            while more do
                match! next enumerator with
                | Some item -> collected.Add item
                | None -> more <- false

            return collected
        }

/// A runner whose captures block until each command's own program name is explicitly released — the
/// deterministic way to script an exact completion order that differs from input order.
type private PacedRunner() =
    let gates = ConcurrentDictionary<string, TaskCompletionSource<unit>>()
    let captured = ConcurrentQueue<string>()

    let gateFor (program: string) =
        gates.GetOrAdd(
            program,
            (fun _ -> TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
        )

    /// Let `program`'s capture finish (before or after it started — either way it completes once).
    member _.Release(program: string) =
        (gateFor program).TrySetResult() |> ignore

    member _.CapturedPrograms = captured.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            captured.Enqueue command.Program
            let gate = gateFor command.Program

            task {
                do! gate.Task

                return
                    Ok(
                        ProcessResult<string>(
                            command.Program,
                            command.Program,
                            "",
                            Outcome.Exited 0,
                            TimeSpan.Zero,
                            false,
                            [ 0 ]
                        )
                    )
            }

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Records how many captures are live at once, and holds each one until the fan-out has actually
/// reached `cap` simultaneous captures — so a cap test proves BOTH halves (never exceeded, and
/// genuinely reached) rather than the easy half only. The wait is bounded, so a fan-out that never
/// reaches the cap fails the assertion instead of hanging the run.
type private ConcurrencyProbeRunner(cap: int) =
    let sync = obj ()
    let mutable active = 0
    let mutable peak = 0

    let capReached =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Peak = lock sync (fun () -> peak)

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            task {
                let live =
                    lock sync (fun () ->
                        active <- active + 1
                        peak <- max peak active
                        active)

                if live >= cap then
                    capReached.TrySetResult() |> ignore

                try
                    do! capReached.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                finally
                    lock sync (fun () -> active <- active - 1)

                return
                    Ok(
                        ProcessResult<string>(
                            command.Program,
                            command.Program,
                            "",
                            Outcome.Exited 0,
                            TimeSpan.Zero,
                            false,
                            [ 0 ]
                        )
                    )
            }

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A runner for the abandoned-stream contract: "fast" completes immediately, "holding" never
/// completes on its own and ends only when ITS OWN capture token is cancelled (what abandoning the
/// stream has to do to an in-flight command), and "queued" must never reach capture at all.
type private AbandonedStreamRunner() =
    let holdingStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let holdingCancelled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let captured = ConcurrentQueue<string>()

    member _.HoldingStarted: Task = holdingStarted.Task :> Task
    member _.HoldingCancelled: Task = holdingCancelled.Task :> Task
    member _.CapturedPrograms = captured.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            captured.Enqueue command.Program

            match command.Program with
            | "fast" ->
                Task.FromResult(
                    Ok(ProcessResult<string>("fast", "fast", "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))
                )
            | "holding" ->
                holdingStarted.TrySetResult() |> ignore

                task {
                    try
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return Unchecked.defaultof<Result<ProcessResult<string>, ProcessError>>
                    with :? OperationCanceledException ->
                        // A real runner kills its child here and reports the cancellation; the point of
                        // the test is that abandoning the stream is what fires this token at all.
                        holdingCancelled.TrySetResult() |> ignore
                        return Error(ProcessError.Cancelled command.Program)
                }
            | "queued" -> failwith "an abandoned batch stream must never start a queued command"
            | other -> failwith $"unexpected command '{other}'"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Counts the captures a batch actually STARTS, and completes each one immediately — the probe for the
/// bounded hand-off contract, where what matters is not what a command returns but whether it was ever
/// allowed to begin. `bound` is the most commands the fan-out may have started once the consumer stops
/// reading; the two signals let a test wait for that ceiling to be reached and fail the moment it is
/// exceeded, instead of sleeping a guessed interval and hoping.
type private HandOffProbeRunner(bound: int) =
    let mutable started = 0

    let saturated =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let overrun =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Started = Volatile.Read(&started)

    /// Completes once the fan-out has started as many commands as the bound allows.
    member _.Saturated: Task = saturated.Task :> Task

    /// Completes the moment the fan-out starts one command MORE than the bound allows.
    member _.Overrun: Task = overrun.Task :> Task

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            let count = Interlocked.Increment(&started)

            if count >= bound then
                saturated.TrySetResult() |> ignore

            if count > bound then
                overrun.TrySetResult() |> ignore

            Task.FromResult(
                Ok(
                    ProcessResult<string>(
                        command.Program,
                        command.Program,
                        "",
                        Outcome.Exited 0,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Driving a completion-ordered stream into the one state the bounded hand-off contract is about: the
/// hand-off buffer full, every concurrency slot held by a command parked on the hand-off (finished, but
/// with nobody to give its item to), and every remaining command still unstarted. Reaching that state is
/// what the streaming tests below all need before they can assert anything about backpressure,
/// cancellation, or abandonment — and none of the other streaming tests ever reach it, because they keep
/// reading.
module private HandOff =

    /// The most commands a bounded hand-off may ever have started once the consumer has taken `consumed`
    /// items and stopped reading: `concurrency` items sitting in the buffer + one command parked on the
    /// hand-off per concurrency slot (each still holding its slot, so nothing else can start) + the ones
    /// already handed to the consumer. It is a ceiling at every instant, not just at rest.
    let allowedStarts (concurrency: int) (consumed: int) = (2 * concurrency) + consumed

    /// Run `commandCount` commands through `Exec.outputStream`, pull exactly ONE item, and then stop
    /// reading until the fan-out has filled both the buffer and every slot. Returns the probe and the
    /// still-live (undisposed) enumerator — how the stream ends is each test's own subject.
    let saturate (concurrency: int) (commandCount: int) (cancellationToken: CancellationToken) =
        task {
            let runner = HandOffProbeRunner(allowedStarts concurrency 1)
            let commands = [ for i in 1..commandCount -> Command.create $"cmd{i}" ]

            let stream =
                Exec.outputStream concurrency (runner :> IProcessRunner) commands cancellationToken

            let enumerator = stream.GetAsyncEnumerator()

            // The one and only read: the fan-out starts on the first pull, and from here on nothing is
            // ever taken out of the hand-off buffer again.
            let! first = BatchStreaming.expect enumerator

            // Positive proof the fan-out really did fill the buffer AND every slot: a fan-out that
            // stalled earlier fails this bounded wait, instead of silently satisfying a "did not run
            // ahead" assertion for the wrong reason.
            do! runner.Saturated.WaitAsync(TimeSpan.FromSeconds 5.0)

            return runner, enumerator, first
        }

/// "boom" errors, everything else succeeds — for proving a completion-ordered stream never
/// short-circuits on one command's failure.
type private MixedOutcomeRunner() =
    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            match command.Program with
            | "boom" -> Task.FromResult(Error(ProcessError.Io "boom"))
            | program ->
                Task.FromResult(
                    Ok(ProcessResult<string>(program, program, "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))
                )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Counts captures and succeeds immediately — for proving a stream starts nothing until enumerated.
type private StreamStartCountingRunner() =
    let mutable captureCount = 0

    member _.CaptureCount = Volatile.Read(&captureCount)

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            Interlocked.Increment(&captureCount) |> ignore

            Task.FromResult(
                Ok(
                    ProcessResult<string>(
                        command.Program,
                        command.Program,
                        "",
                        Outcome.Exited 0,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Echoes each command's program name as raw stdout BYTES, and fails loudly on the text seam — so a
/// bytes verb that quietly routed through `CaptureStringAsync` could not pass.
type private BytesEchoRunner() =
    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            failwith "outputStreamBytes must capture bytes, not text"

        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Text.Encoding.UTF8.GetBytes command.Program,
                        "",
                        Outcome.Exited 0,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports capture")

/// Fails each program's FIRST capture with a retryable error and succeeds on the next one — so only a
/// batch verb that routes through the verb layer (where a command's own `Retry` policy lives) yields
/// a successful item.
type private FlakyRunner() =
    let attempts = ConcurrentDictionary<string, int>()

    member _.AttemptsFor(program: string) =
        match attempts.TryGetValue program with
        | true, count -> count
        | _ -> 0

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            let attempt =
                attempts.AddOrUpdate(command.Program, 1, (fun _ previous -> previous + 1))

            if attempt = 1 then
                Task.FromResult(Error(ProcessError.Io "transient"))
            else
                Task.FromResult(
                    Ok(
                        ProcessResult<string>(
                            command.Program,
                            command.Program,
                            "",
                            Outcome.Exited 0,
                            TimeSpan.Zero,
                            false,
                            [ 0 ]
                        )
                    )
                )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A runner that completes one command, holds another in capture, and records every capture attempt.
type private QueueBlockingRunner() =
    let holdingStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseHolding =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let captured = ConcurrentQueue<string>()

    let success (command: Command) =
        Ok(ProcessResult<string>(command.Program, command.Program, "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))

    member _.HoldingStarted: Task = holdingStarted.Task :> Task
    member _.ReleaseHolding() = releaseHolding.TrySetResult() |> ignore
    member _.CapturedPrograms = captured.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            captured.Enqueue command.Program

            match command.Program with
            | "completed" -> Task.FromResult(success command)
            | "holding" ->
                holdingStarted.TrySetResult() |> ignore

                task {
                    do! releaseHolding.Task
                    return success command
                }
            | "queued" -> failwith "a cancelled queued command must never enter capture"
            | _ -> failwith $"unexpected command '{command.Program}'"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A runner whose text-capture seam is cancelled independently of the verb token.
type private CaptureCancellingRunner() =
    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromException<Result<ProcessResult<string>, ProcessError>>(OperationCanceledException())

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A runner for `BatchPolicy.FailFast`: "ok" succeeds immediately, "failing" errors immediately (the
/// fail-fast trigger), and "holding" blocks in capture until ITS OWN capture token is cancelled —
/// modelling how a real `IProcessRunner` observes a FailFast-triggered cancellation of a command
/// that is already running. "queued" must never reach capture at all.
type private FailFastRunner() =
    let holdingStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let captured = ConcurrentQueue<string>()

    let success (command: Command) =
        Ok(ProcessResult<string>(command.Program, command.Program, "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))

    member _.HoldingStarted: Task = holdingStarted.Task :> Task
    member _.CapturedPrograms = captured.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            captured.Enqueue command.Program

            match command.Program with
            | "ok" -> Task.FromResult(success command)
            | "failing" -> Task.FromResult(Error(ProcessError.Io "boom"))
            | "holding" ->
                holdingStarted.TrySetResult() |> ignore
                // Never completes on its own: only the FailFast trigger's cancellation of THIS token
                // ends the wait, exactly like a real runner's registration that kills the child when
                // its capture token fires.
                task {
                    do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    return Unchecked.defaultof<Result<ProcessResult<string>, ProcessError>>
                }
            | "queued" -> failwith "a FailFast-cancelled queued command must never enter capture"
            | other -> failwith $"unexpected command '{other}'"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A minimal runner for the R-5 slot-acquisition race test: "holding" completes via a
/// `TaskCompletionSource` with SYNCHRONOUS continuations (the default — no
/// `RunContinuationsAsynchronously`), so completing it lands the batch's internal
/// `SemaphoreSlim.Release()` as close as possible, in real time, to the moment `ReleaseHolding` is
/// invoked — the tight timing needed to reliably reproduce `SemaphoreSlim`'s own internal race
/// between completing a pending waiter via `Release()` and cancelling it via a registered callback.
type private RacingSlotRunner() =
    let holdingStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseHolding =
        TaskCompletionSource<Result<ProcessResult<string>, ProcessError>>()

    let captured = ConcurrentQueue<string>()

    // Whether `effectiveToken` already showed cancellation requested at the exact moment "queued"'s
    // capture actually started — the direct, observable signal for whether the R-5 recheck did its
    // job: with the fix, "queued" can only ever reach this point while still `false`, because the
    // recheck intercepts it beforehand whenever it is `true`.
    let queuedCapturedWhileCancelled = ConcurrentQueue<bool>()

    member _.HoldingStarted: Task = holdingStarted.Task :> Task

    member _.ReleaseHolding() =
        let result =
            Ok(ProcessResult<string>("holding", "holding", "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))

        releaseHolding.TrySetResult result |> ignore

    member _.CapturedPrograms = captured.ToArray()
    member _.QueuedCapturedWhileCancelled = queuedCapturedWhileCancelled.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            captured.Enqueue command.Program

            match command.Program with
            | "holding" ->
                holdingStarted.TrySetResult() |> ignore
                releaseHolding.Task
            | "queued" ->
                queuedCapturedWhileCancelled.Enqueue cancellationToken.IsCancellationRequested
                Task.FromResult(Error(ProcessError.Io "a cancelled queued command must never enter capture"))
            | other -> failwith $"unexpected command '{other}'"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// A runner for FailFast tests that need "failing" to observe its own error only AFTER "holding"
/// has verifiably entered capture (and signalled `HoldingStarted`) — "failing" waits on that signal
/// before returning its error, so the FailFast trigger it fires can only ever race a sibling that
/// has actually started running, never one that might still be waiting for its concurrency slot
/// (R-3). When `registerThrowingCallback` is true, "holding" also registers a callback on its own
/// capture token that throws when invoked — modelling a buggy `IProcessRunner`/consumer
/// registration — proving `internalCts.Cancel()` swallows a callback's exception instead of
/// faulting the whole batch (R-1).
type private FailFastSiblingRunner(registerThrowingCallback: bool) =
    let holdingStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let captured = ConcurrentQueue<string>()

    new() = FailFastSiblingRunner(false)

    member _.HoldingStarted: Task = holdingStarted.Task :> Task
    member _.CapturedPrograms = captured.ToArray()

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            captured.Enqueue command.Program

            match command.Program with
            | "failing" ->
                task {
                    do! holdingStarted.Task
                    return Error(ProcessError.Io "boom")
                }
            | "holding" ->
                if registerThrowingCallback then
                    cancellationToken.Register(fun () -> failwith "buggy cancellation callback")
                    |> ignore

                holdingStarted.TrySetResult() |> ignore
                // Never completes on its own: only the FailFast trigger's cancellation of THIS token
                // ends the wait, exactly like a real runner's registration that kills the child when
                // its capture token fires.
                task {
                    do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    return Unchecked.defaultof<Result<ProcessResult<string>, ProcessError>>
                }
            | other -> failwith $"unexpected command '{other}'"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports text capture")

/// Records capture attempts so batch argument validation can prove it ran before any command started.
type private BoundaryValidationRunner() =
    let mutable captureCount = 0

    member _.CaptureCount = Volatile.Read(&captureCount)

    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            Interlocked.Increment(&captureCount) |> ignore
            failwith "invalid batch input must fail before text capture starts"

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Interlocked.Increment(&captureCount) |> ignore
            failwith "invalid batch input must fail before bytes capture starts"

        member _.SpawnAsync(_command, _cancellationToken) =
            raise (NotSupportedException "this test runner only supports capture")

[<TestFixture>]
type ExecBatchTests() =

    [<Test>]
    member _.``outputAll rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAll 1 runner [ Command.create "must-not-run" ] CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputAll rejects null commands before starting capture``() =
        let runner = BoundaryValidationRunner()
        let commands = Unchecked.defaultof<seq<Command>>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAll 1 (runner :> IProcessRunner) commands CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAll rejects a null command before starting capture``() =
        let runner = BoundaryValidationRunner()
        let nullCommand = Unchecked.defaultof<Command>

        let ex =
            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    Exec.outputAll
                        1
                        (runner :> IProcessRunner)
                        [| Command.create "must-not-run"; nullCommand |]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytes rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllBytes 1 runner [ Command.create "must-not-run" ] CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputAllBytes rejects null commands before starting capture``() =
        let runner = BoundaryValidationRunner()
        let commands = Unchecked.defaultof<seq<Command>>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllBytes 1 (runner :> IProcessRunner) commands CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytes rejects a null command before starting capture``() =
        let runner = BoundaryValidationRunner()
        let nullCommand = Unchecked.defaultof<Command>

        let ex =
            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    Exec.outputAllBytes
                        1
                        (runner :> IProcessRunner)
                        [| Command.create "must-not-run"; nullCommand |]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAll rejects zero concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputAll
                        0
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAll rejects negative concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputAll
                        -1
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytes rejects zero concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputAllBytes
                        0
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytes rejects negative concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputAllBytes
                        -1
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAll cancels queued commands without changing completed results``() : Task =
        task {
            let runner = QueueBlockingRunner()
            use cancellation = new CancellationTokenSource()

            let batch =
                Exec.outputAll
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "completed"
                      Command.create "holding"
                      Command.create "queued" ]
                    cancellation.Token

            do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            cancellation.Cancel()
            do! Task.Delay(TimeSpan.FromMilliseconds 50.0)

            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "completed"; "holding" |]))
            Assert.That(batch.IsCompleted, Is.False, "the held capture should still own the semaphore slot")

            runner.ReleaseHolding()
            let! results = batch

            match results[0] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "completed")
            | Error error -> Assert.Fail $"the completed command changed result: {error}"

            match results[1] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "holding")
            | Error error -> Assert.Fail $"the already-running command changed result: {error}"

            match results[2] with
            | Error(ProcessError.Cancelled "queued") -> ()
            | Error(ProcessError.Io detail) -> Assert.Fail $"expected Cancelled, not Io: {detail}"
            | other -> Assert.Fail $"expected queued cancellation, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAll maps capture cancellation to Cancelled``() : Task =
        task {
            let runner: IProcessRunner = CaptureCancellingRunner()
            let! results = Exec.outputAll 1 runner [ Command.create "capture-cancelled" ] CancellationToken.None

            match results[0] with
            | Error(ProcessError.Cancelled "capture-cancelled") -> ()
            | Error(ProcessError.Io detail) -> Assert.Fail $"expected Cancelled, not Io: {detail}"
            | other -> Assert.Fail $"expected capture cancellation, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy CollectAll behaves exactly like outputAll``() : Task =
        task {
            let runner = QueueBlockingRunner()
            use cancellation = new CancellationTokenSource()

            let batch =
                Exec.outputAllWithPolicy
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "completed"
                      Command.create "holding"
                      Command.create "queued" ]
                    BatchPolicy.CollectAll
                    cancellation.Token

            do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            cancellation.Cancel()
            do! Task.Delay(TimeSpan.FromMilliseconds 50.0)

            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "completed"; "holding" |]))

            runner.ReleaseHolding()
            let! results = batch

            match results[0] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "completed")
            | Error error -> Assert.Fail $"the completed command changed result: {error}"

            match results[1] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "holding")
            | Error error -> Assert.Fail $"the already-running command changed result: {error}"

            match results[2] with
            | Error(ProcessError.Cancelled "queued") -> ()
            | other -> Assert.Fail $"expected queued cancellation, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy FailFast cancels a not-yet-started command after the first error, keeping earlier results and input order``
        ()
        : Task =
        task {
            let runner = FailFastRunner()

            let! results =
                Exec.outputAllWithPolicy
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "ok"; Command.create "failing"; Command.create "queued" ]
                    BatchPolicy.FailFast
                    CancellationToken.None

            // "queued" never got its concurrency slot, so it never entered capture at all.
            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "ok"; "failing" |]))

            match results[0] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok")
            | Error error -> Assert.Fail $"the already-completed command changed result: {error}"

            match results[1] with
            | Error(ProcessError.Io "boom") -> ()
            | other -> Assert.Fail $"the triggering command should keep its own real error, got {other}"

            match results[2] with
            | Error(ProcessError.Cancelled "queued") -> ()
            | other -> Assert.Fail $"expected the not-yet-started command to be cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy FailFast cancels an already-running sibling after a concurrent command's first error``
        ()
        : Task =
        task {
            // `FailFastSiblingRunner` makes "failing" wait for "holding" to have already entered
            // capture before it returns its error, so this deterministically proves the cancellation
            // reaches an already-running sibling — not merely one still queued for its concurrency
            // slot (R-3) — and `WaitAsync` bounds the await so a cancellation regression fails fast
            // instead of hanging the test run indefinitely.
            let runner = FailFastSiblingRunner()

            let batch =
                Exec.outputAllWithPolicy
                    2
                    (runner :> IProcessRunner)
                    [ Command.create "holding"; Command.create "failing" ]
                    BatchPolicy.FailFast
                    CancellationToken.None

            let! results = batch.WaitAsync(TimeSpan.FromSeconds 5.0)

            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "holding"; "failing" |]))

            match results[0] with
            | Error(ProcessError.Cancelled "holding") -> ()
            | other -> Assert.Fail $"expected the already-running sibling to be cancelled, got {other}"

            match results[1] with
            | Error(ProcessError.Io "boom") -> ()
            | other -> Assert.Fail $"the triggering command should keep its own real error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy FailFast survives a cancellation callback that throws``() : Task =
        task {
            // The callback registered on "holding"'s capture token throws when `internalCts.Cancel()`
            // invokes it — proving that a buggy registration cannot fault the batch's `Task.WhenAll`
            // and discard every other command's already-computed `Result` (R-1).
            let runner = FailFastSiblingRunner(true)

            let batch =
                Exec.outputAllWithPolicy
                    2
                    (runner :> IProcessRunner)
                    [ Command.create "holding"; Command.create "failing" ]
                    BatchPolicy.FailFast
                    CancellationToken.None

            let! results = batch.WaitAsync(TimeSpan.FromSeconds 5.0)

            match results[0] with
            | Error(ProcessError.Cancelled "holding") -> ()
            | other -> Assert.Fail $"expected the callback-throwing sibling's own result to survive, got {other}"

            match results[1] with
            | Error(ProcessError.Io "boom") -> ()
            | other -> Assert.Fail $"the triggering command should keep its own real error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy FailFast still honours the caller's own cancellationToken``() : Task =
        task {
            let runner = QueueBlockingRunner()
            use cancellation = new CancellationTokenSource()

            let batch =
                Exec.outputAllWithPolicy
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "completed"
                      Command.create "holding"
                      Command.create "queued" ]
                    BatchPolicy.FailFast
                    cancellation.Token

            do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            cancellation.Cancel()
            do! Task.Delay(TimeSpan.FromMilliseconds 50.0)

            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "completed"; "holding" |]))

            runner.ReleaseHolding()
            let! results = batch

            match results[0] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "completed")
            | Error error -> Assert.Fail $"the completed command changed result: {error}"

            match results[1] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "holding")
            | Error error -> Assert.Fail $"the already-running command changed result: {error}"

            match results[2] with
            | Error(ProcessError.Cancelled "queued") -> ()
            | other -> Assert.Fail $"expected queued cancellation, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy FailFast never lets a racing slot acquisition enter capture``() : Task =
        task {
            // `SemaphoreSlim.WaitAsync` can complete successfully even though its token is ALREADY
            // cancelled at that exact moment, when the slot's `Release()` and the cancellation
            // request race each other inside `SemaphoreSlim` itself — genuine, low-level
            // thread-scheduling nondeterminism no black-box test can force on demand. Race
            // `Release()` (via `ReleaseHolding`, freeing the slot "queued" is waiting on, using
            // synchronous TCS continuations to land as close as possible to the real `Release()`
            // call) against `Cancel()` from independent threads, across many iterations, to reliably
            // reproduce that internal race. `RacingSlotRunner` records, at the exact moment "queued"
            // enters capture (if it ever does), whether `effectiveToken` already showed cancellation
            // requested right then — the direct, decisive signal for the R-5 recheck: with the fix,
            // "queued" can only ever reach capture while that reads `false`, because the recheck
            // intercepts it beforehand whenever it is `true`. A benign, unavoidable narrower race
            // remains possible in principle — "queued" legitimately starting a moment before
            // cancellation is even requested, exactly like an already-running command per
            // `BatchPolicy.FailFast`'s own "unless it reaches its own outcome first" contract.
            //
            // `RacingSlotRunner` reads the token again, one more hop later, right at the entry of
            // "queued"'s own capture — a strictly TIGHTER window than the recheck's own check, so
            // even a correct fix can occasionally see one more `Cancel()` land in that extra hop (the
            // same kind of unavoidable propagation latency, just narrower — and, empirically, WIDER
            // under heavy machine load, since the extra hop itself simply takes longer wall-clock time
            // to run). A confirmed regression (the recheck removed) reproduces empirically in close to
            // 90% of iterations — see Exec.fs's `elif effectiveToken.IsCancellationRequested` comment
            // — so tolerate a generous minority of that narrower, unrelated jitter (comfortably above
            // observed noise, comfortably below the regression signal) instead of demanding literally
            // zero, while still failing hard and unmistakably on an actual regression.
            let mutable capturedWhileCancelled = 0
            let mutable iterations = 0

            for _ in 1..500 do
                let runner = RacingSlotRunner()
                use cancellation = new CancellationTokenSource()

                let batch =
                    Exec.outputAllWithPolicy
                        1
                        (runner :> IProcessRunner)
                        [ Command.create "holding"; Command.create "queued" ]
                        BatchPolicy.FailFast
                        cancellation.Token

                do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)

                let releaseTask = Task.Run(fun () -> runner.ReleaseHolding())
                let cancelTask = Task.Run(fun () -> cancellation.Cancel())
                do! Task.WhenAll(releaseTask, cancelTask)

                let! _results = batch.WaitAsync(TimeSpan.FromSeconds 5.0)

                iterations <- iterations + 1

                capturedWhileCancelled <-
                    capturedWhileCancelled
                    + (runner.QueuedCapturedWhileCancelled |> Array.filter id |> Array.length)

            Assert.That(
                capturedWhileCancelled,
                Is.LessThanOrEqualTo(iterations / 5),
                "a command still waiting for its concurrency slot must (overwhelmingly) never enter capture once its token already shows cancellation requested"
            )
        }
        :> Task

    [<Test>]
    member _.``outputAllWithPolicy rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllWithPolicy
                        1
                        runner
                        [ Command.create "must-not-run" ]
                        BatchPolicy.FailFast
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputAllWithPolicy rejects null commands before starting capture``() =
        let runner = BoundaryValidationRunner()
        let commands = Unchecked.defaultof<seq<Command>>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllWithPolicy
                        1
                        (runner :> IProcessRunner)
                        commands
                        BatchPolicy.FailFast
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytesWithPolicy rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllBytesWithPolicy
                        1
                        runner
                        [ Command.create "must-not-run" ]
                        BatchPolicy.FailFast
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputAllWithPolicy rejects a null policy before starting capture``() =
        let runner = BoundaryValidationRunner()
        let nullPolicy = Unchecked.defaultof<BatchPolicy>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllWithPolicy
                        1
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        nullPolicy
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "policy")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputAllBytesWithPolicy rejects a null policy before starting capture``() =
        let runner = BoundaryValidationRunner()
        let nullPolicy = Unchecked.defaultof<BatchPolicy>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputAllBytesWithPolicy
                        1
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        nullPolicy
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "policy")

        Assert.That(runner.CaptureCount, Is.Zero)

    // ── Exec.outputStream / outputStreamBytes: the completion-ordered fan-out ──────────────────

    [<Test>]
    member _.``outputStream yields each result in completion order, not input order``() : Task =
        task {
            // "fast" is released before anything is enumerated and "slow" is never released until the
            // assertion below has already run, so the FIRST item can only be the later-indexed "fast":
            // deterministic proof that a fast command does not wait behind a slow sibling.
            let runner = PacedRunner()
            runner.Release "fast"

            let stream =
                Exec.outputStream
                    2
                    (runner :> IProcessRunner)
                    [ Command.create "slow"; Command.create "fast" ]
                    CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            let! first = BatchStreaming.expect enumerator
            Assert.That(first.Index, Is.EqualTo 1)

            match first.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "fast")
            | Error error -> Assert.Fail $"the fast command failed: {error}"

            runner.Release "slow"
            let! second = BatchStreaming.expect enumerator
            Assert.That(second.Index, Is.EqualTo 0)

            match second.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "slow")
            | Error error -> Assert.Fail $"the slow command failed: {error}"

            do! BatchStreaming.expectEnd enumerator
        }
        :> Task

    [<Test>]
    member _.``outputStream tags every item with its own input index when completion order is shuffled``() : Task =
        task {
            // Release one command at a time, in an order that matches neither the input order nor its
            // reverse: each item can therefore only be the one just released, so the indices the stream
            // reports are checked against a known-correct expectation rather than against itself.
            let runner = PacedRunner()

            let stream =
                Exec.outputStream
                    4
                    (runner :> IProcessRunner)
                    [ Command.create "cmd0"
                      Command.create "cmd1"
                      Command.create "cmd2"
                      Command.create "cmd3" ]
                    CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            for expectedIndex in [ 2; 0; 3; 1 ] do
                let program = $"cmd{expectedIndex}"
                runner.Release program
                let! item = BatchStreaming.expect enumerator
                Assert.That(item.Index, Is.EqualTo expectedIndex)

                match item.Result with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo program)
                | Error error -> Assert.Fail $"{program} failed: {error}"

            do! BatchStreaming.expectEnd enumerator
        }
        :> Task

    [<Test>]
    member _.``outputStream never runs more than concurrency commands at once, and does reach the cap``() : Task =
        task {
            // The probe holds every capture until `cap` of them are live at the same time, so the peak
            // is a genuine simultaneous-overlap measurement: too FEW live at once would stall the wait
            // (bounded, so it fails rather than hangs) and too many would show up as a higher peak.
            let cap = 2
            let runner = ConcurrencyProbeRunner cap

            let commands = [ for i in 0..5 -> Command.create $"cmd{i}" ]

            let stream =
                Exec.outputStream cap (runner :> IProcessRunner) commands CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            let! items = BatchStreaming.drain enumerator
            Assert.That(items.Count, Is.EqualTo 6)

            let failures =
                items
                |> Seq.filter (fun item ->
                    match item.Result with
                    | Ok _ -> false
                    | Error _ -> true)
                |> Seq.length

            Assert.That(failures, Is.Zero)
            Assert.That(runner.Peak, Is.LessThanOrEqualTo cap)
            Assert.That(runner.Peak, Is.EqualTo cap)
        }
        :> Task

    [<Test>]
    member _.``outputStream keeps items already yielded when the batch is cancelled mid-stream``() : Task =
        task {
            // Concurrency 1, so "completed" runs, "holding" takes the freed slot, and "queued" is still
            // waiting for one when the caller cancels: the already-yielded item stays valid, the
            // already-running command keeps its own outcome, and the never-started one becomes
            // `Cancelled` — the same three outcomes `outputAll` produces for this batch, just streamed.
            let runner = QueueBlockingRunner()
            use cancellation = new CancellationTokenSource()

            let stream =
                Exec.outputStream
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "completed"
                      Command.create "holding"
                      Command.create "queued" ]
                    cancellation.Token

            use enumerator = stream.GetAsyncEnumerator()

            let! completed = BatchStreaming.expect enumerator
            Assert.That(completed.Index, Is.EqualTo 0)

            do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            cancellation.Cancel()

            // "holding" ignores its capture token, so nothing else can finish until it is released:
            // the next item can only be the never-started "queued" one.
            let! cancelled = BatchStreaming.expect enumerator
            Assert.That(cancelled.Index, Is.EqualTo 2)

            match cancelled.Result with
            | Error(ProcessError.Cancelled "queued") -> ()
            | other -> Assert.Fail $"expected the not-yet-started command to be cancelled, got {other}"

            runner.ReleaseHolding()
            let! held = BatchStreaming.expect enumerator
            Assert.That(held.Index, Is.EqualTo 1)

            match held.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "holding")
            | Error error -> Assert.Fail $"the already-running command changed result: {error}"

            do! BatchStreaming.expectEnd enumerator

            // The item handed over before the cancellation is still the consumer's, untouched.
            match completed.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "completed")
            | Error error -> Assert.Fail $"an already-yielded item did not survive cancellation: {error}"

            // "queued" never entered capture at all (`QueueBlockingRunner` would have thrown).
            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "completed"; "holding" |]))
        }
        :> Task

    [<Test>]
    member _.``outputStream honours the token the consumer passes to GetAsyncEnumerator``() : Task =
        task {
            // `await foreach (... .WithCancellation(tok))` must not be silently ignored: the
            // enumerator's own token cancels the batch exactly like the verb's own token does.
            let runner = QueueBlockingRunner()
            use cancellation = new CancellationTokenSource()

            let stream =
                Exec.outputStream
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "completed"
                      Command.create "holding"
                      Command.create "queued" ]
                    CancellationToken.None

            let enumerator = stream.GetAsyncEnumerator cancellation.Token

            try
                let! completed = BatchStreaming.expect enumerator
                Assert.That(completed.Index, Is.EqualTo 0)

                do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
                cancellation.Cancel()

                let! cancelled = BatchStreaming.expect enumerator
                Assert.That(cancelled.Index, Is.EqualTo 2)

                match cancelled.Result with
                | Error(ProcessError.Cancelled "queued") -> ()
                | other -> Assert.Fail $"expected the not-yet-started command to be cancelled, got {other}"

                runner.ReleaseHolding()
                let! _held = BatchStreaming.expect enumerator
                do! BatchStreaming.expectEnd enumerator
                Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "completed"; "holding" |]))
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
        :> Task

    [<Test>]
    member _.``outputStream never lets a racing slot acquisition enter capture``() : Task =
        task {
            // The streaming fan-out shares the buffering one's slot-acquisition helper precisely so
            // this guarantee cannot drift between them, and this is the streaming half of that proof.
            // `SemaphoreSlim.WaitAsync` can complete successfully even though its token is ALREADY
            // cancelled at that exact moment, when the slot's `Release()` and the cancellation request
            // race each other inside `SemaphoreSlim` itself — so force that race deliberately (release
            // the slot "queued" is waiting on, from one thread, against `Cancel()` from another, over
            // many iterations) rather than relying on incidental timing. `RacingSlotRunner` records,
            // at the exact moment "queued" enters capture (if it ever does), whether its token already
            // showed cancellation requested right then: with the recheck in place that can only ever
            // read `false`. The same benign, narrower jitter the buffering test documents applies here
            // (the runner reads the token one hop later than the recheck does, and "queued" may also
            // legitimately start a moment before cancellation is even requested), so tolerate the same
            // generous minority instead of demanding literally zero.
            let mutable capturedWhileCancelled = 0
            let mutable iterations = 0

            for _ in 1..500 do
                let runner = RacingSlotRunner()
                use cancellation = new CancellationTokenSource()

                let stream =
                    Exec.outputStream
                        1
                        (runner :> IProcessRunner)
                        [ Command.create "holding"; Command.create "queued" ]
                        cancellation.Token

                let enumerator = stream.GetAsyncEnumerator()

                // The fan-out starts on the first pull, so start it before racing the two threads.
                let firstItem = enumerator.MoveNextAsync().AsTask()
                do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)

                let releaseTask = Task.Run(fun () -> runner.ReleaseHolding())
                let cancelTask = Task.Run(fun () -> cancellation.Cancel())
                do! Task.WhenAll(releaseTask, cancelTask)

                let! _moved = firstItem.WaitAsync(TimeSpan.FromSeconds 5.0)
                let! _rest = BatchStreaming.drain enumerator
                do! enumerator.DisposeAsync().AsTask()

                iterations <- iterations + 1

                capturedWhileCancelled <-
                    capturedWhileCancelled
                    + (runner.QueuedCapturedWhileCancelled |> Array.filter id |> Array.length)

            Assert.That(
                capturedWhileCancelled,
                Is.LessThanOrEqualTo(iterations / 5),
                "a command still waiting for its concurrency slot must (overwhelmingly) never enter capture once its token already shows cancellation requested"
            )
        }
        :> Task

    [<Test>]
    member _.``outputStream abandoned mid-fan-out cancels in-flight commands and starts no queued one``() : Task =
        task {
            // Walking away from the stream is this port's `Drop`: the in-flight command's own capture
            // token fires (a real runner kills its tree there), the command still waiting for a slot
            // never starts at all, and disposal waits for that teardown instead of detaching it.
            //
            // Concurrency 1, so the batch is unambiguously in the intended state when it is abandoned:
            // "fast" has finished and freed its slot, "holding" holds the only one, and "queued" is
            // still waiting for it (with a wider cap "queued" would legitimately start the moment
            // "fast" finished, and the abandonment would prove nothing about a not-yet-started command).
            let runner = AbandonedStreamRunner()

            let stream =
                Exec.outputStream
                    1
                    (runner :> IProcessRunner)
                    [ Command.create "fast"; Command.create "holding"; Command.create "queued" ]
                    CancellationToken.None

            let enumerator = stream.GetAsyncEnumerator()

            let! first = BatchStreaming.expect enumerator
            Assert.That(first.Index, Is.EqualTo 0)
            do! runner.HoldingStarted.WaitAsync(TimeSpan.FromSeconds 2.0)

            // Abandon it: exactly what breaking out of an `await foreach` does.
            do! enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.0)

            do! runner.HoldingCancelled.WaitAsync(TimeSpan.FromSeconds 2.0)
            Assert.That(runner.CapturedPrograms, Is.EquivalentTo([| "fast"; "holding" |]))

            // The item taken before abandoning is still the consumer's.
            match first.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "fast")
            | Error error -> Assert.Fail $"an already-yielded item did not survive abandonment: {error}"
        }
        :> Task

    [<Test>]
    member _.``outputStream stops starting commands once the hand-off buffer and every slot are full``() : Task =
        task {
            // The published backpressure contract — "once the buffer and the live commands are full,
            // nothing further starts". A consumer that takes one item and then stops reading may see at
            // most buffer (2) + one parked command per slot (2) + the item it took (1) = 5 of the 12
            // commands started. An unbounded hand-off, or releasing the concurrency slot BEFORE handing
            // the item over, would run the whole batch ahead into memory and start all 12.
            let concurrency = 2
            let bound = HandOff.allowedStarts concurrency 1
            let! runner, enumerator, _first = HandOff.saturate concurrency 12 CancellationToken.None

            // Proving the negative without a guessed sleep: a fan-out that ignores the bound trips
            // `Overrun` as fast as it can start one more command, so this window ends early on a
            // regression and only ever costs its full length on the green path.
            let! _ = Task.WhenAny(runner.Overrun, Task.Delay(TimeSpan.FromMilliseconds 500.0))

            Assert.That(
                runner.Started,
                Is.EqualTo bound,
                "a consumer that stopped reading must stop the fan-out at buffer + live commands"
            )

            do! enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.0)
        }
        :> Task

    [<Test>]
    member _.``outputStream disposal completes when commands are parked on a full hand-off``() : Task =
        task {
            // The dangerous half of abandonment (KB K-108): the consumer walks away while the buffer is
            // full and finished commands are parked on the hand-off with nobody to give their items to.
            // If disposal cancelled only the RUNS, those parked hand-offs would never wake, the fan-out
            // it then awaits would never finish, and an ordinary `await foreach` + `break` would hang
            // FOREVER in the consumer's own code. A finite deadline is the whole point of the test.
            //
            // The sibling abandonment test above cannot cover this: it reads every item it produces, so
            // its channel is empty and no command is ever parked on the hand-off.
            let concurrency = 2
            let bound = HandOff.allowedStarts concurrency 1
            let! runner, enumerator, _first = HandOff.saturate concurrency 12 CancellationToken.None

            let disposal = enumerator.DisposeAsync().AsTask()
            let! winner = Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds 10.0))

            Assert.That(
                obj.ReferenceEquals(winner, disposal),
                Is.True,
                "disposing an abandoned stream hung on the commands parked on its full hand-off"
            )

            do! disposal

            // Teardown cancels the runs BEFORE it frees the parked hand-offs, so the slots those parked
            // commands give up on their way out can never start a command the abandoned batch had
            // already promised to leave unstarted.
            Assert.That(
                runner.Started,
                Is.EqualTo bound,
                "abandoning the stream started a command that was still waiting for a concurrency slot"
            )
        }
        :> Task

    [<Test>]
    member _.``outputStream still delivers every item when a full hand-off is cancelled mid-fan-out``() : Task =
        task {
            // Cancelling the batch is not abandoning it: every command still owes the consumer exactly
            // one item, including the ones parked on a full hand-off and the ones that never started.
            // That is why the hand-off has its own disposal-only token — cancelling the RUNS through the
            // same token the hand-off waits on would silently drop precisely the items already computed.
            let concurrency = 2
            let bound = HandOff.allowedStarts concurrency 1
            use cancellation = new CancellationTokenSource()
            let! runner, enumerator, first = HandOff.saturate concurrency 12 cancellation.Token

            cancellation.Cancel()

            let! rest = BatchStreaming.drain enumerator
            do! enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.0)

            let items = ResizeArray<BatchItem<string>>()
            items.Add first
            items.AddRange rest

            Assert.That(
                items.Count,
                Is.EqualTo 12,
                "a cancelled batch dropped items that were already parked on the full hand-off"
            )

            Assert.That(items |> Seq.map (fun item -> item.Index) |> Seq.toArray, Is.EquivalentTo [| 0..11 |])
            Assert.That(runner.Started, Is.EqualTo bound)

            // The ones that had already run keep their own results; the ones the cancellation caught
            // still waiting for a slot are `Cancelled` — never a truncated stream.
            let succeeded =
                items
                |> Seq.filter (fun item ->
                    match item.Result with
                    | Ok _ -> true
                    | Error _ -> false)
                |> Seq.length

            let cancelled =
                items
                |> Seq.filter (fun item ->
                    match item.Result with
                    | Error(ProcessError.Cancelled _) -> true
                    | _ -> false)
                |> Seq.length

            Assert.That(succeeded, Is.EqualTo bound)
            Assert.That(cancelled, Is.EqualTo(12 - bound))
        }
        :> Task

    [<Test>]
    member _.``outputStream does not short-circuit on a command's error``() : Task =
        task {
            let runner = MixedOutcomeRunner()

            let stream =
                Exec.outputStream
                    3
                    (runner :> IProcessRunner)
                    [ Command.create "ok-0"; Command.create "boom"; Command.create "ok-2" ]
                    CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            let! items = BatchStreaming.drain enumerator
            Assert.That(items.Count, Is.EqualTo 3)

            let byIndex = items |> Seq.map (fun item -> item.Index, item.Result) |> dict

            match byIndex[0] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok-0")
            | Error error -> Assert.Fail $"the first command failed: {error}"

            match byIndex[1] with
            | Error(ProcessError.Io "boom") -> ()
            | other -> Assert.Fail $"the failing command should keep its own error, got {other}"

            match byIndex[2] with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok-2")
            | Error error -> Assert.Fail $"a sibling of the failing command was short-circuited: {error}"
        }
        :> Task

    [<Test>]
    member _.``outputStream starts nothing until it is enumerated``() : Task =
        task {
            let runner = StreamStartCountingRunner()

            let stream =
                Exec.outputStream
                    4
                    (runner :> IProcessRunner)
                    [ Command.create "cmd0"; Command.create "cmd1" ]
                    CancellationToken.None

            do! Task.Delay(TimeSpan.FromMilliseconds 50.0)
            Assert.That(runner.CaptureCount, Is.Zero)

            use enumerator = stream.GetAsyncEnumerator()

            let! items = BatchStreaming.drain enumerator
            Assert.That(items.Count, Is.EqualTo 2)
            Assert.That(runner.CaptureCount, Is.EqualTo 2)
        }
        :> Task

    [<Test>]
    member _.``outputStream on an empty batch yields nothing``() : Task =
        task {
            let runner = StreamStartCountingRunner()

            let stream =
                Exec.outputStream 4 (runner :> IProcessRunner) [] CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            do! BatchStreaming.expectEnd enumerator
            Assert.That(runner.CaptureCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``outputStream applies each command's own Retry policy``() : Task =
        task {
            // Routed through the verb layer (`Runner.outputString`), not the raw capture seam, so the
            // command's own `Retry` turns the runner's first transient error into a successful item.
            let runner = FlakyRunner()

            let command =
                Command.create "flaky" |> Command.retry 2 TimeSpan.Zero (fun _ -> true)

            let stream =
                Exec.outputStream 1 (runner :> IProcessRunner) [ command ] CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            let! item = BatchStreaming.expect enumerator
            Assert.That(item.Index, Is.Zero)

            match item.Result with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "flaky")
            | Error error -> Assert.Fail $"the retry policy did not apply: {error}"

            Assert.That(runner.AttemptsFor "flaky", Is.EqualTo 2)
            do! BatchStreaming.expectEnd enumerator
        }
        :> Task

    [<Test>]
    member _.``outputStreamBytes captures raw stdout tagged with its input index``() : Task =
        task {
            let runner = BytesEchoRunner()

            let stream =
                Exec.outputStreamBytes
                    2
                    (runner :> IProcessRunner)
                    [ Command.create "first"; Command.create "second" ]
                    CancellationToken.None

            use enumerator = stream.GetAsyncEnumerator()

            let! items = BatchStreaming.drain enumerator
            Assert.That(items.Count, Is.EqualTo 2)

            let decoded =
                items
                |> Seq.map (fun item ->
                    match item.Result with
                    | Ok result -> item.Index, Text.Encoding.UTF8.GetString result.Stdout
                    | Error error -> item.Index, $"unexpected error: {error}")
                |> dict

            Assert.That(decoded[0], Is.EqualTo "first")
            Assert.That(decoded[1], Is.EqualTo "second")
        }
        :> Task

    [<Test>]
    member _.``outputStream rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputStream 1 runner [ Command.create "must-not-run" ] CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputStream rejects null commands before starting capture``() =
        let runner = BoundaryValidationRunner()
        let commands = Unchecked.defaultof<seq<Command>>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputStream 1 (runner :> IProcessRunner) commands CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputStream rejects a null command before starting capture``() =
        let runner = BoundaryValidationRunner()
        let nullCommand = Unchecked.defaultof<Command>

        let ex =
            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    Exec.outputStream
                        1
                        (runner :> IProcessRunner)
                        [| Command.create "must-not-run"; nullCommand |]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "commands")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputStream rejects zero concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputStream
                        0
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)

    [<Test>]
    member _.``outputStreamBytes rejects a null runner at the boundary``() =
        let runner = Unchecked.defaultof<IProcessRunner>

        let ex =
            Assert.Throws<ArgumentNullException>(
                Action(fun () ->
                    Exec.outputStreamBytes 1 runner [ Command.create "must-not-run" ] CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "runner")

    [<Test>]
    member _.``outputStreamBytes rejects negative concurrency before starting capture``() =
        let runner = BoundaryValidationRunner()

        let ex =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Exec.outputStreamBytes
                        -1
                        (runner :> IProcessRunner)
                        [ Command.create "must-not-run" ]
                        CancellationToken.None
                    |> ignore)
            )

        match ex with
        | null -> Assert.Fail("Assert.Throws did not return an exception.")
        | ex -> Assert.That(ex.ParamName, Is.EqualTo "concurrency")

        Assert.That(runner.CaptureCount, Is.Zero)
