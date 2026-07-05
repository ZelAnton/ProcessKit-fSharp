namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NUnit.Framework
open ProcessKit
open ProcessKit.Extensions.DependencyInjection

/// Captures the last command it was asked to run (and returns a canned success), to assert on the
/// defaults a decorating runner applied.
type private DiCapturingRunner() =
    member val Last: Command option = None with get, set

    interface IProcessRunner with
        member this.CaptureStringAsync(command, _cancellationToken) =
            this.Last <- Some command

            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, "", "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]))
            )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "n/a"))

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "n/a"))

type private DiCapturingLogger() =
    let records = ConcurrentQueue<string>()
    member _.Text = String.Join("\n", records)

    interface ILogger with
        member _.Log(_logLevel, _eventId, state, error, formatter) =
            records.Enqueue(formatter.Invoke(state, error))

        member _.IsEnabled(_logLevel) = true

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

type private FakeLoggerFactory(logger: ILogger) =
    interface ILoggerFactory with
        member _.CreateLogger(_categoryName) = logger
        member _.AddProvider(_provider) = ()
        member _.Dispose() = ()

[<TestFixture>]
type DependencyInjectionTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let sleeper () =
        if isWindows then
            shell "ping -n 20 127.0.0.1 >nul"
        else
            shell "sleep 20"

    /// Poll until `pid` is no longer a live process (reaped/exited), or the deadline elapses.
    let waitProcessGone (pid: int) (deadlineMs: int) : Task<bool> =
        task {
            let sw = Stopwatch.StartNew()
            let mutable gone = false

            while not gone && sw.ElapsedMilliseconds < int64 deadlineMs do
                gone <-
                    try
                        use p = Process.GetProcessById pid
                        p.HasExited
                    with _ ->
                        // No such process → reaped/gone.
                        true

                if not gone then
                    do! Task.Delay 100

            return gone
        }

    [<Test>]
    member _.``AddProcessKit registers a working IProcessRunner``() : Task =
        task {
            let services = ServiceCollection()
            services.AddProcessKit() |> ignore
            use provider = services.BuildServiceProvider()
            let runner = provider.GetRequiredService<IProcessRunner>()

            match! Runner.run runner CancellationToken.None (shell "echo injected") with
            | Ok output -> Assert.That(output, Does.Contain "injected")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``the resolved runner is logger-aware when a logger factory is registered``() : Task =
        task {
            let captured = DiCapturingLogger()
            let services = ServiceCollection()
            services.AddSingleton<ILoggerFactory>(new FakeLoggerFactory(captured)) |> ignore
            services.AddProcessKit() |> ignore
            use provider = services.BuildServiceProvider()
            let runner = provider.GetRequiredService<IProcessRunner>()

            match! Runner.run runner CancellationToken.None (shell "echo injected") with
            | Ok _ -> Assert.That(captured.Text, Does.Contain "spawned")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``AddProcessKit leaves a pre-existing IProcessRunner registration alone``() =
        let existing: IProcessRunner = ProcessKit.Testing.ScriptedRunner()
        let services = ServiceCollection()
        services.AddSingleton<IProcessRunner>(existing) |> ignore
        services.AddProcessKit() |> ignore
        use provider = services.BuildServiceProvider()
        Assert.That(provider.GetRequiredService<IProcessRunner>(), Is.SameAs existing)

    [<Test>]
    member _.``the defaults runner applies an option default only when the command sets none``() : Task =
        task {
            let inner = DiCapturingRunner()

            let options =
                ProcessKitOptions(DefaultTimeout = Nullable(TimeSpan.FromSeconds 30.0))

            let runner = DefaultsRunner(inner, options) :> IProcessRunner

            // A command with no timeout inherits the default.
            let! _ = runner.CaptureStringAsync(Command.create "tool", CancellationToken.None)
            Assert.That(inner.Last.Value.Config.Timeout, Is.EqualTo(Some(TimeSpan.FromSeconds 30.0)))

            // A command with its own timeout keeps it — the default never overrides.
            let own = Command.create "tool" |> Command.timeout (TimeSpan.FromSeconds 5.0)
            let! _ = runner.CaptureStringAsync(own, CancellationToken.None)
            Assert.That(inner.Last.Value.Config.Timeout, Is.EqualTo(Some(TimeSpan.FromSeconds 5.0)))
        }
        :> Task

    [<Test>]
    member _.``ProcessKitOptions.DefaultTimeout rejects a negative value at assignment``() =
        // Validated at the options boundary (the setter), so a misconfiguration surfaces at
        // setup/binding time rather than as an exception escaping a Result-returning verb later
        // (inside DefaultsRunner.applyDefaults).
        let options = ProcessKitOptions()

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> options.DefaultTimeout <- Nullable(TimeSpan.FromSeconds -1.0))
        )
        |> ignore

        // A non-negative value (and clearing it back to null) still works.
        options.DefaultTimeout <- Nullable(TimeSpan.FromSeconds 1.0)
        Assert.That(options.DefaultTimeout, Is.EqualTo(Nullable(TimeSpan.FromSeconds 1.0)))
        options.DefaultTimeout <- Nullable()
        Assert.That(options.DefaultTimeout.HasValue, Is.False)

    [<Test>]
    member _.``AddProcessKit(configure) applies the default timeout to a resolved run``() : Task =
        task {
            let services = ServiceCollection()

            services.AddProcessKit(fun o -> o.DefaultTimeout <- Nullable(TimeSpan.FromMilliseconds 200.0))
            |> ignore

            use provider = services.BuildServiceProvider()
            let runner = provider.GetRequiredService<IProcessRunner>()

            // The sleeper runs far longer than the injected 200 ms default, so the default must fire.
            match! Runner.outputString runner CancellationToken.None (sleeper ()) with
            | Ok result ->
                Assert.That(result.IsTimedOut, Is.True, "the configured default timeout should have killed the run")
            | Error(ProcessError.Timeout _) -> Assert.Pass()
            | Error error -> Assert.Fail $"expected a timeout, got {error}"
        }
        :> Task

    [<Test>]
    member _.``keyed clients target distinct programs and route through the injected runner``() : Task =
        task {
            // A shared scripted runner keeps the whole test hermetic — no real process is spawned.
            let scripted =
                ProcessKit.Testing.ScriptedRunner().On([ "git" ], ProcessKit.Testing.Reply.Ok "on branch main")

            let services = ServiceCollection()
            services.AddSingleton<IProcessRunner>(scripted) |> ignore
            services.AddProcessKitClient("git", "git") |> ignore
            services.AddProcessKitClient("ffmpeg", "ffmpeg") |> ignore
            use provider = services.BuildServiceProvider()

            let git = provider.GetRequiredKeyedService<CliClient> "git"
            let ffmpeg = provider.GetRequiredKeyedService<CliClient> "ffmpeg"

            // Distinct programs.
            Assert.That(git.Command([ "status" ]).Program, Is.EqualTo "git")
            Assert.That(ffmpeg.Command([ "-version" ]).Program, Is.EqualTo "ffmpeg")

            // The git client routes through the injected scripted runner (hermetic).
            match! git.OutputStringAsync [ "status" ] with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "on branch main")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``AddProcessKitGroup shares a container-managed group; disposing the provider reaps its children``
        ()
        : Task =
        task {
            let services = ServiceCollection()
            services.AddProcessKitGroup() |> ignore
            let provider = services.BuildServiceProvider()

            let runner = provider.GetRequiredService<IProcessRunner>()
            let group = provider.GetRequiredService<ProcessGroup>()

            // A quick run through the group-backed runner works...
            match! Runner.run runner CancellationToken.None (shell "echo grouped") with
            | Ok output -> Assert.That(output, Does.Contain "grouped")
            | Error error -> Assert.Fail $"grouped run failed: {error}"

            // ...and a long-lived child started into the group is tracked.
            let! started = group.StartAsync(sleeper ())

            let running =
                match started with
                | Ok r -> r
                | Error e -> failwith $"start failed: {e}"

            let pid =
                match running.Pid with
                | Some p -> p
                | None -> failwith "expected the started child to report a pid"

            // Disposing the provider disposes the container-managed group: it is released...
            do! (provider :> IAsyncDisposable).DisposeAsync()

            match group.Members() with
            | Error _ -> () // released, as expected
            | Ok _ -> Assert.Fail "the group should be released after the provider is disposed"

            // ...and its child is reaped.
            let! gone = waitProcessGone pid 15000

            Assert.That(
                gone,
                Is.True,
                "the container-managed group's child must be killed when the provider is disposed"
            )

            GC.KeepAlive running
        }
        :> Task
