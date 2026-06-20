namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NUnit.Framework
open ProcessKit

/// An `ILogger` that captures every formatted message, for asserting on the lifecycle taxonomy.
type private CapturingLogger() =
    let records = ConcurrentQueue<string>()
    member _.Text = String.Join("\n", records)

    interface ILogger with
        member _.Log(_logLevel, _eventId, state, error, formatter) =
            records.Enqueue(formatter.Invoke(state, error))

        member _.IsEnabled(_logLevel) = true

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

/// A runner that always reports a crash — to drive a supervisor restart deterministically.
type private AlwaysCrash() =
    interface IProcessRunner with
        member _.OutputString(command, _cancellationToken) =
            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, "", "boom", Outcome.Exited 1, TimeSpan.Zero, false, [ 0 ]))
            )

        member _.OutputBytes(_command, _cancellationToken) =
            failwith "AlwaysCrash has no OutputBytes"

        member _.Start(_command, _cancellationToken) = failwith "AlwaysCrash has no Start"

[<TestFixture>]
type LoggingTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    [<Test>]
    member _.``a run logs spawn and exit``() : Task =
        task {
            let logger = CapturingLogger()
            let command = shell "echo logged" |> Command.withLogger logger

            match! command.Run() with
            | Ok _ ->
                Assert.That(logger.Text, Does.Contain "spawned")
                Assert.That(logger.Text, Does.Contain "finished")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a timeout is logged``() : Task =
        task {
            let logger = CapturingLogger()

            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let command =
                sleeper
                |> Command.timeout (TimeSpan.FromMilliseconds 300.0)
                |> Command.withLogger logger

            let! _ = command.OutputString()
            Assert.That(logger.Text, Does.Contain "timed out")
        }
        :> Task

    [<Test>]
    member _.``argv is never logged``() : Task =
        task {
            let logger = CapturingLogger()

            let command =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args [ (if isWindows then "/c" else "-c"); "echo ok"; "--token=SUPERSECRET" ]
                |> Command.withLogger logger

            let! _ = command.OutputString()
            Assert.That(logger.Text, Does.Not.Contain "SUPERSECRET")
            // ...but the lifecycle events that do not carry argv are present.
            Assert.That(logger.Text, Does.Contain "spawned")
        }
        :> Task

    [<Test>]
    member _.``a supervisor restart is logged``() : Task =
        task {
            let logger = CapturingLogger()

            let sup =
                Supervisor(Command.create "worker" |> Command.withLogger logger)
                    .WithRunner(AlwaysCrash())
                    .MaxRestarts(1)
                    .Backoff(TimeSpan.Zero, 1.0)
                    .Jitter(false)

            match! sup.Run() with
            | Ok _ -> Assert.That(logger.Text, Does.Contain "restarting")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task
