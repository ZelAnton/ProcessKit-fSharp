namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NUnit.Framework
open ProcessKit

/// An `ILogger` that captures every formatted message plus its `EventId`, for asserting on the
/// lifecycle taxonomy and correlation.
type private CapturingLogger() =
    let records = ConcurrentQueue<EventId * string>()
    member _.Text = String.Join("\n", records |> Seq.map snd)
    member _.Records = records |> Seq.toList

    /// The formatted message for the first record carrying `eventId`, if any.
    member _.MessageFor(eventId: EventId) =
        records
        |> Seq.tryPick (fun (id, msg) -> if id = eventId then Some msg else None)

    interface ILogger with
        member _.Log(_logLevel, eventId, state, error, formatter) =
            records.Enqueue(eventId, formatter.Invoke(state, error))

        member _.IsEnabled(_logLevel) = true

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

/// An `ILogger` that reports every level disabled and throws if actually asked to log — proving the
/// `LoggerMessage.Define` hot path formats/logs nothing when the level is off.
type private DisabledThrowingLogger() =
    interface ILogger with
        member _.Log(_logLevel, _eventId, _state, _error, _formatter) =
            failwith "the logger must not be invoked when the level is disabled"

        member _.IsEnabled(_logLevel) = false

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

/// A runner that always reports a crash — to drive a supervisor restart deterministically.
type private AlwaysCrash() =
    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, "", "boom", Outcome.Exited 1, TimeSpan.Zero, false, [ 0 ]))
            )

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            failwith "AlwaysCrash has no CaptureBytes"

        member _.SpawnAsync(_command, _cancellationToken) = failwith "AlwaysCrash has no Spawn"

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
            let command = shell "echo logged" |> Command.logger logger

            match! command.RunAsync() with
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
                |> Command.logger logger

            let! _ = command.OutputStringAsync()
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
                |> Command.logger logger

            let! _ = command.OutputStringAsync()
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
                Supervisor(Command.create "worker" |> Command.logger logger)
                    .WithRunner(AlwaysCrash())
                    .MaxRestarts(1)
                    .Backoff(TimeSpan.Zero, 1.0)
                    .Jitter(false)

            match! sup.RunAsync() with
            | Ok _ -> Assert.That(logger.Text, Does.Contain "restarting")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``lifecycle events carry stable EventIds and a shared run id with the pid``() : Task =
        task {
            let logger = CapturingLogger()
            let command = shell "echo hi" |> Command.logger logger

            match! command.RunAsync() with
            | Ok _ ->
                // Stable EventIds (name + number), asserted through what a consumer actually filters on.
                let spawn = logger.MessageFor Log.Events.Spawn
                let exit = logger.MessageFor Log.Events.Exit
                Assert.That(spawn.IsSome, "a ProcessSpawned event")
                Assert.That(exit.IsSome, "a ProcessExited event")
                Assert.That(Log.Events.Spawn.Id, Is.EqualTo 1)
                Assert.That(Log.Events.Exit.Id, Is.EqualTo 2)

                // Correlation: spawn and exit of one run share the run id; the pid rides on spawn.
                let runIdOf (m: string) =
                    Regex.Match(m, @"run ([0-9a-f]+)").Groups[1].Value

                let spawnRunId = runIdOf spawn.Value
                Assert.That(spawnRunId, Is.Not.Empty, "spawn carries a run id")
                Assert.That(runIdOf exit.Value, Is.EqualTo spawnRunId, "spawn and exit share the run id")
                Assert.That(spawn.Value, Does.Match @"pid \d+", "the pid rides on spawn")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``the hot path formats and logs nothing when the level is disabled``() : Task =
        task {
            // A run emits only Debug events (spawn/exit); the LoggerMessage.Define delegates must skip
            // formatting/logging when the level is off — the throwing logger proves it is never invoked.
            let command = shell "echo hi" |> Command.logger (DisabledThrowingLogger())

            match! command.RunAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a run emits one ProcessKit activity tagged by program, never argv``() : Task =
        task {
            let activities = ConcurrentQueue<Activity>()
            use listener = new ActivityListener()
            listener.ShouldListenTo <- (fun s -> s.Name = ProcessKitDiagnostics.ActivitySourceName)
            listener.Sample <- SampleActivity<ActivityContext>(fun _o -> ActivitySamplingResult.AllData)
            listener.ActivityStopped <- (fun a -> activities.Enqueue a)
            ActivitySource.AddActivityListener listener

            let command =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args [ (if isWindows then "/c" else "-c"); "echo ok"; "--token=SUPERSECRET" ]

            match! command.RunAsync() with
            | Ok _ ->
                let runActs =
                    activities
                    |> Seq.filter (fun a -> a.OperationName = "processkit.run")
                    |> Seq.toList

                Assert.That(runActs, Is.Not.Empty, "expected a processkit.run span")

                // The program tag is present, and no tag anywhere leaks argv.
                Assert.That(
                    runActs
                    |> List.exists (fun a -> a.TagObjects |> Seq.exists (fun t -> t.Key = "processkit.program")),
                    Is.True,
                    "the span is tagged with the program"
                )

                for a in runActs do
                    for tag in a.TagObjects do
                        Assert.That(
                            string tag.Value,
                            Does.Not.Contain "SUPERSECRET",
                            "argv must never reach a trace tag"
                        )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a run records ProcessKit metrics tagged only by program/outcome, never argv``() : Task =
        task {
            let startedPrograms = ConcurrentQueue<string>()
            let allTagValues = ConcurrentQueue<string>()

            use listener = new MeterListener()

            listener.InstrumentPublished <-
                (fun instrument l ->
                    if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                        l.EnableMeasurementEvents instrument)

            listener.SetMeasurementEventCallback<int64>(
                MeasurementCallback<int64>(fun instrument _value tags _state ->
                    for i in 0 .. tags.Length - 1 do
                        let tag = tags[i]
                        allTagValues.Enqueue(string tag.Value)

                        if instrument.Name = "processkit.runs.started" && tag.Key = "processkit.program" then
                            startedPrograms.Enqueue(string tag.Value))
            )

            listener.Start()

            let command =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args [ (if isWindows then "/c" else "-c"); "echo ok"; "--token=SUPERSECRET" ]

            match! command.RunAsync() with
            | Ok _ ->
                Assert.That(startedPrograms |> Seq.isEmpty |> not, "expected a runs.started measurement")

                Assert.That(
                    allTagValues |> Seq.forall (fun v -> not (v.Contains "SUPERSECRET")),
                    Is.True,
                    "argv must never reach a metric tag"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task
