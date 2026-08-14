namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type TimeoutTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let sleeper () =
        if isWindows then
            shell "ping 127.0.0.1 -n 10 >NUL"
        else
            shell "sleep 8"

    let gracefulSleeper (writeFirst: bool) =
        if isWindows then
            if writeFirst then
                shell "echo ready&ping 127.0.0.1 -n 10 >NUL"
            else
                sleeper ()
        else
            let prefix = if writeFirst then "echo ready; " else ""
            shell $"trap 'sleep 0.6; exit 0' TERM; {prefix}while :; do sleep 0.05; done"

    let assertTimeout (expected: TimeSpan) (result: ProcessResult<string>) =
        match result.EnsureSuccess() with
        | Error(ProcessError.Timeout(program, actual, _, _) as error) ->
            Assert.That(actual, Is.EqualTo expected, "the error must carry the configured deadline that fired")
            Assert.That(error.Message, Does.StartWith($"'{program}' timed out after {expected.TotalSeconds}s"))
        | other -> Assert.Fail $"expected Timeout, got {other}"

    [<Test>]
    member _.``Timeout kills the run promptly and reports Timeout``() : Task =
        task {
            let command = sleeper () |> Command.timeout (TimeSpan.FromMilliseconds 400.0)
            let stopwatch = Stopwatch.StartNew()
            let! result = command.RunAsync()
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected Timeout, got {other}"

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
        }
        :> Task

    [<Test>]
    member _.``Timeout surfaces as Outcome.TimedOut on outputString``() : Task =
        task {
            let command = sleeper () |> Command.timeout (TimeSpan.FromMilliseconds 400.0)

            match! command.OutputStringAsync() with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``total Timeout preserves its configured duration through graceful teardown``() : Task =
        task {
            let configured = TimeSpan.FromMilliseconds 200.0

            let command =
                gracefulSleeper false
                |> Command.timeout configured
                |> Command.idleTimeout (TimeSpan.FromSeconds 3.0)
                |> Command.timeoutGrace (TimeSpan.FromSeconds 1.0)

            match! command.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.IsTimedOut, Is.True)
                assertTimeout configured result

                if not isWindows then
                    Assert.That(
                        result.Duration,
                        Is.GreaterThan(TimeSpan.FromMilliseconds 500.0),
                        "the artificial grace delay should make elapsed time differ from the configured timeout"
                    )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``idle Timeout preserves its configured duration through graceful teardown``() : Task =
        task {
            let configured = TimeSpan.FromMilliseconds 200.0

            let command =
                gracefulSleeper true
                |> Command.timeout (TimeSpan.FromSeconds 3.0)
                |> Command.idleTimeout configured
                |> Command.timeoutGrace (TimeSpan.FromSeconds 1.0)

            match! command.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.IsTimedOut, Is.True)
                Assert.That(result.Stdout, Does.Contain "ready")
                assertTimeout configured result

                if not isWindows then
                    Assert.That(
                        result.Duration,
                        Is.GreaterThan(TimeSpan.FromMilliseconds 500.0),
                        "the artificial grace delay should make elapsed time differ from the configured idle window"
                    )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``idle deadline cancels the losing total-timeout timer``() : Task =
        task {
            use idle = new Timeouts.IdleTimer(TimeSpan.FromMilliseconds 25.0)
            use timeoutCts = new CancellationTokenSource()

            let wait =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let timeoutEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let releaseTimeout =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let onTimeout (_: TimeSpan) : Task =
                task {
                    timeoutEntered.TrySetResult() |> ignore
                    do! releaseTimeout.Task
                }
                :> Task

            let race =
                Timeouts.raceTimeoutWithCts
                    timeoutCts
                    (TimeSpan.FromSeconds 5.0)
                    None
                    "test"
                    "idle-cancels-total"
                    (Some(TimeSpan.FromMinutes 1.0))
                    (Some idle)
                    onTimeout
                    wait.Task

            try
                do! timeoutEntered.Task

                releaseTimeout.TrySetResult() |> ignore

                let deadline = Stopwatch.StartNew()

                while not timeoutCts.IsCancellationRequested
                      && deadline.Elapsed < TimeSpan.FromSeconds 1.0 do
                    do! Task.Delay 10

                Assert.That(
                    timeoutCts.IsCancellationRequested,
                    Is.True,
                    "the losing total-timeout timer must be cancelled"
                )

                wait.TrySetResult(Outcome.Exited 0) |> ignore
                let! outcome = race
                Assert.That(outcome, Is.EqualTo Outcome.TimedOut)
            finally
                releaseTimeout.TrySetResult() |> ignore
                wait.TrySetResult(Outcome.Exited 0) |> ignore
        }
        :> Task

    // ---- T-351: the post-kill reap is bounded on the timeout path --------------------------------
    //
    // After a deadline fires, the timeout race kills the tree and then reaps it. A child wedged in
    // uninterruptible (`D`-state) sleep defers even SIGKILL until its I/O unblocks, so that reap can
    // stall indefinitely — and the race used to await it unbounded, hanging the very timeout it was
    // enforcing long after the kill had been delivered and `TimedOut` had already been decided.

    [<Test>]
    member _.``a post-kill reap that never lands still reports TimedOut, bounded (T-351)``() : Task =
        task {
            use timeoutCts = new CancellationTokenSource()
            let budget = TimeSpan.FromMilliseconds 200.0

            // The injected never-completing post-kill wait: the kill IS delivered (onTimeout runs), the
            // child simply never becomes reapable.
            let wait =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let kills = ref 0

            let onTimeout (_: TimeSpan) : Task =
                Interlocked.Increment(&kills.contents) |> ignore
                Task.CompletedTask

            let adoptedBefore = PostKillReap.adoptedWaitCount ()
            let stopwatch = Stopwatch.StartNew()

            let! outcome =
                Timeouts.raceTimeoutWithCts
                    timeoutCts
                    budget
                    None
                    "test"
                    "post-kill-bounded"
                    (Some(TimeSpan.FromMilliseconds 20.0))
                    None
                    onTimeout
                    wait.Task

            stopwatch.Stop()

            Assert.That(
                outcome,
                Is.EqualTo Outcome.TimedOut,
                "the already-decided disposition must survive a late reap"
            )

            Assert.That(
                Volatile.Read(&kills.contents),
                Is.EqualTo 1,
                "the deadline must still deliver exactly one kill"
            )

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 3.0),
                "the never-completing post-kill wait held the timeout past its bounded budget"
            )

            Assert.That(
                PostKillReap.adoptedWaitCount () - adoptedBefore,
                Is.EqualTo 1,
                "the abandoned wait must be adopted by the ledger exactly once, not simply dropped"
            )

            // The late conclusion still arrives on the adopted wait; nothing may fault or re-decide.
            wait.TrySetResult(Outcome.Exited 0) |> ignore
            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)
        }
        :> Task

    [<Test>]
    member _.``a post-kill reap inside the budget is awaited, not adopted (T-351)``() : Task =
        task {
            use timeoutCts = new CancellationTokenSource()

            let wait =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            // The ordinary child: the kill lands and the reap follows immediately. No budget is paid and
            // no ownership changes hands.
            let onTimeout (_: TimeSpan) : Task =
                wait.TrySetResult(Outcome.Signalled(Some 9)) |> ignore
                Task.CompletedTask

            let adoptedBefore = PostKillReap.adoptedWaitCount ()

            let! outcome =
                Timeouts.raceTimeoutWithCts
                    timeoutCts
                    (TimeSpan.FromSeconds 30.0)
                    None
                    "test"
                    "post-kill-prompt"
                    (Some(TimeSpan.FromMilliseconds 20.0))
                    None
                    onTimeout
                    wait.Task

            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)

            Assert.That(
                PostKillReap.adoptedWaitCount () - adoptedBefore,
                Is.Zero,
                "a reap that landed inside the budget must not hand ownership to the ledger"
            )
        }
        :> Task

    [<Test>]
    member _.``Retry re-runs a failing command the configured number of times``() : Task =
        let id = Guid.NewGuid().ToString("N")
        let marker = Path.Combine(Path.GetTempPath(), $"pk-retry-{id}.txt")

        task {
            try
                let script =
                    if isWindows then
                        $"echo x>>{marker}&exit 1"
                    else
                        $"echo x >> {marker}; exit 1"

                let command =
                    shell script |> Command.retry 3 (TimeSpan.FromMilliseconds 50.0) (fun _ -> true)

                match! command.RunAsync() with
                | Error _ -> ()
                | Ok _ -> Assert.Fail "expected the command to fail"

                let attempts =
                    if File.Exists marker then
                        File.ReadAllLines(marker).Length
                    else
                        0

                Assert.That(attempts, Is.EqualTo 3) // retry 3 = 3 runs total (initial + 2 retries)
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``Retry 0 (or any non-positive maxAttempts) runs the command exactly once``() : Task =
        let id = Guid.NewGuid().ToString("N")
        let marker = Path.Combine(Path.GetTempPath(), $"pk-retry0-{id}.txt")

        task {
            try
                let script =
                    if isWindows then
                        $"echo x>>{marker}&exit 1"
                    else
                        $"echo x >> {marker}; exit 1"

                // `maxAttempts` counts total runs, so 0 (a non-positive value) is still a single run — a
                // command always runs at least once, and the `- 1` guard can't underflow into a storm.
                let command =
                    shell script |> Command.retry 0 (TimeSpan.FromMilliseconds 50.0) (fun _ -> true)

                let! _ = command.RunAsync()

                let attempts =
                    if File.Exists marker then
                        File.ReadAllLines(marker).Length
                    else
                        0

                Assert.That(attempts, Is.EqualTo 1)
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``CancelOn cancels the run when its token fires``() : Task =
        task {
            use cts = new CancellationTokenSource()
            let command = sleeper () |> Command.cancelOn cts.Token
            let runTask = command.RunAsync()
            do! Task.Delay 300
            cts.Cancel()

            match! runTask with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task
