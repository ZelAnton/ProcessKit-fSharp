namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type LimitsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let runInGroup (group: ProcessGroup) =
        task {
            let runner: IProcessRunner = group
            return! runner.OutputStringAsync(shell "echo limited", CancellationToken.None)
        }

    [<Test>]
    member _.``ProcessGroupOptions builders set the limits``() =
        let options =
            ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(50).WithCpuQuota(1.5)

        Assert.That(options.Limits.MemoryMax, Is.EqualTo(Some(256L * 1024L * 1024L)))
        Assert.That(options.Limits.MaxProcesses, Is.EqualTo(Some 50))
        Assert.That(options.Limits.CpuQuota, Is.EqualTo(Some 1.5))
        Assert.That(ResourceLimits.None.Any, Is.False)

    [<Test>]
    member _.``ResourceLimits builders reject non-positive values``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithMemoryMax 0L |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithMemoryMax -1L |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithMaxProcesses 0 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithMaxProcesses -1 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithCpuQuota 0.0 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithCpuQuota -1.0 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuQuota Double.NaN |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``ProcessGroupOptions.WithShutdownTimeout rejects a negative window but accepts zero``() =
        ProcessGroupOptions().WithShutdownTimeout TimeSpan.Zero |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ProcessGroupOptions().WithShutdownTimeout(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a group with no limits behaves as the default mechanism``() : Task =
        task {
            match ProcessGroup.Create(ProcessGroupOptions()) with
            | Error error -> Assert.Fail $"{error}"
            | Ok group ->
                use group = group

                match! runInGroup group with
                | Ok result -> Assert.That(result.Stdout, Does.Contain "limited")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a group with limits uses a limit-capable mechanism or fails fast``() : Task =
        task {
            let options =
                ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(64).WithCpuQuota(2.0)

            let result = ProcessGroup.Create options

            if isWindows then
                // The Job Object always enforces limits.
                match result with
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.JobObject)

                    match! runInGroup group with
                    | Ok r -> Assert.That(r.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"{error}"
                | Error error -> Assert.Fail $"Windows job limits should always apply, got {error}"
            elif isMacOs then
                // No whole-tree limit primitive — must fail fast.
                match result with
                | Error(ProcessError.ResourceLimit _) -> Assert.Pass()
                | other -> Assert.Fail $"expected ResourceLimit on macOS, got {other}"
            else
                // Linux: cgroup v2 at the real cgroup root enforces; under systemd / an ordinary
                // container (the usual CI case) the controllers can't be enabled, so it fails fast.
                // Both are acceptable — never a silently-unbounded group. The privileged CI leg moves
                // this process to the real cgroup root and sets PROCESSKIT_EXPECT_CGROUP, which makes
                // this test *require* the cgroup path (so the enforcement code is actually exercised).
                let expectCgroup =
                    Environment.GetEnvironmentVariable "PROCESSKIT_EXPECT_CGROUP" = "1"

                match result with
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    match! runInGroup group with
                    | Ok r -> Assert.That(r.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"{error}"
                | Error(ProcessError.ResourceLimit _) when not expectCgroup -> Assert.Pass()
                | Error other -> Assert.Fail $"expected CgroupV2 (PROCESSKIT_EXPECT_CGROUP set), got {other}"
        }
        :> Task

    [<Test>]
    member _.``a failed cgroup migration kills the child and returns an honest error``() : Task =
        task {
            if isWindows || isMacOs then
                Assert.Ignore "cgroup v2 migration is Linux-only"
            else
                // A cgroup directory that does not exist, so the migration write to
                // <path>/cgroup.procs throws (the parent directory is absent) and migration must fail.
                // This exercises CgroupBackend.Track's failure path directly (a real ProcessGroup only
                // ever holds a valid cgroup path, so the failure has to be injected at the backend).
                let missingCgroup =
                    Path.Combine(Path.GetTempPath(), $"processkit-missing-cgroup-{Guid.NewGuid():N}")

                let backend: IContainmentBackend = CgroupBackend missingCgroup

                // A long-lived child with no piped stdio (nothing for the parent to drain/close), so the
                // only thing under test is whether Track leaves it running after the migration fails.
                let child =
                    Command.create "sleep"
                    |> Command.args [ "30" ]
                    |> Command.stdout StdioMode.Null
                    |> Command.stderr StdioMode.Null

                match backend.Spawn child with
                | Error error -> Assert.Fail $"spawn failed: {error}"
                | Ok spawned ->
                    let pid =
                        match backend.PidOf spawned with
                        | Some p -> p
                        | None -> failwith "expected a spawned pid"

                    match backend.Track spawned with
                    | Ok() -> Assert.Fail "Track should fail when the child cannot be migrated into the cgroup"
                    | Error(ProcessError.ResourceLimit detail) ->
                        // (a) an honest error of the expected variant, carrying a real detail.
                        Assert.That(detail, Is.Not.Empty)

                        // (b) no live, unconstrained child left behind: Track killed and reaped it, so the
                        // pid no longer exists (a fully-reaped leader, not a zombie). Poll briefly to
                        // absorb any tiny scheduling lag in the SIGKILL taking effect.
                        let mutable gone = false
                        let mutable attempts = 0

                        while not gone && attempts < 100 do
                            match Native.Posix.signalPid pid 0 with
                            | Native.Common.SignalDelivery.TargetGone -> gone <- true
                            | _ ->
                                do! Task.Delay 10
                                attempts <- attempts + 1

                        Assert.That(gone, Is.True, "the child was left alive after a failed migration")
                    | Error other -> Assert.Fail $"expected ProcessError.ResourceLimit, got {other}"
        }
        :> Task

    [<Test>]
    member _.``CgroupBackend: Track racing HardRelease reaps the child exactly once``() : Task =
        task {
            if isWindows || isMacOs then
                Assert.Ignore "cgroup v2 migration is Linux-only"
            else
                // The cgroup failure-path double-reap the fix closes. With a missing cgroup directory the
                // migration write fails, so Track must kill+reap the child — but a teardown (HardRelease)
                // draining the tracked pid can race it. Track now reaps ONLY if it still owns the pid
                // (guarded on `children.Remove`) and HardRelease drains atomically, so exactly one side
                // reaps: the second killpg/waitpid on an OS-recycled pid (a wrong-target kill) can no
                // longer happen. Deterministically asserted: after the race the child is fully reaped
                // (gone, not a zombie) with no exception or hang, however the two interleave.
                for _ in 1..25 do
                    let missingCgroup =
                        Path.Combine(Path.GetTempPath(), $"processkit-race-cgroup-{Guid.NewGuid():N}")

                    let backend: IContainmentBackend = CgroupBackend missingCgroup

                    let child =
                        Command.create "sleep"
                        |> Command.args [ "30" ]
                        |> Command.stdout StdioMode.Null
                        |> Command.stderr StdioMode.Null

                    let spawned =
                        match backend.Spawn child with
                        | Ok s -> s
                        | Error e -> failwith $"spawn failed: {e}"

                    let pid =
                        match backend.PidOf spawned with
                        | Some p -> p
                        | None -> failwith "expected a spawned pid"

                    // Race Track (migration fails -> guarded reap) against HardRelease (drain -> reap).
                    let trackTask = Task.Run(fun () -> backend.Track spawned |> ignore)
                    let releaseTask = Task.Run(fun () -> backend.HardRelease())
                    do! Task.WhenAll(trackTask, releaseTask)

                    // Exactly one side reaped it, so the child is gone. Poll briefly for the SIGKILL to land.
                    let mutable gone = false
                    let mutable attempts = 0

                    while not gone && attempts < 200 do
                        match Native.Posix.signalPid pid 0 with
                        | Native.Common.SignalDelivery.TargetGone -> gone <- true
                        | _ ->
                            do! Task.Delay 10
                            attempts <- attempts + 1

                    Assert.That(gone, Is.True, "the child survived the Track-vs-HardRelease race")
        }
        :> Task

    [<Test>]
    member _.``the cgroup mechanism drives the control verbs``() : Task =
        task {
            if isWindows || isMacOs then
                Assert.Ignore "cgroup v2 is Linux-only"
            else
                let options =
                    ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(64)

                match ProcessGroup.Create options with
                | Error(ProcessError.ResourceLimit _) ->
                    Assert.Ignore "cgroup v2 limits not enforceable here (not at the real cgroup root)"
                | Error other -> Assert.Fail $"{other}"
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    match! group.StartAsync(shell "sleep 3") with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok running ->
                        // Membership comes from cgroup.procs.
                        match group.Members() with
                        | Ok members -> Assert.That(members, Is.Not.Empty)
                        | Error error -> Assert.Fail $"members: {error}"

                        // Stats read the cgroup accounting (cpu.stat / memory.peak).
                        match group.Stats() with
                        | Ok stats -> Assert.That(stats.ActiveProcessCount, Is.GreaterThanOrEqualTo 1)
                        | Error error -> Assert.Fail $"stats: {error}"

                        // Suspend/Resume via cgroup.freeze.
                        match group.Suspend() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"suspend: {error}"

                        match group.Resume() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"resume: {error}"

                        running.Kill()
                        let! _ = running.WaitAsync()
                        ()
        }
        :> Task
