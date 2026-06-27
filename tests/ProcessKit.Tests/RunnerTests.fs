namespace ProcessKit.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

[<TestFixture>]
type RunnerTests() =

    // A subprocess-free runner: "echo" succeeds with output, "false" exits 1, "boom" exits 2.
    let runner: IProcessRunner =
        ScriptedRunner()
            .On([ "echo" ], Reply.Ok "hello\n")
            .On([ "false" ], Reply.Fail(1, ""))
            .On([ "boom" ], Reply.Fail(2, "kaboom"))

    [<Test>]
    member _.``run trims trailing whitespace and returns stdout``() : Task =
        task {
            let! result = Command.create "echo" |> Runner.run runner CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected Ok, got {error}"
        }
        :> Task

    [<Test>]
    member _.``run surfaces a non-zero exit as Exit``() : Task =
        task {
            let! result = Command.create "boom" |> Runner.run runner CancellationToken.None

            match result with
            | Error(ProcessError.Exit(_, code, _, stderr)) ->
                Assert.That(code, Is.EqualTo 2)
                Assert.That(stderr, Is.EqualTo "kaboom")
            | other -> Assert.Fail $"expected an Exit error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``outputString does not error on a non-zero exit``() : Task =
        task {
            let! result = Command.create "false" |> Runner.outputString runner CancellationToken.None

            match result with
            | Ok value ->
                Assert.That(value.Code, Is.EqualTo(Some 1))
                Assert.That(value.IsSuccess, Is.False)
            | Error error -> Assert.Fail $"expected Ok, got {error}"
        }
        :> Task

    [<Test>]
    member _.``exitCode returns the code``() : Task =
        task {
            let! result = Command.create "false" |> Runner.exitCode runner CancellationToken.None
            Assert.That(result, Is.EqualTo(Ok 1: Result<int, ProcessError>))
        }
        :> Task

    [<Test>]
    member _.``probe maps 0 to true and 1 to false``() : Task =
        task {
            let! pass = Command.create "echo" |> Runner.probe runner CancellationToken.None
            let! fail = Command.create "false" |> Runner.probe runner CancellationToken.None
            Assert.That(pass, Is.EqualTo(Ok true: Result<bool, ProcessError>))
            Assert.That(fail, Is.EqualTo(Ok false: Result<bool, ProcessError>))
        }
        :> Task

    [<Test>]
    member _.``probe errors on an unexpected exit code``() : Task =
        task {
            let! result = Command.create "boom" |> Runner.probe runner CancellationToken.None

            match result with
            | Error(ProcessError.Exit(_, 2, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected an Exit error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``retry never re-runs a cancelled error``() : Task =
        task {
            let mutable calls = 0

            let cancelling =
                { new IProcessRunner with
                    member _.OutputString(command, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Cancelled command.Program))

                    member _.OutputBytes(command, _) =
                        Task.FromResult(Error(ProcessError.Cancelled command.Program))

                    member _.Start(command, _) =
                        Task.FromResult(Error(ProcessError.Cancelled command.Program)) }

            // A retry policy that would re-run on ANY error, to prove the Cancelled short-circuit wins
            // (otherwise each attempt re-fails instantly and burns the whole budget).
            let command = Command.create "svc" |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.outputString cancelling CancellationToken.None

            match result with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task
