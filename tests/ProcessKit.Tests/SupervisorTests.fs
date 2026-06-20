namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// A subprocess-free `IProcessRunner` that returns a fixed *sequence* of replies, one per call —
/// the supervisor analog of the Rust `SeqRunner`. Running out of replies fails the test loudly, so
/// an unexpected restart is caught rather than hanging.
type private SequenceRunner(replies: Result<ProcessResult<string>, ProcessError> list) =
    let queue = Queue<Result<ProcessResult<string>, ProcessError>>(replies)

    interface IProcessRunner with
        member _.OutputString(_command, _cancellationToken) =
            if queue.Count = 0 then
                failwith "SequenceRunner ran out of scripted replies"

            Task.FromResult(queue.Dequeue())

        member _.OutputBytes(_command, _cancellationToken) =
            failwith "SequenceRunner only scripts OutputString"

        member _.Start(_command, _cancellationToken) =
            failwith "SequenceRunner only scripts OutputString"

/// Records the output-buffer policy of the command it is asked to run, so a test can assert the
/// supervisor applied its capture policy to each incarnation.
type private CapturingRunner() =
    member val SeenPolicy: OutputBufferPolicy option = None with get, set

    interface IProcessRunner with
        member this.OutputString(command, _cancellationToken) =
            this.SeenPolicy <- Some command.Config.OutputBuffer

            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, "out", "", Outcome.Exited 0, TimeSpan.Zero, false))
            )

        member _.OutputBytes(_command, _cancellationToken) =
            failwith "CapturingRunner only scripts OutputString"

        member _.Start(_command, _cancellationToken) =
            failwith "CapturingRunner only scripts OutputString"

/// A virtual clock: `Sleep` records the requested delay and advances `Now` instead of waiting, so
/// backoff and storm-decay timing is deterministic and instant (the analog of tokio's paused clock).
type private FakeClock() =
    let mutable now = 0.0
    let delays = ResizeArray<TimeSpan>()

    member _.Delays = delays

    member _.Now() = now

    member _.Sleep(delay: TimeSpan, _cancellationToken: CancellationToken) : Task =
        delays.Add delay
        now <- now + delay.TotalSeconds
        Task.CompletedTask

