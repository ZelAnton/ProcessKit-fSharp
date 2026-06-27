namespace ProcessKit.Tests

open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit
open ProcessKit.Testing

/// A decorator over `DelegatingProcessRunner` that counts and forwards `OutputString`.
type private CountingRunner(inner: IProcessRunner) =
    inherit DelegatingProcessRunner(inner)
    let mutable count = 0
    member _.Count = count

    override this.OutputString(command, cancellationToken) =
        count <- count + 1
        base.OutputString(command, cancellationToken)

/// Tests for the additive testability seams: FakeProcess, failable/timed-out Reply, ScriptedRunner.Start,
/// and DelegatingProcessRunner.
[<TestFixture>]
type TestabilityTests() =

    [<Test>]
    member _.``FakeProcess builds a RunningProcess the buffered verbs can consume``() : Task =
        task {
            use proc = FakeProcess.Create("svc").WithStdout("hello\nworld").WithExit(3).Build()

            match! proc.OutputString() with
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
            let enumerator = proc.StdoutLines().GetAsyncEnumerator()
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
    member _.``ScriptedRunner.Start serves a fake streaming process``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "svc" ], Reply.Ok "ready\ngo")

            match! runner.Start(Command.create "svc", CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc ->
                use proc = proc

                match! proc.OutputString() with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ready\ngo")
                | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a runner-level error``() : Task =
        task {
            let runner: IProcessRunner =
                ScriptedRunner().On([ "x" ], Reply.Error(ProcessError.NotFound("x", None)))

            match! runner.OutputString(Command.create "x", CancellationToken.None) with
            | Error(ProcessError.NotFound _) -> ()
            | other -> Assert.Fail $"expected a NotFound error, got {other}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a timeout``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.TimedOut)

            match! runner.OutputString(Command.create "x", CancellationToken.None) with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    [<Test>]
    member _.``DelegatingProcessRunner forwards and lets a decorator intercept``() : Task =
        task {
            let scripted: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.Ok "hi")
            let counter = CountingRunner scripted

            match! (counter :> IProcessRunner).OutputString(Command.create "x", CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "hi")
                Assert.That(counter.Count, Is.EqualTo 1)
            | Error error -> Assert.Fail $"forwarded OutputString failed: {error.Message}"
        }
