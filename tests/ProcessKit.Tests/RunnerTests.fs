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

/// A `RunningProcess` over a host with no real child — enough for a runner double to route through the
/// real capture launch boundary (`CaptureVerbs.runToCompletion`). That boundary is what commits a
/// one-shot stdin payload once a child exists, so a double that goes through it can tell a pre-child
/// failure apart from a post-child one exactly as a live runner does.
module private LaunchedDouble =

    let runningProcess (command: Command) : RunningProcess =
        let host: RunningHost =
            { Config = command.Config
              Pid = None
              Stdout = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stderr = None
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = 0L
              StartTimeIdentity = None
              Wait = fun () -> Task.FromResult(Outcome.Exited 0)
              // No background feed on this double, so its (absent) stdin fault is already decided.
              StdinError = fun () -> Task.FromResult None
              StdinFeedComplete = ignore
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host)

/// A runner double that drains `stream` into its captured stdout — the same evidence a live child's
/// stdin feeder would leave behind — so a run that follows one which ended without a child can prove
/// it was handed the ORIGINAL payload rather than the empty remains of an exhausted source.
module private DrainingDouble =

    let over (stream: Stream) : IProcessRunner =
        { new IProcessRunner with
            member _.CaptureStringAsync(_, _) =
                task {
                    use fed = new MemoryStream()
                    do! stream.CopyToAsync fed
                    return Ok(ProcessResult.Success(Encoding.UTF8.GetString(fed.ToArray())))
                }

            member _.CaptureBytesAsync(_, _) =
                Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

            member _.SpawnAsync(command, _) =
                Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

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

    // ----- one-shot stdin + retry (T-088, narrowed to a post-attempt gate by T-340) -----

    [<Test>]
    member _.``a pre-spawn failure leaves a one-shot stdin source (FromStream) intact for the retry``() : Task =
        task {
            let mutable calls = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // Attempt 1 fails the way a transient spawn failure does — before any child exists, so
            // nothing has read the stream. Attempt 2 drains it, exactly as a live child's stdin feeder
            // would, and reports what it read: proof the retried attempt got the ORIGINAL payload
            // rather than the empty input an exhausted source would have fed it.
            let flaky =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls = 1 then
                            Task.FromResult(Error(ProcessError.Spawn("svc", "resource temporarily unavailable")))
                        else
                            task {
                                use fed = new MemoryStream()
                                do! stream.CopyToAsync fed
                                return Ok(ProcessResult.Success(Encoding.UTF8.GetString(fed.ToArray())))
                            }

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run flaky CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the retry to succeed on the original payload, got {error}"

            Assert.That(calls, Is.EqualTo 2)
        }
        :> Task

    [<Test>]
    member _.``a pre-spawn NotFound is retried for a one-shot stdin source (FromLines)``() : Task =
        task {
            let mutable calls = 0

            let flaky =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls = 1 then
                            Task.FromResult(Error(ProcessError.NotFound("svc", Some "/usr/bin")))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

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

            let! result = command |> Runner.run flaky CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected the retry to succeed, got {error}"

            // The program was never located, so no child could have enumerated the lines.
            Assert.That(calls, Is.EqualTo 2)
        }
        :> Task

    [<Test>]
    member _.``a post-child Exit ends a one-shot stdin run after exactly one attempt``() : Task =
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

            // A non-zero exit means a child ran and may already have drained the stream, so the run ends
            // on the first attempt's own error instead of replaying empty stdin into a second one.
            match result with
            | Error(ProcessError.Exit(_, 1, _, _)) -> ()
            | other -> Assert.Fail $"expected the first attempt's Exit error, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``a post-child Timeout ends a one-shot stdin run after exactly one attempt``() : Task =
        task {
            let mutable calls = 0

            let timingOut =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls = 1 then
                            Task.FromResult(
                                Error(ProcessError.Timeout("svc", TimeSpan.FromSeconds 5.0, "partial", "slow"))
                            )
                        else
                            Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run timingOut CancellationToken.None

            // The first error survives intact — it is not replaced by a later attempt's result, nor by a
            // `RetryPredicate` from a classifier the gate never consults.
            match result with
            | Error(ProcessError.Timeout(_, timeout, stdout, _)) ->
                Assert.That(timeout, Is.EqualTo(TimeSpan.FromSeconds 5.0))
                Assert.That(stdout, Is.EqualTo "partial")
            | other -> Assert.Fail $"expected the first attempt's Timeout error, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``a Cancelled error is never retried for a one-shot stdin source``() : Task =
        task {
            let mutable calls = 0

            let cancelling =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls = 1 then
                            Task.FromResult(Error(ProcessError.Cancelled "svc"))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run cancelling CancellationToken.None

            match result with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``an ambiguous Io error is never retried for a one-shot stdin source``() : Task =
        task {
            let mutable calls = 0

            // `Io` is what `ProcessError.isTransient` (and this command's own always-true classifier)
            // would happily retry — but it is raised both before a child and while driving a live one,
            // so it can never authorize re-feeding a one-shot payload.
            let failing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls = 1 then
                            Task.FromResult(Error(ProcessError.Io "the pipe broke"))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "hello"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run failing CancellationToken.None

            match result with
            | Error(ProcessError.Io detail) -> Assert.That(detail, Is.EqualTo "the pipe broke")
            | other -> Assert.Fail $"expected the first attempt's Io error, got {other}"

            Assert.That(calls, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``an Unsupported refused at the launch boundary is retried for a one-shot stdin source``() : Task =
        task {
            let mutable launches = 0

            // Routes through the real capture launch boundary: the first launch is refused before any
            // child exists (the shape of a platform primitive this host cannot honour), so nothing
            // committed the payload and the run may attempt again.
            let refusingOnce =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, cancellationToken) =
                        CaptureVerbs.runToCompletion
                            command
                            cancellationToken
                            (fun () ->
                                launches <- launches + 1

                                if launches = 1 then
                                    Task.FromResult(Error(ProcessError.Unsupported "Pty (needs Windows 10 1809+)"))
                                else
                                    Task.FromResult(Ok(LaunchedDouble.runningProcess command)))
                            (fun running ->
                                task {
                                    use _ = running
                                    return Ok(ProcessResult.Success "hello")
                                })

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run refusingOnce CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected the retry to succeed, got {error}"

            Assert.That(launches, Is.EqualTo 2)
        }
        :> Task

    [<Test>]
    member _.``an Unsupported raised after the child was launched is not retried for a one-shot stdin source``
        ()
        : Task =
        task {
            let mutable launches = 0

            // The same error CASE, on the other side of the launch boundary: a child exists (so the
            // launch committed the one-shot payload) and the failure arrives afterwards, the way a live
            // `RunningProcess` verb reports an unsupported operation. Evidence, not the case name,
            // decides — so this run must stop with one launch.
            let failingAfterLaunch =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, cancellationToken) =
                        CaptureVerbs.runToCompletion
                            command
                            cancellationToken
                            (fun () ->
                                launches <- launches + 1
                                Task.FromResult(Ok(LaunchedDouble.runningProcess command)))
                            (fun running ->
                                task {
                                    use _ = running
                                    return Error(ProcessError.Unsupported "Resize (not a PTY run)")
                                })

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run failingAfterLaunch CancellationToken.None

            match result with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Is.EqualTo "Resize (not a PTY run)")
            | other -> Assert.Fail $"expected the post-child Unsupported error, got {other}"

            Assert.That(launches, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``a run that never reached a child hands the one-shot payload back to the next run``() : Task =
        task {
            let mutable calls = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // The first run burns its whole budget on pre-spawn failures, so it never fed the payload:
            // it must hand the source back rather than keep it reserved for good.
            let notFoundThenReading =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls <= 2 then
                            Task.FromResult(Error(ProcessError.NotFound("svc", None)))
                        else
                            task {
                                use fed = new MemoryStream()
                                do! stream.CopyToAsync fed
                                return Ok(ProcessResult.Success(Encoding.UTF8.GetString(fed.ToArray())))
                            }

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 2 TimeSpan.Zero (fun _ -> true)

            let! first = command |> Runner.run notFoundThenReading CancellationToken.None

            match first with
            | Error(ProcessError.NotFound _) -> ()
            | other -> Assert.Fail $"expected the exhausted budget to report NotFound, got {other}"

            Assert.That(calls, Is.EqualTo 2)

            let! second = command |> Runner.run notFoundThenReading CancellationToken.None

            match second with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a run refused by an already-cancelled token hands the one-shot payload back``() : Task =
        task {
            let mutable launches = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // Routes through the real capture launch boundary, which refuses to start a child under an
            // already-cancelled token: the first run below therefore reports `Cancelled` without ever
            // reaching one, and its reservation must not outlive it.
            let launching =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, cancellationToken) =
                        CaptureVerbs.runToCompletion
                            command
                            cancellationToken
                            (fun () ->
                                launches <- launches + 1
                                Task.FromResult(Ok(LaunchedDouble.runningProcess command)))
                            (fun running ->
                                task {
                                    use _ = running
                                    use fed = new MemoryStream()
                                    do! stream.CopyToAsync fed
                                    return Ok(ProcessResult.Success(Encoding.UTF8.GetString(fed.ToArray())))
                                })

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            use cts = new CancellationTokenSource()
            cts.Cancel()

            let! cancelled = command |> Runner.run launching cts.Token

            match cancelled with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(launches, Is.EqualTo 0)

            // No child was ever started, so the payload is untouched: the next run is handed it rather
            // than the reservation's "another run already holds it" refusal.
            let! second = command |> Runner.run launching CancellationToken.None

            match second with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error}"

            Assert.That(launches, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``a retry backoff cancelled after a pre-spawn failure hands the one-shot payload back``() : Task =
        task {
            let provider = RetryTimeProvider()
            use cancelOn = new CancellationTokenSource()
            let mutable calls = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let spawnFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Spawn("svc", "resource temporarily unavailable")))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.timeProvider provider
                |> Command.cancelOn cancelOn.Token
                |> Command.retry 3 (TimeSpan.FromSeconds 60.0) (fun _ -> true)

            let run = command |> Runner.run spawnFailing CancellationToken.None

            // The armed backoff timer is the deterministic signal that attempt 1 failed before any
            // child and the loop is now waiting to retry. Cancelling there ends the run on a
            // `Cancelled` produced by the retry machinery itself, over a payload nothing has read.
            let! _ = provider.WaitForTimer 0
            cancelOn.Cancel()

            match! run with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(calls, Is.EqualTo 1)

            // A fresh command over the SAME stream — the claim is keyed on the payload object, not on
            // the command — must be handed the untouched payload.
            let! second =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)
                |> Runner.run (DrainingDouble.over stream) CancellationToken.None

            match second with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a throwing retry classifier after a pre-spawn failure hands the one-shot payload back``() : Task =
        task {
            let mutable calls = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let spawnFailing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1
                        Task.FromResult(Error(ProcessError.Spawn("svc", "resource temporarily unavailable")))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            // The classifier is only ever consulted after a pre-child failure, so a fault out of it
            // ends the run over a payload that provably survived attempt 1.
            let throwing = fun _ -> raise (InvalidOperationException "classifier boom")

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero throwing

            let! result = command |> Runner.run spawnFailing CancellationToken.None

            match result with
            | Error(ProcessError.RetryPredicate(_, original, _)) ->
                Assert.That(original, Is.EqualTo(ProcessError.Spawn("svc", "resource temporarily unavailable")))
            | other -> Assert.Fail $"expected RetryPredicate, got {other}"

            Assert.That(calls, Is.EqualTo 1)

            let! second =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)
                |> Runner.run (DrainingDouble.over stream) CancellationToken.None

            match second with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error}"
        }
        :> Task

    [<Test>]
    member _.``an attempt that throws before launching hands the one-shot payload back``() : Task =
        task {
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // A runner that faults instead of returning a typed result never reached a launch boundary
            // here, so the payload is exactly as intact as after a `Spawn` failure. The reservation is
            // settled on the way out rather than stranded for the life of the stream.
            let throwing =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        raise (InvalidOperationException "runner boom")

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let mutable faulted = false

            try
                let! _ = command |> Runner.run throwing CancellationToken.None
                ()
            with :? InvalidOperationException ->
                // The runner's own fault is what this arm is proving reaches the caller unchanged; the
                // reservation settlement it must NOT skip is asserted by the second run below.
                faulted <- true

            Assert.That(faulted, Is.True)

            let! second =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)
                |> Runner.run (DrainingDouble.over stream) CancellationToken.None

            match second with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a cancellation that arrives mid-attempt keeps the one-shot payload held``() : Task =
        task {
            use cts = new CancellationTokenSource()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            // The token is live when the attempt starts and fires while it is in flight — the shape of
            // a cancellation that may have killed a child already draining the source. Nothing proves
            // the payload survived, so this run must keep holding it.
            let cancellingMidAttempt =
                { new IProcessRunner with
                    member _.CaptureStringAsync(command, _) =
                        cts.Cancel()
                        Task.FromResult(Error(ProcessError.Cancelled command.Program))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! first = command |> Runner.run cancellingMidAttempt cts.Token

            match first with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            let! second =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)
                |> Runner.run (DrainingDouble.over stream) CancellationToken.None

            match second with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
            | other -> Assert.Fail $"expected the held payload to refuse a later run, got {other}"
        }
        :> Task

    [<Test>]
    member _.``two concurrent retrying runs never feed one one-shot stdin source to two children``() : Task =
        task {
            let mutable drains = 0
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")
            use attemptStarted = new SemaphoreSlim(0)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            // Holds its attempt open until released, so the second run is started while the first is
            // still inside one. Only one attempt is ever expected to reach the drain, so the counter
            // has a single writer by design — and the payload assertions below fail loudly if that
            // stops being true.
            let gated =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        task {
                            attemptStarted.Release() |> ignore
                            do! release.Task
                            drains <- drains + 1
                            use fed = new MemoryStream()
                            do! stream.CopyToAsync fed
                            return Ok(ProcessResult.Success(Encoding.UTF8.GetString(fed.ToArray())))
                        }

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "svc"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let first = command |> Runner.run gated CancellationToken.None
            do! attemptStarted.WaitAsync()

            let! second = command |> Runner.run gated CancellationToken.None

            // The loser is refused loudly instead of being handed a source the winner is feeding.
            match second with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
            | other -> Assert.Fail $"expected the concurrent run to be refused, got {other}"

            release.SetResult()
            let! firstResult = first

            match firstResult with
            | Ok text -> Assert.That(text, Is.EqualTo "payload")
            | Error error -> Assert.Fail $"expected the owning run to read the whole payload, got {error}"

            Assert.That(drains, Is.EqualTo 1)
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
