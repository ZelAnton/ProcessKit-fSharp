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

/// An `ILogger` that reports every level enabled but throws whenever it is actually asked to log —
/// a deliberately broken sink, to prove that a faulting logger provider can never change the result
/// of spawn/run/retry/pipeline/supervisor or leak the `runs.active` metric.
type private ThrowingLogger() =
    interface ILogger with
        member _.Log(_logLevel, _eventId, _state, _error, _formatter) =
            failwith "a broken ILogger must never derail process control"

        member _.IsEnabled(_logLevel) = true

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
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Stays alive a few seconds without exiting on its own — so a handle can be dropped while the
    // child is still running, without racing its natural exit.
    let lingering () =
        if isWindows then
            shell "ping 127.0.0.1 -n 5 >NUL"
        else
            shell "sleep 4"

    // Listen to every `int64` measurement on ProcessKit's meter, tallying `runs.started`/`runs.completed`
    // (net counts) and every `runs.active` delta (the individual +1/-1 measurements, not summed here —
    // callers sum them to check the net balance). Tests run sequentially (no `[Parallelizable]` in this
    // suite), so a listener started immediately before the run(s) under test sees only its own measurements.
    let listenToRunMetrics () =
        let activeDeltas = ConcurrentQueue<int64>()
        let mutable startedCount = 0L
        let mutable completedCount = 0L

        let listener = new MeterListener()

        listener.InstrumentPublished <-
            (fun instrument l ->
                if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                    l.EnableMeasurementEvents instrument)

        listener.SetMeasurementEventCallback<int64>(
            MeasurementCallback<int64>(fun instrument value _tags _state ->
                match instrument.Name with
                | "processkit.runs.active" -> activeDeltas.Enqueue value
                | "processkit.runs.started" -> Interlocked.Add(&startedCount, value) |> ignore
                | "processkit.runs.completed" -> Interlocked.Add(&completedCount, value) |> ignore
                | _ -> ())
        )

        listener.Start()
        listener, activeDeltas, (fun () -> startedCount), (fun () -> completedCount)

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
                // Stable EventIds (name + number), asserted through the public ids a consumer filters on.
                let spawn = logger.MessageFor ProcessKitDiagnostics.Events.ProcessSpawned
                let exit = logger.MessageFor ProcessKitDiagnostics.Events.ProcessExited
                Assert.That(spawn.IsSome, "a ProcessSpawned event")
                Assert.That(exit.IsSome, "a ProcessExited event")
                Assert.That(ProcessKitDiagnostics.Events.ProcessSpawned.Id, Is.EqualTo 1)
                Assert.That(ProcessKitDiagnostics.Events.ProcessExited.Id, Is.EqualTo 2)

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

    [<Test>]
    member _.``an abandoned streaming handle clears runs.active without counting as completed``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                // Never touch a terminal verb (OutputString/Wait/Profile/Finish/…) — just drop the handle
                // while the child is still alive, the exact "StartAsync -> DisposeAsync, no verb" scenario
                // that used to leave `runs.active` permanently inflated.
                do! (running :> IAsyncDisposable).DisposeAsync()

            Assert.That(started (), Is.EqualTo 1L, "expected exactly one runs.started measurement")
            Assert.That(completed (), Is.EqualTo 0L, "an abandoned run must not count as completed")

            Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero for the abandoned run")
        }
        :> Task

    [<Test>]
    member _.``a terminal verb followed by disposing the handle does not double-decrement runs.active``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error error -> Assert.Fail $"{error.Message}"
                | Ok _ -> ()

                // Redundant with the terminal verb's own teardown, but a caller may still do this (e.g. a
                // `use` binding around the handle) — `conclude` already cleared `runs.active`, so this must
                // be a no-op rather than decrementing a second time.
                do! (running :> IAsyncDisposable).DisposeAsync()

            Assert.That(started (), Is.EqualTo 1L)
            Assert.That(completed (), Is.EqualTo 1L, "a run through a terminal verb must count as completed")
            Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "active must settle at zero, not go negative")
        }
        :> Task

    [<Test>]
    member _.``a throwing logger changes neither a run's result nor its metrics``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // The logger throws on spawn AND exit; the run must still succeed with the right output, and
            // its `runs.started`/`runs.completed`/`runs.active` accounting must be exactly as if it hadn't.
            let command = shell "echo logged" |> Command.logger (ThrowingLogger())

            match! command.RunAsync() with
            | Ok stdout -> Assert.That(stdout, Is.EqualTo "logged")
            | Error error -> Assert.Fail $"a broken logger must not fail the run: {error}"

            Assert.That(started (), Is.EqualTo 1L)
            Assert.That(completed (), Is.EqualTo 1L)

            Assert.That(
                activeDeltas |> Seq.sum,
                Is.EqualTo 0L,
                "runs.active must return to zero even when the logger faults on every event"
            )
        }
        :> Task

    [<Test>]
    member _.``a throwing logger does not break a timeout``() : Task =
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            // The timeout path logs `Log.timeout` (Warning) then `Log.exit`; a throw from either must not
            // stop the deadline kill from being observed as a timeout.
            let command =
                sleeper
                |> Command.timeout (TimeSpan.FromMilliseconds 300.0)
                |> Command.logger (ThrowingLogger())

            match! command.OutputStringAsync() with
            | Ok result ->
                Assert.That(
                    result.IsTimedOut,
                    Is.True,
                    "the run must still be reported timed out despite the logger fault"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a throwing logger does not break retry``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Every retry logs `Log.retry`; a throw from it must not abort the retry loop — all three
            // attempts (initial + two retries) must still run and be accounted for.
            let command =
                shell "exit 1"
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)
                |> Command.logger (ThrowingLogger())

            match! command.RunAsync() with
            | Ok _ -> Assert.Fail "a command that always exits 1 must fail after exhausting its retries"
            | Error _ -> ()

            Assert.That(started (), Is.EqualTo 3L, "the retry loop must run every attempt despite the logger fault")

            Assert.That(
                completed (),
                Is.EqualTo 3L,
                "each attempt reaped its (non-zero) exit, so each counts as completed"
            )

            Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must settle at zero across the retries")
        }
        :> Task

    [<Test>]
    member _.``a throwing logger does not break a pipeline``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Stage 0's logger is the pipeline's logger; it throws on the whole-run spawn/exit events. The
            // pipeline must still produce the right output and account for exactly one run.
            let pipeline =
                (shell "echo hi" |> Command.logger (ThrowingLogger())).Pipe(Command.create "sort")

            match! pipeline.RunAsync() with
            | Ok stdout -> Assert.That(stdout, Is.EqualTo "hi")
            | Error error -> Assert.Fail $"a broken logger must not fail the pipeline: {error}"

            Assert.That(started (), Is.EqualTo 1L, "a pipeline is one whole-run started measurement")
            Assert.That(completed (), Is.EqualTo 1L)
            Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero for the pipeline")
        }
        :> Task

    [<Test>]
    member _.``a throwing logger does not break a supervisor restart``() : Task =
        task {
            // `Log.supervisorRestart` fires on the restart; a throw from it must not fail supervision.
            let sup =
                Supervisor(Command.create "worker" |> Command.logger (ThrowingLogger()))
                    .WithRunner(AlwaysCrash())
                    .MaxRestarts(1)
                    .Backoff(TimeSpan.Zero, 1.0)
                    .Jitter(false)

            match! sup.RunAsync() with
            | Ok outcome ->
                Assert.That(
                    outcome.Restarts,
                    Is.EqualTo 1,
                    "supervision must complete its one restart despite the logger fault"
                )

                Assert.That(outcome.Stopped, Is.EqualTo StopReason.RestartsExhausted)
            | Error error -> Assert.Fail $"a broken logger must not fail supervision: {error}"
        }
        :> Task

    [<Test>]
    member _.``a throwing metrics listener does not break a run``() : Task =
        task {
            // A `MeterListener` whose measurement callback throws on every emit (runs.started/active/
            // completed) must not derail the run — the `Diag` emits swallow the fault.
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                (fun instrument l ->
                    if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                        l.EnableMeasurementEvents instrument)

            listener.SetMeasurementEventCallback<int64>(
                MeasurementCallback<int64>(fun _instrument _value _tags _state ->
                    failwith "a broken metrics listener must never derail process control")
            )

            listener.Start()

            match! (shell "echo hi").RunAsync() with
            | Ok stdout -> Assert.That(stdout, Is.EqualTo "hi")
            | Error error -> Assert.Fail $"a broken metrics listener must not fail the run: {error}"
        }
        :> Task

    [<Test>]
    member _.``a throwing activity listener does not break a run``() : Task =
        task {
            // An `ActivityListener` whose start/stop callbacks throw must not derail the completion-span
            // emission that runs inside `Diag.runCompleted` — the emit swallows the fault.
            use listener = new ActivityListener()
            listener.ShouldListenTo <- (fun s -> s.Name = ProcessKitDiagnostics.ActivitySourceName)
            listener.Sample <- SampleActivity<ActivityContext>(fun _o -> ActivitySamplingResult.AllData)

            listener.ActivityStarted <-
                (fun _a -> failwith "a broken activity listener must never derail process control")

            listener.ActivityStopped <-
                (fun _a -> failwith "a broken activity listener must never derail process control")

            ActivitySource.AddActivityListener listener

            match! (shell "echo hi").RunAsync() with
            | Ok stdout -> Assert.That(stdout, Is.EqualTo "hi")
            | Error error -> Assert.Fail $"a broken activity listener must not fail the run: {error}"
        }
        :> Task
