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
open ProcessKit.Testing

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

/// What a synthetic host was asked to do on the CANCELLATION teardown path (T-373), asserted directly
/// rather than inferred from timing: the soft signals it was asked to deliver, in order, and how many
/// hard kills followed. `Signals` is empty and `Kills` is 1 for the unchanged default (no
/// `Command.CancelGrace`); the ladder shows up as a soft signal FIRST and a kill only after the window.
type private CancelCalls() =
    let gate = obj ()
    let signals = ResizeArray<Signal>()
    let mutable kills = 0

    /// The soft signals delivered, in order, rendered as a single comma-joined string (`""` for none,
    /// `"Term"` for one). A scalar so the assertion reads as one exact expectation — which signal, how
    /// many, in what order — instead of several weaker ones.
    member _.SignalTrace: string =
        lock gate (fun () -> signals |> Seq.map string |> String.concat ",")

    member _.Kills = Volatile.Read(&kills)

    member _.RecordSignal(signal: Signal) =
        lock gate (fun () -> signals.Add signal)

    member _.CountKill() = Interlocked.Increment(&kills) |> ignore

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
        | Error(ProcessError.Timeout(program, actual, _, _, _) as error) ->
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
    ///
    /// `publishDelay` is how long after the exit wait STARTS that an already-finished child's status
    /// becomes observable: no real host answers `host.Wait()` synchronously, so a non-zero value is the
    /// plumbing hop (pidfd/kqueue readiness, `RegisterWaitForSingleObject` → thread pool) that a
    /// deadline armed for a mere sliver of leftover budget can otherwise beat. It applies only to an
    /// `alreadyExited` child; a killed one still publishes at once.
    ///
    /// `recordedCompletion` builds the handle the way a replayed cassette entry does (T-348): its
    /// completion metadata is frozen at the recorded duration/truncation instead of measured. `None` —
    /// every fixture below but the replay one — is an ordinary live handle.
    let backdatedProcessCore
        (config: CommandConfig)
        (age: TimeSpan)
        (alreadyExited: bool)
        (publishDelay: TimeSpan)
        (recordedCompletion: (TimeSpan * bool) option)
        (exit: Outcome)
        =
        let calls = HostCalls()

        let exited =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        if alreadyExited && publishDelay = TimeSpan.Zero then
            exited.TrySetResult exit |> ignore

        let kill () =
            calls.CountKill()
            exited.TrySetResult exit |> ignore

        let wait () =
            calls.CountWait()

            if alreadyExited && publishDelay > TimeSpan.Zero then
                task {
                    do! Task.Delay publishDelay
                    exited.TrySetResult exit |> ignore
                    return! exited.Task
                }
            else
                exited.Task

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stderr = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = spawnedAgo age
              StartTimeIdentity = None
              Wait = wait
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

        let running =
            match recordedCompletion with
            | Some(recordedDuration, recordedTruncated) -> new RunningProcess(host, recordedDuration, recordedTruncated)
            | None -> new RunningProcess(host)

        running, calls

    /// A live handle over a synthetic host spawned `age` ago, with the publishing hop modelled.
    let backdatedProcessPublishing
        (config: CommandConfig)
        (age: TimeSpan)
        (alreadyExited: bool)
        (publishDelay: TimeSpan)
        (exit: Outcome)
        =
        backdatedProcessCore config age alreadyExited publishDelay None exit

    /// The plain fixture: an already-finished child's status is there for the asking, so nothing about
    /// a test using it depends on the plumbing hop `backdatedProcessPublishing` can model.
    let backdatedProcess (config: CommandConfig) (age: TimeSpan) (alreadyExited: bool) (exit: Outcome) =
        backdatedProcessPublishing config age alreadyExited TimeSpan.Zero exit

    /// The same host behind a REPLAY handle: one carrying a cassette entry's recorded completion (T-348),
    /// so `RunningProcess.Elapsed` / `ProcessResult.Duration` report `recordedDuration` however long this
    /// handle has really existed. Its child has always already finished (that is what a recording is), so
    /// its status is published `publishDelay` after the exit wait starts.
    let replayedProcess
        (config: CommandConfig)
        (age: TimeSpan)
        (publishDelay: TimeSpan)
        (recordedDuration: TimeSpan)
        (exit: Outcome)
        =
        backdatedProcessCore config age true publishDelay (Some(recordedDuration, false)) exit

    /// The command every backdated-handle test below is built from: a 2-second total deadline, long
    /// enough that "the whole budget was re-issued" (2s) is unmistakably distinguishable from "only
    /// what was left of it" (0s .. 0.6s) on any CI runner.
    let twoSecondTimeout =
        (Command.create "test" |> Command.timeout (TimeSpan.FromSeconds 2.0)).Config

    /// How long a spawn-anchored deadline may take to fire once its budget is already spent: the
    /// bounded settle window that lets an already-published exit be observed, plus slack for a loaded
    /// runner — and still far below the 2s a re-issued full budget would have cost.
    let spentBudgetCeiling = TimeSpan.FromSeconds 1.0

    /// Assert that `totalDeadline` resolved to a deadline reporting `configured`, arming `armed`, and
    /// settling for `settle` if it fires. The three are asserted separately (rather than by comparing
    /// whole records) so a failure names which part drifted — the reported duration, the armed one, or
    /// the window an already-published exit still has to surface in.
    let assertDeadline
        (configured: TimeSpan)
        (armed: TimeSpan)
        (settle: TimeSpan)
        (context: string)
        (actual: Timeouts.TotalDeadline option)
        =
        match actual with
        | Some deadline ->
            Assert.That(deadline.Configured, Is.EqualTo configured, context)
            Assert.That(deadline.Armed, Is.EqualTo armed, context)
            Assert.That(deadline.Settle, Is.EqualTo settle, context)
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

    // ---- T-373 fixtures: the cancellation teardown seam, observed directly -----------------------

    /// A `RunningProcess` over a synthetic host that records exactly what the cancellation teardown
    /// asked of it. No real child: `StartKill` publishes the exit the way a killed child's status
    /// becomes observable, and `Signal` succeeds and records, the way a live tree's soft signal does.
    let cancelLadderProcess (config: CommandConfig) =
        let calls = CancelCalls()

        let exited =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stderr = Some(new MemoryStream(Array.empty<byte>) :> Stream)
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> exited.Task
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill =
                fun () ->
                    calls.CountKill()
                    exited.TrySetResult(Outcome.Signalled(Some 9)) |> ignore
              Signal =
                fun signal ->
                    calls.RecordSignal signal
                    Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host), calls

    /// Poll `condition` until it holds or `budget` runs out. Used where the assertion is about an event
    /// the child (or a detached escalation timer) produces asynchronously, so the test never sleeps out
    /// a fixed worst case on a fast machine.
    let waitUntil (budget: TimeSpan) (condition: unit -> bool) : Task<bool> =
        task {
            let startedAt = Stopwatch.GetTimestamp()
            let mutable ok = condition ()

            while not ok && Stopwatch.GetElapsedTime startedAt < budget do
                do! Task.Delay 20
                ok <- condition ()

            return ok
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

    // ---- T-373: the opt-in graceful cancellation ladder -------------------------------------------
    //
    // `Command.CancelGrace`/`CancelSignal` route a CANCELLATION through the same soft-signal -> grace ->
    // hard-kill shape `TimeoutGrace`/`StopSignal` give a deadline, and are deliberately independent of
    // them. Two invariants carry the whole feature and are asserted from both ends below — directly on
    // the teardown seam (deterministic, no clock beyond the configured window) and end to end through a
    // real child:
    //
    //   1. Unset (the default) is byte-for-byte the old behaviour: one immediate hard kill, no signal.
    //   2. The OUTCOME never changes. A cancelled run reports `ProcessError.Cancelled` whichever rung of
    //      the ladder ended the child — only the manner of the goodbye is gentler.

    [<Test>]
    member _.``CancelGrace and CancelSignal are unset by default and record what was configured (T-373)``() : unit =
        let plain = Command.create "tool"
        Assert.That(plain.Config.CancelGrace, Is.EqualTo(None: TimeSpan option))
        Assert.That(plain.Config.CancelSignal, Is.EqualTo(None: Signal option))

        // The resolved soft signal defaults to Term — the same default `StopSignal` carries.
        Assert.That(CommandConfig.cancelSignal plain.Config, Is.EqualTo Signal.Term)

        // Instance and module builders agree, and the last write wins (as everywhere else).
        for command in
            [ Command.create "tool"
              |> Command.cancelGrace (TimeSpan.FromSeconds 1.0)
              |> Command.cancelGrace (TimeSpan.FromSeconds 7.0)
              |> Command.cancelSignal Signal.Hup
              Command("tool")
                  .CancelGrace(TimeSpan.FromSeconds 1.0)
                  .CancelGrace(TimeSpan.FromSeconds 7.0)
                  .CancelSignal(Signal.Hup) ] do
            Assert.That(command.Config.CancelGrace, Is.EqualTo(Some(TimeSpan.FromSeconds 7.0)))
            Assert.That(command.Config.CancelSignal, Is.EqualTo(Some Signal.Hup))

        // Neither knob gap-fills the timeout pair, in either direction: a command that configures only
        // the deadline ladder cancels exactly as it did before, and one that configures only the
        // cancellation ladder still times out exactly as it did before.
        let timeoutOnly =
            Command.create "tool"
            |> Command.timeoutGrace (TimeSpan.FromSeconds 3.0)
            |> Command.stopSignal Signal.Usr1

        Assert.That(timeoutOnly.Config.CancelGrace, Is.EqualTo(None: TimeSpan option))
        Assert.That(CommandConfig.cancelSignal timeoutOnly.Config, Is.EqualTo Signal.Term)

        let cancelOnly =
            Command.create "tool"
            |> Command.cancelGrace (TimeSpan.FromSeconds 3.0)
            |> Command.cancelSignal Signal.Usr2

        Assert.That(cancelOnly.Config.TimeoutGrace, Is.EqualTo(None: TimeSpan option))
        Assert.That(cancelOnly.Config.StopSignal, Is.EqualTo Signal.Term)

    [<Test>]
    member _.``CancelGrace rejects a negative window and CancelSignal rejects a non-graceful signal (T-373)``() : unit =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> Command("tool").CancelGrace(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

        // The same graceful-stop screening `StopSignal` applies: a hard kill is not a soft signal, and a
        // non-deliverable raw number is not a signal at all.
        Assert.Throws<ArgumentException>(Action(fun () -> Command("tool").CancelSignal Signal.Kill |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> Command("tool").CancelSignal(Signal.Other 0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a cancellation hard-kills at once when no CancelGrace is configured (T-373)``() : Task =
        task {
            let running, calls = cancelLadderProcess (Command.create "test").Config
            use _ = running

            running.BeginCancelTeardown()

            // Synchronous and unchanged: the kill has already been delivered by the time the teardown
            // call returns, and nothing was soft-signalled first.
            Assert.That(calls.Kills, Is.EqualTo 1, "the default cancellation must hard-kill immediately")
            Assert.That(calls.SignalTrace, Is.EqualTo "", "no soft signal may be sent without CancelGrace")
        }
        :> Task

    [<Test>]
    member _.``CancelGrace sends the soft signal first and escalates only after the window (T-373)``() : Task =
        task {
            let grace = TimeSpan.FromMilliseconds 400.0

            let config = (Command.create "test" |> Command.cancelGrace grace).Config

            let running, calls = cancelLadderProcess config
            use _ = running

            running.BeginCancelTeardown()

            // The soft signal is delivered inline; the hard kill is NOT — that is the whole point.
            Assert.That(calls.SignalTrace, Is.EqualTo "Term", "the ladder must open with the soft signal")
            Assert.That(calls.Kills, Is.EqualTo 0, "the hard kill must wait for the grace window")

            let! escalated = waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> calls.Kills > 0)

            Assert.That(escalated, Is.True, "a child that ignored the soft signal must still be hard-killed")
            Assert.That(calls.SignalTrace, Is.EqualTo "Term", "the soft signal must be sent exactly once")
        }
        :> Task

    [<Test>]
    member _.``a run torn down inside the cancel grace is never escalated (T-373)``() : Task =
        task {
            // A grace far longer than this test: the escalation may only be skipped because the handle's
            // own teardown cancelled it, never because the window elapsed.
            let config =
                (Command.create "test" |> Command.cancelGrace (TimeSpan.FromSeconds 30.0)).Config

            let running, calls = cancelLadderProcess config

            running.BeginCancelTeardown()
            Assert.That(calls.SignalTrace, Is.EqualTo "Term")

            // The child obeyed the soft signal and the run concluded: disposing the handle is what tears
            // the tree down from here, so the pending escalation must stand down rather than fire a kill
            // at a run that is over (and, on a real host, at a pid the OS may have recycled).
            do! (running :> IAsyncDisposable).DisposeAsync().AsTask()
            do! Task.Delay 300

            Assert.That(calls.Kills, Is.EqualTo 0, "a torn-down run must not be hard-killed by a stale escalation")
        }
        :> Task

    [<Test>]
    member _.``CancelSignal chooses the cancellation signal and StopSignal never gap-fills it (T-373)``() : Task =
        task {
            // A command that configures only the DEADLINE signal: its cancellation still opens with the
            // default Term, because the two knobs are independent by contract.
            let stopOnly =
                (Command.create "test"
                 |> Command.stopSignal Signal.Usr1
                 |> Command.cancelGrace (TimeSpan.FromMilliseconds 200.0))
                    .Config

            let running, calls = cancelLadderProcess stopOnly
            use _ = running
            running.BeginCancelTeardown()

            Assert.That(calls.SignalTrace, Is.EqualTo "Term", "StopSignal must not gap-fill the cancel signal")

            // And an explicitly chosen cancellation signal is the one that goes out — even alongside a
            // different `StopSignal`.
            let both =
                (Command.create "test"
                 |> Command.stopSignal Signal.Usr1
                 |> Command.cancelSignal Signal.Hup
                 |> Command.cancelGrace (TimeSpan.FromMilliseconds 200.0))
                    .Config

            let chosen, chosenCalls = cancelLadderProcess both
            use _ = chosen
            chosen.BeginCancelTeardown()

            Assert.That(chosenCalls.SignalTrace, Is.EqualTo "Hup", "CancelSignal must choose the soft signal")
        }
        :> Task

    [<Test>]
    member _.``a graceful cancellation still reports Cancelled and lets the child clean up (T-373)``() : Task =
        if isWindows then
            Assert.Ignore
                "POSIX signal delivery: Windows has no signal tier (its soft phase is the documented best-effort WM_CLOSE/CTRL+BREAK)."

        let marker =
            Path.Combine(Path.GetTempPath(), $"pk-cancel-grace-{Guid.NewGuid():N}.txt")

        task {
            try
                use cts = new CancellationTokenSource()

                // The child's SIGTERM handler is the only thing that can create the marker, so the file
                // is positive proof that the soft rung of the ladder actually reached the tree.
                let command =
                    shell $"trap 'echo stopped >> {marker}; exit 0' TERM; while :; do sleep 0.05; done"
                    |> Command.cancelOn cts.Token
                    |> Command.cancelGrace (TimeSpan.FromSeconds 5.0)

                let runTask = command.RunAsync()
                do! Task.Delay 400
                cts.Cancel()

                match! runTask with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"a cancelled run must still report Cancelled, got {other}"

                let! cleanedUp = waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> File.Exists marker)

                Assert.That(
                    cleanedUp,
                    Is.True,
                    "the cancellation must deliver the soft signal so the child can clean up"
                )
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``a cancellation without CancelGrace never delivers a soft signal (T-373)``() : Task =
        if isWindows then
            Assert.Ignore "POSIX signal delivery: Windows has no signal tier to prove the absence of."

        let marker =
            Path.Combine(Path.GetTempPath(), $"pk-cancel-hard-{Guid.NewGuid():N}.txt")

        task {
            try
                use cts = new CancellationTokenSource()

                // The same child, minus the opt-in: the default cancellation is a hard kill, which no
                // SIGTERM handler can observe, so the marker must never appear.
                let command =
                    shell $"trap 'echo stopped >> {marker}; exit 0' TERM; while :; do sleep 0.05; done"
                    |> Command.cancelOn cts.Token

                let runTask = command.RunAsync()
                do! Task.Delay 400
                cts.Cancel()

                match! runTask with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"expected Cancelled, got {other}"

                // Give a (wrongly delivered) signal every chance to land before concluding it did not.
                do! Task.Delay 750

                Assert.That(
                    File.Exists marker,
                    Is.False,
                    "the default cancellation must stay an immediate hard kill, with no soft signal"
                )
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``a graceful cancellation reports Cancelled on every platform (T-373)``() : Task =
        task {
            use cts = new CancellationTokenSource()

            // Windows has no signal tier: the soft phase is the documented best-effort WM_CLOSE/CTRL+BREAK
            // (which a console `ping` has neither of), so the grace elapses and the hard kill lands — the
            // same shape `TimeoutGrace` has there. Either way the answer is `Cancelled`.
            let command =
                sleeper ()
                |> Command.cancelOn cts.Token
                |> Command.cancelGrace (TimeSpan.FromMilliseconds 300.0)

            let runTask = command.RunAsync()
            do! Task.Delay 300
            cts.Cancel()

            match! runTask with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a graceful cancellation lets the child clean up on FirstLineAsync too (T-373)``() : Task =
        if isWindows then
            Assert.Ignore
                "POSIX signal delivery: Windows has no signal tier (its soft phase is the documented best-effort WM_CLOSE/CTRL+BREAK)."

        let marker =
            Path.Combine(Path.GetTempPath(), $"pk-cancel-grace-firstline-{Guid.NewGuid():N}.txt")

        task {
            try
                use cts = new CancellationTokenSource()

                // The streamed twin of the buffered ladder test above, and the one that needs asserting
                // separately: `FirstLineAsync` reaches its answer by streaming rather than by awaiting the
                // child's exit, so it is the completion verb that could answer while the ladder it started
                // is still on its first rung — and answering is what disposes the handle and hard-kills the
                // tree. The marker is written only by the child's own SIGTERM handler, so finding it
                // ALREADY THERE the moment the verb answers is positive proof the grace window belonged to
                // the child instead of being collapsed by the verb's own teardown.
                let command =
                    shell $"trap 'echo stopped >> {marker}; exit 0' TERM; while :; do echo tick; sleep 0.05; done"
                    |> Command.cancelOn cts.Token
                    |> Command.cancelGrace (TimeSpan.FromSeconds 5.0)

                // A predicate no line can satisfy: the verb streams until the token, never until a match.
                let runTask = command.FirstLineAsync(fun _ -> false)
                do! Task.Delay 400
                cts.Cancel()

                match! runTask with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"a cancelled FirstLineAsync must still report Cancelled, got {other}"

                Assert.That(
                    File.Exists marker,
                    Is.True,
                    "FirstLineAsync must let the cancellation ladder finish before it answers and reaps the tree"
                )
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``a cancelled FirstLineAsync waits for the grace, and only when one is configured (T-373)``() : Task =
        task {
            // The same contract from the timing side, on every platform: a child that will not leave on the
            // soft signal (POSIX ignores SIGTERM outright; a Windows console `ping` has neither a window nor
            // a console group for the best-effort soft tier to reach) can only be ended by the escalation,
            // so "the verb answered before the window elapsed" means the ladder was skipped.
            let grace = TimeSpan.FromSeconds 2.0

            let stubborn () =
                if isWindows then
                    sleeper ()
                else
                    shell "trap '' TERM; while :; do sleep 0.05; done"

            // Cancel a `FirstLineAsync` mid-run and report how long the verb then took to answer.
            let timeCancellation (command: Command) =
                task {
                    use cts = new CancellationTokenSource()
                    let runTask = (command |> Command.cancelOn cts.Token).FirstLineAsync(fun _ -> false)
                    do! Task.Delay 300
                    let stopwatch = Stopwatch.StartNew()
                    cts.Cancel()
                    let! result = runTask
                    stopwatch.Stop()

                    match result with
                    | Error(ProcessError.Cancelled _) -> ()
                    | other -> Assert.Fail $"expected Cancelled, got {other}"

                    return stopwatch.Elapsed
                }

            let! withLadder = timeCancellation (stubborn () |> Command.cancelGrace grace)

            Assert.That(
                withLadder,
                Is.GreaterThan(TimeSpan.FromMilliseconds 1200.0),
                "FirstLineAsync must wait out the configured cancel grace instead of hard-killing on the way out"
            )

            // And the default is untouched: with no `CancelGrace` there is no window to wait for, so the
            // verb answers as promptly as it always has — nowhere near the window the ladder run paid.
            let! withoutLadder = timeCancellation (stubborn ())

            Assert.That(
                withoutLadder,
                Is.LessThan(TimeSpan.FromMilliseconds 1000.0),
                "a cancellation with no CancelGrace must still answer at once, with no grace window"
            )
        }
        :> Task

    [<Test>]
    member _.``the in-library test doubles walk the same cancellation ladder (T-373)``() : Task =
        task {
            // `ScriptedRunner`/`DryRunRunner`/`FaultInjectingRunner` all serve their completion verbs
            // through `Seam.complete`, whose cancellation registration is exactly this seam — so a
            // consumer whose tests lean on a double sees the same ladder the real runners walk, instead
            // of a double that quietly still hard-kills.
            let command =
                Command.create "tool"
                |> Command.cancelSignal Signal.Hup
                |> Command.cancelGrace (TimeSpan.FromSeconds 30.0)

            let fake = FakeProcess.OfCommand command
            let running = fake.Build()

            running.BeginCancelTeardown()

            Assert.That(
                fake.Signals |> Seq.map string |> String.concat ",",
                Is.EqualTo "Hup",
                "a double must record the configured soft signal instead of jumping straight to the kill"
            )

            // Disposal stands the pending escalation down, exactly as it does on a real handle.
            do! (running :> IAsyncDisposable).DisposeAsync().AsTask()
        }
        :> Task

    [<Test>]
    member _.``Windows refuses a custom Command CancelSignal at spawn (T-373)``() : Task =
        if not isWindows then
            Assert.Ignore "Windows-specific representability contract."

        task {
            // The mirror of the `StopSignal` refusal: an arbitrary POSIX signal is not representable on
            // Windows, so the spawn fails with a typed `Unsupported` rather than quietly falling back to
            // the hard kill while the knob reads as honoured.
            let command =
                sleeper ()
                |> Command.cancelSignal Signal.Int
                |> Command.cancelGrace (TimeSpan.FromSeconds 1.0)

            match! command.StartAsync() with
            | Error(ProcessError.Unsupported operation) ->
                Assert.That(operation, Does.Contain "CancelSignal", "the refusal must name the offending knob")
            | Error error -> Assert.Fail $"expected Unsupported, got {error}"
            | Ok running ->
                do! (running :> IAsyncDisposable).DisposeAsync().AsTask()
                Assert.Fail "a custom Windows cancellation signal was silently accepted"
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

        // Consumed immediately: the whole budget is available, exactly as before this change — and it
        // is longer than the settle window, so nothing is added to it.
        Timeouts.totalDeadline (Some configured) TimeSpan.Zero
        |> assertDeadline configured configured TimeSpan.Zero "consumed at once"

        // Consumed partway through: only the remainder is armed, and the configured duration — what
        // `ProcessError.Timeout` and the timeout log report — is untouched.
        Timeouts.totalDeadline (Some configured) (TimeSpan.FromMilliseconds 1_500.0)
        |> assertDeadline
            configured
            (TimeSpan.FromMilliseconds 500.0)
            TimeSpan.Zero
            "consumed with a quarter of the budget left"

        // A spawn stamp from the future (only a synthetic host can produce one) must not WIDEN the
        // budget past what was configured.
        Timeouts.totalDeadline (Some configured) (TimeSpan.FromSeconds -30.0)
        |> assertDeadline configured configured TimeSpan.Zero "a spawn stamp from the future"

    [<Test>]
    member _.``a budget already spent arms nothing and settles for the bounded window (T-356)``() : unit =
        let configured = TimeSpan.FromSeconds 2.0

        let spent =
            "a spent budget must not be re-issued — only the settle window that lets an already-published exit be seen"

        for elapsed in [ configured; configured + TimeSpan.FromTicks 1L; TimeSpan.FromHours 3.0 ] do
            Timeouts.totalDeadline (Some configured) elapsed
            |> assertDeadline configured TimeSpan.Zero Timeouts.exitSettleWindow spent

        Assert.That(
            Timeouts.exitSettleWindow,
            Is.LessThan(TimeSpan.FromSeconds 1.0),
            "the settle window is a moment to observe an exit, never a second budget"
        )

        // A zero-length budget has no exit-inside-it to observe, so it keeps firing at once.
        Timeouts.totalDeadline (Some TimeSpan.Zero) (TimeSpan.FromSeconds 5.0)
        |> assertDeadline TimeSpan.Zero TimeSpan.Zero TimeSpan.Zero "a zero-length budget"

    // R-01/R-02: the settle window is what keeps a late collect from fabricating a timeout for a child
    // that had already finished — so it cannot depend on the remainder happening to be exactly zero
    // (a 5 ms remainder is no more able to surface an exit than none at all), and it cannot push a kill
    // past the duration the caller configured (a `Timeout(50ms)` collected late must still be killed
    // 50 ms later, not a fixed quarter second later). Both are one rule, asserted here without a clock:
    // every wait is in flight for `min(exitSettleWindow, Configured)` before its deadline may fire.

    [<Test>]
    member _.``every deadline gets the same settle window in flight, however small the remainder (T-356)``() : unit =
        let configured = TimeSpan.FromSeconds 2.0

        // A remainder shorter than the window is topped up to it; one longer already had it.
        for remainingMs in [ 0.0; 1.0; 5.0; 100.0; 249.0; 250.0; 251.0; 1_000.0 ] do
            let remaining = TimeSpan.FromMilliseconds remainingMs

            match Timeouts.totalDeadline (Some configured) (configured - remaining) with
            | Some deadline ->
                Assert.That(deadline.Armed, Is.EqualTo remaining, $"{remainingMs} ms left of the budget")

                Assert.That(
                    deadline.Armed + deadline.Settle,
                    Is.EqualTo(max remaining Timeouts.exitSettleWindow),
                    $"a wait armed for {remainingMs} ms must still get the settle window in flight before it may fire"
                )
            | None -> Assert.Fail $"expected a deadline with {remainingMs} ms left, got none"

    [<Test>]
    member _.``the settle window never defers a kill past the configured duration (T-356)``() : unit =
        // The window a wait is entitled to, and the moment (measured from spawn) its deadline fires.
        let window (configured: TimeSpan) =
            min Timeouts.exitSettleWindow configured

        let firesAt (configured: TimeSpan) (elapsed: TimeSpan) =
            match Timeouts.totalDeadline (Some configured) elapsed with
            | Some deadline -> elapsed + deadline.Armed + deadline.Settle
            | None -> failwith $"expected a deadline for {configured} consumed at {elapsed}"

        for configuredMs in [ 0.0; 10.0; 50.0; 100.0; 250.0; 400.0; 2_000.0 ] do
            let configured = TimeSpan.FromMilliseconds configuredMs

            match Timeouts.totalDeadline (Some configured) (TimeSpan.FromSeconds 5.0) with
            | Some deadline ->
                Assert.That(
                    deadline.Settle,
                    Is.LessThanOrEqualTo configured,
                    $"a {configuredMs} ms timeout may not settle for longer than the caller configured"
                )
            | None -> Assert.Fail $"expected a deadline for a {configuredMs} ms timeout"

            // Sweeping the collect time: the deadline fires at the configured duration for a prompt
            // consumer, and never later than the settle window past a late one — and never EARLIER for a
            // later consumer, which is what makes the rule a deadline rather than a lottery (a 99 ms
            // collect of a 100 ms timeout must not be killed after a 101 ms one).
            let mutable previous = TimeSpan.MinValue

            for elapsedMs in [ 0..5..600 ] do
                let elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
                let fires = firesAt configured elapsed

                Assert.That(
                    fires,
                    Is.EqualTo(max configured (elapsed + window configured)),
                    $"a {configuredMs} ms timeout collected at {elapsedMs} ms"
                )

                Assert.That(
                    fires,
                    Is.GreaterThanOrEqualTo previous,
                    $"a {configuredMs} ms timeout collected at {elapsedMs} ms fires earlier than an earlier collect did"
                )

                previous <- fires

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
    member _.``a child that finished inside its deadline is not timed out for a sliver of budget (T-356)``() : Task =
        task {
            // The same honest result, but with the budget only ALMOST spent: 1.9s of a 2s deadline gone
            // when the collecting verb arrives, so the exit wait is armed for the ~0.1s remainder — less
            // than the settle window, and so no more able to surface an exit the child had already made
            // than no budget at all would be. The status arrives on a kernel callback and a thread-pool
            // hop, modelled here by publishing it 160 ms after the wait starts: comfortably after the
            // remainder runs out (without the settle window the `Task.Delay` wins, a tree that is already
            // gone is killed, and a run that exceeded nothing is reported as `TimedOut`) and comfortably
            // inside the settle window that must now cover it. A remainder of single-digit milliseconds
            // — the shape a caller would actually hit — is the same case, and is left to the clock-free
            // arithmetic tests above, where scheduling jitter cannot blunt it.
            let running, calls =
                backdatedProcessPublishing
                    twoSecondTimeout
                    (TimeSpan.FromMilliseconds 1_900.0)
                    true
                    (TimeSpan.FromMilliseconds 160.0)
                    (Outcome.Exited 0)

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! result = running.OutputStringAsync()
            stopwatch.Stop()

            match result with
            | Ok captured ->
                Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))

                Assert.That(
                    captured.IsTimedOut,
                    Is.False,
                    "the child exited inside its deadline; how much of the budget was left when it was collected cannot change that"
                )
            | Error error -> Assert.Fail $"{error}"

            Assert.That(stopwatch.Elapsed, Is.LessThan spentBudgetCeiling)
            Assert.That(calls.Kills, Is.Zero, "a child that already exited must not be killed")
        }
        :> Task

    [<Test>]
    member _.``a Timeout shorter than the settle window is still killed within it (T-356)``() : Task =
        task {
            // The settle window is capped by the configured duration, so a timeout shorter than the
            // window cannot be deferred to it: `Timeout(50ms)` collected a second late is killed ~50 ms
            // later — the same moment it would have been killed before the deadline was anchored at the
            // spawn — not a fixed quarter second later.
            let shortTimeout =
                (Command.create "test" |> Command.timeout (TimeSpan.FromMilliseconds 50.0)).Config

            let running, calls =
                backdatedProcess shortTimeout (TimeSpan.FromSeconds 1.0) false (Outcome.Signalled(Some 9))

            use _ = running
            let stopwatch = Stopwatch.StartNew()
            let! outcome = running.WaitAsync()
            stopwatch.Stop()

            Assert.That(outcome, Is.EqualTo Outcome.TimedOut)

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan Timeouts.exitSettleWindow,
                "a 50 ms timeout must not be held to the full settle window before its (already overdue) kill"
            )

            Assert.That(calls.Kills, Is.EqualTo 1, "exactly one kill")
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
    member _.``a replayed handle's frozen completion clock never feeds the deadline (T-356 x T-348)``() : Task =
        task {
            // Two clocks on one handle, and they must not be confused. A replayed cassette handle freezes
            // its COMPLETION clock (`Elapsed`/`ProcessResult.Duration`) at the entry's recorded duration,
            // while the total deadline is measured from this handle's own monotonic spawn stamp. Reading
            // the frozen one as the deadline's clock would re-arm `Timeout - recordedDuration` on every
            // wait instead of resolving to one absolute deadline — and for a recording at least as long as
            // the timeout it arms nothing at all, so the deadline fires inside the settle window and
            // reports a `TimedOut` that was in neither the recording nor the elapsed time.
            //
            // Spawned NOW with a 2s deadline over a 5s recording, the status arriving 600 ms into the wait:
            // far inside the real remaining budget, far outside the ~0 the frozen clock would have left.
            let recorded = TimeSpan.FromSeconds 5.0

            let running, calls =
                replayedProcess
                    twoSecondTimeout
                    TimeSpan.Zero
                    (TimeSpan.FromMilliseconds 600.0)
                    recorded
                    (Outcome.Exited 0)

            use _ = running

            match! running.OutputStringAsync() with
            | Ok result ->
                Assert.That(
                    result.Outcome,
                    Is.EqualTo(Outcome.Exited 0),
                    "the run's real outcome, not a deadline armed from a clock that cannot advance"
                )

                Assert.That(result.IsTimedOut, Is.False)

                Assert.That(
                    result.Duration,
                    Is.EqualTo recorded,
                    "the recorded duration is still exactly what the result reports (T-348)"
                )
            | Error error -> Assert.Fail $"{error}"

            Assert.That(running.Elapsed, Is.EqualTo recorded, "the completion clock stays frozen at the recording")
            Assert.That(calls.Kills, Is.Zero, "there is no live tree behind a recording to kill")
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
