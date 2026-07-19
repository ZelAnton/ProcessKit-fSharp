namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type StatsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let sleeper =
        if isWindows then
            shell "ping -n 4 127.0.0.1 >nul"
        else
            shell "sleep 3"

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    // A synthetic `RunningHost` whose pid is THIS test process's own (so `Process.GetProcessById`
    // genuinely succeeds and reports real metrics), with a caller-supplied `StartTimeIdentity` — the
    // T-097 seam: a mismatched identity models a recycled pid the OS handed to an unrelated process
    // after the original child was reaped, without needing a real pid-reuse race on CI.
    let hostOverCurrentProcess (startTimeIdentity: DateTime option) : RunningHost =
        { Config = (Command.create "test").Config
          Pid = Some(Process.GetCurrentProcess().Id)
          Stdout = None
          Stderr = None
          Stdin = None
          StartTime = DateTime.UtcNow
          StartedTimestamp = Stopwatch.GetTimestamp()
          StartTimeIdentity = startTimeIdentity
          Wait = fun () -> Task.FromResult(Outcome.Exited 0)
          StdinError = fun () -> None
          StdinFeedComplete = ignore
          StartKill = ignore
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          Teardown = fun () -> ValueTask() }

    [<Test>]
    member _.``Profile returns timing and sample counts``() : Task =
        task {
            let workload =
                if isWindows then
                    shell "ping -n 2 127.0.0.1 >nul"
                else
                    shell "sleep 0.3"

            match! workload.StartAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! profile = running.ProfileAsync(TimeSpan.FromMilliseconds 50.0)
                Assert.That(profile.ExitCode, Is.EqualTo(Some 0))
                Assert.That(profile.Duration, Is.GreaterThan TimeSpan.Zero)
                Assert.That(profile.Samples, Is.GreaterThanOrEqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``ProfileAsync rejects a non-positive interval``() : Task =
        task {
            // The interval is validated before the pipes are claimed, so a synthetic host (no real
            // child) suffices and the rejected call never consumes the one-shot handle. `use` inside
            // the task CE disposes the `IAsyncDisposable` handle.
            use running = new RunningProcess(hostOverCurrentProcess None)

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> running.ProfileAsync(TimeSpan.Zero) |> ignore))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> running.ProfileAsync(TimeSpan.FromSeconds -1.0) |> ignore)
            )
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``group Stats reports an active process count``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match group.Stats() with
                | Ok stats ->
                    Assert.That(stats.ActiveProcessCount, Is.GreaterThanOrEqualTo 1)

                    if isWindows then
                        // The Job Object reports cumulative CPU and peak committed memory.
                        Assert.That(stats.TotalCpuTime.IsSome, Is.True)
                        Assert.That(stats.PeakMemoryBytes.IsSome, Is.True)
                    else
                        // The POSIX process-group mechanism has no kernel accumulator.
                        Assert.That(stats.TotalCpuTime.IsNone, Is.True)
                        Assert.That(stats.PeakMemoryBytes.IsNone, Is.True)
                | Error error -> Assert.Fail $"{error}"

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``SampleStats yields a periodic series``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let enumerator =
                    group.SampleStatsAsync(TimeSpan.FromMilliseconds 50.0).GetAsyncEnumerator(CancellationToken.None)

                let! first = enumerator.MoveNextAsync()
                Assert.That(first, Is.True)
                Assert.That(enumerator.Current.ActiveProcessCount, Is.GreaterThanOrEqualTo 1)

                let! second = enumerator.MoveNextAsync()
                Assert.That(second, Is.True)

                do! enumerator.DisposeAsync()
                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``SampleStatsAsync rejects a non-positive interval``() =
        // Rejected eagerly by the call itself (not deferred to enumeration), so no enumerator is
        // ever produced for a non-positive cadence.
        use group = create ()

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> group.SampleStatsAsync(TimeSpan.Zero) |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> group.SampleStatsAsync(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``per-process metrics are available while the child runs``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                if not isMacOs then
                    // CPU time and peak working set are reported on Windows and Linux; macOS BCL
                    // coverage is less certain, so only smoke-test that the members are callable there.
                    Assert.That(running.CpuTime.IsSome, Is.True)
                    Assert.That(running.PeakMemoryBytes.IsSome, Is.True)
                else
                    running.CpuTime |> ignore
                    running.PeakMemoryBytes |> ignore

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``CpuTime/PeakMemoryBytes withhold metrics when the pid's identity no longer matches the child``() : Task =
        task {
            // A start time that cannot possibly be this (long-running test) process's real one — models
            // an OS-recycled pid whose current occupant is a stranger, not our reaped child.
            let host = hostOverCurrentProcess (Some(DateTime.UtcNow.AddDays -1.0))
            use running = new RunningProcess(host)

            Assert.That(running.CpuTime.IsNone, Is.True, "CpuTime must not report a mismatched pid's metrics")

            Assert.That(
                running.PeakMemoryBytes.IsNone,
                Is.True,
                "PeakMemoryBytes must not report a mismatched pid's metrics"
            )

            do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``CpuTime/PeakMemoryBytes still report metrics when the identity matches``() : Task =
        task {
            let actualStartTime = Process.GetCurrentProcess().StartTime
            let host = hostOverCurrentProcess (Some actualStartTime)
            use running = new RunningProcess(host)

            if not isMacOs then
                // CPU time and peak working set are reported on Windows and Linux; macOS BCL coverage is
                // less certain (see the equivalent skip in "per-process metrics are available...").
                Assert.That(running.CpuTime.IsSome, Is.True)
                Assert.That(running.PeakMemoryBytes.IsSome, Is.True)
            else
                running.CpuTime |> ignore
                running.PeakMemoryBytes |> ignore

            do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``CpuTime/PeakMemoryBytes fall back to a raw read when no identity was captured``() : Task =
        task {
            // `StartTimeIdentity = None` (e.g. a synthetic host/fake, or a spawn-time identity read that
            // failed) must not spuriously withhold metrics — the gate defers to the raw read exactly as
            // before T-097.
            let host = hostOverCurrentProcess None
            use running = new RunningProcess(host)

            if not isMacOs then
                Assert.That(running.CpuTime.IsSome, Is.True)
                Assert.That(running.PeakMemoryBytes.IsSome, Is.True)
            else
                running.CpuTime |> ignore
                running.PeakMemoryBytes |> ignore

            do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``AvgCpuCores divides CPU time by duration``() =
        let profile =
            RunProfile(Outcome.Exited 0, TimeSpan.FromSeconds 2.0, Some(TimeSpan.FromSeconds 1.0), None, 5)

        match profile.AvgCpuCores with
        | Some avg -> Assert.That(avg, Is.EqualTo(0.5).Within 1e-9)
        | None -> Assert.Fail "expected an average"

        let noDuration =
            RunProfile(Outcome.Exited 0, TimeSpan.Zero, Some(TimeSpan.FromSeconds 1.0), None, 1)

        Assert.That(noDuration.AvgCpuCores.IsNone, Is.True)

    [<Test>]
    member _.``RunProfile.Outcome distinguishes a timeout and a signal kill (both leave ExitCode None)``() =
        // The point of carrying the full Outcome: ExitCode is None for both a timeout and a signal kill,
        // so a profiled run can only tell them apart via Outcome / TimedOut / Signal.
        let timedOut = RunProfile(Outcome.TimedOut, TimeSpan.FromSeconds 1.0, None, None, 1)
        Assert.That(timedOut.ExitCode.IsNone, Is.True)
        Assert.That(timedOut.TimedOut, Is.True)
        Assert.That(timedOut.Signal.IsNone, Is.True)

        let signalled =
            RunProfile(Outcome.Signalled(Some 9), TimeSpan.FromSeconds 1.0, None, None, 1)

        Assert.That(signalled.ExitCode.IsNone, Is.True)
        Assert.That(signalled.TimedOut, Is.False)
        Assert.That(signalled.Signal, Is.EqualTo(Some 9))

        let exited = RunProfile(Outcome.Exited 3, TimeSpan.FromSeconds 1.0, None, None, 1)
        Assert.That(exited.ExitCode, Is.EqualTo(Some 3))
        Assert.That(exited.Outcome, Is.EqualTo(Outcome.Exited 3))
