namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NUnit.Framework
open ProcessKit
open ProcessKit.Extensions.DependencyInjection

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
