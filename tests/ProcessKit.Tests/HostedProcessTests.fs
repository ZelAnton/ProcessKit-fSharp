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

    let finished =
        new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Started = started.Task
    member _.StopRequested = stopRequested.Task

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
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
                      Wait = fun () -> finished.Task
                      StdinError = fun () -> None
                      StartKill = fun () -> finished.TrySetResult(Outcome.Signalled None) |> ignore
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
