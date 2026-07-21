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
                      StdinFeedComplete = ignore
                      StartKill =
                        fun () ->
                            killed.TrySetResult() |> ignore
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                      GracefulKill =
                        fun _ ->
                            stopRequested.TrySetResult() |> ignore
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                            Task.CompletedTask
                      ResizePty = None
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

/// An `IProcessRunner` whose spawned child's graceful stop (the internal `RunningProcess.StopAsync`'s
/// underlying `host.GracefulKill`) resolves the process exit promptly but THEN faults on a delay —
/// simulating a pump fault while finishing off the child, arriving well after the external caller's own
/// wait on that stop has already been abandoned by an expired token (T-128 regression coverage, now for
/// `SupervisionSession.StopActiveAsync` — the hosting stop seam — observing the abandoned internal stop
/// task's late fault).
type private LateFaultingStopRunner() =
    let started =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Started = started.Task

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
                let stdout = new MemoryStream()
                let stderr = new MemoryStream()

                let finished =
                    new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

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
                      StdinFeedComplete = ignore
                      StartKill = fun () -> finished.TrySetResult(Outcome.Signalled None) |> ignore
                      GracefulKill =
                        fun _ ->
                            task {
                                do! Task.Delay 400
                                // The child has actually exited (an escalated hard-kill elsewhere would
                                // have reaped it around here too) but the grace machinery itself then
                                // faults — e.g. a pump fault while finishing off the child.
                                finished.TrySetResult(Outcome.Signalled None) |> ignore
                                failwith "late graceful-kill fault"
                            }
                            :> Task
                      ResizePty = None
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

/// An `IProcessRunner` whose `SpawnAsync` fails *asynchronously* (a faulted task) instead of returning a
/// `Result` error — simulating a failure that escapes the already-built supervision run rather than one it
/// classifies itself. A *synchronous* throw from `SpawnAsync` is, by `SupervisionSession`'s contract, the
/// signal for a capture-only runner (the session latches onto its capture primitive); a real spawn-capable
/// runner that genuinely fails does so by faulting its returned task, which is what must surface as an
/// observable `LastOutcome` rather than being silently downgraded to the capture path.
type private ThrowingRunner() =
    interface IProcessRunner with
        member _.SpawnAsync(_command, _cancellationToken) : Task<Result<RunningProcess, ProcessError>> =
            Task.FromException<Result<RunningProcess, ProcessError>>(
                InvalidOperationException "boom from ThrowingRunner"
            )

        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

/// An `IProcessRunner` whose `SpawnAsync` blocks *synchronously* (before it returns its task) until the
/// test releases it — standing in for the real `JobRunner`'s synchronous native-spawn prefix
/// (`ProcessGroup.Create()` + `StartInternal`, i.e. CreateProcessW/posix_spawnp, done inline before the
/// first await). Lets a test prove `StartAsync` no longer holds the service's internal `gate` across
/// that synchronous spawn: a concurrent property read must not block on it (T-154).
type private SlowSpawnRunner() =
    let spawnEntered =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let releaseSpawn = new ManualResetEventSlim(false)

    let stopRequested =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let finished =
        new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes (asynchronously) the instant `SpawnAsync` is entered and about to block mid-spawn.
    member _.SpawnEntered = spawnEntered.Task

    /// Unblocks the in-flight synchronous spawn. Idempotent — safe to call from both the test body and
    /// its `finally`.
    member _.ReleaseSpawn() = releaseSpawn.Set()

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
                // Signal entry, then block the *synchronous* portion of the spawn: this is exactly where
                // the real JobRunner would be doing its native spawn inline, before the first await.
                spawnEntered.TrySetResult() |> ignore
                releaseSpawn.Wait()

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
                      StdinFeedComplete = ignore
                      StartKill = fun () -> finished.TrySetResult(Outcome.Signalled None) |> ignore
                      GracefulKill =
                        fun _ ->
                            stopRequested.TrySetResult() |> ignore
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                            Task.CompletedTask
                      ResizePty = None
                      Teardown =
                        fun () ->
                            stdout.Dispose()
                            stderr.Dispose()
                            ValueTask.CompletedTask }

                Task.FromResult(Ok(new RunningProcess(host)))

        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "not used"))

