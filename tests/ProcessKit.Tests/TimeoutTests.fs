namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// What a synthetic host was asked to do — asserted directly, rather than inferred from timing:
/// `Kills` proves the single kill (never two, never one on a run that concluded on its own) and
/// `Waits` proves the single `host.Wait()` behind the memoized exit wait (KB K-016).
type private HostCalls() =
    let mutable kills = 0
    let mutable waits = 0

    member _.Kills = Volatile.Read(&kills)
    member _.Waits = Volatile.Read(&waits)
    member _.CountKill() = Interlocked.Increment(&kills) |> ignore
    member _.CountWait() = Interlocked.Increment(&waits) |> ignore

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

    // ---- T-356 fixtures: a handle whose child was spawned in the PAST -----------------------------
    //
    // The bug these drive is about WHEN the total deadline is anchored, so the fixture has to express
    // "the caller only got around to consuming this handle N seconds after the spawn". A backdated
    // monotonic spawn stamp says exactly that, deterministically and without sleeping: it is the same
    // `Stopwatch` timestamp a real spawn records (`ProcessGroup.StartAsync`), just older.

    /// `Stopwatch.GetTimestamp()` as it would have read `age` ago.
    let spawnedAgo (age: TimeSpan) =
        Stopwatch.GetTimestamp()
        - (age.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)

    /// A `RunningProcess` over a synthetic host spawned `age` ago, with no real child behind it.
    /// `exit` is the exit status the child will report; it is published when the tree is killed (a
    /// real child dies when killed, so the post-kill reap lands at once and no test pays the
    /// post-kill budget) or, when `alreadyExited` is set, before the handle is ever consumed — the
    /// "it finished on its own long before anyone looked" case.
    let backdatedProcess (config: CommandConfig) (age: TimeSpan) (alreadyExited: bool) (exit: Outcome) =
        let calls = HostCalls()

        let exited =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        if alreadyExited then
            exited.TrySetResult exit |> ignore

        let kill () =
            calls.CountKill()
            exited.TrySetResult exit |> ignore

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stderr = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = spawnedAgo age
              StartTimeIdentity = None
              Wait =
                fun () ->
                    calls.CountWait()
                    exited.Task
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = kill
              Signal = fun _ -> Ok()
              GracefulKill =
                fun _ ->
                    kill ()
                    Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host), calls

    /// The command every backdated-handle test below is built from: a 2-second total deadline, long
    /// enough that "the whole budget was re-issued" (2s) is unmistakably distinguishable from "only
    /// what was left of it" (0s .. 0.6s) on any CI runner.
    let twoSecondTimeout =
        (Command.create "test" |> Command.timeout (TimeSpan.FromSeconds 2.0)).Config

    /// How long a spawn-anchored deadline may take to fire once its budget is already spent: the
    /// bounded settle window that lets an already-published exit be observed, plus slack for a loaded
    /// runner — and still far below the 2s a re-issued full budget would have cost.
    let spentBudgetCeiling = TimeSpan.FromSeconds 1.0

    /// Assert that `totalDeadline` resolved to a deadline reporting `configured` and arming `armed`.
    /// The two are asserted separately (rather than by comparing whole records) so a failure names
    /// which half drifted — the reported duration or the armed one.
    let assertDeadline
        (configured: TimeSpan)
        (armed: TimeSpan)
        (context: string)
        (actual: Timeouts.TotalDeadline option)
        =
        match actual with
        | Some deadline ->
            Assert.That(deadline.Configured, Is.EqualTo configured, context)
            Assert.That(deadline.Armed, Is.EqualTo armed, context)
        | None -> Assert.Fail $"expected a deadline ({context}), got none"

    /// Enumerate an async sequence to its end, discarding the items — these tests are about when the
    /// deadline fires, not about the output it bounds.
    let drain (source: IAsyncEnumerable<'T>) =
        task {
            let enumerator = source.GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! next = enumerator.MoveNextAsync()
                more <- next

            do! enumerator.DisposeAsync()
        }

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
                    (Timeouts.totalDeadline (Some(TimeSpan.FromMinutes 1.0)) TimeSpan.Zero)
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
                    (Timeouts.totalDeadline (Some(TimeSpan.FromMilliseconds 20.0)) TimeSpan.Zero)
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
                    (Timeouts.totalDeadline (Some(TimeSpan.FromMilliseconds 20.0)) TimeSpan.Zero)
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

    // ---- T-356: the total deadline is anchored at SPAWN, not at the first consumer ----------------
    //
    // `Command.Timeout` bounds the RUN, but the exit wait that enforces it is created LAZILY by
    // whichever consumer reaches `waitWithTimeout` first — a buffered verb, a streaming/event session,
    // a readiness probe, `WaitAny`/`WaitAll`. On a live `StartAsync` handle that can be long after the
    // child started, and re-issuing the whole configured duration there gave the child
    // `(delay before the first consumer) + Timeout` to live in: `Timeout(1s)` consumed five seconds
    // later bounded nothing anyone had asked for. The deadline is now computed from the monotonic spawn
    // stamp, so only what is LEFT of the budget is armed — while the CONFIGURED duration is what the
    // error, the result, and the log keep reporting.

    // The deadline arithmetic itself, exhaustively and without any clock: `totalDeadline` is the one
    // place the spawn anchor turns into an armed delay.

    [<Test>]
    member _.``an unset or unarmable Timeout is still no deadline at all, however late the wait (T-356)``() : unit =
        Assert.That(Timeouts.totalDeadline None (TimeSpan.FromSeconds 5.0), Is.EqualTo None)

        // Longer than a BCL timer can hold is "effectively never" — decided by the CONFIGURED value
        // alone, so being consumed late cannot turn it into a deadline that fires.
        Assert.That(Timeouts.totalDeadline (Some TimeSpan.MaxValue) (TimeSpan.FromSeconds 5.0), Is.EqualTo None)

        Assert.That(
            Timeouts.totalDeadline (Some(Timeouts.maxArmable + TimeSpan.FromMilliseconds 1.0)) TimeSpan.Zero,
            Is.EqualTo None
        )

    [<Test>]
    member _.``a deadline arms the budget left since spawn and reports the configured one (T-356)``() : unit =
        let configured = TimeSpan.FromSeconds 2.0

        // Consumed immediately: the whole budget is available, exactly as before this change.
        Timeouts.totalDeadline (Some configured) TimeSpan.Zero
        |> assertDeadline configured configured "consumed at once"

        // Consumed partway through: only the remainder is armed, and the configured duration — what
        // `ProcessError.Timeout` and the timeout log report — is untouched.
        Timeouts.totalDeadline (Some configured) (TimeSpan.FromMilliseconds 1_500.0)
        |> assertDeadline configured (TimeSpan.FromMilliseconds 500.0) "consumed with a quarter of the budget left"

        // A spawn stamp from the future (only a synthetic host can produce one) must not WIDEN the
        // budget past what was configured.
        Timeouts.totalDeadline (Some configured) (TimeSpan.FromSeconds -30.0)
        |> assertDeadline configured configured "a spawn stamp from the future"

    [<Test>]
    member _.``a budget already spent arms only the bounded settle window (T-356)``() : unit =
        let configured = TimeSpan.FromSeconds 2.0

        let spent =
            "a spent budget must not be re-issued — only the settle window that lets an already-published exit be seen"

        for elapsed in [ configured; configured + TimeSpan.FromTicks 1L; TimeSpan.FromHours 3.0 ] do
            Timeouts.totalDeadline (Some configured) elapsed
            |> assertDeadline configured Timeouts.spentBudgetSettle spent

        Assert.That(
            Timeouts.spentBudgetSettle,
            Is.LessThan(TimeSpan.FromSeconds 1.0),
            "the settle window is a moment to observe an exit, never a second budget"
        )

        // A zero-length budget has no exit-inside-it to observe, so it keeps firing at once.
        Timeouts.totalDeadline (Some TimeSpan.Zero) (TimeSpan.FromSeconds 5.0)
        |> assertDeadline TimeSpan.Zero TimeSpan.Zero "a zero-length budget"

    // The same anchor, end to end through a live handle: every terminal path below is driven on a
    // handle whose child was spawned longer ago than its own 2-second deadline, so a re-issued budget
    // would cost a further 2 seconds and be caught by `spentBudgetCeiling`.

    [<Test>]
    member _.``OutputString on a late-consumed handle times out at once, reporting the configured deadline (T-356)``
        ()
        : Task =
        task {
            let running, calls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! result = running.OutputStringAsync()
            stopwatch.Stop()

            match result with
            | Ok captured ->
                Assert.That(captured.IsTimedOut, Is.True, "the run was already past its deadline when it was consumed")
                // The remainder was spent long ago, but the deadline the caller SET is what is reported.
                assertTimeout (TimeSpan.FromSeconds 2.0) captured
            | Error error -> Assert.Fail $"{error}"

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan spentBudgetCeiling,
                "a spent budget must not be re-issued from the moment of the first consumer"
            )

            Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill")
            Assert.That(calls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")
        }
        :> Task

    [<Test>]
    member _.``OutputBytes, Wait and Profile share the same spawn-anchored deadline (T-356)``() : Task =
        task {
            let consume (verb: RunningProcess -> Task<Outcome>) =
                task {
                    let running, calls =
                        backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

                    use _ = running
                    let stopwatch = Stopwatch.StartNew()
                    let! outcome = verb running
                    stopwatch.Stop()

                    Assert.That(outcome, Is.EqualTo Outcome.TimedOut)
                    Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
                    Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill")
                    Assert.That(calls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")
                }

            do!
                consume (fun running ->
                    task {
                        match! running.OutputBytesAsync() with
                        | Ok captured -> return captured.Outcome
                        | Error error -> return failwith $"{error}"
                    })

            do! consume (fun running -> running.WaitAsync())

            do!
                consume (fun running ->
                    task {
                        let! profile = running.ProfileAsync(TimeSpan.FromMilliseconds 50.0)
                        return profile.Outcome
                    })
        }
        :> Task

    [<Test>]
    member _.``stdout and event streaming sessions share the same spawn-anchored deadline (T-356)``() : Task =
        task {
            // Streaming claims the pipes (and creates its own exit wait) when the session starts, so
            // the budget it arms is the one left at THAT moment — the same absolute deadline.
            let streamed, streamedCalls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = streamed
            let stopwatch = Stopwatch.StartNew()

            do! drain (streamed.StdoutLinesAsync())

            match! streamed.FinishAsync() with
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo Outcome.TimedOut)
            | Error error -> Assert.Fail $"{error}"

            stopwatch.Stop()
            Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(streamedCalls.Kills, Is.EqualTo 1, "exactly one kill")
            Assert.That(streamedCalls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")

            let events, eventCalls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = events
            let eventStopwatch = Stopwatch.StartNew()

            do! drain (events.OutputEventsAsync())

            let! outcome = RunningProcess.WaitAllAsync [| events |]
            eventStopwatch.Stop()

            Assert.That(outcome, Is.EqualTo<Outcome[]>([| Outcome.TimedOut |]))
            Assert.That(eventStopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(eventCalls.Kills, Is.EqualTo 1, "exactly one kill")
            Assert.That(eventCalls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")
        }
        :> Task

    [<Test>]
    member _.``a readiness probe and the verb after it share one spawn-anchored deadline (T-356)``() : Task =
        task {
            // A probe starts the one shared exit wait without claiming the pipes (KB K-016), so it is
            // also the call that arms the deadline — the `WaitAsync` after it must join that same wait
            // rather than start a second one with a second budget.
            let running, calls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = running
            let stopwatch = Stopwatch.StartNew()

            match! running.WaitForAsync((fun () -> Task.FromResult false), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.NotReady _) -> ()
            | other -> Assert.Fail $"expected NotReady once the deadline killed the child, got {other}"

            let! outcome = running.WaitAsync()
            stopwatch.Stop()

            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)
            Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill across the probe and the verb")
            Assert.That(calls.Waits, Is.EqualTo 1, "exactly one exit wait / reap across the probe and the verb")
        }
        :> Task

    [<Test>]
    member _.``WaitAny and WaitAll honour the spawn-anchored deadline (T-356)``() : Task =
        task {
            let first, firstCalls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            let second, secondCalls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = first
            use _ = second
            let stopwatch = Stopwatch.StartNew()
            let! any = RunningProcess.WaitAnyAsync [| first; second |]
            let! all = RunningProcess.WaitAllAsync [| first; second |]
            stopwatch.Stop()

            Assert.That(any.Outcome, Is.EqualTo Outcome.TimedOut)
            Assert.That(all, Is.EqualTo<Outcome[]>([| Outcome.TimedOut; Outcome.TimedOut |]))
            Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(firstCalls.Kills, Is.EqualTo 1, "exactly one kill")
            Assert.That(secondCalls.Kills, Is.EqualTo 1, "exactly one kill")
            Assert.That(firstCalls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")
            Assert.That(secondCalls.Waits, Is.EqualTo 1, "exactly one exit wait / reap")
        }
        :> Task

    [<Test>]
    member _.``a partly spent budget arms only its remainder (T-356)``() : Task =
        task {
            // 1.4s of a 2s deadline already gone: the kill must land around 0.6s from here — later than
            // an instant (proving the remainder was armed rather than "already spent") and well before
            // the 2s a re-issued budget would have cost.
            let running, calls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromMilliseconds 1_400.0) false (Outcome.Signalled(Some 9))

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! outcome = running.WaitAsync()
            stopwatch.Stop()

            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)

            Assert.That(
                stopwatch.Elapsed,
                Is.GreaterThan(TimeSpan.FromMilliseconds 300.0),
                "the remaining budget must still be honoured, not treated as already spent"
            )

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromMilliseconds 1_800.0),
                "the whole budget must not be re-issued from the first consumer"
            )

            Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill")
        }
        :> Task

    [<Test>]
    member _.``a child that finished inside its deadline still reports its real outcome when consumed late (T-356)``
        ()
        : Task =
        task {
            // The honest-result half of the anchor: the deadline was blown by the CALLER's schedule, not
            // by the child, which exited on its own. Reporting `TimedOut` here — and killing a tree that
            // is already gone — would fabricate a failure out of nothing.
            let running, calls =
                backdatedProcess twoSecondTimeout (TimeSpan.FromSeconds 5.0) true (Outcome.Exited 0)

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! result = running.OutputStringAsync()
            stopwatch.Stop()

            match result with
            | Ok captured ->
                Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(captured.IsTimedOut, Is.False)
            | Error error -> Assert.Fail $"{error}"

            Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(calls.Kills, Is.Zero, "a child that already exited must not be killed")
        }
        :> Task

    [<Test>]
    member _.``a handle with no armable Timeout is unaffected by how late it is consumed (T-356)``() : Task =
        task {
            let noDeadline = (Command.create "test").Config

            let neverArmed = (Command.create "test" |> Command.timeout TimeSpan.MaxValue).Config

            for config in [ noDeadline; neverArmed ] do
                let running, calls =
                    backdatedProcess config (TimeSpan.FromSeconds 5.0) true (Outcome.Exited 3)

                use _ = running
                let! outcome = running.WaitAsync()

                Assert.That(outcome, Is.EqualTo(Outcome.Exited 3), "no deadline may be invented by a late consumer")
                Assert.That(calls.Kills, Is.Zero, "nothing to kill without a deadline")
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout keeps measuring inactivity from the wait, not from spawn (T-356)``() : Task =
        task {
            // The idle window is an INACTIVITY deadline, so it starts when output actually begins to be
            // consumed. Backdating it to spawn (like the total deadline) would charge a handle for the
            // quiet gap before anyone was reading — this proves the total-deadline anchor did not leak
            // into it: the kill lands a full idle window after the verb, not instantly.
            let idleOnly =
                (Command.create "test" |> Command.idleTimeout (TimeSpan.FromMilliseconds 700.0)).Config

            let running, calls =
                backdatedProcess idleOnly (TimeSpan.FromSeconds 5.0) false (Outcome.Signalled(Some 9))

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! outcome = running.WaitAsync()
            stopwatch.Stop()

            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)

            Assert.That(
                stopwatch.Elapsed,
                Is.GreaterThan(TimeSpan.FromMilliseconds 400.0),
                "the idle window must not be shortened by the time before the exit wait began"
            )

            Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill")
        }
        :> Task

    // The same two outcomes over a REAL child, end to end through `StartAsync` — the shape a caller
    // actually writes (start, do something else, collect), which is what made the re-anchored deadline
    // invisible to the immediately-consuming verbs.

    [<Test>]
    member _.``a real child consumed after its deadline is killed at once, not given a fresh budget (T-356)``() : Task =
        task {
            let command = sleeper () |> Command.timeout (TimeSpan.FromSeconds 1.0)

            match! command.StartAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                do! Task.Delay 1_500
                let stopwatch = Stopwatch.StartNew()
                let! outcome = running.WaitAsync()
                stopwatch.Stop()

                Assert.That(outcome, Is.EqualTo Outcome.TimedOut)

                Assert.That(
                    stopwatch.Elapsed,
                    Is.LessThan(TimeSpan.FromMilliseconds 900.0),
                    "the child had already outlived its 1s deadline; consuming it must not grant another one"
                )
        }
        :> Task

    [<Test>]
    member _.``a real child that exited inside its deadline still reports its output when consumed late (T-356)``
        ()
        : Task =
        task {
            let command = shell "echo done" |> Command.timeout (TimeSpan.FromSeconds 1.0)

            match! command.StartAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                do! Task.Delay 1_500

                match! running.OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.IsTimedOut, Is.False, "the child exited well inside its deadline")
                    Assert.That(result.Stdout, Does.Contain "done")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task
