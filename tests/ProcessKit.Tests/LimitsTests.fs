namespace ProcessKit.Tests

open System
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
            return! runner.OutputString(shell "echo limited", CancellationToken.None)
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

                    match! group.Start(shell "sleep 3") with
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

                        running.StartKill()
                        let! _ = running.Wait()
                        ()
        }
        :> Task