/// An `IProcessRunner` whose spawned child "crashes" immediately — it is already exited with a non-zero
/// code the moment the supervisor awaits it — so an `OnCrash` policy schedules a restart. With a long
/// backoff configured, the loop then parks in a between-incarnations backoff sleep with NO live child.
/// Counts spawns so a test can prove a stop taken during that sleep ends supervision promptly without ever
/// launching the next incarnation (T-180: the stale active-child tracking bug manifested exactly here —
/// a stop between incarnations found a stale, already-dead handle and neither interrupted the backoff nor
/// left `LastStopOutcome` untouched).
type private CrashThenBackoffRunner() =
    let mutable spawnCount = 0

    /// Total `SpawnAsync` calls so far — one per incarnation actually launched.
    member _.SpawnCount = Volatile.Read(&spawnCount)

    interface IProcessRunner with
        member _.SpawnAsync(command, cancellationToken) =
            if cancellationToken.IsCancellationRequested then
                Task.FromResult(Error(ProcessError.Cancelled command.Program))
            else
                Interlocked.Increment(&spawnCount) |> ignore
                let stdout = new MemoryStream()
                let stderr = new MemoryStream()

                let finished =
                    new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                // Crash immediately: the child is "already exited" with a non-zero code the moment the
                // supervisor's capture awaits it, so an `OnCrash` policy schedules a backoff + restart.
                finished.TrySetResult(Outcome.Exited 1) |> ignore

                let host: RunningHost =
                    { Config = command.Config
                      Pid = Some 4242
                      Stdout = Some(stdout :> Stream)
                      Stderr = Some(stderr :> Stream)
                      Stdin = None
                      StartTime = DateTime.UtcNow
                      StartedTimestamp = Stopwatch.GetTimestamp()
                      StartTimeIdentity = None
                      Wait = fun () -> finished.Task
                      StdinError = fun () -> None
                      StdinFeedComplete = ignore
                      StartKill = fun () -> finished.TrySetResult(Outcome.Signalled None) |> ignore
                      GracefulKill =
                        fun _ ->
                            finished.TrySetResult(Outcome.Signalled None) |> ignore
                            Task.CompletedTask
                      ResizePty = None
                      Teardown =
                        fun () ->
                            stdout.Dispose()
                            stderr.Dispose()
                            ValueTask.CompletedTask }

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
    member _.``StartAsync does not hold the gate across the synchronous child spawn``() : Task =
        task {
            let runner = SlowSpawnRunner()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider

            try
                // Kick `StartAsync` off the test thread: with the fix it returns immediately, but a
                // regression that held `gate` across the synchronous spawn would wedge inside this call
                // rather than fail an assertion, so it must never be awaited inline.
                let startTask = Task.Run(fun () -> hosted.StartAsync CancellationToken.None)

                // The supervision loop has now entered `SpawnAsync` and is blocked mid-spawn.
                do! runner.SpawnEntered.WaitAsync(TimeSpan.FromSeconds 2.0)

                // While that spawn is still blocked, a concurrent `gate` read must complete promptly. If
                // `StartAsync` still held `gate` across the spawn, this read would block until the
                // `ReleaseSpawn` below, and the 2s ceiling would elect `Task.Delay` instead — failing the
                // assertion.
                let readTask = Task.Run(fun () -> service.IsSupervisionActive)
                let! firstDone = Task.WhenAny(readTask :> Task, Task.Delay(TimeSpan.FromSeconds 2.0))

                Assert.That(
                    Object.ReferenceEquals(firstDone, readTask),
                    Is.True,
                    "IsSupervisionActive must not block while the child is being spawned"
                )

                Assert.That(readTask.Result, Is.True, "supervision is active the moment StartAsync publishes the task")

                // Release the blocked spawn, confirm `StartAsync` itself completed, and tear down cleanly.
                runner.ReleaseSpawn()
                do! startTask
                do! hosted.StopAsync(CancellationToken.None)
            finally
                runner.ReleaseSpawn()
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
    member _.``A repeat StopAsync that finds no active child does not erase a previously published LastStopOutcome``
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

            do! hosted.StopAsync(CancellationToken.None)
            do! runner.StopRequested.WaitAsync(TimeSpan.FromSeconds 2.0)

            let firstStopOutcome = service.LastStopOutcome
            Assert.That(firstStopOutcome.IsSome, Is.True)

            // By the time the first StopAsync returned, the capture that owned the active child had
            // already cleared it (its `finally` ran as part of awaiting the same supervision task), so
            // this repeat StopAsync finds no active child — it must not stomp the outcome above.
            do! hosted.StopAsync(CancellationToken.None)
            Assert.That(service.LastStopOutcome, Is.EqualTo firstStopOutcome)
        }
        :> Task

    [<Test>]
    member _.``StopAsync observes a StopActiveAsync fault that arrives after the external token already expired``
        ()
        : Task =
        task {
            let runner = LateFaultingStopRunner()
            let mutable unobserved = false

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved <- true
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                let provider, hosted, _service =
                    startRegisteredServiceWithRunner
                        "worker"
                        (Command.create "worker")
                        (Func<Supervisor, Supervisor>(id))
                        (Some(runner :> IProcessRunner))

                use _provider = provider
                do! hosted.StartAsync(CancellationToken.None)
                do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

                // Expires well before the fake `GracefulKill`'s 400ms-delayed fault, so
                // `StopActiveAsync` takes the `OperationCanceledException` branch and abandons the
                // still-running internal `RunningProcess.StopAsync` task.
                use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
                do! hosted.StopAsync(cts.Token)

                // Give the abandoned internal stop task time to actually fault, then force a GC pass:
                // the CLR reports a still-unobserved task fault from the finalizer once the task is
                // collected.
                do! Task.Delay(TimeSpan.FromMilliseconds 800.0)
                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                Assert.That(
                    unobserved,
                    Is.False,
                    "the abandoned internal StopAsync task's late fault should be observed, not left unobserved"
                )
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
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
    member _.``Dispose while a capture is in flight surfaces the run as Cancelled, not the killed child's own result``
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

            (service :> IDisposable).Dispose()

            do! runner.Killed.WaitAsync(TimeSpan.FromSeconds 2.0)

            let! outcomeSettled = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(outcomeSettled, Is.True)

            match service.LastOutcome with
            | Some(Error(ProcessError.Cancelled program)) -> Assert.That(program, Is.EqualTo "worker")
            | other ->
                Assert.Fail
                    $"expected the cancelled-during-teardown capture to surface as Error(Cancelled ...), got %A{other}"
        }
        :> Task

    [<Test>]
    member _.``Command.CancelOn on a hosted command kills the child and ends supervision with Cancelled``() : Task =
        task {
            let runner = BlockingRunner()
            use cancelOnCts = new CancellationTokenSource()

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    ((Command.create "worker").CancelOn(cancelOnCts.Token))
                    (Func<Supervisor, Supervisor>(id))
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            // Cancel only the command's own `CancelOn` token — never the host's lifetime/stop/dispose
            // path — so the kill can only have come from `Command.CancelOn` being honoured.
            cancelOnCts.Cancel()

            do! runner.Killed.WaitAsync(TimeSpan.FromSeconds 2.0)

            let! outcomeSettled = waitUntil (fun () -> service.LastOutcome.IsSome)
            Assert.That(outcomeSettled, Is.True, "Command.CancelOn should end supervision once it fires")

            match service.LastOutcome with
            | Some(Error(ProcessError.Cancelled program)) -> Assert.That(program, Is.EqualTo "worker")
            | other ->
                Assert.Fail $"expected Command.CancelOn to surface the run as Error(Cancelled ...), got %A{other}"

            do! hosted.StopAsync(CancellationToken.None)
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

    [<Test>]
    member _.``A user StopWhen configured via configureSupervisor still ends supervision without any host stop request``
        ()
        : Task =
        task {
            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor
                        .Restart(RestartPolicy.Always)
                        .Backoff(TimeSpan.Zero, 1.0)
                        .StopWhen(
                            Func<ProcessResult<string>, bool>(fun result -> result.Stdout.Contains "combo-marker")
                        ))

            let provider, hosted, service =
                startRegisteredService "combo-predicate" (shell "echo combo-marker") configure

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            let serviceForWait = service
            let! completed = waitUntil (fun () -> serviceForWait.LastOutcome.IsSome)
            Assert.That(completed, Is.True, "the user's own StopWhen predicate should end supervision on its own")

            match service.LastOutcome with
            | Some(Ok outcome) -> Assert.That(outcome.Stopped, Is.EqualTo StopReason.Predicate)
            | other -> Assert.Fail $"expected a predicate-matched outcome, got %A{other}"

            // No host stop was ever requested, so the combined predicate must not have relied on it.
            Assert.That(service.LastStopOutcome.IsNone, Is.True)
        }
        :> Task

    [<Test>]
    member _.``The host stop flag still ends supervision when the user's own StopWhen never matches``() : Task =
        task {
            let runner = BlockingRunner()

            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor.StopWhen(Func<ProcessResult<string>, bool>(fun _ -> false)))

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "worker"
                    (Command.create "worker")
                    configure
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)
            do! runner.Started.WaitAsync(TimeSpan.FromSeconds 2.0)

            do! hosted.StopAsync(CancellationToken.None)
            do! runner.StopRequested.WaitAsync(TimeSpan.FromSeconds 2.0)
            Assert.That(service.LastStopOutcome.IsSome, Is.True)

            let serviceForWait = service
            let! completed = waitUntil (fun () -> serviceForWait.LastOutcome.IsSome)

            Assert.That(
                completed,
                Is.True,
                "the host stop should end supervision even though the user's own StopWhen never matches"
            )

            // A host-driven graceful stop now ends supervision through the `SupervisionSession`'s own
            // graceful path, so the reason is `StopReason.Stopped` — the honest reason for a deliberate
            // stop — rather than the `StopReason.Predicate` the previous implementation reported as an
            // artifact of folding the host stop into a combined `StopWhen` predicate. The user's own
            // `StopWhen` (which never matches here) is preserved untouched and simply does not fire.
            match service.LastOutcome with
            | Some(Ok outcome) -> Assert.That(outcome.Stopped, Is.EqualTo StopReason.Stopped)
            | other -> Assert.Fail $"expected a graceful-stop outcome (StopReason.Stopped), got %A{other}"
        }
        :> Task

    [<Test>]
    member _.``StopAsync taken between incarnations during backoff ends supervision promptly without a new incarnation``
        ()
        : Task =
        task {
            let runner = CrashThenBackoffRunner()

            let inBackoff =
                new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let configure =
                Func<Supervisor, Supervisor>(fun supervisor ->
                    supervisor
                        .Restart(RestartPolicy.OnCrash)
                        // A long, jitter-free backoff so the loop parks in the sleep long enough for the
                        // stop below to land firmly between incarnations, with no live child.
                        .Backoff(TimeSpan.FromSeconds 30.0, 1.0)
                        .Jitter(false)
                        .OnRestart(Action<SupervisorRestartEvent>(fun _ -> inBackoff.TrySetResult() |> ignore)))

            let provider, hosted, service =
                startRegisteredServiceWithRunner
                    "backoff-crasher"
                    (Command.create "backoff-crasher")
                    configure
                    (Some(runner :> IProcessRunner))

            use _provider = provider
            do! hosted.StartAsync(CancellationToken.None)

            // The first incarnation crashed and the loop is now parked in the 30s backoff sleep with no
            // live child — `OnRestart` fires immediately before that sleep (see `Supervisor.sleepBackoff`).
            do! inBackoff.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            // A stop taken here (between incarnations) must interrupt the backoff and end supervision, NOT
            // wait the 30s out and then launch another incarnation. `StopAsync` is given no deadline, so a
            // regression that fails to interrupt the sleep leaves it pending past the guard below rather
            // than merely returning early on an expired token.
            let stopTask = hosted.StopAsync(CancellationToken.None)
            let! firstDone = Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds 10.0))

            Assert.That(
                Object.ReferenceEquals(firstDone, stopTask),
                Is.True,
                "StopAsync taken during a backoff sleep must interrupt it, not wait the full delay out"
            )

            do! stopTask

            // No live child was stopped, so a StopAsync that found nothing active must not publish a
            // LastStopOutcome (the previous stale-handle bug published the dead incarnation's outcome here).
            Assert.That(
                service.LastStopOutcome.IsNone,
                Is.True,
                "a between-incarnations stop stopped no live child, so it must not publish a LastStopOutcome"
            )

            // Supervision actually ended, rather than looping into another incarnation.
            Assert.That(
                service.LastOutcome.IsSome,
                Is.True,
                "supervision must end promptly, not launch another incarnation after the stop"
            )

            // Exactly one incarnation ever spawned: the crashed first one. The second never started.
            Assert.That(
                runner.SpawnCount,
                Is.EqualTo 1,
                "no new incarnation may spawn after a stop taken between incarnations"
            )
        }
        :> Task

    [<Test>]
    member _.``A duplicate hosted-process name is rejected, so the second registration is never silently dropped``() =
        let services = ServiceCollection()

        services.AddProcessKitHostedProcess("worker", Command.create "first") |> ignore

        // The second registration under the same name differs (a different command); it must not be
        // silently discarded by the underlying TryAdd — it is a configuration mistake, surfaced honestly.
        try
            services.AddProcessKitHostedProcess("worker", Command.create "second") |> ignore
            Assert.Fail "expected a duplicate hosted-process name to be rejected"
        with :? InvalidOperationException as ex ->
            Assert.That(ex.Message, Does.Contain "worker")

        // The rejected duplicate left no second IHostedService behind: exactly one is registered, so the
        // host starts/stops the single keyed service exactly once (the previously hidden double-start bug).
        use provider = services.BuildServiceProvider()
        let hostedServices = provider.GetServices<IHostedService>() |> Seq.toList
        Assert.That(hostedServices.Length, Is.EqualTo 1)

    [<Test>]
    member _.``Distinct hosted-process names each register their own IHostedService``() =
        let services = ServiceCollection()
        services.AddProcessKitHostedProcess("alpha", Command.create "first") |> ignore
        services.AddProcessKitHostedProcess("beta", Command.create "second") |> ignore

        use provider = services.BuildServiceProvider()
        let hostedServices = provider.GetServices<IHostedService>() |> Seq.length
        Assert.That(hostedServices, Is.EqualTo 2)
