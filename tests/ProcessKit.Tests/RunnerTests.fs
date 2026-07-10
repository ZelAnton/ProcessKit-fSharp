namespace ProcessKit.Tests

open System
open System.IO
open System.Text
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
                    member _.CaptureStringAsync(command, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Cancelled command.Program))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Cancelled command.Program))

                    member _.SpawnAsync(command, _) =
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

    [<Test>]
    member _.``retry does not re-run the command when only the parser fails``() : Task =
        task {
            let mutable calls = 0

            let succeeding =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Success "raw output"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            // Retry on ANY error, including a parse failure — yet a parser that rejects a *successfully*
            // produced output must not re-spawn the command: the run is retried, the parse is not.
            let command = Command.create "svc" |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result =
                command
                |> Runner.parse succeeding CancellationToken.None (fun _ -> failwith "boom")

            match result with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``RetryNever overrides a default Retry inherited from CliClient.WithDefaults``() : Task =
        task {
            let mutable calls = 0

            // Every attempt fails with a non-zero exit, and `shouldRetry` unconditionally accepts it —
            // if `RetryNever` did not win, the loop would burn all 3 configured attempts.
            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let client =
                CliClient
                    .create("svc")
                    .WithRunner(alwaysFailing)
                    .WithDefaults(fun c -> c.Retry(3, TimeSpan.Zero, fun _ -> true))

            // Build a command through the client (inheriting the template's default Retry), then
            // explicitly opt out on top of it.
            let command = client.Command([]).RetryNever()

            let! result = command |> Runner.run alwaysFailing CancellationToken.None

            match result with
            | Error(ProcessError.Exit(_, 1, _, _)) -> ()
            | other -> Assert.Fail $"expected an Exit error, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``RetryNever and an unset Retry both run once, but are distinct configuration states``() : Task =
        task {
            let unset = Command.create "svc"
            let retryNever = Command.create "svc" |> Command.retryNever

            // Different observable configuration: `Retry` stays `None` either way, but `RetryDisabled`
            // distinguishes "no policy" from "explicitly disabled" — the whole point of the new signal.
            Assert.That(unset.Config.Retry, Is.EqualTo None)
            Assert.That(unset.Config.RetryDisabled, Is.False)
            Assert.That(retryNever.Config.Retry, Is.EqualTo None)
            Assert.That(retryNever.Config.RetryDisabled, Is.True)

            // Same observable single-run behaviour for both, against a runner that would happily be
            // retried (no `Retry` policy is configured on either command, so nothing schedules a retry).
            let counting () =
                let mutable calls = 0

                let runner =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(_, _) =
                            calls <- calls + 1
                            Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                        member _.CaptureBytesAsync(_, _) =
                            Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                        member _.SpawnAsync(command, _) =
                            Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

                runner, (fun () -> calls)

            let unsetRunner, unsetCalls = counting ()
            let retryNeverRunner, retryNeverCalls = counting ()

            let! _ = unset |> Runner.run unsetRunner CancellationToken.None
            let! _ = retryNever |> Runner.run retryNeverRunner CancellationToken.None

            Assert.That(unsetCalls (), Is.EqualTo 1)
            Assert.That(retryNeverCalls (), Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``Retry after RetryNever in the same chain re-enables retrying (last call wins)``() : Task =
        task {
            let mutable calls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            // Order matters: `.RetryNever().Retry(...)` re-opts back in, the mirror image of
            // `.Retry(...).RetryNever()` (which suppresses it).
            let command =
                Command.create "svc"
                |> Command.retryNever
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            Assert.That(command.Config.RetryDisabled, Is.False)

            let! result = command |> Runner.run alwaysFailing CancellationToken.None

            match result with
            | Error(ProcessError.Exit(_, 1, _, _)) -> ()
            | other -> Assert.Fail $"expected an Exit error, got {other}"

            Assert.That(calls, Is.EqualTo 3)
        }
        :> Task

    // ----- one-shot stdin + retry (T-088) -----

    [<Test>]
    member _.``retry refuses a one-shot stdin source (FromStream) before the first attempt``() : Task =
        task {
            let mutable calls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run alwaysFailing CancellationToken.None

            match result with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected an Unsupported error, got {other}"

            // Refused before the first attempt — never spawned at all, so it can never observe the
            // stream already exhausted by a prior attempt.
            Assert.That(calls, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``retry refuses a one-shot stdin source (FromLines) before the first attempt``() : Task =
        task {
            let mutable calls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (
                    Stdin.FromLines(
                        seq {
                            "one"
                            "two"
                        }
                    )
                )
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run alwaysFailing CancellationToken.None

            match result with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected an Unsupported error, got {other}"

            Assert.That(calls, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``retry with a repeatable stdin source (FromString) is unaffected``() : Task =
        task {
            let mutable calls = 0

            // Fails twice, then succeeds on the 3rd attempt — proves the retry loop still runs
            // normally when the stdin source is repeatable.
            let flaky =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls < 3 then
                            Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromString "payload")
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run flaky CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected Ok, got {error}"

            Assert.That(calls, Is.EqualTo 3)
        }
        :> Task

    [<Test>]
    member _.``a single run (no retry) with a one-shot stdin source is unaffected``() : Task =
        task {
            let mutable calls = 0

            let succeeding =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // No `Retry` configured at all: a one-shot stdin source must run exactly like before —
            // only an active Retry with more than one attempt triggers the pre-flight refusal.
            let command = Command.create "svc" |> Command.stdin (Stdin.FromStream stream)

            let! result = command |> Runner.run succeeding CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected Ok, got {error}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``Retry(1, ...) with a one-shot stdin source is unaffected (a single run, not a retry)``() : Task =
        task {
            let mutable calls = 0

            let succeeding =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 1 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run succeeding CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected Ok, got {error}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``a pre-cancelled token makes the scripted runner report Cancelled``() : Task =
        task {
            use cts = new CancellationTokenSource()
            cts.Cancel()

            // "echo" is scripted to succeed; a cancelled token must still win, so the scripted seam is
            // honest about cancellation (and the Cancelled path is testable through it) like a real run.
            let! result = Command.create "echo" |> Runner.outputString runner cts.Token

            match result with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task
