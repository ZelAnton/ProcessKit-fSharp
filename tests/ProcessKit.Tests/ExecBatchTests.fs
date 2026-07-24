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
            failwith "this test runner only supports text capture"

/// A runner whose text-capture seam is cancelled independently of the verb token.
type private CaptureCancellingRunner() =
    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromException<Result<ProcessResult<string>, ProcessError>>(OperationCanceledException())

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

        member _.SpawnAsync(_command, _cancellationToken) =
            failwith "this test runner only supports text capture"

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
            failwith "this test runner only supports capture"

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
