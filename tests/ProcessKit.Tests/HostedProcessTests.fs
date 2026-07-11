namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NUnit.Framework
open ProcessKit
open ProcessKit.Extensions.Hosting

type private BlockingRunner() =
    let started =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let stopRequested =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let killed =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let finished =
        new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable spawnCount = 0

    member _.Started = started.Task
    member _.StopRequested = stopRequested.Task

    /// Resolves once the (fire-and-forget) hard kill path — `RunningProcess.Kill()` /
    /// `IHostedService`'s `Dispose` — runs, as distinct from a graceful `StopRequested`.
    member _.Killed = killed.Task

    /// Total `SpawnAsync` calls so far — lets a test assert a second `StartAsync` (e.g. after
    /// `Dispose`) never spawns another child.
    member _.SpawnCount = Volatile.Read(&spawnCount)

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
                Interlocked.Increment(&spawnCount) |> ignore
                let stdout = new MemoryStream()
                let stderr = new MemoryStream()

                let host: RunningHost =
                    { Config = command.Config
                      Pid = Some 12345
                      Stdout = Some(stdout :> Stream)
                      Stderr = Some(stderr :> Stream)
                      Stdin = None
                      StartTime = DateTime.UtcNow
                      StartedTimestamp = Stopwatch.GetTimestamp()
                      StartTimeIdentity = None
                      Wait = fun () -> finished.Task
                      StdinError = fun () -> None
                      StartKill =
                        fun () ->
                            killed.TrySetResult() |> ignore
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                      GracefulKill =
                        fun _ ->
                            stopRequested.TrySetResult() |> ignore
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                            Task.CompletedTask
                      Teardown =
                        fun () ->
                            stdout.Dispose()
                            stderr.Dispose()
                            ValueTask.CompletedTask }

                started.TrySetResult() |> ignore
                Task.FromResult(Ok(new RunningProcess(host)))

        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

/// An `IProcessRunner` whose `SpawnAsync` throws instead of returning a `Result` error — simulating a
/// failure that escapes an already-built `Supervisor.RunAsync` rather than one it classifies itself.
type private ThrowingRunner() =
    interface IProcessRunner with
        member _.SpawnAsync(_command, _cancellationToken) : Task<Result<RunningProcess, ProcessError>> =
            raise (InvalidOperationException "boom from ThrowingRunner")

        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

