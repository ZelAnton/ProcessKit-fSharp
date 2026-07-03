namespace ProcessKit.Tests

open System
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
