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
            let runner = FailFastRunner()

            let! results =
                Exec.outputAllWithPolicy
                    2
                    (runner :> IProcessRunner)
                    [ Command.create "holding"; Command.create "failing" ]
                    BatchPolicy.FailFast
                    CancellationToken.None

            match results[0] with
            | Error(ProcessError.Cancelled "holding") -> ()
            | other -> Assert.Fail $"expected the already-running sibling to be cancelled, got {other}"

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