[<TestFixture>]
type HostedProcessTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let quoteForSh (value: string) =
        "'" + value.Replace("'", "'\"'\"'") + "'"

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let tempFile name =
        Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ProcessKit-{Guid.NewGuid():N}-{name}.txt")

    let waitUntil (predicate: unit -> bool) : Task<bool> =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 6.0
            let mutable done' = predicate ()

            while not done' && DateTime.UtcNow < deadline do
                do! Task.Delay 50
                done' <- predicate ()

            return done'
        }

    let restartThenRun marker =
        if isWindows then
            shell $"if exist \"{marker}\" (ping -n 20 127.0.0.1 >nul) else (echo first > \"{marker}\" & exit /b 3)"
        else
            shell $"if [ -f {quoteForSh marker} ]; then sleep 20; else printf first > {quoteForSh marker}; exit 3; fi"

    let startRegisteredServiceWithRunner name command configure (runner: IProcessRunner option) =
        let services = ServiceCollection()

        match runner with
        | Some(runner: IProcessRunner) -> services.AddSingleton<IProcessRunner>(runner) |> ignore
        | None -> ()

        services.AddProcessKitHostedProcess(name, command, configure) |> ignore

        services.ConfigureProcessKitHostedProcess(
            name,
            Action<HostedProcessOptions>(fun options -> options.ShutdownGracePeriod <- TimeSpan.FromMilliseconds 250.0)
        )
        |> ignore

        let provider = services.BuildServiceProvider()
        let hosted = provider.GetServices<IHostedService>() |> Seq.exactlyOne
        let service = provider.GetRequiredKeyedService<HostedProcessService> name
        provider, hosted, service

    let startRegisteredService name command configure =
        startRegisteredServiceWithRunner name command configure None

    [<Test>]
    member _.``StartAsync launches supervisor without waiting for the child to exit``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)
            Assert.That(service.LastOutcome.IsNone, Is.True)

            do! hosted.StopAsync(CancellationToken.None)
        }
        :> Task

    [<Test>]
    member _.``StopAsync stops the active child process through RunningProcess StopAsync``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            do! hosted.StopAsync(CancellationToken.None)
            do! runner.StopRequested.WaitAsync(TimeSpan.FromSeconds 2.0)
            Assert.That(service.LastStopOutcome.IsSome, Is.True)
        }
        :> Task

    [<Test>]
    member _.``Supervisor restart and storm-pause events remain observable``() : Task =
        task {
            let mutable restarts = 0
            let mutable stormPauses = 0

            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor
                        .Restart(RestartPolicy.OnCrash)
                        .MaxRestarts(1)
                        .Backoff(TimeSpan.Zero, 1.0)
                        .StormPause(TimeSpan.Zero)
                        .FailureThreshold(0.0)
                        .OnRestart(Action<SupervisorRestartEvent>(fun _ -> Interlocked.Increment(&restarts) |> ignore))
                        .OnStormPause(
                            Action<SupervisorStormPauseEvent>(fun _ -> Interlocked.Increment(&stormPauses) |> ignore)
                        ))

            let provider, hosted, service =
                startRegisteredService "crashy" (shell "exit 7") configure

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let serviceForWait = service
            let! completed = waitUntil (fun () -> serviceForWait.LastOutcome.IsSome)
            Assert.That(completed, Is.True)
            Assert.That(Volatile.Read(&restarts), Is.EqualTo 1)
            Assert.That(Volatile.Read(&stormPauses), Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``Final supervision outcome is available after supervision stops``() : Task =
        task {
            let provider, hosted, service =
                startRegisteredService
                    "oneshot"
                    (shell "echo done")
                    (Func<Supervisor, Supervisor>(fun supervisor -> supervisor.Restart RestartPolicy.Never))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let serviceForWait = service
            let! completed = waitUntil (fun () -> serviceForWait.LastOutcome.IsSome)
            Assert.That(completed, Is.True)

            match service.LastOutcome with
            | Some(Ok outcome) ->
                Assert.That(outcome.FinalResult.Stdout, Does.Contain "done")
                Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
            | Some(Error error) -> Assert.Fail $"expected a supervision outcome, got {error.Message}"
            | None -> Assert.Fail "expected a supervision outcome"
        }
        :> Task

    [<Test>]
    member _.``Service remains active after a crashed child is restarted``() : Task =
        task {
            let marker = tempFile "restart"
            let mutable restarts = 0

            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor
                        .Restart(RestartPolicy.OnCrash)
                        .Backoff(TimeSpan.Zero, 1.0)
                        .OnRestart(Action<SupervisorRestartEvent>(fun _ -> Interlocked.Increment(&restarts) |> ignore)))

            let provider, hosted, service =
                startRegisteredService "restarting" (restartThenRun marker) configure

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! restarted = waitUntil (fun () -> Volatile.Read(&restarts) >= 1)
            Assert.That(restarted, Is.True)
            Assert.That(service.LastOutcome.IsNone, Is.True)

            do! hosted.StopAsync(CancellationToken.None)
            Assert.That(service.LastOutcome.IsSome, Is.True)
        }
        :> Task

    [<Test>]
    member _.``Dispose without a prior StopAsync hard-kills the active child and ends supervision``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            (service :> IDisposable).Dispose()

            do! runner.Killed.WaitAsync(TimeSpan.FromSeconds 2.0)

            let! outcomeSettled = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(outcomeSettled, Is.True, "supervision should end once Dispose kills the active child")

            // Idempotent: a repeat Dispose must not throw (e.g. re-cancelling/re-disposing the CTS).
            Assert.DoesNotThrow(Action(fun () -> (service :> IDisposable).Dispose()))

            // Dispose permanently forbids new starts: a subsequent StartAsync must not spawn again.
            do! hosted.StartAsync(CancellationToken.None)
            do! Task.Delay(TimeSpan.FromMilliseconds 200.0)
            Assert.That(runner.SpawnCount, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``StartAsync with an already-cancelled token starts no supervision or child``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider

            use cts = new CancellationTokenSource()
            cts.Cancel()

            let startTask = hosted.StartAsync(cts.Token)

            try
                do! startTask
                Assert.Fail "expected StartAsync to report a cancelled task"
            with :? OperationCanceledException ->
                ()

            Assert.That(startTask.IsCanceled, Is.True)

            do! Task.Delay(TimeSpan.FromMilliseconds 200.0)
            Assert.That(runner.Started.IsCompleted, Is.False)
            Assert.That(service.LastOutcome.IsNone, Is.True)
        }
        :> Task

    [<Test>]
    member _.``Concurrent StopAsync and Dispose race safely against a long-lived child``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            let stopTask = hosted.StopAsync(CancellationToken.None)
            let disposeTask = Task.Run(fun () -> (service :> IDisposable).Dispose())

            do! Task.WhenAll(stopTask, disposeTask)

            let! outcomeSettled = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(outcomeSettled, Is.True)

            // Neither a repeat Dispose nor a repeat StopAsync should throw once torn down.
            Assert.DoesNotThrow(Action(fun () -> (service :> IDisposable).Dispose()))
            do! hosted.StopAsync(CancellationToken.None)
        }
        :> Task

    [<Test>]
    member _.``DisposeAsync awaits supervision to actually finish before returning``() : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            do! (service :> IAsyncDisposable).DisposeAsync().AsTask()

            // No polling here (unlike the plain-`Dispose` test above): by the time `DisposeAsync`
            // returns, supervision must have actually finished.
            Assert.That(service.LastOutcome.IsSome, Is.True)

            // Idempotent: a repeat DisposeAsync must not throw and completes immediately.
            do! (service :> IAsyncDisposable).DisposeAsync().AsTask()

            // Dispose permanently forbids new starts, same as the plain `Dispose` path.
            do! hosted.StartAsync(CancellationToken.None)
            do! Task.Delay(TimeSpan.FromMilliseconds 200.0)
            Assert.That(runner.SpawnCount, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``DisposeAsync still awaits supervision to finish when a prior Dispose already claimed teardown``
        ()
        : Task =
        task {
            let runner = BlockingRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            // The synchronous `Dispose()` wins the teardown claim first, exactly as the regression
            // required: it initiates teardown (hard-kill + cancel) but cannot itself await supervision.
            (service :> IDisposable).Dispose()

            // `DisposeAsync()` loses the claim race (teardown already claimed by `Dispose()` above), but
            // must still await supervision to actually finish before returning — its documented contract
            // does not depend on winning the race.
            do! (service :> IAsyncDisposable).DisposeAsync().AsTask()

            Assert.That(
                service.LastOutcome.IsSome,
                Is.True,
                "DisposeAsync must await supervision to completion even when a prior Dispose already claimed teardown"
            )
        }
        :> Task

    [<Test>]
    member _.``An exception escaping the built supervisor surfaces as an observable LastOutcome``() : Task =
        task {
            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(fun supervisor -> supervisor.Restart RestartPolicy.Never))
                    (Some(ThrowingRunner() :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let serviceForWait = service

            let! completed = waitUntil (fun () -> serviceForWait.LastOutcome.IsSome)

            Assert.That(
                completed,
                Is.True,
                "an exception escaping the supervisor should surface as LastOutcome, not only a log"
            )

            match service.LastOutcome with
            | Some(Error error) -> Assert.That(error.Message, Does.Contain "boom from ThrowingRunner")
            | other -> Assert.Fail $"expected an Error outcome, got %A{other}"
        }
        :> Task
