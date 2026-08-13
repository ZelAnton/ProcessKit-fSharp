namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

type private RetryTimer(callback: TimerCallback, state: obj | null) =
    let mutable disposed = 0

    member _.Fire() =
        if Volatile.Read(&disposed) = 0 then
            callback.Invoke state

    interface ITimer with
        member _.Change(_dueTime, _period) = Volatile.Read(&disposed) = 0

        member _.Dispose() =
            Interlocked.Exchange(&disposed, 1) |> ignore

        member _.DisposeAsync() =
            Interlocked.Exchange(&disposed, 1) |> ignore
            ValueTask()

type private RetryTimeProvider() =
    inherit TimeProvider()

    let gate = obj ()
    let timers = ResizeArray<RetryTimer * TimeSpan>()
    let created = new SemaphoreSlim(0)

    override _.CreateTimer(callback, state, dueTime, _period) =
        let timer = new RetryTimer(callback, state)
        lock gate (fun () -> timers.Add(timer, dueTime))
        created.Release() |> ignore
        timer :> ITimer

    member _.WaitForTimer(index: int) : Task<TimeSpan> =
        task {
            let mutable found = None

            while found.IsNone do
                found <-
                    lock gate (fun () ->
                        if timers.Count > index then
                            Some(snd timers[index])
                        else
                            None)

                if found.IsNone then
                    do! created.WaitAsync()

            return found.Value
        }

    member _.Fire(index: int) =
        let timer = lock gate (fun () -> fst timers[index])
        timer.Fire()

