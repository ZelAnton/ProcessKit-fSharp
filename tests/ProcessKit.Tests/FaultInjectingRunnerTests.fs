namespace ProcessKit.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

type private FaultTimer(callback: TimerCallback, state: obj | null) =
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

type private FaultTimeProvider() =
    inherit TimeProvider()

    let timerCreated =
        TaskCompletionSource<FaultTimer * TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously)

    override _.CreateTimer(callback, state, dueTime, _period) =
        let timer = new FaultTimer(callback, state)
        timerCreated.TrySetResult(timer, dueTime) |> ignore
        timer :> ITimer

    member _.TimerCreated = timerCreated.Task

[<TestFixture>]
type FaultInjectingRunnerTests() =

    let inner () : IProcessRunner =
        ScriptedRunner().Fallback(Reply.Ok "delegated")

    [<Test>]
    member _.``first-call policy injects N typed failures then delegates``() : Task =
        task {
            let injected = FaultInjection.Error(ProcessError.Io "transient")
            let runner = FaultInjectingRunner(inner (), 2, injected)
            let seam = runner :> IProcessRunner
            let command = Command.create "tool"

            for _ in 1..2 do
                match! seam.OutputStringAsync(command, CancellationToken.None) with
                | Error(ProcessError.Io "transient") -> ()
                | other -> Assert.Fail $"expected injected I/O error, got {other}"

            match! seam.OutputStringAsync(command, CancellationToken.None) with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "delegated")
            | Error error -> Assert.Fail $"expected delegation, got {error}"

            Assert.That(runner.InvocationCount, Is.EqualTo 3)
        }
        :> Task

    [<Test>]
    member _.``scripted outcomes and delayed delegation preserve invocation order``() : Task =
        task {
            let runner =
                FaultInjectingRunner(
                    inner (),
                    [ FaultInjection.Outcome(Outcome.Signalled(Some 15))
                      FaultInjection.Outcome Outcome.TimedOut
                      FaultInjection.Delegate() ]
                )
                :> IProcessRunner

            match! runner.OutputStringAsync(Command.create "tool", CancellationToken.None) with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Signalled(Some 15)))
            | Error error -> Assert.Fail $"expected synthetic signal, got {error}"

            match! runner.OutputStringAsync(Command.create "tool", CancellationToken.None) with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo Outcome.TimedOut)
            | Error error -> Assert.Fail $"expected synthetic timeout, got {error}"

            match! runner.OutputStringAsync(Command.create "tool", CancellationToken.None) with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "delegated")
            | Error error -> Assert.Fail $"expected scripted delegation, got {error}"
        }
        :> Task

    [<Test>]
    member _.``seeded policy is reproducible for the same seed and invocation order``() : Task =
        task {
            let injection = FaultInjection.Error(ProcessError.Spawn("tool", "seeded"))

            let first =
                FaultInjectingRunner.Seeded(inner (), 12345, 0.5, injection) :> IProcessRunner

            let second =
                FaultInjectingRunner.Seeded(inner (), 12345, 0.5, injection) :> IProcessRunner

            let sample (runner: IProcessRunner) =
                task {
                    let values = ResizeArray<bool>()

                    for _ in 1..32 do
                        match! runner.OutputStringAsync(Command.create "tool", CancellationToken.None) with
                        | Error(ProcessError.Spawn _) -> values.Add true
                        | Ok _ -> values.Add false
                        | Error other -> Assert.Fail $"unexpected seeded result: {other}"

                    return values.ToArray()
                }

            let! firstValues = sample first
            let! secondValues = sample second

            Assert.That(
                String.Concat(firstValues |> Array.map string),
                Is.EqualTo(String.Concat(secondValues |> Array.map string))
            )

            Assert.That(firstValues, Has.Some.True)
            Assert.That(firstValues, Has.Some.False)
        }
        :> Task

    [<Test>]
    member _.``injected latency uses Command TimeProvider without real sleeping``() : Task =
        task {
            let provider = FaultTimeProvider()

            let injection =
                FaultInjection.Error(ProcessError.Io "late").WithLatency(TimeSpan.FromSeconds 7.0)

            let runner = FaultInjectingRunner(inner (), 1, injection) :> IProcessRunner
            let command = (Command.create "tool").TimeProvider provider
            let pending = runner.OutputStringAsync(command, CancellationToken.None)
            let! timer, dueTime = provider.TimerCreated

            Assert.That(dueTime, Is.EqualTo(TimeSpan.FromSeconds 7.0))
            Assert.That(pending.IsCompleted, Is.False)
            timer.Fire()

            match! pending with
            | Error(ProcessError.Io "late") -> ()
            | other -> Assert.Fail $"expected delayed injected error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``synthetic outcome also works through the live Spawn seam``() : Task =
        task {
            let runner =
                FaultInjectingRunner(inner (), 1, FaultInjection.Outcome(Outcome.Exited 23)) :> IProcessRunner

            match! runner.StartAsync(Command.create "tool", CancellationToken.None) with
            | Error error -> Assert.Fail $"expected a fake live handle, got {error}"
            | Ok running ->
                use running = running

                match! running.OutputStringAsync() with
                | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 23))
                | Error error -> Assert.Fail $"expected synthetic exit, got {error}"
        }
        :> Task
