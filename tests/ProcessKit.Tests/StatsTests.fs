namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

/// A no-OS backend adapter used to drive the public ProcessGroup.MemberStats lifecycle path from a
/// deterministic native/backend seam. All unrelated verbs are inert because these tests exercise only
/// the public lifecycle gate and the delegated member-resource snapshot.
type internal MemberStatsSeamBackend(memberStats: unit -> Result<MemberStats list, ProcessError>) =
    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(_command) =
            Error(ProcessError.Unsupported "the member-stats seam does not spawn")

        member _.Track(_spawned) = Ok()
        member _.Adopt(_pid) = Ok()
        member _.Release(_spawned) = ()
        member _.Wait(_handle) = Task.FromResult(Outcome.Exited 0)
        member _.PidOf(spawned) = Some(int spawned.Handle)
        member _.KillChild(_spawned) = ()
        member _.KillTree() = Ok()
        member _.GracefulKillTree (_signal) (_grace) = Task.CompletedTask
        member _.SignalChild(_spawned, _signal) = Ok()
        member _.Members() = Ok []
        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(0, None, None, None, None))

        member _.MemberStats() = memberStats ()
        member _.UpdateLimits(_limits) = Ok()
        member _.HardRelease() = ()

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

    let syntheticSpawned (pid: int) : Native.Common.Spawned =
        { Handle = nativeint pid
          Stdout = None
          Stderr = None
          Stdin = None
          ExtraFds = []
          WindowsCtrlGroup = false
          PtyControl = None }

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
          StdinError = RunningHost.NoStdinError
          StdinFeedComplete = ignore
          StartKill = ignore
          Signal = fun _ -> Ok()
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          TreeStats = None
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

                if isWindows then
                    Assert.That(profile.IoReadBytes.IsSome, Is.True)
                    Assert.That(profile.IoWriteBytes.IsSome, Is.True)
                    Assert.That(profile.IoReadOperations.IsSome, Is.True)
                    Assert.That(profile.IoWriteOperations.IsSome, Is.True)
                else
                    Assert.That(profile.IoReadBytes.IsNone, Is.True)
                    Assert.That(profile.IoWriteBytes.IsNone, Is.True)
                    Assert.That(profile.IoReadOperations.IsNone, Is.True)
                    Assert.That(profile.IoWriteOperations.IsNone, Is.True)
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
                        Assert.That(stats.PeakProcessCount.IsNone, Is.True)
                        Assert.That(stats.TotalCpuTime.IsSome, Is.True)
                        Assert.That(stats.PeakMemoryBytes.IsSome, Is.True)
                        Assert.That(stats.IoReadBytes.IsSome, Is.True)
                        Assert.That(stats.IoWriteBytes.IsSome, Is.True)
                        Assert.That(stats.IoReadOperations.IsSome, Is.True)
                        Assert.That(stats.IoWriteOperations.IsSome, Is.True)
                    else
                        // The POSIX process-group mechanism has no kernel accumulator.
                        Assert.That(stats.PeakProcessCount.IsNone, Is.True)
                        Assert.That(stats.TotalCpuTime.IsNone, Is.True)
                        Assert.That(stats.PeakMemoryBytes.IsNone, Is.True)
                        Assert.That(stats.IoReadBytes.IsNone, Is.True)
                        Assert.That(stats.IoWriteBytes.IsNone, Is.True)
                        Assert.That(stats.IoReadOperations.IsNone, Is.True)
                        Assert.That(stats.IoWriteOperations.IsNone, Is.True)
                | Error error -> Assert.Fail $"{error}"

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``MemberStats reports resources for a live child without argv or environment``() : Task =
        task {
            use group = create ()

            let secret = "PROCESSKIT_MEMBER_STATS_SECRET_4c12"
            let command = sleeper |> Command.env "PROCESSKIT_MEMBER_STATS_SECRET" secret

            match! group.StartAsync command with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some pid ->
                    match group.MemberStats() with
                    | Error error -> Assert.Fail $"MemberStats failed: {error}"
                    | Ok members ->
                        match members |> Seq.tryFind (fun stats -> stats.Pid = pid) with
                        | None -> Assert.Fail "the live child is missing from MemberStats"
                        | Some stats ->
                            Assert.That(stats.Pid, Is.EqualTo pid)

                            if isWindows || RuntimeInformation.IsOSPlatform OSPlatform.Linux then
                                Assert.That(stats.CpuTime.IsSome, Is.True, "expected per-member CPU time")

                                Assert.That(
                                    stats.ResidentMemoryBytes.IsSome,
                                    Is.True,
                                    "expected per-member resident memory"
                                )

                                Assert.That(stats.IoReadBytes.IsSome, Is.True, "expected per-member I/O counters")
                            else
                                // macOS/BSD availability is intentionally best-effort; the contract is the
                                // option shape, not a fabricated zero when a native metric is absent.
                                stats.CpuTime |> ignore
                                stats.ResidentMemoryBytes |> ignore
                                stats.IoReadBytes |> ignore

                            // MemberStats is numeric by construction and never reads argv/environment;
                            // retain the secret only in the child configuration to make that boundary
                            // explicit in this regression test.
                            Assert.That(secret, Is.Not.Empty)

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``the POSIX per-member reader omits a vanished pid``() =
        if isWindows then
            Assert.Ignore
                "the Windows reader requires a live Job Object; lifecycle coverage exercises its vanished-member path"
        else
            let vanishedPid = 0x7FFFFFF0
            Assert.That(Native.Posix.readMemberStats vanishedPid, Is.EqualTo None)

            let currentPid = Process.GetCurrentProcess().Id
            Assert.That((Native.Posix.readMemberStats currentPid).IsSome, Is.True)

    [<Test>]
    member _.``MemberStats returns a typed lifecycle error after release``() : Task =
        task {
            let group = create ()
            do! (group :> IAsyncDisposable).DisposeAsync()

            match group.MemberStats() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for a released group, got {other}"
        }
        :> Task

    [<Test>]
    member _.``public MemberStats fails closed for unknown and recycled POSIX identities``() =
        if isWindows then
            Assert.Ignore "the POSIX identity gate is not used by the Windows Job backend"
        else
            let unknownPid = 2_100_000_001
            let recycledPid = 2_100_000_002
            let current = Dictionary<int, uint64 option>()
            current[unknownPid] <- None
            current[recycledPid] <- Some 11UL

            let originalProcessGroupAlive = Native.Posix.processGroupAliveForTests
            let originalReadProcessIdentity = Native.Posix.readProcessIdentityForTests
            let originalReadMemberStats = Native.Posix.readMemberStatsForTests

            try
                Native.Posix.processGroupAliveForTests <- Some(fun _ -> true)

                Native.Posix.readProcessIdentityForTests <-
                    Some(fun pid ->
                        match current.TryGetValue pid with
                        | true, identity -> identity
                        | false, _ -> None)

                Native.Posix.readMemberStatsForTests <- Some(fun pid -> Some(MemberStats(pid, None, None, None)))

                let backend = ProcessGroupBackend() :> IContainmentBackend
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                try
                    backend.Track(syntheticSpawned unknownPid) |> ignore
                    backend.Track(syntheticSpawned recycledPid) |> ignore
                    current[recycledPid] <- Some 99UL

                    match group.MemberStats() with
                    | Error error -> Assert.Fail $"MemberStats failed: {error}"
                    | Ok members ->
                        Assert.That(
                            members,
                            Is.Empty,
                            "unknown and recycled identities must not be represented by a numeric-pid fallback"
                        )
                finally
                    (group :> IDisposable).Dispose()
            finally
                Native.Posix.processGroupAliveForTests <- originalProcessGroupAlive
                Native.Posix.readProcessIdentityForTests <- originalReadProcessIdentity
                Native.Posix.readMemberStatsForTests <- originalReadMemberStats

    [<Test>]
    member _.``public MemberStats rejects a cgroup pid whose identity changes during sampling``() =
        if isWindows then
            Assert.Ignore "the cgroup identity gate is Linux/POSIX-only"
        else
            let keptPid = 2_100_000_011
            let recycledPid = 2_100_000_012
            let departedPid = 2_100_000_013
            let current = Dictionary<int, uint64>()
            current[keptPid] <- 101UL
            current[recycledPid] <- 202UL
            current[departedPid] <- 203UL

            let pointInTimePids = [ keptPid; recycledPid; departedPid ]
            let trackedIdentities = Dictionary<int, uint64 option>()
            trackedIdentities[keptPid] <- Some 101UL
            trackedIdentities[recycledPid] <- Some 202UL
            trackedIdentities[departedPid] <- Some 203UL
            let adoptedIdentities = Dictionary<int, uint64 option>()
            let mutable currentMembers = pointInTimePids

            let originalReadProcessIdentity = Native.Posix.readProcessIdentityForTests
            let originalReadMemberStats = Native.Posix.readMemberStatsForTests

            try
                Native.Posix.readProcessIdentityForTests <-
                    Some(fun pid ->
                        match current.TryGetValue pid with
                        | true, identity -> Some identity
                        | false, _ -> None)

                Native.Posix.readMemberStatsForTests <-
                    Some(fun pid ->
                        if pid = recycledPid then
                            current[pid] <- 999UL

                        Some(MemberStats(pid, None, None, None)))

                let backend =
                    MemberStatsSeamBackend(fun () ->
                        CgroupMemberStats.sample pointInTimePids trackedIdentities adoptedIdentities (fun () ->
                            Ok currentMembers)
                        |> Result.mapError ProcessError.Io)
                    :> IContainmentBackend

                // The second membership read loses a stable member independently of the identity change.
                currentMembers <- [ keptPid; recycledPid ]

                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                try
                    match group.MemberStats() with
                    | Error error -> Assert.Fail $"MemberStats failed: {error}"
                    | Ok members ->
                        let pids = members |> Seq.map (fun stats -> stats.Pid) |> Set.ofSeq
                        let expected = Set.singleton keptPid
                        Assert.That((pids = expected), Is.True)
                finally
                    (group :> IDisposable).Dispose()
            finally
                Native.Posix.readProcessIdentityForTests <- originalReadProcessIdentity
                Native.Posix.readMemberStatsForTests <- originalReadMemberStats

    [<Test>]
    member _.``cgroup MemberStats uses tracked identity across exit and same-cgroup reuse``() =
        if isWindows then
            Assert.Ignore "the cgroup identity ledger is Linux/POSIX-only"
        else
            let keptPid = 2_100_000_041
            let recycledPid = 2_100_000_042
            let untrackedPid = 2_100_000_043
            let ambiguousPid = 2_100_000_044
            let descendantPid = 2_100_000_045
            let current = Dictionary<int, uint64 option>()
            current[keptPid] <- Some 301UL
            current[recycledPid] <- Some 302UL
            current[untrackedPid] <- Some 303UL
            current[ambiguousPid] <- None
            current[descendantPid] <- Some 304UL

            let pointInTimePids =
                [ keptPid; recycledPid; untrackedPid; ambiguousPid; descendantPid ]

            let trackedIdentities = Dictionary<int, uint64 option>()
            trackedIdentities[keptPid] <- Some 301UL
            trackedIdentities[recycledPid] <- Some 302UL
            trackedIdentities[ambiguousPid] <- None
            let adoptedIdentities = Dictionary<int, uint64 option>()
            let originalReadProcessIdentity = Native.Posix.readProcessIdentityForTests
            let originalReadMemberStats = Native.Posix.readMemberStatsForTests

            try
                Native.Posix.readProcessIdentityForTests <-
                    Some(fun pid ->
                        match current.TryGetValue pid with
                        | true, identity -> identity
                        | false, _ -> None)

                Native.Posix.readMemberStatsForTests <- Some(fun pid -> Some(MemberStats(pid, None, None, None)))

                // The original tracked process exited and its pid was reused by a different process that
                // remains in this same cgroup. The cgroup file cannot tell those generations apart; the
                // tracked ledger omits that PID, while the genuinely untracked descendant members are
                // admitted through their own snapshot identity.
                current[recycledPid] <- Some 9_999UL
                current[ambiguousPid] <- Some 8_888UL

                let backend =
                    MemberStatsSeamBackend(fun () ->
                        CgroupMemberStats.sample pointInTimePids trackedIdentities adoptedIdentities (fun () ->
                            Ok pointInTimePids)
                        |> Result.mapError ProcessError.Io)
                    :> IContainmentBackend

                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                try
                    match group.MemberStats() with
                    | Error error -> Assert.Fail $"MemberStats failed: {error}"
                    | Ok members ->
                        let pids = members |> Seq.map (fun stats -> stats.Pid) |> Set.ofSeq
                        let expected: Set<int> = set [ keptPid; untrackedPid; descendantPid ]

                        Assert.That(
                            pids,
                            Is.EqualTo<Set<int>>(expected),
                            "tracked identity must reject reuse while untracked descendants remain attributable"
                        )
                finally
                    (group :> IDisposable).Dispose()
            finally
                Native.Posix.readProcessIdentityForTests <- originalReadProcessIdentity
                Native.Posix.readMemberStatsForTests <- originalReadMemberStats

    [<Test>]
    member _.``cgroup MemberStats includes adopted and descendant members with identity-safe sampling``() =
        if isWindows then
            Assert.Ignore "cgroup membership is Linux/POSIX-only"
        else
            let adoptedPid = 2_100_000_081
            let adoptedWithoutIdentityPid = 2_100_000_080
            let descendantPid = 2_100_000_082
            let changingDescendantPid = 2_100_000_083
            let current = Dictionary<int, uint64>()
            current[adoptedPid] <- 501UL
            current[descendantPid] <- 502UL
            current[changingDescendantPid] <- 503UL

            let pointInTimePids =
                [ adoptedPid; adoptedWithoutIdentityPid; descendantPid; changingDescendantPid ]

            let trackedIdentities = Dictionary<int, uint64 option>()
            let adoptedIdentities = Dictionary<int, uint64 option>()
            adoptedIdentities[adoptedPid] <- Some 501UL
            adoptedIdentities[adoptedWithoutIdentityPid] <- None
            let currentMembers = pointInTimePids

            let originalReadProcessIdentity = Native.Posix.readProcessIdentityForTests
            let originalReadMemberStats = Native.Posix.readMemberStatsForTests

            try
                Native.Posix.readProcessIdentityForTests <-
                    Some(fun pid ->
                        match current.TryGetValue pid with
                        | true, identity -> Some identity
                        | false, _ -> None)

                Native.Posix.readMemberStatsForTests <-
                    Some(fun pid ->
                        if pid = changingDescendantPid then
                            current[pid] <- 9_999UL

                        Some(MemberStats(pid, None, None, None)))

                let backend =
                    MemberStatsSeamBackend(fun () ->
                        CgroupMemberStats.sample pointInTimePids trackedIdentities adoptedIdentities (fun () ->
                            Ok currentMembers)
                        |> Result.mapError ProcessError.Io)
                    :> IContainmentBackend

                // The adopted leader is pinned at adoption time, while descendants get a snapshot token
                // immediately before sampling. An adopted member without an identity is fail-closed.
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                try
                    match group.MemberStats() with
                    | Error error -> Assert.Fail $"MemberStats failed: {error}"
                    | Ok members ->
                        let pids = members |> Seq.map (fun stats -> stats.Pid) |> Set.ofSeq
                        let expected: Set<int> = set [ adoptedPid; descendantPid ]

                        Assert.That(
                            pids,
                            Is.EqualTo<Set<int>>(expected),
                            "adopted and stable descendant members must be retained, while changed or unknown identities are omitted"
                        )
                finally
                    (group :> IDisposable).Dispose()
            finally
                Native.Posix.readProcessIdentityForTests <- originalReadProcessIdentity
                Native.Posix.readMemberStatsForTests <- originalReadMemberStats

    [<Test>]
    member _.``public MemberStats retains an inaccessible Windows member and omits a gone member``() =
        if not isWindows then
            Assert.Ignore "the OpenProcess failure classification is Windows-only"
        else
            let inaccessiblePid = 2_100_000_021
            let gonePid = 2_100_000_022
            let originalMembershipQuery = Native.Windows.queryInformationJobObjectHook
            let originalIdentitySnapshot = Native.Windows.processIdentitySnapshotForTests

            Native.Windows.queryInformationJobObjectHook <-
                fun _ _ buffer _ ->
                    Marshal.WriteInt32(buffer, 0, 1)
                    Marshal.WriteInt32(buffer, 4, 1)
                    Marshal.WriteIntPtr(buffer, 8, nativeint inaccessiblePid)
                    struct (true, 0)

            Native.Windows.processIdentitySnapshotForTests <- Some(fun () -> Some(Map.ofList [ inaccessiblePid, 101L ]))

            Native.Windows.openMemberProcessForTests <- Some(fun _ pid -> if pid = gonePid then Error 87 else Error 5)

            let backend =
                MemberStatsSeamBackend(fun () ->
                    Native.Windows.readMemberStatsForPids IntPtr.Zero [ inaccessiblePid; gonePid ]
                    |> Ok)
                :> IContainmentBackend

            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

            try
                match group.MemberStats() with
                | Error error -> Assert.Fail $"MemberStats failed: {error}"
                | Ok members ->
                    Assert.That(members.Count, Is.EqualTo 1)
                    let memberStats = members.[0]
                    Assert.That(memberStats.Pid, Is.EqualTo inaccessiblePid)
                    Assert.That(memberStats.CpuTime.IsNone, Is.True)
                    Assert.That(memberStats.ResidentMemoryBytes.IsNone, Is.True)
                    Assert.That(memberStats.IoReadBytes.IsNone, Is.True)
            finally
                (group :> IDisposable).Dispose()
                Native.Windows.openMemberProcessForTests <- None
                Native.Windows.processIdentitySnapshotForTests <- originalIdentitySnapshot
                Native.Windows.queryInformationJobObjectHook <- originalMembershipQuery

    [<Test>]
    member _.``public MemberStats omits an inaccessible reused PID absent from the current Job``() =
        if not isWindows then
            Assert.Ignore "the Windows Job membership confirmation is Windows-only"
        else
            let reusedPid = 2_100_000_031
            let originalMembershipQuery = Native.Windows.queryInformationJobObjectHook
            let originalIdentitySnapshot = Native.Windows.processIdentitySnapshotForTests

            // The supplied per-call snapshot represents the original Job member. The refresh represents
            // the post-exit/reuse state: the protected replacement is outside the Job, so the current Job
            // member list is empty even though OpenProcess returns ACCESS_DENIED for the reused number.
            Native.Windows.queryInformationJobObjectHook <-
                fun _ _ buffer _ ->
                    Marshal.WriteInt32(buffer, 0, 0)
                    Marshal.WriteInt32(buffer, 4, 0)
                    struct (true, 0)

            Native.Windows.openMemberProcessForTests <- Some(fun _ _ -> Error 5)
            Native.Windows.processIdentitySnapshotForTests <- Some(fun () -> Some(Map.ofList [ reusedPid, 201L ]))

            let backend =
                MemberStatsSeamBackend(fun () -> Native.Windows.readMemberStatsForPids IntPtr.Zero [ reusedPid ] |> Ok)
                :> IContainmentBackend

            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

            try
                match group.MemberStats() with
                | Error error -> Assert.Fail $"MemberStats failed: {error}"
                | Ok members ->
                    Assert.That(
                        members,
                        Is.Empty,
                        "an ACCESS_DENIED pid absent from the refreshed Job membership must be omitted"
                    )
            finally
                (group :> IDisposable).Dispose()
                Native.Windows.openMemberProcessForTests <- None
                Native.Windows.processIdentitySnapshotForTests <- originalIdentitySnapshot
                Native.Windows.queryInformationJobObjectHook <- originalMembershipQuery

    [<Test>]
    member _.``Windows MemberStats rejects exit-after-pre-read and same-Job identity reuse``() =
        if not isWindows then
            Assert.Ignore "the Windows process-handle identity gate is Windows-only"
        else
            let originalOpen = Native.Windows.openMemberProcessForTests
            let originalMembership = Native.Windows.isProcessInJobForTests
            let originalTimes = Native.Windows.getProcessTimesForTests
            let originalIdentitySnapshot = Native.Windows.processIdentitySnapshotForTests
            let pid = 2_100_000_051

            try
                // A zero handle is sufficient because every native read used by this seam is replaced;
                // CloseHandle(NULL) is a harmless failed close in the test-only path.
                Native.Windows.openMemberProcessForTests <- Some(fun _ _ -> Ok IntPtr.Zero)
                Native.Windows.isProcessInJobForTests <- Some(fun _ _ -> true)

                let run (expectedIdentity: int64) (times: (int64 * int64 * int64 * int64) list) =
                    let mutable index = 0

                    Native.Windows.processIdentitySnapshotForTests <-
                        Some(fun () -> Some(Map.ofList [ pid, expectedIdentity ]))

                    Native.Windows.getProcessTimesForTests <-
                        Some(fun _ ->
                            if index >= times.Length then
                                None
                            else
                                let value = times[index]
                                index <- index + 1
                                Some value)

                    Native.Windows.readMemberStatsForPids IntPtr.Zero [ pid ]

                let exitedAfterPreRead = run 101L [ (101L, 0L, 1L, 2L); (101L, 1L, 1L, 2L) ]

                Assert.That(
                    exitedAfterPreRead,
                    Is.Empty,
                    "a member that exits after the pre-read must not be returned from its still-valid handle"
                )

                let reusedWithinJob = run 201L [ (201L, 0L, 1L, 2L); (202L, 0L, 1L, 2L) ]

                Assert.That(
                    reusedWithinJob,
                    Is.Empty,
                    "a same-Job PID whose stable creation identity changes must be omitted"
                )
            finally
                Native.Windows.openMemberProcessForTests <- originalOpen
                Native.Windows.isProcessInJobForTests <- originalMembership
                Native.Windows.getProcessTimesForTests <- originalTimes
                Native.Windows.processIdentitySnapshotForTests <- originalIdentitySnapshot

    [<Test>]
    member _.``Windows MemberStats rejects same-Job PID reuse before OpenProcess``() =
        if not isWindows then
            Assert.Ignore "the Windows process identity snapshot is Windows-only"
        else
            let originalOpen = Native.Windows.openMemberProcessForTests
            let originalMembership = Native.Windows.isProcessInJobForTests
            let originalTimes = Native.Windows.getProcessTimesForTests
            let originalIdentitySnapshot = Native.Windows.processIdentitySnapshotForTests
            let pid = 2_100_000_061
            let mutable currentIdentity = 301L

            try
                // The identity snapshot sees the original member. The OpenProcess seam then models the
                // original exiting and a new process in the SAME Job reusing the numeric pid before the
                // sampling handle is opened; both pre/post handle checks would otherwise see the new one.
                Native.Windows.processIdentitySnapshotForTests <- Some(fun () -> Some(Map.ofList [ pid, 301L ]))

                Native.Windows.openMemberProcessForTests <-
                    Some(fun _ _ ->
                        currentIdentity <- 302L
                        Ok IntPtr.Zero)

                Native.Windows.isProcessInJobForTests <- Some(fun _ _ -> true)
                Native.Windows.getProcessTimesForTests <- Some(fun _ -> Some(currentIdentity, 0L, 1L, 2L))

                let members = Native.Windows.readMemberStatsForPids IntPtr.Zero [ pid ]

                Assert.That(
                    members,
                    Is.Empty,
                    "a same-Job PID reused before OpenProcess must be rejected by the pre-sampling identity ledger"
                )
            finally
                Native.Windows.openMemberProcessForTests <- originalOpen
                Native.Windows.isProcessInJobForTests <- originalMembership
                Native.Windows.getProcessTimesForTests <- originalTimes
                Native.Windows.processIdentitySnapshotForTests <- originalIdentitySnapshot

    [<Test>]
    member _.``Windows MemberStats rejects inaccessible same-Job PID reuse before OpenProcess``() =
        if not isWindows then
            Assert.Ignore "the Windows process identity snapshot is Windows-only"
        else
            let originalOpen = Native.Windows.openMemberProcessForTests
            let originalIdentitySnapshot = Native.Windows.processIdentitySnapshotForTests
            let originalMembershipQuery = Native.Windows.queryInformationJobObjectHook
            let pid = 2_100_000_071
            let mutable currentIdentity = 401L

            try
                Native.Windows.processIdentitySnapshotForTests <-
                    Some(fun () -> Some(Map.ofList [ pid, currentIdentity ]))

                Native.Windows.openMemberProcessForTests <-
                    Some(fun _ _ ->
                        currentIdentity <- 402L
                        Error 5)

                Native.Windows.queryInformationJobObjectHook <-
                    fun _ _ buffer _ ->
                        Marshal.WriteInt32(buffer, 0, 1)
                        Marshal.WriteInt32(buffer, 4, 1)
                        Marshal.WriteIntPtr(buffer, 8, nativeint pid)
                        struct (true, 0)

                let members = Native.Windows.readMemberStatsForPids IntPtr.Zero [ pid ]

                Assert.That(
                    members,
                    Is.Empty,
                    "an inaccessible same-Job PID whose generation changed before OpenProcess must be omitted"
                )
            finally
                Native.Windows.openMemberProcessForTests <- originalOpen
                Native.Windows.processIdentitySnapshotForTests <- originalIdentitySnapshot
                Native.Windows.queryInformationJobObjectHook <- originalMembershipQuery

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
    member _.``cgroup io.stat sums counters across block devices``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"processkit-io-stats-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 5\n")
            File.WriteAllText(Path.Combine(directory, "memory.peak"), "42\n")
            File.WriteAllText(Path.Combine(directory, "pids.peak"), "7\n")

            File.WriteAllText(
                Path.Combine(directory, "io.stat"),
                "8:0 rbytes=4 wbytes=9 rios=1 wios=2 dbytes=3 dios=1\n"
                + "8:16 rbytes=7 wbytes=13 rios=3 wios=4\n"
            )

            let cpu, memory, processCount, io = ProcessKit.Native.Cgroup.cgroupStats directory
            Assert.That(cpu, Is.EqualTo(Some(TimeSpan.FromTicks 50L)))
            Assert.That(memory, Is.EqualTo(Some 42L))
            Assert.That(processCount, Is.EqualTo(Some 7L))
            Assert.That(io.IsSome, Is.True)
            Assert.That(io.Value.ReadBytes, Is.EqualTo 11L)
            Assert.That(io.Value.WriteBytes, Is.EqualTo 22L)
            Assert.That(io.Value.ReadOperations, Is.EqualTo 4L)
            Assert.That(io.Value.WriteOperations, Is.EqualTo 6L)
        finally
            Directory.Delete(directory, true)

    [<Test>]
    member _.``cgroup pids.peak stays unavailable when its native counter is absent or unreadable``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"processkit-pids-peak-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let _, _, absent, _ = ProcessKit.Native.Cgroup.cgroupStats directory
            Assert.That(absent.IsNone, Is.True, "a missing pids.peak must not be reported as zero")

            Directory.CreateDirectory(Path.Combine(directory, "pids.peak")) |> ignore
            let _, _, unreadable, _ = ProcessKit.Native.Cgroup.cgroupStats directory
            Assert.That(unreadable.IsNone, Is.True, "an unreadable pids.peak must not be reported as zero")
        finally
            Directory.Delete(directory, true)

    [<Test>]
    member _.``ProfileAsync projects private tree I/O counters from its final snapshot``() : Task =
        task {
            let counters =
                { ReadBytes = 101L
                  WriteBytes = 202L
                  ReadOperations = 3L
                  WriteOperations = 4L }

            let stats = ProcessGroupStats(0, None, None, None, Some counters)

            let host =
                { hostOverCurrentProcess None with
                    TreeStats = Some(fun () -> Some stats) }

            use running = new RunningProcess(host)
            let! profile = running.ProfileAsync(TimeSpan.FromMilliseconds 10.0)
            Assert.That(profile.IoReadBytes, Is.EqualTo(Some 101L))
            Assert.That(profile.IoWriteBytes, Is.EqualTo(Some 202L))
            Assert.That(profile.IoReadOperations, Is.EqualTo(Some 3L))
            Assert.That(profile.IoWriteOperations, Is.EqualTo(Some 4L))
        }
        :> Task

    [<Test>]
    member _.``ProfileAsync does not attribute a shared group's aggregate I/O to one run``() : Task =
        task {
            use group = create ()

            match! group.StartAsync(shell "exit 0") with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! profile = running.ProfileAsync(TimeSpan.FromMilliseconds 10.0)
                Assert.That(profile.IoReadBytes.IsNone, Is.True)
                Assert.That(profile.IoWriteBytes.IsNone, Is.True)
                Assert.That(profile.IoReadOperations.IsNone, Is.True)
                Assert.That(profile.IoWriteOperations.IsNone, Is.True)
        }
        :> Task

    [<Test>]
    member _.``AvgCpuCores divides CPU time by duration``() =
        let profile =
            RunProfile(Outcome.Exited 0, TimeSpan.FromSeconds 2.0, Some(TimeSpan.FromSeconds 1.0), None, None, 5)

        match profile.AvgCpuCores with
        | Some avg -> Assert.That(avg, Is.EqualTo(0.5).Within 1e-9)
        | None -> Assert.Fail "expected an average"

        let noDuration =
            RunProfile(Outcome.Exited 0, TimeSpan.Zero, Some(TimeSpan.FromSeconds 1.0), None, None, 1)

        Assert.That(noDuration.AvgCpuCores.IsNone, Is.True)

    [<Test>]
    member _.``RunProfile.Outcome distinguishes a timeout and a signal kill (both leave ExitCode None)``() =
        // The point of carrying the full Outcome: ExitCode is None for both a timeout and a signal kill,
        // so a profiled run can only tell them apart via Outcome / TimedOut / Signal.
        let timedOut =
            RunProfile(Outcome.TimedOut, TimeSpan.FromSeconds 1.0, None, None, None, 1)

        Assert.That(timedOut.ExitCode.IsNone, Is.True)
        Assert.That(timedOut.TimedOut, Is.True)
        Assert.That(timedOut.Signal.IsNone, Is.True)

        let signalled =
            RunProfile(Outcome.Signalled(Some 9), TimeSpan.FromSeconds 1.0, None, None, None, 1)

        Assert.That(signalled.ExitCode.IsNone, Is.True)
        Assert.That(signalled.TimedOut, Is.False)
        Assert.That(signalled.Signal, Is.EqualTo(Some 9))

        let exited =
            RunProfile(Outcome.Exited 3, TimeSpan.FromSeconds 1.0, None, None, None, 1)

        Assert.That(exited.ExitCode, Is.EqualTo(Some 3))
        Assert.That(exited.Outcome, Is.EqualTo(Outcome.Exited 3))
