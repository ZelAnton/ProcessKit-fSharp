namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

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
