namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open NUnit.Framework
open ProcessKit
open ProcessKit.Extensions.Hosting

/// Covers the `HostedProcessHealthCheck` Healthy/Degraded/Unhealthy mapping (T-106), the
/// `RestartCount`/`IsSupervisionActive`/`IsStormPaused` observable state it reads, and the opt-in
/// `AddProcessKitHostedProcessHealthCheck` DI registration.
[<TestFixture>]
type HostedProcessHealthCheckTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let sleeper =
        if isWindows then
            shell "ping 127.0.0.1 -n 30 >nul"
        else
            shell "sleep 30"

    let waitUntil (predicate: unit -> bool) : Task<bool> =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 6.0
            let mutable done' = predicate ()

            while not done' && DateTime.UtcNow < deadline do
                do! Task.Delay 50
                done' <- predicate ()

            return done'
        }

    let checkHealth (healthCheck: IHealthCheck) : Task<HealthCheckResult> =
        healthCheck.CheckHealthAsync(HealthCheckContext(), CancellationToken.None)

    let startRegisteredService name command configure (runner: IProcessRunner option) =
        let services = ServiceCollection()

        match runner with
        | Some(runner: IProcessRunner) -> services.AddSingleton<IProcessRunner>(runner) |> ignore
        | None -> ()

        services.AddProcessKitHostedProcess(name, command, configure) |> ignore
        services.AddProcessKitHostedProcessHealthCheck(name) |> ignore

        let provider = services.BuildServiceProvider()
        let hosted = provider.GetServices<IHostedService>() |> Seq.exactlyOne
        let service = provider.GetRequiredKeyedService<HostedProcessService> name
        let healthCheck = provider.GetRequiredKeyedService<HostedProcessHealthCheck> name
        provider, hosted, service, healthCheck :> IHealthCheck

    [<Test>]
    member _.``AddProcessKitHostedProcessHealthCheck registers a keyed IHealthCheck resolvable by the same name``() =
        let services = ServiceCollection()
        services.AddProcessKitHostedProcess("worker", shell "echo hi") |> ignore
        services.AddProcessKitHostedProcessHealthCheck("worker") |> ignore

        use provider = services.BuildServiceProvider()

        let healthCheck =
            provider.GetRequiredKeyedService<HostedProcessHealthCheck> "worker"

        Assert.That(box healthCheck, Is.InstanceOf<IHealthCheck>())

    [<Test>]
    member _.``Health check reports Healthy while the child is running``() : Task =
        task {
            let provider, hosted, service, healthCheck =
                startRegisteredService "worker" sleeper (Func<Supervisor, Supervisor>(id)) None

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! active = waitUntil (fun () -> service.IsSupervisionActive)
            Assert.That(active, Is.True)

            let! result = checkHealth healthCheck
            Assert.That(result.Status, Is.EqualTo HealthStatus.Healthy)

            do! hosted.StopAsync(CancellationToken.None)
        }
        :> Task

    [<Test>]
    member _.``Health check reports Unhealthy before StartAsync has ever run``() : Task =
        task {
            let provider, _hosted, _service, healthCheck =
                startRegisteredService "worker" (shell "echo hi") (Func<Supervisor, Supervisor>(id)) None

            use _provider = provider
            let! result = checkHealth healthCheck
            Assert.That(result.Status, Is.EqualTo HealthStatus.Unhealthy)
        }
        :> Task

    [<Test>]
    member _.``Health check reports Unhealthy once supervision ends``() : Task =
        task {
            let configure =
                Func<Supervisor, Supervisor>(fun supervisor -> supervisor.Restart RestartPolicy.Never)

            let provider, hosted, service, healthCheck =
                startRegisteredService "oneshot" (shell "echo done") configure None

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! completed = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(completed, Is.True)
            Assert.That(service.IsSupervisionActive, Is.False)

            let! result = checkHealth healthCheck
            Assert.That(result.Status, Is.EqualTo HealthStatus.Unhealthy)
        }
        :> Task

    [<Test>]
    member _.``Health check reports Degraded while the failure-storm guard is pausing restarts``() : Task =
        task {
            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor
                        .Restart(RestartPolicy.OnCrash)
                        .Backoff(TimeSpan.Zero, 1.0)
                        .Jitter(false)
                        .StormPause(TimeSpan.FromSeconds 2.0)
                        .FailureThreshold(0.0))

            let provider, hosted, service, healthCheck =
                startRegisteredService "crashy" (shell "exit 7") configure None

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! paused = waitUntil (fun () -> service.IsStormPaused)
            Assert.That(paused, Is.True, "expected the storm guard to pause restarts on the very first crash")
            Assert.That(service.IsSupervisionActive, Is.True, "storm-paused supervision is still alive")

            let! result = checkHealth healthCheck
            Assert.That(result.Status, Is.EqualTo HealthStatus.Degraded)

            do! hosted.StopAsync(CancellationToken.None)
        }
        :> Task

    [<Test>]
    member _.``RestartCount tracks live restarts up to the final tally``() : Task =
        task {
            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor.Restart(RestartPolicy.OnCrash).MaxRestarts(2).Backoff(TimeSpan.Zero, 1.0))

            let provider, hosted, service, _healthCheck =
                startRegisteredService "restarter" (shell "exit 7") configure None

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! completed = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(completed, Is.True)
            Assert.That(service.RestartCount, Is.EqualTo 2)
        }
        :> Task

    [<Test>]
    member _.``RestartCount resets to 0 on a fresh StartAsync after supervision ended``() : Task =
        task {
            let mutable configuredRuns = 0

            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    let run = Interlocked.Increment(&configuredRuns)

                    if run = 1 then
                        supervisor.Restart(RestartPolicy.OnCrash).MaxRestarts(1).Backoff(TimeSpan.Zero, 1.0)
                    else
                        // The second supervision must finish without a new restart. This makes the
                        // observable zero unambiguous: it is the fresh run's reset, not a value that
                        // happened to be sampled before its first immediate crash/restart.
                        supervisor.Restart RestartPolicy.Never)

            let provider, hosted, service, _healthCheck =
                startRegisteredService "restarter" (shell "exit 7") configure None

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let! completed = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(completed, Is.True)
            Assert.That(service.RestartCount, Is.EqualTo 1)

            do! hosted.StartAsync(CancellationToken.None)

            let! restartedAndCompleted =
                waitUntil (fun () -> Volatile.Read(&configuredRuns) = 2 && not service.IsSupervisionActive)

            Assert.That(restartedAndCompleted, Is.True)
            Assert.That(service.RestartCount, Is.EqualTo 0)

            do! hosted.StopAsync(CancellationToken.None)
        }
        :> Task