[<TestFixture>]
type SupervisorTests() =

    let ok () : Result<ProcessResult<string>, ProcessError> =
        Ok(ProcessResult<string>("fake", "out", "", Outcome.Exited 0, TimeSpan.Zero, false))

    let failWith (code: int) : Result<ProcessResult<string>, ProcessError> =
        Ok(ProcessResult<string>("fake", "", "boom", Outcome.Exited code, TimeSpan.Zero, false))

    let timedOut () : Result<ProcessResult<string>, ProcessError> =
        Ok(ProcessResult<string>("fake", "", "", Outcome.TimedOut, TimeSpan.FromSeconds 1.0, false))

    let spawnErr () : Result<ProcessResult<string>, ProcessError> =
        Error(ProcessError.Spawn("fake", "no such binary"))

    let cancelledErr () : Result<ProcessResult<string>, ProcessError> = Error(ProcessError.Cancelled "fake")

    let runner (replies: Result<ProcessResult<string>, ProcessError> list) =
        SequenceRunner replies :> IProcessRunner

    // A supervisor with zero backoff and no jitter — for behaviour tests where timing is irrelevant.
    let supervise (replies: Result<ProcessResult<string>, ProcessError> list) =
        Supervisor(Command.create "fake").WithRunner(runner replies).Backoff(TimeSpan.Zero, 1.0).Jitter(false)

    let withClock (clock: FakeClock) (supervisor: Supervisor) =
        supervisor.WithClock(clock.Now, (fun delay ct -> clock.Sleep(delay, ct)))

    let totalMs (clock: FakeClock) =
        clock.Delays |> Seq.sumBy (fun d -> d.TotalMilliseconds)

    // ----- restart policy & stop semantics -----

    [<Test>]
    member _.``OnCrash restarts until success``() : Task =
        task {
            match! supervise([ failWith 1; failWith 1; ok () ]).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
                Assert.That(outcome.FinalResult.IsSuccess, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``zero MaxRestarts means a single run``() : Task =
        task {
            match! supervise([ failWith 1; ok () ]).MaxRestarts(0).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 0)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.RestartsExhausted)
                Assert.That(outcome.FinalResult.Code, Is.EqualTo(Some 1))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OnCrash accepts a clean first run``() : Task =
        task {
            match! supervise([ ok () ]).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 0)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``the predicate beats the policy``() : Task =
        task {
            let sup =
                supervise([ ok () ]).Restart(RestartPolicy.Always).StopWhen(fun result -> result.Code = Some 0)

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 0)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.Predicate)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Always restarts clean runs until the predicate``() : Task =
        task {
            let mutable seen = 0

            let sup =
                supervise([ ok (); ok (); ok () ])
                    .Restart(RestartPolicy.Always)
                    .StopWhen(fun _ ->
                        let n = seen
                        seen <- seen + 1
                        n = 2)

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.Predicate)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Never reports a failing run without restarting``() : Task =
        task {
            match! supervise([ failWith 3 ]).Restart(RestartPolicy.Never).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 0)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
                Assert.That(outcome.FinalResult.Code, Is.EqualTo(Some 3))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``exhausting the budget reports the last failure``() : Task =
        task {
            match! supervise([ failWith 7; failWith 7; failWith 7 ]).MaxRestarts(2).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.RestartsExhausted)
                Assert.That(outcome.FinalResult.Code, Is.EqualTo(Some 7))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a timeout counts as a crash``() : Task =
        task {
            match! supervise([ timedOut (); ok () ]).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 1)
                Assert.That(outcome.FinalResult.IsSuccess, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a terminal spawn error surfaces as Error``() : Task =
        task {
            match! supervise([ spawnErr (); spawnErr () ]).MaxRestarts(1).Run() with
            | Error(ProcessError.Spawn _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Spawn, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a spawn error is retried like a crash``() : Task =
        task {
            match! supervise([ spawnErr (); ok () ]).Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 1)
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a cancelled incarnation is terminal under Always``() : Task =
        task {
            // Always would restart any failure; Cancelled must end supervision at once — the second
            // reply is never consumed (SequenceRunner fails loudly if it is).
            let sup =
                supervise([ cancelledErr (); ok () ]).Restart(RestartPolicy.Always).MaxRestarts(5)

            match! sup.Run() with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Never returns a spawn error directly``() : Task =
        task {
            match! supervise([ spawnErr () ]).Restart(RestartPolicy.Never).Run() with
            | Error(ProcessError.Spawn _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Spawn, got {other}"
        }
        :> Task

    // ----- backoff timing (virtual clock) -----

    [<Test>]
    member _.``backoff doubles per restart without jitter``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                Supervisor(Command.create "fake")
                    .WithRunner(runner [ failWith 1; failWith 1; ok () ])
                    .Backoff(TimeSpan.FromMilliseconds 200.0, 2.0)
                    .Jitter(false)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(clock.Delays.Count, Is.EqualTo 2)
                Assert.That(totalMs clock, Is.EqualTo(600.0).Within 1.0) // 200 + 400
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``MaxBackoff caps the delay``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                Supervisor(Command.create "fake")
                    .WithRunner(runner [ failWith 1; failWith 1; ok () ])
                    .Backoff(TimeSpan.FromMilliseconds 200.0, 2.0)
                    .MaxBackoff(TimeSpan.FromMilliseconds 300.0)
                    .Jitter(false)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(totalMs clock, Is.EqualTo(500.0).Within 1.0) // 200 + 400->300
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a nonsense backoff factor decays to a constant delay``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                Supervisor(Command.create "fake")
                    .WithRunner(runner [ failWith 1; failWith 1; ok () ])
                    .Backoff(TimeSpan.FromMilliseconds 100.0, 0.0)
                    .Jitter(false)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(totalMs clock, Is.EqualTo(200.0).Within 1.0) // 100 + 100
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``jitter stays within its band``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                Supervisor(Command.create "fake")
                    .WithRunner(runner [ failWith 1; ok () ])
                    .Backoff(TimeSpan.FromMilliseconds 1000.0, 1.0)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 1)
                let waited = (Seq.head clock.Delays).TotalMilliseconds
                Assert.That(waited, Is.InRange(500.0, 1500.0))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // ----- failure-storm guard -----

    [<Test>]
    member _.``the storm guard is off by default``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                supervise [ failWith 1; failWith 1; failWith 1; failWith 1; ok () ]
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.StormPauses, Is.EqualTo 0)
                Assert.That(totalMs clock, Is.EqualTo 0.0) // no hidden pauses
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``the storm trips past the threshold``() : Task =
        task {
            // Zero backoff -> zero decay: scores 1, 2, 3; the third crosses 2.5 -> one pause.
            let clock = FakeClock()

            let sup =
                supervise([ failWith 1; failWith 1; failWith 1; ok () ])
                    .StormPause(TimeSpan.FromSeconds 1.0)
                    .FailureThreshold(2.5)
                    .FailureDecay(TimeSpan.FromSeconds 1000.0)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 3)
                Assert.That(outcome.StormPauses, Is.EqualTo 1)
                Assert.That(totalMs clock, Is.EqualTo(1000.0).Within 1.0)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``spaced failures decay below the threshold``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                Supervisor(Command.create "fake")
                    .WithRunner(runner [ failWith 1; failWith 1; failWith 1; ok () ])
                    .Backoff(TimeSpan.FromSeconds 10.0, 1.0)
                    .Jitter(false)
                    .StormPause(TimeSpan.FromSeconds 1.0)
                    .FailureThreshold(2.5)
                    .FailureDecay(TimeSpan.FromSeconds 1.0)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 3)
                Assert.That(outcome.StormPauses, Is.EqualTo 0)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a storm pause resets the score``() : Task =
        task {
            // Threshold 1.5: scores 1, 2(pause), 1, 2(pause) — reset after each pause.
            let clock = FakeClock()

            let sup =
                supervise([ failWith 1; failWith 1; failWith 1; failWith 1; ok () ])
                    .StormPause(TimeSpan.FromSeconds 1.0)
                    .FailureThreshold(1.5)
                    .FailureDecay(TimeSpan.FromSeconds 1000.0)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 4)
                Assert.That(outcome.StormPauses, Is.EqualTo 2)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``an exhausted budget wins over the storm gate``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                supervise([ failWith 1; failWith 1 ])
                    .MaxRestarts(1)
                    .StormPause(TimeSpan.FromSeconds 60.0)
                    .FailureThreshold(1.5)
                    .FailureDecay(TimeSpan.FromSeconds 1000.0)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.RestartsExhausted)
                Assert.That(outcome.StormPauses, Is.EqualTo 0)
                Assert.That(totalMs clock, Is.EqualTo 0.0)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``clean restarts under Always do not feed the storm score``() : Task =
        task {
            let mutable seen = 0
            let clock = FakeClock()

            let sup =
                supervise([ ok (); ok (); ok () ])
                    .Restart(RestartPolicy.Always)
                    .StormPause(TimeSpan.FromSeconds 60.0)
                    .FailureThreshold(1.5)
                    .FailureDecay(TimeSpan.FromSeconds 1000.0)
                    .StopWhen(fun _ ->
                        let n = seen
                        seen <- seen + 1
                        n = 2)
                |> withClock clock

            match! sup.Run() with
            | Ok outcome ->
                Assert.That(outcome.Restarts, Is.EqualTo 2)
                Assert.That(outcome.StormPauses, Is.EqualTo 0)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``cancellation is terminal before any storm pause``() : Task =
        task {
            let clock = FakeClock()

            let sup =
                supervise([ cancelledErr () ]).StormPause(TimeSpan.FromSeconds 60.0).FailureThreshold(0.0)
                |> withClock clock

            match! sup.Run() with
            | Error(ProcessError.Cancelled _) -> Assert.That(totalMs clock, Is.EqualTo 0.0) // no storm pause was taken
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Run applies the capture policy to each incarnation``() : Task =
        task {
            let capturing = CapturingRunner()

            match! Supervisor(Command.create "server").Restart(RestartPolicy.Never).WithRunner(capturing).Run() with
            | Ok _ ->
                Assert.That(capturing.SeenPolicy.Value.MaxLines, Is.EqualTo(Some Supervision.DefaultSupervisionTail))
            | Error error -> Assert.Fail $"{error}"

            let unboundedRunner = CapturingRunner()

            match!
                Supervisor(Command.create "server")
                    .Restart(RestartPolicy.Never)
                    .Capture(OutputBufferPolicy.Unbounded())
                    .WithRunner(unboundedRunner)
                    .Run()
            with
            | Ok _ -> Assert.That(unboundedRunner.SeenPolicy.Value.MaxLines.IsNone, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // ----- pure supervision math -----

    [<Test>]
    member _.``decayed failure score math``() =
        let hl = TimeSpan.FromSeconds 30.0
        Assert.That(Supervision.decayedFailureScore 0.0 0.0 hl, Is.EqualTo 1.0)
        Assert.That(Supervision.decayedFailureScore 1.0 0.0 hl, Is.EqualTo 2.0)
        Assert.That(Supervision.decayedFailureScore 2.0 30.0 hl, Is.EqualTo(2.0).Within 1e-9) // one half-life
        Assert.That(Supervision.decayedFailureScore 4.0 30.0 hl, Is.EqualTo(3.0).Within 1e-9)
        Assert.That(Supervision.decayedFailureScore 8.0 3000.0 hl, Is.EqualTo(1.0).Within 1e-9) // many half-lives
        Assert.That(Supervision.decayedFailureScore 100.0 0.0 TimeSpan.Zero, Is.EqualTo 1.0) // no history
        Assert.That(Supervision.decayedFailureScore nan 0.0 hl, Is.EqualTo 1.0) // poisoned -> reset

    [<Test>]
    member _.``backoff delay math``() =
        let baseDelay = TimeSpan.FromMilliseconds 100.0
        let cap = TimeSpan.FromSeconds 30.0
        Assert.That(Supervision.backoffDelay baseDelay 2.0 0 cap, Is.EqualTo baseDelay)
        Assert.That(Supervision.backoffDelay baseDelay 2.0 1 cap, Is.EqualTo(TimeSpan.FromMilliseconds 200.0))
        Assert.That(Supervision.backoffDelay baseDelay 2.0 3 cap, Is.EqualTo(TimeSpan.FromMilliseconds 800.0))
        Assert.That(Supervision.backoffDelay baseDelay 2.0 1000 cap, Is.EqualTo cap) // astronomic -> cap
        Assert.That(Supervision.backoffDelay TimeSpan.Zero 2.0 5 cap, Is.EqualTo TimeSpan.Zero)

    [<Test>]
    member _.``the jitter factor stays in band``() =
        for _ in 1..256 do
            let f = Supervision.jitterFactor ()
            Assert.That(f, Is.InRange(0.5, 1.5))

    [<Test>]
    member _.``applyJitter clamps instead of overflowing``() =
        let clamped = Supervision.applyJitter TimeSpan.MaxValue true
        Assert.That(clamped, Is.LessThanOrEqualTo Supervision.maxDelay)
        Assert.That(Supervision.applyJitter TimeSpan.MaxValue false, Is.EqualTo TimeSpan.MaxValue)
        Assert.That(Supervision.applyJitter TimeSpan.Zero true, Is.EqualTo TimeSpan.Zero)
        let normal = Supervision.applyJitter (TimeSpan.FromSeconds 10.0) true
        Assert.That(normal.TotalSeconds, Is.InRange(5.0, 15.0))

    [<Test>]
    member _.``the default capture bounds an unbounded command``() =
        let unbounded = Command.create "server"
        let policy = Supervision.defaultCapture unbounded
        Assert.That(policy.MaxLines, Is.EqualTo(Some Supervision.DefaultSupervisionTail))
        Assert.That(policy.Overflow, Is.EqualTo OverflowMode.DropOldest)

        let failLoud =
            Command.create "server"
            |> Command.outputBuffer (OutputBufferPolicy.Unbounded().WithOverflow OverflowMode.Error)

        let policy = Supervision.defaultCapture failLoud
        Assert.That(policy.MaxLines, Is.EqualTo(Some Supervision.DefaultSupervisionTail))
        Assert.That(policy.Overflow, Is.EqualTo OverflowMode.Error) // unbounded+Error -> bounded fail-loud

        let explicit =
            Command.create "server" |> Command.outputBuffer (OutputBufferPolicy.FailLoud 50)

        let policy = Supervision.defaultCapture explicit
        Assert.That(policy.MaxLines, Is.EqualTo(Some 50)) // explicit cap respected
        Assert.That(policy.Overflow, Is.EqualTo OverflowMode.Error)
