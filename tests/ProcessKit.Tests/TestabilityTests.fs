namespace ProcessKit.Tests

open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit
open ProcessKit.Testing

/// A decorator over `DelegatingProcessRunner` that counts and forwards the `CaptureString` seam.
type private CountingRunner(inner: IProcessRunner) =
    inherit DelegatingProcessRunner(inner)
    let mutable count = 0
    member _.Count = count

    override this.CaptureStringAsync(command, cancellationToken) =
        count <- count + 1
        base.CaptureStringAsync(command, cancellationToken)

/// Tests for the additive testability seams: FakeProcess, failable/timed-out Reply, ScriptedRunner.Start,
/// and DelegatingProcessRunner.
[<TestFixture>]
type TestabilityTests() =

    [<Test>]
    member _.``FakeProcess builds a RunningProcess the buffered verbs can consume``() : Task =
        task {
            use proc = FakeProcess.Create("svc").WithStdout("hello\nworld").WithExit(3).Build()

            match! proc.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "hello\nworld")
                Assert.That(result.Code, Is.EqualTo(Some 3))
            | Error error -> Assert.Fail $"FakeProcess OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``FakeProcess streams stdout lines``() : Task =
        task {
            use proc = FakeProcess.Create().WithStdoutLines([ "a"; "b"; "c" ]).Build()
            let collected = ResizeArray<string>()
            let enumerator = proc.StdoutLinesAsync().GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then
                    collected.Add enumerator.Current
                else
                    more <- false

            do! enumerator.DisposeAsync()
            CollectionAssert.AreEqual([| "a"; "b"; "c" |], collected.ToArray())
        }

    [<Test>]
    member _.``ScriptedRunner.StartAsync serves a fake streaming process``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "svc" ], Reply.Ok "ready\ngo")

            match! runner.StartAsync(Command.create "svc", CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc ->
                use proc = proc

                match! proc.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ready\ngo")
                | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a runner-level error``() : Task =
        task {
            let runner: IProcessRunner =
                ScriptedRunner().On([ "x" ], Reply.Error(ProcessError.NotFound("x", None)))

            match! runner.OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Error(ProcessError.NotFound _) -> ()
            | other -> Assert.Fail $"expected a NotFound error, got {other}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a timeout``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.TimedOut)

            match! runner.OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    [<Test>]
    member _.``DelegatingProcessRunner forwards and lets a decorator intercept``() : Task =
        task {
            let scripted: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.Ok "hi")
            let counter = CountingRunner scripted

            match! (counter :> IProcessRunner).OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "hi")
                Assert.That(counter.Count, Is.EqualTo 1)
            | Error error -> Assert.Fail $"forwarded OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner capture and Start agree byte-for-byte``() : Task =
        task {
            // Both seam paths route through the same FakeProcess, so a reply with internal *and*
            // trailing newlines is captured identically whether read via OutputString or via a
            // started RunningProcess — guarding against the capture path drifting from Start.
            let runner: IProcessRunner =
                ScriptedRunner().On([ "svc" ], Reply.Ok "first\nsecond\n")

            let command = Command.create "svc"

            let! captured = runner.OutputStringAsync(command, CancellationToken.None)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc ->
                use proc = proc
                let! streamed = proc.OutputStringAsync()

                match captured, streamed with
                | Ok cap, Ok str ->
                    Assert.That(str.Stdout, Is.EqualTo cap.Stdout)
                    Assert.That(str.Code, Is.EqualTo cap.Code)
                | _ -> Assert.Fail "expected both capture paths to succeed"
        }

    [<Test>]
    member _.``ScriptedRunner.OutputBytesAsync captures the scripted output``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.Ok "payload-bytes")

            match! runner.OutputBytesAsync(Command.create "x", CancellationToken.None) with
            | Ok result ->
                // ASCII payload, so the decoded bytes are encoding-agnostic.
                Assert.That(System.Text.Encoding.UTF8.GetString result.Stdout, Is.EqualTo "payload-bytes")
            | Error error -> Assert.Fail $"OutputBytes failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner honours the command's OkCodes``() : Task =
        task {
            // A non-zero exit the command accepts must surface as success through the scripted seam.
            let runner: IProcessRunner = ScriptedRunner().On([ "svc" ], Reply.Exit 3)
            let command = Command.create "svc" |> Command.okCodes [ 0; 3 ]

            match! runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.True)
                Assert.That(result.Code, Is.EqualTo(Some 3))
                CollectionAssert.AreEqual([| 0; 3 |], result.AcceptedCodes)
            | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``FakeProcess.OfCommand carries the command's OkCodes into IsSuccess``() : Task =
        task {
            let command = Command.create "svc" |> Command.okCodes [ 0; 3 ]
            use proc = FakeProcess.OfCommand(command).WithExit(3).Build()

            match! proc.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.True)
                CollectionAssert.AreEqual([| 0; 3 |], result.AcceptedCodes)
            | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ProcessResult.Success builds an accepted result``() =
        let result = ProcessResult.Success "hello"
        Assert.That(result.Stdout, Is.EqualTo "hello")
        Assert.That(result.Code, Is.EqualTo(Some 0))
        Assert.That(result.IsSuccess, Is.True)

        match ProcessResult.ensureSuccess result with
        | Ok ok -> Assert.That(ok.Stdout, Is.EqualTo "hello")
        | Error error -> Assert.Fail $"expected success, got {error}"

    [<Test>]
    member _.``ProcessResult.Success works for a byte[] capture``() =
        let result = ProcessResult.Success [| 1uy; 2uy; 3uy |]
        CollectionAssert.AreEqual([| 1uy; 2uy; 3uy |], result.Stdout)
        Assert.That(result.IsSuccess, Is.True)

    [<Test>]
    member _.``ProcessResult.Failure rejects a zero exit code``() =
        Assert.Throws<System.ArgumentOutOfRangeException>(
            System.Action(fun () -> ProcessResult.Failure "" "" 0 |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``ProcessResult.Failure builds a non-zero exit that EnsureSuccess rejects``() =
        let result = ProcessResult.Failure "out" "boom" 2
        Assert.That(result.Code, Is.EqualTo(Some 2))
        Assert.That(result.Stderr, Is.EqualTo "boom")
        Assert.That(result.IsSuccess, Is.False)

        match ProcessResult.ensureSuccess result with
        | Error(ProcessError.Exit(_, code, _, stderr)) ->
            Assert.That(code, Is.EqualTo 2)
            Assert.That(stderr, Is.EqualTo "boom")
        | other -> Assert.Fail $"expected an Exit error, got {other}"

    [<Test>]
    member _.``ProcessResult.Create carries a chosen Outcome and duration``() =
        let result =
            ProcessResult.Create "" "" Outcome.TimedOut (System.TimeSpan.FromSeconds 5.0)

        Assert.That(result.IsTimedOut, Is.True)
        Assert.That(result.IsSuccess, Is.False)
        Assert.That(result.Duration, Is.EqualTo(System.TimeSpan.FromSeconds 5.0))

        match ProcessResult.ensureSuccess result with
        | Error(ProcessError.Timeout _) -> ()
        | other -> Assert.Fail $"expected a Timeout error, got {other}"