type private FirstLineFinishGate(stdinError: exn option) =
    let finishStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let finishOutcome =
        TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable killCount = 0
    let mutable tornDown = 0

    member _.FinishStarted = finishStarted.Task

    member _.KillCount = Volatile.Read(&killCount)

    member _.IsTornDown = Volatile.Read(&tornDown) = 1

    member _.Release(outcome: Outcome) =
        finishOutcome.TrySetResult(outcome) |> ignore

    member _.Build(command: Command) : RunningProcess =
        let stdout = new MemoryStream(Encoding.UTF8.GetBytes "ready\n") :> Stream

        let host: RunningHost =
            { Config = command.Config
              Pid = None
              Stdout = Some stdout
              Stderr = None
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = 0L
              StartTimeIdentity = None
              Wait =
                fun () ->
                    finishStarted.TrySetResult() |> ignore
                    finishOutcome.Task
              // This double has no background feed, so its (already-decided) fault needs no bounded
              // observation window — it answers the verb's final observation immediately.
              StdinError = fun () -> Task.FromResult stdinError
              StdinFeedComplete = ignore
              StartKill = fun () -> Interlocked.Increment(&killCount) |> ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown =
                fun () ->
                    Interlocked.Exchange(&tornDown, 1) |> ignore
                    ValueTask() }

        new RunningProcess(host)

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
    member _.``a throwing retry predicate returns a typed terminal error with the original attempt``() : Task =
        task {
            let mutable calls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Exit("svc", 7, "stdout", "stderr")))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let throwing = fun _ -> raise (InvalidOperationException "classifier boom")

            let commands =
                [ Command.create "svc" |> Command.retry 3 TimeSpan.Zero throwing
                  Command.create "svc"
                  |> Command.retryBackoff 3 TimeSpan.Zero 2.0 TimeSpan.Zero false throwing ]

            for command in commands do
                let! result = command |> Runner.run alwaysFailing CancellationToken.None

                match result with
                | Error(ProcessError.RetryPredicate(program, original, detail)) ->
                    Assert.That(program, Is.EqualTo "svc")
                    Assert.That(original, Is.EqualTo(ProcessError.Exit("svc", 7, "stdout", "stderr")))
                    Assert.That(original.Stdout, Is.EqualTo(Some "stdout"))
                    Assert.That(original.Stderr, Is.EqualTo(Some "stderr"))
                    Assert.That(detail, Does.Contain "classifier boom")
                | other -> Assert.Fail $"expected RetryPredicate, got {other}"

            Assert.That(calls, Is.EqualTo 2, "a throwing predicate must not start another attempt")
        }
        :> Task

    [<Test>]
    member _.``a throwing retry predicate is typed across capture and exit verbs``() : Task =
        task {
            let mutable calls = 0

            let failing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Exit("svc", 7, "stdout", "stderr")))

                    member _.CaptureBytesAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Exit("svc", 7, "stdout", "stderr")))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let throwing = fun _ -> raise (InvalidOperationException "classifier boom")
            let command = Command.create "svc" |> Command.retry 3 TimeSpan.Zero throwing

            let assertRetryPredicate label result =
                match result with
                | Error(ProcessError.RetryPredicate(program, original, detail)) ->
                    Assert.That(program, Is.EqualTo "svc")
                    Assert.That(original, Is.EqualTo(ProcessError.Exit("svc", 7, "stdout", "stderr")))
                    Assert.That(detail, Does.Contain "classifier boom")
                | other -> Assert.Fail $"{label} returned {other}"

            let! outputStringResult = command |> Runner.outputString failing CancellationToken.None
            assertRetryPredicate "outputString" outputStringResult

            let! outputBytesResult = command |> Runner.outputBytes failing CancellationToken.None
            assertRetryPredicate "outputBytes" outputBytesResult

            let! runResult = command |> Runner.run failing CancellationToken.None
            assertRetryPredicate "run" runResult

            let! runUnitResult = command |> Runner.runUnit failing CancellationToken.None
            assertRetryPredicate "runUnit" runUnitResult

            let! exitCodeResult = command |> Runner.exitCode failing CancellationToken.None
            assertRetryPredicate "exitCode" exitCodeResult

            let! probeResult = command |> Runner.probe failing CancellationToken.None
            assertRetryPredicate "probe" probeResult

            Assert.That(calls, Is.EqualTo 6, "each common withRetry surface should stop after the callback fault")
        }
        :> Task

    [<Test>]
    member _.``a throwing retry predicate is not called when the retry budget has no retry``() : Task =
        task {
            let mutable calls = 0
            let mutable predicateCalls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Exit("svc", 3, "", "failed")))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let throwing =
                fun _ ->
                    predicateCalls <- predicateCalls + 1
                    raise (InvalidOperationException "must not run")

            let command = Command.create "svc" |> Command.retry 1 TimeSpan.Zero throwing
            let! result = command |> Runner.run alwaysFailing CancellationToken.None

            match result with
            | Error(ProcessError.Exit("svc", 3, "", "failed")) -> ()
            | other -> Assert.Fail $"expected the original terminal Exit, got {other}"

            Assert.That(calls, Is.EqualTo 1)
            Assert.That(predicateCalls, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``non-throwing retry predicates keep their true and false decisions``() : Task =
        let runWith predicate =
            task {
                let mutable calls = 0

                let flaky =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(_, _) =
                            calls <- calls + 1

                            if calls < 3 then
                                Task.FromResult(Error(ProcessError.Exit("svc", 1, "", "retryable")))
                            else
                                Task.FromResult(Ok(ProcessResult.Success "ok"))

                        member _.CaptureBytesAsync(command, _) =
                            Task.FromResult(Error(ProcessError.Unsupported command.Program))

                        member _.SpawnAsync(command, _) =
                            Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

                let command = Command.create "svc" |> Command.retry 3 TimeSpan.Zero predicate
                let! result = command |> Runner.run flaky CancellationToken.None
                return result, calls
            }

        task {
            let! falseResult, falseCalls = runWith (fun _ -> false)
            let! trueResult, trueCalls = runWith (fun _ -> true)

            match falseResult with
            | Error(ProcessError.Exit("svc", 1, "", "retryable")) -> ()
            | other -> Assert.Fail $"false shouldRetry should keep the first error, got {other}"

            match trueResult with
            | Ok "ok" -> ()
            | other -> Assert.Fail $"true shouldRetry should reach the successful attempt, got {other}"

            Assert.That(falseCalls, Is.EqualTo 1)
            Assert.That(trueCalls, Is.EqualTo 3)
        }
        :> Task

    [<Test>]
    member _.``Retry rejects negative delays at every builder entry point``() =
        let shouldRetry = Func<ProcessError, bool>(fun _ -> true)

        for delay in [ TimeSpan.FromTicks -1L; Timeout.InfiniteTimeSpan ] do
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> Command("svc").Retry(3, delay, shouldRetry) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> Command.create "svc" |> Command.retry 3 delay (fun _ -> true) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    CliClient("svc").WithDefaults(fun command -> command.Retry(3, delay, shouldRetry))
                    |> ignore)
            )
            |> ignore

        Command("svc").Retry(0, TimeSpan.Zero, shouldRetry) |> ignore
        Command("svc").Retry(1, TimeSpan.Zero, shouldRetry) |> ignore

    [<Test>]
    member _.``RetryBackoff validates every delay and factor at the builder boundary``() =
        let shouldRetry = Func<ProcessError, bool>(fun _ -> true)
        let zero = TimeSpan.Zero

        for delay in [ TimeSpan.FromTicks -1L; Timeout.InfiniteTimeSpan ] do
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> Command("svc").RetryBackoff(3, delay, 2.0, zero, true, shouldRetry) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Command.create "svc"
                    |> Command.retryBackoff 3 zero 2.0 delay true (fun _ -> true)
                    |> ignore)
            )
            |> ignore

        for factor in [ 0.99; Double.NaN; Double.PositiveInfinity; Double.NegativeInfinity ] do
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    CliClient("svc")
                        .WithDefaults(fun command -> command.RetryBackoff(3, zero, factor, zero, true, shouldRetry))
                    |> ignore)
            )
            |> ignore

        Command("svc").RetryBackoff(1, zero, 1.0, zero, false, shouldRetry) |> ignore

    [<Test>]
    member _.``RetryBackoff inherits through CliClient and uses TimeProvider with deterministic jitter``() : Task =
        task {
            let provider = RetryTimeProvider()
            let samples = System.Collections.Generic.Queue<float>([ 0.0; 0.5; 0.9 ])
            let mutable calls = 0

            let flaky =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls < 4 then
                            Task.FromResult(Ok(ProcessResult.Failure "" "transient" 1))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "ready"))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let client =
                CliClient("svc")
                    .WithRunner(flaky)
                    .WithDefaults(fun command ->
                        command.RetryBackoff(
                            4,
                            TimeSpan.FromMilliseconds 100.0,
                            2.0,
                            TimeSpan.FromMilliseconds 250.0,
                            true,
                            Func<ProcessError, bool>(fun _ -> true)
                        ))

            let command =
                client.Command([]).TimeProvider(provider).WithRetryJitterSource(fun () -> samples.Dequeue())

            let run = command |> Runner.run flaky CancellationToken.None
            let expected = [| 50.0; 200.0; 350.0 |]

            for index in 0 .. expected.Length - 1 do
                let! delay = provider.WaitForTimer index
                Assert.That(delay.TotalMilliseconds, Is.EqualTo(expected[index]).Within 0.001)
                provider.Fire index

            match! run with
            | Ok "ready" -> ()
            | other -> Assert.Fail $"expected the fourth attempt to succeed, got {other}"

            Assert.That(calls, Is.EqualTo 4)
            Assert.That(samples.Count, Is.EqualTo 0, "each retry must draw exactly one jitter sample")
        }
        :> Task

    [<Test>]
    member _.``CancelOn interrupts a retry backoff``() : Task =
        task {
            use cancelOn = new CancellationTokenSource()

            let firstAttemptFinished =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let mutable calls = 0

            let alwaysFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        firstAttemptFinished.TrySetResult() |> ignore
                        Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Failure [||] "boom" 1))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.cancelOn cancelOn.Token
                |> Command.retry 5 (TimeSpan.FromSeconds 60.0) (fun _ -> true)

            let run = command |> Runner.run alwaysFailing CancellationToken.None
            do! firstAttemptFinished.Task.WaitAsync(TimeSpan.FromSeconds 2.0)

            // Give the retry loop time to enter its deliberately long delay, then cancel only the
            // command-scoped token. The short completion bound proves the backoff did not sleep out.
            do! Task.Delay(TimeSpan.FromMilliseconds 100.0)
            cancelOn.Cancel()

            let! finished = Task.WhenAny(run :> Task, Task.Delay(TimeSpan.FromSeconds 2.0))

            Assert.That(
                obj.ReferenceEquals(finished, run),
                Is.True,
                "CancelOn should interrupt the retry backoff instead of waiting for its full delay"
            )

            match! run with
            | Error(ProcessError.Cancelled "svc") -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(calls, Is.EqualTo 1, "CancelOn must prevent a second attempt")
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

    [<Test>]
    member _.``firstLine reports cancellation raised inside the matching predicate``() : Task =
        task {
            use cts = new CancellationTokenSource()
            let scripted = ScriptedRunner().Fallback(Reply.Ok "ready\n") :> IProcessRunner
            let command = Command.create "first-line"

            let! result =
                Runner.firstLine
                    scripted
                    cts.Token
                    (fun _ ->
                        cts.Cancel()
                        true)
                    command

            match result with
            | Error(ProcessError.Cancelled "first-line") -> ()
            | other -> Assert.Fail $"expected predicate cancellation, got {other}"
        }
        :> Task

    [<Test>]
    member _.``firstLine gives cancellation priority after FinishAsync reaps the match``() : Task =
        task {
            use cts = new CancellationTokenSource()
            let gate = FirstLineFinishGate(None)
            let command = Command.create "first-line"

            let runner =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.SpawnAsync(_, _) = Task.FromResult(Ok(gate.Build command)) }

            let pending = Runner.firstLine runner cts.Token (fun line -> line = "ready") command
            do! gate.FinishStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            Assert.That(pending.IsCompleted, Is.False, "FinishAsync should still be waiting for the gated reap")

            cts.Cancel()
            Assert.That(gate.KillCount, Is.GreaterThanOrEqualTo 2, "cancellation must kill the live handle")
            gate.Release(Outcome.Exited 0)

            match! pending with
            | Error(ProcessError.Cancelled "first-line") -> ()
            | other -> Assert.Fail $"expected cancellation after FinishAsync, got {other}"

            Assert.That(gate.IsTornDown, Is.True, "the matched child must be reaped after cancellation")
        }
        :> Task

    [<Test>]
    member _.``firstLine returns None when stdout closes without a match``() : Task =
        task {
            let scripted = ScriptedRunner().Fallback(Reply.Ok "ready\n") :> IProcessRunner

            let! result =
                Runner.firstLine scripted CancellationToken.None (fun _ -> false) (Command.create "first-line")

            Assert.That(result, Is.EqualTo(Ok None: Result<string option, ProcessError>))
        }
        :> Task

    [<Test>]
    member _.``firstLine preserves a finish error after a matching line``() : Task =
        task {
            let gate =
                FirstLineFinishGate(Some(InvalidOperationException "synthetic stdin failure" :> exn))

            let command = Command.create "first-line"

            let runner =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.CaptureBytesAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program))

                    member _.SpawnAsync(_, _) = Task.FromResult(Ok(gate.Build command)) }

            let pending =
                Runner.firstLine runner CancellationToken.None (fun line -> line = "ready") command

            do! gate.FinishStarted.WaitAsync(TimeSpan.FromSeconds 2.0)
            gate.Release(Outcome.Exited 0)

            match! pending with
            | Error(ProcessError.Stdin("first-line", detail)) ->
                Assert.That(detail, Does.Contain "synthetic stdin failure")
            | other -> Assert.Fail $"expected the finish error, got {other}"
        }
        :> Task
