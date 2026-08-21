namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

module private WindowsIoRateControlTestSupport =

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")>]
    extern uint32 queryDosDevice(string deviceName, StringBuilder targetPath, uint32 maxChars)

    let private tryQueryNtVolume (root: string) : string option =
        let deviceName = root.TrimEnd([| '\\'; '/' |])
        let buffer = StringBuilder(1024)
        let length = queryDosDevice (deviceName, buffer, uint32 buffer.Capacity)

        if length = 0u then None else Some(buffer.ToString())

    let volumeTargets () : string list =
        if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
            []
        else
            try
                let systemRoots =
                    match Path.GetPathRoot Environment.SystemDirectory with
                    | null -> []
                    | root -> [ root ]

                let roots =
                    [ yield! systemRoots
                      for drive in DriveInfo.GetDrives() do
                          if drive.IsReady then
                              yield drive.RootDirectory.FullName ]

                roots
                |> List.choose (fun root ->
                    if String.IsNullOrWhiteSpace root then
                        None
                    else
                        tryQueryNtVolume root)
                |> List.distinct
            with
            | :? IOException
            | :? UnauthorizedAccessException ->
                // A runner can expose a transient or inaccessible drive while the test process is starting;
                // no NT volume target can be claimed honestly in that case, so the gated test skips.
                []

/// Test support for `Native.Cgroup.limitEvidence` (T-381) — a fresh temp directory standing in for a
/// cgroup v2 directory, pure file I/O, no real cgroup v2 mount required (mirrors the `cgroupStats` tests
/// in StatsTests.fs).
module private LimitEvidenceTestSupport =

    let withTempCgroupDir (body: string -> unit) =
        let directory =
            Path.Combine(Path.GetTempPath(), $"processkit-limit-evidence-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            body directory
        finally
            Directory.Delete(directory, true)

/// A zero-cost stand-in for a pidfd used by the identity-safe delivery seam tests below:
/// `Native.Cgroup.deliverIdentitySafe` is generic over the pin handle, so a test pins with this token
/// instead of a real file descriptor.
type private FakePidfd = FakeHandle

/// A synthetic `IContainmentBackend` that models a limit-capable backend's honest `UpdateLimits`
/// contract without any real OS container (T-207). It tracks the caps actually "in force" (`InForce`,
/// the container stand-in) and, on `UpdateLimits`, consults `shouldFail`: a set that trips it models a
/// partial native apply that then best-effort restores the previous set — `InForce` is left unchanged
/// and a typed `ProcessError.ResourceLimit` is returned; any other set is applied and becomes the new
/// `InForce`. It exists to prove `ProcessGroup.UpdateLimits` keeps `Options.Limits` in lockstep with
/// what the backend reports as in force — swapping the snapshot only on a real apply, never reporting
/// caps a failed apply did not leave enforced. Every other verb is an unused book-keeping no-op.
type internal LimitContractBackend(initial: ResourceLimits, shouldFail: ResourceLimits -> bool) =
    let mutable inForce = initial

    /// The caps currently enforced on the container stand-in — what `Options.Limits` must always match.
    member _.InForce = inForce

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject

        member _.Spawn(_command) =
            Error(ProcessError.Unsupported "LimitContractBackend does not spawn")

        member _.Track(_spawned) = Ok()

        member _.Adopt(_pid) =
            Error(ProcessError.Unsupported "LimitContractBackend does not adopt")

        member _.AdoptByPid(_pid) =
            Error(ProcessError.Unsupported "LimitContractBackend does not adopt by pid")

        member _.Release(_spawned) = ()
        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(_spawned) = None
        member _.KillChild(_spawned) = ()
        member _.KillTree() = Ok()

        member _.GracefulKillTree (_signal) (_grace) =
            Task.FromResult
                { Soft = SoftDelivery.Sent
                  Drained = true
                  Escalated = false }

        member _.SoftStopScope() = SoftStopScope.Unsupported
        member _.SignalChild(_spawned, _signal) = Ok()
        member _.Members() = Ok []
        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(0, None, None, None, None))

        member _.MemberStats() = Ok []

        member _.UpdateLimits(limits) =
            if shouldFail limits then
                // Model a limit-capable backend whose native apply failed partway and then best-effort
                // restored the previous set: nothing net changed (InForce stays put), and it surfaces the
                // honest typed refusal — exactly what the real Windows/cgroup backends now do (T-207).
                Error(ProcessError.ResourceLimit "simulated partial apply failure (previous set restored)")
            else
                inForce <- limits
                Ok()

        member _.LimitEvidence(_capped) =
            LimitEvidence(LimitVerdict.Unknown, LimitVerdict.Unknown, LimitVerdict.Unknown)

        member _.HardRelease() = ()

/// A synthetic `IContainmentBackend` whose `LimitEvidence` echoes exactly the `CappedAxes` it was called
/// with (`Tripped` for a capped axis, `NotTripped` for one that is not — an arbitrary but distinguishable
/// mapping, never meant to model a real cgroup counter; `Native.Cgroup.limitEvidence`'s own tests cover
/// that), and records it on `LastCapped` — so a test can assert `ProcessGroup`'s sticky cap-tracking
/// (recorded at `Create` and at every `UpdateLimits`, even a failing one) without any real OS container.
/// `updateLimitsFails` lets a test drive the "recorded even though the apply itself failed" case (T-207's
/// own `LimitContractBackend` covers the `Options`-reflection half of that contract; this one covers the
/// evidence-record half).
type internal LimitEvidenceEchoBackend(updateLimitsFails: bool) =
    let mutable lastCapped: CappedAxes option = None
    let mutable updateLimitsCallCount = 0

    /// The `CappedAxes` this backend's `LimitEvidence` was last called with, or `None` before the group
    /// this backend is behind has been torn down (`ProcessGroup.hardRelease` is the only caller).
    member _.LastCapped = lastCapped
    member _.UpdateLimitsCallCount = updateLimitsCallCount

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject

        member _.Spawn(_command) =
            Error(ProcessError.Unsupported "LimitEvidenceEchoBackend does not spawn")

        member _.Track(_spawned) = Ok()

        member _.Adopt(_pid) =
            Error(ProcessError.Unsupported "LimitEvidenceEchoBackend does not adopt")

        member _.AdoptByPid(_pid) =
            Error(ProcessError.Unsupported "LimitEvidenceEchoBackend does not adopt by pid")

        member _.Release(_spawned) = ()
        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(_spawned) = None
        member _.KillChild(_spawned) = ()
        member _.KillTree() = Ok()

        member _.GracefulKillTree (_signal) (_grace) =
            Task.FromResult
                { Soft = SoftDelivery.Sent
                  Drained = true
                  Escalated = false }

        member _.SoftStopScope() = SoftStopScope.Unsupported
        member _.SignalChild(_spawned, _signal) = Ok()
        member _.Members() = Ok []
        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(0, None, None, None, None))

        member _.MemberStats() = Ok []

        member _.UpdateLimits(_limits) =
            updateLimitsCallCount <- updateLimitsCallCount + 1

            if updateLimitsFails then
                Error(ProcessError.ResourceLimit "simulated update failure (T-381 evidence-record contract)")
            else
                Ok()

        member _.LimitEvidence(capped: CappedAxes) : LimitEvidence =
            lastCapped <- Some capped

            let verdict (isCapped: bool) =
                if isCapped then
                    LimitVerdict.Tripped
                else
                    LimitVerdict.NotTripped

            LimitEvidence(verdict capped.Memory, verdict capped.Processes, verdict capped.Cpu)

        member _.HardRelease() = ()

[<TestFixture>]
type LimitsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let assertIoMaxEqual (expected: IoMax option) (actual: IoMax option) =
        match expected, actual with
        | None, None -> ()
        | Some expected, Some actual ->
            Assert.That(actual.Target, Is.EqualTo expected.Target)
            Assert.That(actual.ReadBytesPerSecond, Is.EqualTo expected.ReadBytesPerSecond)
            Assert.That(actual.WriteBytesPerSecond, Is.EqualTo expected.WriteBytesPerSecond)
            Assert.That(actual.ReadOperationsPerSecond, Is.EqualTo expected.ReadOperationsPerSecond)
            Assert.That(actual.WriteOperationsPerSecond, Is.EqualTo expected.WriteOperationsPerSecond)
        | _ -> Assert.Fail "I/O limit presence differs"

    // The pinned cores as an option-of-list, so an assertion can compare against exactly the set it asked
    // for. Deliberately NOT a bare `int list`: NUnit's `Is.EqualTo` cannot disambiguate its `'T` and
    // `'T seq` overloads for an F# list (FS0041), whereas an option is not a sequence, so
    // `Is.EqualTo(Some [ ... ])` resolves cleanly AND stays order-sensitive — which matters here, because
    // the ascending normalization is part of the contract.
    let pinnedCores (limits: ResourceLimits) : int list option =
        limits.CpuAffinity |> Option.map List.ofSeq

    // The lowest `count` CPU cores THIS process is actually allowed to run on, ascending. A Job Object's
    // affinity mask must be a subset of the creating process's own, so the Windows affinity tests derive
    // their request from the live mask instead of assuming cores 0..n exist and are available to it — a
    // constrained CI runner (a container with a cpu-set, an ARM64 host) may have neither. Windows-only, and
    // its callers are already gated on that.
    let lowestAvailableCores (count: int) : int list =
        try
            use current = System.Diagnostics.Process.GetCurrentProcess()
            let mask = uint64 (unativeint current.ProcessorAffinity)

            [ 0..63 ]
            |> List.filter (fun bit -> mask &&& (1UL <<< bit) <> 0UL)
            |> List.truncate count
        with _ ->
            // The affinity mask could not be read (an unsupported platform, a denied query, an already-exited
            // handle). There is then no core this test can honestly claim is pinnable, and the callers treat
            // an empty result as "skip" rather than asserting against a guessed core index.
            []

    // POSIX errno numbers the identity-safe delivery seam tests inject through the syscall closures.
    let ESRCH = 3
    let ENOSYS = 38
    let EPERM = 1

    // Probe whether this kernel/sandbox actually exposes pidfd_open, by pinning our own pid; the
    // real-pidfd integration tests below skip (rather than false-fail) when it does not. This is the
    // signal path's true requirement (pidfd_open + pidfd_send_signal), which is looser than the wait
    // path's `Native.Posix.pidfdActive` (that also needs waitid(P_PIDFD), Linux 5.4+).
    let pidfdAvailable () =
        match Native.Posix.pidfdOpenChecked Environment.ProcessId with
        | Ok fd ->
            Native.Posix.closePidfd fd
            true
        | Error _ -> false

    // A real, long-lived child to pin: `sleep` is POSIX-standard and does not trap SIGTERM, so a
    // delivered SIGTERM kills it.
    let spawnSleeper () : System.Diagnostics.Process =
        let psi = System.Diagnostics.ProcessStartInfo("sleep", "30")
        psi.UseShellExecute <- false

        match System.Diagnostics.Process.Start psi with
        | null -> failwith "Process.Start returned null spawning `sleep 30`"
        | proc -> proc

    // Ensure a spawned sleeper is killed and reaped however a test concluded.
    let killAndReap (child: System.Diagnostics.Process) =
        (try
            if not child.HasExited then
                child.Kill()
                child.WaitForExit()
         with :? InvalidOperationException ->
             // The child already exited/was reaped between the HasExited check and Kill — nothing to do.
             ())

        child.Dispose()

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

    // Drain the merged output-event stream into a list for assertions, resolving `sawMarker` as soon as
    // a line containing `marker` is framed. That signal is what lets a caller wait for the CHILD to have
    // actually spoken before tearing the run down, instead of inferring it from a parent-side proxy.
    // `sawMarker` is resolved on stream end too, so a run whose child never wrote the marker fails on the
    // caller's own assertions rather than hanging on a signal that can no longer arrive.
    let collectUntil (marker: string) (sawMarker: TaskCompletionSource) (items: IAsyncEnumerable<OutputEvent>) =
        task {
            let acc = ResizeArray<OutputEvent>()
            let e = items.GetAsyncEnumerator()
            let mutable more = true

            while more do
                match! e.MoveNextAsync() with
                | true ->
                    acc.Add e.Current

                    if e.Current.Text.Contains marker then
                        sawMarker.TrySetResult() |> ignore
                | false -> more <- false

            do! e.DisposeAsync()
            sawMarker.TrySetResult() |> ignore
            return acc
        }

    [<Test>]
    member _.``ProcessGroupOptions builders set the limits``() =
        let options =
            ProcessGroupOptions()
                .WithMemoryMax(256L * 1024L * 1024L)
                .WithOomGroupKill()
                .WithMaxProcesses(50)
                .WithCpuQuota(1.5)
                .WithCpuTimeMax(TimeSpan.FromSeconds 3.0)
                .WithStopSignal(Signal.Int)

        Assert.That(options.Limits.MemoryMax, Is.EqualTo(Some(256L * 1024L * 1024L)))
        Assert.That(options.Limits.OomGroupKill, Is.True)
        Assert.That(options.Limits.MaxProcesses, Is.EqualTo(Some 50))
        Assert.That(options.Limits.CpuQuota, Is.EqualTo(Some 1.5))
        Assert.That(options.Limits.CpuTimeMax, Is.EqualTo(Some(TimeSpan.FromSeconds 3.0)))
        Assert.That(options.StopSignal, Is.EqualTo Signal.Int)
        Assert.That(ResourceLimits.None.Any, Is.False)

    [<Test>]
    member _.``whole-tree OOM kill is honestly unsupported outside Linux cgroup v2``() =
        if RuntimeInformation.IsOSPlatform OSPlatform.Linux then
            Assert.Ignore "non-cgroup refusal is exercised on Windows and macOS"

        match ProcessGroup.Create(ProcessGroupOptions().WithOomGroupKill()) with
        | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "memory.oom.group")
        | Error other -> Assert.Fail $"expected Unsupported, got {other}"
        | Ok group ->
            (group :> IDisposable).Dispose()
            Assert.Fail "a non-cgroup mechanism must not claim whole-tree OOM-kill support"

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

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuTimeMax TimeSpan.Zero |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromTicks -1L) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``CPU-time alone does not require a whole-tree container``() =
        let limits = ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 1.0)
        Assert.That(limits.Any, Is.True)
        Assert.That(limits.WholeTreeAny, Is.False)

    [<Test>]
    member _.``Windows live updates preserve an unchanged cumulative CPU-time deadline``() =
        if not isWindows then
            Assert.Ignore "Windows Job-time preservation contract."

        match Native.Windows.createWindowsJob () with
        | Error error -> Assert.Fail $"could not create a Job Object: {error}"
        | Ok job ->
            let initial = ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 5.0)

            match Native.Windows.applyWindowsJobLimits job initial with
            | Error message ->
                Native.Windows.closeWindowsHandle job
                Assert.Fail $"could not establish the Job CPU-time limit: {message}"
            | Ok() ->
                let backend = JobObjectBackend(job, initial) :> IContainmentBackend
                let originalDeadline = Native.Windows.queryWindowsJobCpuTimeLimit job

                try
                    let updated = initial.WithMemoryMax(256L * 1024L * 1024L)

                    match backend.UpdateLimits updated with
                    | Error error -> Assert.Fail $"unrelated live limit update failed: {error}"
                    | Ok() ->
                        Assert.That(
                            Native.Windows.queryWindowsJobCpuTimeLimit job,
                            Is.EqualTo originalDeadline,
                            "an unrelated update granted the Job a fresh CPU-time budget"
                        )
                finally
                    backend.HardRelease()

    [<Test>]
    member _.``Windows rollback preserves an unchanged cumulative CPU-time deadline after a late failure``() =
        if not isWindows then
            Assert.Ignore "Windows Job-time rollback contract."

        match Native.Windows.createWindowsJob () with
        | Error error -> Assert.Fail $"could not create a Job Object: {error}"
        | Ok job ->
            let initial = ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 30.0)

            match Native.Windows.applyWindowsJobLimits job initial with
            | Error message ->
                Native.Windows.closeWindowsHandle job
                Assert.Fail $"could not establish the Job CPU-time limit: {message}"
            | Ok() ->
                let originalDeadline = Native.Windows.queryWindowsJobCpuTimeLimit job

                try
                    Native.Windows.cpuRateWriteErrorForTests <- Some 5

                    let attempted = initial.WithMemoryMax(256L * 1024L * 1024L).WithCpuQuota 1.0

                    match Native.Windows.applyWindowsJobLimitsPreservingCpuTime job attempted with
                    | Ok() -> Assert.Fail "the injected CPU-rate failure did not fail the update"
                    | Error _ ->
                        Assert.That(
                            Native.Windows.queryWindowsJobCpuTimeLimit job,
                            Is.EqualTo originalDeadline,
                            "rollback granted a fresh cumulative CPU-time budget"
                        )
                finally
                    Native.Windows.cpuRateWriteErrorForTests <- None
                    Native.Windows.closeWindowsHandle job

    [<Test>]
    member _.``Windows rollback restores an absolute CPU-time deadline after a failed time-limit change``() : Task =
        if not isWindows then
            Assert.Ignore "Windows Job-time rollback contract."

        task {
            match Native.Windows.createWindowsJob () with
            | Error error -> Assert.Fail $"could not create a Job Object: {error}"
            | Ok job ->
                let initial = ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 30.0)

                match Native.Windows.applyWindowsJobLimits job initial with
                | Error message ->
                    Native.Windows.closeWindowsHandle job
                    Assert.Fail $"could not establish the Job CPU-time limit: {message}"
                | Ok() ->
                    let backend = JobObjectBackend(job, initial) :> IContainmentBackend

                    use group =
                        ProcessGroup.FromBackend(
                            backend,
                            ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromSeconds 30.0)
                        )

                    let busy =
                        Command.create "powershell.exe"
                        |> Command.args [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; "while ($true) { }" ]

                    match! group.StartAsync busy with
                    | Error error -> Assert.Fail $"CPU-bound child failed to start: {error}"
                    | Ok running ->
                        use running = running
                        let accumulation = Diagnostics.Stopwatch.StartNew()
                        let mutable accumulated = false

                        while not accumulated && accumulation.Elapsed < TimeSpan.FromSeconds 10.0 do
                            match group.Stats() with
                            | Ok stats ->
                                accumulated <-
                                    stats.TotalCpuTime
                                    |> Option.exists (fun time -> time >= TimeSpan.FromMilliseconds 250.0)
                            | Error error -> Assert.Fail $"could not query Job accounting: {error}"

                            if not accumulated then
                                do! Task.Delay 20

                        if not accumulated then
                            Assert.Fail "the CPU-bound child did not accumulate enough Job user time"

                        match group.Suspend() with
                        | Error error -> Assert.Fail $"could not suspend the Job before rollback: {error}"
                        | Ok() -> ()

                        let originalDeadline = Native.Windows.queryWindowsJobCpuTimeLimit job

                        try
                            Native.Windows.cpuRateWriteErrorForTests <- Some 5

                            let attempted =
                                ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 60.0).WithCpuQuota 1.0

                            match group.UpdateLimits attempted with
                            | Ok() -> Assert.Fail "the injected CPU-rate failure did not fail the update"
                            | Error(ProcessError.ResourceLimit _) ->
                                Assert.That(
                                    Native.Windows.queryWindowsJobCpuTimeLimit job,
                                    Is.EqualTo originalDeadline,
                                    "rollback rebased the prior absolute deadline as a fresh budget"
                                )
                            | Error error -> Assert.Fail $"expected ResourceLimit, got {error}"
                        finally
                            Native.Windows.cpuRateWriteErrorForTests <- None
                            group.Resume() |> ignore
                            running.Kill()
        }
        :> Task

    [<Test>]
    member _.``Windows refuses a custom group stop signal instead of silently replacing it``() =
        if not isWindows then
            Assert.Ignore "Windows-specific representability contract."

        match ProcessGroup.Create(ProcessGroupOptions().WithStopSignal Signal.Int) with
        | Error(ProcessError.Unsupported _) -> ()
        | Error error -> Assert.Fail $"expected Unsupported, got {error}"
        | Ok group ->
            (group :> IDisposable).Dispose()
            Assert.Fail "a custom Windows group stop signal was silently accepted"

    [<Test>]
    member _.``CPU-time limit terminates a CPU-bound run``() : Task =
        task {
            let options = ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromMilliseconds 500.0)

            match ProcessGroup.Create options with
            | Error error -> Assert.Fail $"CPU-time limited group creation failed: {error}"
            | Ok group ->
                use group = group

                let busy =
                    if isWindows then
                        Command.create "powershell.exe"
                        |> Command.args [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; "while ($true) { }" ]
                    else
                        Command.create "/bin/sh" |> Command.args [ "-c"; "while :; do :; done" ]

                match! group.StartAsync busy with
                | Error error -> Assert.Fail $"CPU-bound child failed to start: {error}"
                | Ok running ->
                    use running = running
                    let completion = running.ExitTask
                    let! winner = Task.WhenAny(completion :> Task, Task.Delay(TimeSpan.FromSeconds 15.0))

                    if not (Object.ReferenceEquals(winner, completion)) then
                        Assert.Fail "CPU-time limit did not terminate the CPU-bound child within 15 seconds"

                    let! outcome = completion

                    if isWindows then
                        match outcome with
                        | Outcome.Exited 0 -> Assert.Fail "CPU-bound child exited cleanly despite the CPU-time limit"
                        | _ -> ()
                    else
                        match outcome with
                        | Outcome.Signalled(Some _) -> ()
                        | other -> Assert.Fail $"expected POSIX RLIMIT_CPU to surface a signal outcome, got {other}"
        }
        :> Task

    [<Test>]
    member _.``POSIX CPU-time wrapper preserves NotFound and PreferLocal resolution``() : Task =
        if isWindows then
            Assert.Ignore "POSIX-specific spawn-resolution contract."

        task {
            let options = ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromSeconds 2.0)

            match ProcessGroup.Create options with
            | Error error -> Assert.Fail $"CPU-time limited group creation failed: {error}"
            | Ok group ->
                use group = group

                match! group.StartAsync(Command.create "processkit-cpu-limit-missing-program") with
                | Error(ProcessError.NotFound _) -> ()
                | Error error -> Assert.Fail $"expected typed NotFound before the shell wrapper, got {error}"
                | Ok running ->
                    use _running = running
                    Assert.Fail "a missing CPU-limited target unexpectedly spawned"

                let root =
                    Path.Combine(Path.GetTempPath(), $"processkit-cpu-prefer-{Guid.NewGuid():N}")

                Directory.CreateDirectory root |> ignore

                try
                    let program = "processkit-cpu-prefer-local"
                    let executable = Path.Combine(root, program)
                    File.WriteAllText(executable, "#!/bin/sh\nprintf local")

                    File.SetUnixFileMode(
                        executable,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let command = Command.create program |> Command.preferLocal root |> Command.envClear

                    match! group.StartAsync command with
                    | Error error -> Assert.Fail $"PreferLocal CPU-limited child failed to start: {error}"
                    | Ok running ->
                        use running = running

                        match! running.OutputStringAsync() with
                        | Error error -> Assert.Fail $"PreferLocal CPU-limited child failed: {error}"
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "local")
                finally
                    Directory.Delete(root, true)
        }
        :> Task

    // `WithCpuTimeMax` alone is NOT `WholeTreeAny` (see `Capabilities.chooseUsing`), so a group asking for
    // only it always selects `Mechanism.ProcessGroup` — never the cgroup backend, even on Linux with
    // cgroup v2 available — and spawns through `Native.Posix.withCpuTimeLimit`'s `/bin/sh` `RLIMIT_CPU`
    // shim (T-376/R-01). `Command.Arg0` must be refused there rather than silently applied to the shim's
    // own `argv[0]` while the real program (reached only through the shim's `exec "$@"`) keeps its
    // unmodified name.
    [<Test>]
    member _.``Arg0 combined with a POSIX CpuTimeMax run is a typed Unsupported, not a silent misapplication to the RLIMIT_CPU shim``
        ()
        : Task =
        task {
            if isWindows then
                Assert.Ignore "Arg0/CpuTimeMax are both POSIX-only here"

            let options = ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromSeconds 5.0)

            match ProcessGroup.Create options with
            | Error error -> Assert.Fail $"CPU-time limited group creation failed: {error}"
            | Ok group ->
                use group = group

                let command =
                    Command.create "/bin/sh"
                    |> Command.arg0 "override"
                    |> Command.args [ "-c"; "printf %s \"$0\"" ]

                match! group.StartAsync command with
                | Error(ProcessError.Unsupported _) -> ()
                | Error error -> Assert.Fail $"expected ProcessError.Unsupported, got {error}"
                | Ok running ->
                    use _running = running
                    Assert.Fail "Arg0 combined with CpuTimeMax should be Unsupported, not silently accepted"
        }
        :> Task

    [<Test>]
    member _.``ResourceLimits.WithCpuQuota rejects infinities and a value that would overflow the cgroup quota``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuQuota Double.PositiveInfinity |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuQuota Double.NegativeInfinity |> ignore)
        )
        |> ignore

        // Native.Cgroup.cpuMaxValue rounds cores * 100_000 (microseconds) into an int64; a value that
        // makes that product reach or exceed Int64.MaxValue must be rejected up front, uniformly, rather
        // than only failing later and only on the Linux cgroup backend.
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ResourceLimits.None.WithCpuQuota 1e20 |> ignore))
        |> ignore

        // A large-but-safe value (well under the overflow boundary) is still accepted.
        let accepted = ResourceLimits.None.WithCpuQuota 1_000_000.0
        Assert.That(accepted.CpuQuota, Is.EqualTo(Some 1_000_000.0))

    [<Test>]
    member _.``ProcessGroupOptions.WithCpuQuota rejects the same invalid values as ResourceLimits``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ProcessGroupOptions().WithCpuQuota Double.PositiveInfinity |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> ProcessGroupOptions().WithCpuQuota 1e20 |> ignore))
        |> ignore

        let accepted = ProcessGroupOptions().WithCpuQuota 2.0
        Assert.That(accepted.Limits.CpuQuota, Is.EqualTo(Some 2.0))

    [<Test>]
    member _.``ProcessGroupOptions.WithShutdownTimeout rejects a negative window but accepts zero``() =
        ProcessGroupOptions().WithShutdownTimeout TimeSpan.Zero |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ProcessGroupOptions().WithShutdownTimeout(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``ProcessGroupOptions.WithStopSignal rejects hard kill and non-deliverable raw numbers``() =
        let commandKill =
            try
                Command("tool").StopSignal Signal.Kill |> ignore
                failwith "Command.StopSignal accepted Signal.Kill"
            with :? ArgumentException as error ->
                error

        let groupKill =
            try
                ProcessGroupOptions().WithStopSignal Signal.Kill |> ignore
                failwith "ProcessGroupOptions.WithStopSignal accepted Signal.Kill"
            with :? ArgumentException as error ->
                error

        Assert.That(groupKill.Message, Is.EqualTo commandKill.Message)
        Assert.That(groupKill.ParamName, Is.EqualTo commandKill.ParamName)

        let commandRaw =
            try
                Command("tool").StopSignal(Signal.Other 0) |> ignore
                failwith "Command.StopSignal accepted Signal.Other 0"
            with :? ArgumentOutOfRangeException as error ->
                error

        let groupRaw =
            try
                ProcessGroupOptions().WithStopSignal(Signal.Other 0) |> ignore
                failwith "ProcessGroupOptions.WithStopSignal accepted Signal.Other 0"
            with :? ArgumentOutOfRangeException as error ->
                error

        Assert.That(groupRaw.Message, Is.EqualTo commandRaw.Message)
        Assert.That(groupRaw.ParamName, Is.EqualTo commandRaw.ParamName)

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

    // ---- Job Object UI restrictions (Windows-only whole-tree desktop restrictions) ---------------

    [<Test>]
    member _.``WithUiRestrictions records the flag set and counts as a limit``() =
        let restrictions =
            WindowsUiRestrictions.ReadClipboard
            ||| WindowsUiRestrictions.WriteClipboard
            ||| WindowsUiRestrictions.ExitWindows

        let options = ProcessGroupOptions().WithUiRestrictions restrictions
        Assert.That(options.Limits.UiRestrictions, Is.EqualTo restrictions)
        // Restrictions alone still need the limit-capable mechanism, so they must make `Any` true —
        // otherwise `ProcessGroup.Create` would skip the apply and hand back an unrestricted Job.
        Assert.That(options.Limits.Any, Is.True)
        Assert.That(ResourceLimits.None.UiRestrictions, Is.EqualTo WindowsUiRestrictions.None)
        Assert.That(ResourceLimits.None.Any, Is.False)

    [<Test>]
    member _.``WithUiRestrictions composes with the resource caps and can be cleared again``() =
        let both =
            ProcessGroupOptions().WithMemoryMax(128L * 1024L * 1024L).WithUiRestrictions(WindowsUiRestrictions.Desktop)

        Assert.That(both.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))
        Assert.That(both.Limits.UiRestrictions, Is.EqualTo WindowsUiRestrictions.Desktop)

        // Replace semantics, like every other dimension: `None` clears the set rather than merging.
        let cleared = both.WithUiRestrictions WindowsUiRestrictions.None
        Assert.That(cleared.Limits.UiRestrictions, Is.EqualTo WindowsUiRestrictions.None)
        Assert.That(cleared.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))

    [<Test>]
    member _.``WithUiRestrictions rejects bits outside the defined set``() =
        // An undefined bit has no meaning to SetInformationJobObject; refusing it at the builder keeps an
        // out-of-range cast from being written to the Job as an unknown restriction class.
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                ResourceLimits.None.WithUiRestrictions(enum<WindowsUiRestrictions> 0x1000)
                |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                ProcessGroupOptions().WithUiRestrictions(enum<WindowsUiRestrictions> -1)
                |> ignore)
        )
        |> ignore

        // The whole defined set is of course accepted.
        let all = ResourceLimits.None.WithUiRestrictions WindowsUiRestrictions.All
        Assert.That(all.UiRestrictions, Is.EqualTo WindowsUiRestrictions.All)

    [<Test>]
    member _.``UI restrictions round-trip through the Job Object (Windows)``() =
        if not isWindows then
            Assert.Ignore "Job Object UI restrictions are Windows-only; the POSIX refusal is asserted below."
        else
            // The real apply path `ProcessGroup.Create`/`UpdateLimits` uses, asserted against the Job's own
            // read-back: the UI behaviour itself (a blocked clipboard read) is not observable from a test,
            // but the configuration the kernel now holds is.
            match Native.Windows.createWindowsJob () with
            | Error error -> Assert.Fail $"could not create a Job Object: {error}"
            | Ok job ->
                try
                    let requested =
                        WindowsUiRestrictions.ReadClipboard
                        ||| WindowsUiRestrictions.Desktop
                        ||| WindowsUiRestrictions.ExitWindows

                    let limits = ResourceLimits.None.WithUiRestrictions requested

                    match Native.Windows.applyWindowsJobLimits job limits with
                    | Error message -> Assert.Fail $"applying UI restrictions failed: {message}"
                    | Ok() ->
                        match Native.Windows.queryWindowsUiRestrictions job with
                        | None -> Assert.Fail "the Job's UI restrictions could not be read back"
                        | Some inForce ->
                            Assert.That(inForce, Is.EqualTo(uint32 (int requested)))

                            // Replace semantics on a LIVE Job: dropping the set lifts the restrictions
                            // rather than leaving the previous ones in force.
                            match Native.Windows.applyWindowsJobLimits job ResourceLimits.None with
                            | Error message -> Assert.Fail $"clearing UI restrictions failed: {message}"
                            | Ok() ->
                                match Native.Windows.queryWindowsUiRestrictions job with
                                | None -> Assert.Fail "the Job's UI restrictions could not be read back after clearing"
                                | Some cleared -> Assert.That(cleared, Is.EqualTo 0u)
                finally
                    Native.Windows.closeWindowsHandle job

    [<Test>]
    member _.``a group created with UI restrictions still runs children, and can update or clear them (Windows)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "Job Object UI restrictions are Windows-only; the refusal is asserted below."
            else
                // The public path end to end: Create applies them alongside a resource cap, the group
                // still works, and a live UpdateLimits replaces (then clears) the set with `Options`
                // following what is actually enforced.
                let options =
                    ProcessGroupOptions()
                        .WithMaxProcesses(64)
                        .WithUiRestrictions(WindowsUiRestrictions.ReadClipboard ||| WindowsUiRestrictions.ExitWindows)

                match ProcessGroup.Create options with
                | Error error -> Assert.Fail $"a Job Object should accept UI restrictions, got {error}"
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.JobObject)

                    Assert.That(
                        group.Options.Limits.UiRestrictions,
                        Is.EqualTo(WindowsUiRestrictions.ReadClipboard ||| WindowsUiRestrictions.ExitWindows)
                    )

                    // A restricted tree is still a working tree: an ordinary console child is unaffected.
                    match! runInGroup group with
                    | Ok result -> Assert.That(result.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"a child in a UI-restricted group should still run, got {error}"

                    let widened =
                        ResourceLimits.None.WithMaxProcesses(64).WithUiRestrictions WindowsUiRestrictions.All

                    match group.UpdateLimits widened with
                    | Ok() -> Assert.That(group.Options.Limits.UiRestrictions, Is.EqualTo WindowsUiRestrictions.All)
                    | Error error -> Assert.Fail $"updating UI restrictions on a live Job should succeed, got {error}"

                    // Replace semantics through the public verb too: a set without restrictions lifts them.
                    match group.UpdateLimits(ResourceLimits.None.WithMaxProcesses 64) with
                    | Ok() -> Assert.That(group.Options.Limits.UiRestrictions, Is.EqualTo WindowsUiRestrictions.None)
                    | Error error -> Assert.Fail $"clearing UI restrictions on a live Job should succeed, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a group asking for UI restrictions is honestly Unsupported off Windows``() =
        if isWindows then
            Assert.Ignore "The Unsupported gate is the off-Windows behaviour; Windows applies the restrictions."
        else
            // No POSIX primitive has this concept at all — so it is `Unsupported`, distinct from the
            // `ResourceLimit` used when a cap that exists in principle merely cannot be enforced here.
            // Never a group that quietly runs its tree with no restrictions.
            let options =
                ProcessGroupOptions().WithUiRestrictions WindowsUiRestrictions.ReadClipboard

            match ProcessGroup.Create options with
            | Error(ProcessError.Unsupported _) -> Assert.Pass()
            | Ok group ->
                (group :> IDisposable).Dispose()
                Assert.Fail "a group with UI restrictions must not be created off Windows"
            | Error other -> Assert.Fail $"expected Unsupported for UI restrictions off Windows, got {other}"

    [<Test>]
    member _.``UpdateLimits carrying UI restrictions is honestly Unsupported on the POSIX backends``() =
        // Backend-level (runs on every platform): both non-Job backends must refuse the update rather
        // than apply the resource half and silently drop the restriction half.
        let limits =
            ResourceLimits.None.WithMemoryMax(64L * 1024L * 1024L).WithUiRestrictions(WindowsUiRestrictions.All)

        let processGroup: IContainmentBackend = ProcessGroupBackend()

        match processGroup.UpdateLimits limits with
        | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Is.Not.Empty)
        | other -> Assert.Fail $"expected Unsupported from the process-group backend, got {other}"

        // The cgroup backend refuses before it touches a single controller file, so a non-existent path
        // is enough to prove the gate runs first.
        let cgroup: IContainmentBackend =
            CgroupBackend(Path.Combine(Path.GetTempPath(), $"processkit-ui-{Guid.NewGuid():N}"))

        match cgroup.UpdateLimits limits with
        | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Is.Not.Empty)
        | other -> Assert.Fail $"expected Unsupported from the cgroup backend, got {other}"

    // ---- CPU affinity (Job Object affinity mask / cgroup v2 cpuset.cpus) -------------------------

    [<Test>]
    member _.``WithCpuAffinity records the pinned cores in ascending order and counts as a limit``() =
        // The set is normalized, so the same pin written in any order is the same limit — and a pin alone
        // must make `Any` true, or `ProcessGroup.Create` would hand back an unpinned container.
        let options = ProcessGroupOptions().WithCpuAffinity [ 3; 0; 2 ]
        Assert.That(pinnedCores options.Limits, Is.EqualTo(Some [ 0; 2; 3 ]))
        Assert.That(options.Limits.Any, Is.True)

        // The default pins nothing at all.
        Assert.That(ResourceLimits.None.CpuAffinity |> Option.isNone, Is.True)
        Assert.That(ResourceLimits.None.Any, Is.False)

    [<Test>]
    member _.``the CpuAffinity read-back is a fresh copy, so the limit set cannot be mutated through it``() =
        // A caller holding the returned list must not be able to reach into the (immutable) limit set.
        let limits = ResourceLimits.None.WithCpuAffinity [ 0; 1 ]

        match limits.CpuAffinity, limits.CpuAffinity with
        | Some first, Some second -> Assert.That(first, Is.Not.SameAs second)
        | _ -> Assert.Fail "the pinned cores should have been recorded"

        // Two independent reads still report exactly the same pin — a copy, not a different answer.
        Assert.That(pinnedCores limits, Is.EqualTo(Some [ 0; 1 ]))

    [<Test>]
    member _.``WithCpuAffinity composes with the resource caps and replaces a previous pin``() =
        let both =
            ProcessGroupOptions().WithMemoryMax(128L * 1024L * 1024L).WithCpuAffinity [ 1 ]

        Assert.That(both.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))
        Assert.That(pinnedCores both.Limits, Is.EqualTo(Some [ 1 ]))

        // Replace semantics, like every other dimension: the new set replaces rather than merges, and the
        // other caps ride along untouched.
        let repinned = both.WithCpuAffinity [ 0; 1 ]
        Assert.That(pinnedCores repinned.Limits, Is.EqualTo(Some [ 0; 1 ]))
        Assert.That(repinned.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))

        // And a set built without a pin genuinely carries none — this is how a live update lifts one.
        let unpinned = ResourceLimits.None.WithMemoryMax(128L * 1024L * 1024L)
        Assert.That(unpinned.CpuAffinity |> Option.isNone, Is.True)

    [<Test>]
    member _.``WithCpuAffinity rejects a null, empty, negative, or repeated core set``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () -> ResourceLimits.None.WithCpuAffinity(Unchecked.defaultof<seq<int>>) |> ignore)
        )
        |> ignore

        // No core to run on could never let anything run — a misconfiguration, not a limit.
        Assert.Throws<ArgumentException>(Action(fun () -> ResourceLimits.None.WithCpuAffinity [] |> ignore))
        |> ignore

        // Cores are numbered from 0; a negative index has no meaning in either platform encoding (and on
        // Windows would shift a mask bit out of range).
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithCpuAffinity [ 0; -1 ] |> ignore)
        )
        |> ignore

        // An affinity set is a set: a repeat is a typo in a generated list, not an intent.
        Assert.Throws<ArgumentException>(Action(fun () -> ResourceLimits.None.WithCpuAffinity [ 1; 0; 1 ] |> ignore))
        |> ignore

    [<Test>]
    member _.``ProcessGroupOptions.WithCpuAffinity rejects the same invalid sets as ResourceLimits``() =
        Assert.Throws<ArgumentException>(Action(fun () -> ProcessGroupOptions().WithCpuAffinity [] |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ProcessGroupOptions().WithCpuAffinity [ -3 ] |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(Action(fun () -> ProcessGroupOptions().WithCpuAffinity [ 2; 2 ] |> ignore))
        |> ignore

        let accepted = ProcessGroupOptions().WithCpuAffinity [ 0 ]
        Assert.That(pinnedCores accepted.Limits, Is.EqualTo(Some [ 0 ]))

    [<Test>]
    member _.``the cgroup cpuset rendering collapses consecutive cores into ranges``() =
        // `cpuset.cpus` speaks a comma-separated list of single cores and `lo-hi` runs, and prints back in
        // exactly that shape — pure string logic, so it is asserted on every platform, not just Linux.
        Assert.That(Native.Cgroup.formatCpuList [ 0 ], Is.EqualTo "0")
        Assert.That(Native.Cgroup.formatCpuList [ 0; 1 ], Is.EqualTo "0-1")
        Assert.That(Native.Cgroup.formatCpuList [ 0; 2; 3 ], Is.EqualTo "0,2-3")
        Assert.That(Native.Cgroup.formatCpuList [ 1; 3; 5 ], Is.EqualTo "1,3,5")
        Assert.That(Native.Cgroup.formatCpuList [ 0; 1; 2; 3; 8; 9 ], Is.EqualTo "0-3,8-9")
        // Unsorted / repeated input still renders canonically (the builder already normalizes, so this is
        // the defensive half of the contract rather than a path production takes).
        Assert.That(Native.Cgroup.formatCpuList [ 3; 0; 2; 3 ], Is.EqualTo "0,2-3")

    [<Test>]
    member _.``a Job affinity mask is a bitmask, and refuses a core it cannot express``() =
        // Pure bit math, so it runs everywhere. The mask is one pointer-sized word covering a SINGLE
        // Windows processor group, so an index at or beyond its width has no representation — and must be
        // refused rather than silently wrapped onto some other, wrong core.
        let width = IntPtr.Size * 8

        match Native.Windows.windowsAffinityMask [ 0 ] with
        | Ok mask -> Assert.That(mask, Is.EqualTo 1un)
        | Error message -> Assert.Fail $"core 0 must be expressible, got {message}"

        match Native.Windows.windowsAffinityMask [ 0; 2 ] with
        | Ok mask -> Assert.That(mask, Is.EqualTo 5un)
        | Error message -> Assert.Fail $"cores 0 and 2 must be expressible, got {message}"

        match Native.Windows.windowsAffinityMask [ width - 1 ] with
        | Ok mask -> Assert.That(mask, Is.EqualTo(1un <<< (width - 1)))
        | Error message -> Assert.Fail $"the top mask bit must be expressible, got {message}"

        match Native.Windows.windowsAffinityMask [ 0; width ] with
        | Error message -> Assert.That(message, Does.Contain(string width))
        | Ok mask -> Assert.Fail $"core {width} has no bit in a {width}-bit mask, got {mask}"

    [<Test>]
    member _.``CPU affinity round-trips through the Job Object (Windows)``() =
        if not isWindows then
            Assert.Ignore "The Job Object affinity mask is the Windows encoding; the other platforms are covered below."
        else
            // Pin to cores this process is actually allowed to run on: a Job's affinity mask must be a
            // subset of its own, so deriving the request from the live mask keeps the test honest on a
            // constrained CI runner instead of assuming cores 0..n exist and are available.
            match lowestAvailableCores 2 with
            | [] -> Assert.Ignore "could not read this process's own affinity mask to derive a pinnable core"
            | cores ->
                match Native.Windows.createWindowsJob () with
                | Error error -> Assert.Fail $"could not create a Job Object: {error}"
                | Ok job ->
                    try
                        let limits = ResourceLimits.None.WithCpuAffinity cores

                        match Native.Windows.applyWindowsJobLimits job limits with
                        | Error message -> Assert.Fail $"applying a CPU-affinity pin failed: {message}"
                        | Ok() ->
                            let expected = cores |> List.fold (fun mask core -> mask ||| (1un <<< core)) 0un

                            match Native.Windows.queryWindowsJobAffinity job with
                            | None -> Assert.Fail "the Job's affinity mask could not be read back"
                            | Some inForce ->
                                Assert.That(inForce, Is.EqualTo expected)

                                // Replace semantics on a LIVE Job: dropping the pin clears
                                // JOB_OBJECT_LIMIT_AFFINITY, so the tree may use every core again rather
                                // than staying stuck on the previous mask.
                                match Native.Windows.applyWindowsJobLimits job ResourceLimits.None with
                                | Error message -> Assert.Fail $"clearing the CPU-affinity pin failed: {message}"
                                | Ok() ->
                                    Assert.That(Native.Windows.queryWindowsJobAffinity job |> Option.isNone, Is.True)
                    finally
                        Native.Windows.closeWindowsHandle job

    [<Test>]
    member _.``a group created with a CPU-affinity pin still runs children, and can update or clear it (Windows)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "The Job Object holds the pin on Windows; the other platforms are covered below."
            else
                match lowestAvailableCores 2 with
                | [] -> Assert.Ignore "could not read this process's own affinity mask to derive a pinnable core"
                | cores ->
                    // The public path end to end: Create pins alongside a resource cap, the pinned tree
                    // still works, and a live UpdateLimits narrows then lifts the pin with `Options`
                    // following what is actually enforced.
                    let options = ProcessGroupOptions().WithMaxProcesses(64).WithCpuAffinity cores

                    match ProcessGroup.Create options with
                    | Error error -> Assert.Fail $"a Job Object should accept a CPU-affinity pin, got {error}"
                    | Ok group ->
                        use group = group
                        Assert.That(group.Mechanism, Is.EqualTo Mechanism.JobObject)

                        Assert.That(pinnedCores group.Options.Limits, Is.EqualTo(Some cores))

                        // A pinned tree is still a working tree.
                        match! runInGroup group with
                        | Ok result -> Assert.That(result.Stdout, Does.Contain "limited")
                        | Error error -> Assert.Fail $"a child in a pinned group should still run, got {error}"

                        // Narrow the pin to a single core on the live Job.
                        let narrowed =
                            ResourceLimits.None.WithMaxProcesses(64).WithCpuAffinity [ List.head cores ]

                        match group.UpdateLimits narrowed with
                        | Ok() -> Assert.That(pinnedCores group.Options.Limits, Is.EqualTo(Some [ List.head cores ]))
                        | Error error -> Assert.Fail $"narrowing the pin on a live Job should succeed, got {error}"

                        // Replace semantics through the public verb too: a set without a pin lifts it.
                        match group.UpdateLimits(ResourceLimits.None.WithMaxProcesses 64) with
                        | Ok() -> Assert.That(group.Options.Limits.CpuAffinity |> Option.isNone, Is.True)
                        | Error error -> Assert.Fail $"clearing the pin on a live Job should succeed, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a CPU-affinity pin no Job mask can express is refused before anything is applied (Windows)``() =
        if not isWindows then
            Assert.Ignore "The mask-width ceiling is a Job Object property; see the pure mask test above."
        else
            // A core index beyond the mask's width is a typed refusal at `Create`, not a pin quietly
            // dropped (nor an index wrapped onto a different core). Asserted through the public verb so the
            // whole chain — builder accepts, backend refuses — is covered.
            let options = ProcessGroupOptions().WithCpuAffinity [ IntPtr.Size * 8 ]

            match ProcessGroup.Create options with
            | Error(ProcessError.ResourceLimit detail) -> Assert.That(detail, Is.Not.Empty)
            | Ok group ->
                (group :> IDisposable).Dispose()
                Assert.Fail "a core index outside the Job affinity mask must not produce a group"
            | Error other -> Assert.Fail $"expected ResourceLimit for an inexpressible core, got {other}"

    [<Test>]
    member _.``a group asking for a CPU-affinity pin uses a limit-capable mechanism or fails fast``() : Task =
        task {
            // Same contract as the other caps: a pin needs a Job Object or a cgroup v2 `cpuset`, and where
            // there is none the group must fail fast rather than run its tree across every core.
            //
            // On Windows the mask must be a subset of this process's own affinity, so derive the core
            // rather than assuming core 0 is available to it. Off Windows the affinity mask cannot be read
            // (`lowestAvailableCores` reports none), and the expectation there is a refusal — or, on Linux,
            // either outcome — so any valid index will do.
            let requested =
                match lowestAvailableCores 1 with
                | [] -> [ 0 ]
                | cores -> cores

            let options = ProcessGroupOptions().WithCpuAffinity requested
            let result = ProcessGroup.Create options

            if isWindows then
                match result with
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.JobObject)

                    match! runInGroup group with
                    | Ok r -> Assert.That(r.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"{error}"
                | Error error -> Assert.Fail $"a Job Object should be able to pin {requested}, got {error}"
            elif isMacOs then
                // No whole-tree primitive at all — and macOS has no CPU-affinity API to fall back on
                // either, so this can only ever be an honest refusal.
                match result with
                | Error(ProcessError.ResourceLimit _) -> Assert.Pass()
                | Ok group ->
                    (group :> IDisposable).Dispose()
                    Assert.Fail "macOS cannot pin a tree to cores; the group must not be created"
                | other -> Assert.Fail $"expected ResourceLimit on macOS, got {other}"
            else
                // Linux: a cgroup v2 at the real root enforces the pin through `cpuset.cpus`. Unlike
                // memory/pids/cpu, `cpuset` is a controller a hierarchy may simply not delegate, so even
                // the privileged CI leg cannot be required to reach the enforcing path — both outcomes are
                // acceptable, and neither is a silently-unpinned group.
                match result with
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    match! runInGroup group with
                    | Ok r -> Assert.That(r.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"{error}"
                | Error(ProcessError.ResourceLimit _) -> Assert.Pass()
                | Error other -> Assert.Fail $"expected CgroupV2 or a typed ResourceLimit, got {other}"
        }
        :> Task

    [<Test>]
    member _.``UpdateLimits carrying a CPU-affinity pin is an honest typed ResourceLimit on the POSIX backend``() =
        // Backend-level, so it runs on every platform: the process-group mechanism has no whole-tree
        // primitive to pin with, so a live update asking for one must be refused the same typed way
        // `Create` refuses — a `ResourceLimit` (the cap exists as a concept, this mechanism just cannot
        // enforce it), never a silent no-op that leaves the tree on every core while `Options` claims a pin.
        let backend: IContainmentBackend = ProcessGroupBackend()

        match backend.UpdateLimits(ResourceLimits.None.WithCpuAffinity [ 0; 1 ]) with
        | Error(ProcessError.ResourceLimit detail) -> Assert.That(detail, Is.Not.Empty)
        | other -> Assert.Fail $"expected ProcessError.ResourceLimit, got {other}"

    [<Test>]
    member _.``UpdateLimits on the POSIX process-group backend is an honest typed ResourceLimit``() =
        // The process-group mechanism has no whole-tree limit primitive, so a LIVE limit update must be
        // refused the same typed way `Create` refuses to build a limited group over it — never a silent
        // no-op pretending caps were applied. Backend-level, so it runs on every platform.
        let backend: IContainmentBackend = ProcessGroupBackend()
        let newLimits = ResourceLimits.None.WithMemoryMax(128L * 1024L * 1024L)

        match backend.UpdateLimits newLimits with
        | Error(ProcessError.ResourceLimit detail) -> Assert.That(detail, Is.Not.Empty)
        | other -> Assert.Fail $"expected ProcessError.ResourceLimit, got {other}"

    [<Test>]
    member _.``UpdateLimits on a limit-free group (POSIX process-group mechanism) returns ResourceLimit``() : Task =
        task {
            if isWindows then
                Assert.Ignore "a limit-free group on Windows is still a Job Object, which CAN update limits"
            else
                // A no-limits group uses the POSIX process-group mechanism, which cannot enforce
                // whole-tree limits — a live update is refused with the same typed error, through the
                // public verb (not just the backend).
                match ProcessGroup.Create() with
                | Error error -> Assert.Fail $"{error}"
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.ProcessGroup)

                    match group.UpdateLimits(ResourceLimits.None.WithMaxProcesses 32) with
                    | Error(ProcessError.ResourceLimit _) -> ()
                    | other -> Assert.Fail $"expected ResourceLimit updating a process-group group, got {other}"
        }
        :> Task

    [<Test>]
    member _.``UpdateLimits rejects null before lifecycle state backend and sticky evidence``() =
        let backend = LimitEvidenceEchoBackend(updateLimitsFails = false)
        let initialOptions = ProcessGroupOptions()

        let group = ProcessGroup.FromBackend(backend :> IContainmentBackend, initialOptions)

        // `Unchecked.defaultof` supplies the same null reference a C# caller can pass to this public member.
        let error =
            Assert.Throws<ArgumentNullException>(
                Action(fun () -> group.UpdateLimits(Unchecked.defaultof<ResourceLimits>) |> ignore)
            )

        match error with
        | null -> Assert.Fail "Assert.Throws did not return an exception"
        | error -> Assert.That(error.ParamName, Is.EqualTo "limits")

        Assert.That(backend.UpdateLimitsCallCount, Is.Zero, "the backend must not observe a rejected call")
        Assert.That(group.Options, Is.SameAs initialOptions, "the live options snapshot must not be replaced")

        (group :> IDisposable).Dispose()

        match backend.LastCapped with
        | Some capped ->
            Assert.That(
                (capped.Memory, capped.Processes, capped.Cpu, capped.CpuTimeMax),
                Is.EqualTo((false, false, false, false)),
                "a rejected call must not add any axis to sticky limit evidence"
            )
        | None -> Assert.Fail "teardown did not capture sticky limit evidence"

    [<Test>]
    member _.``UpdateLimits re-applies caps to a live limit-capable group and refreshes the Options snapshot``
        ()
        : Task =
        task {
            let options =
                ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(64)

            // Only meaningful where a real limit container exists: Windows (always a Job Object) and
            // Linux at the real cgroup root. Elsewhere `Create` fails fast (macOS, unprivileged Linux),
            // so there is nothing live to update and the test is not applicable.
            match ProcessGroup.Create options with
            | Error(ProcessError.ResourceLimit _) when not isWindows ->
                Assert.Ignore "no live limit-capable container here (macOS, or Linux not at the real cgroup root)"
            | Error error -> Assert.Fail $"{error}"
            | Ok group ->
                use group = group

                // A different cap set: tighten memory, add a CPU quota, and DROP the process cap — so the
                // None-clears-to-unbounded replace path is exercised alongside the applied caps.
                let updated =
                    ResourceLimits.None.WithMemoryMax(128L * 1024L * 1024L).WithCpuQuota(1.0)

                match group.UpdateLimits updated with
                | Ok() ->
                    // The `Options` snapshot a consumer reads back reflects exactly the new set.
                    Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))
                    Assert.That(group.Options.Limits.CpuQuota, Is.EqualTo(Some 1.0))
                    Assert.That(group.Options.Limits.MaxProcesses, Is.EqualTo None)

                    // The re-tuned group still runs children (the container was updated in place, not
                    // torn down or recreated).
                    match! runInGroup group with
                    | Ok result -> Assert.That(result.Stdout, Does.Contain "limited")
                    | Error error -> Assert.Fail $"{error}"
                | Error error -> Assert.Fail $"UpdateLimits on a live limit-capable group failed: {error}"

                // A second update that DROPS the CPU quota (Some -> None) — exercises clearing a
                // previously-applied cap in place (Windows disables the live Job's CPU rate control; the
                // cgroup resets cpu.max to unbounded). It must apply cleanly and the snapshot must show
                // CPU gone, memory kept.
                match group.UpdateLimits(ResourceLimits.None.WithMemoryMax(128L * 1024L * 1024L)) with
                | Ok() ->
                    Assert.That(group.Options.Limits.CpuQuota, Is.EqualTo None)
                    Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some(128L * 1024L * 1024L)))
                    Assert.That(group.Options.Limits.MaxProcesses, Is.EqualTo None)
                | Error error -> Assert.Fail $"dropping the CPU quota on a live group failed: {error}"
        }
        :> Task

    [<Test>]
    member _.``a failed UpdateLimits leaves Options on the previous set, never reporting caps that did not apply``() =
        // The honest partial-failure contract (T-207), exercised through the public verb over a synthetic
        // limit-capable backend so it runs on every platform. A backend whose native apply fails partway
        // best-effort restores the previous set (nothing net changes) and returns a typed error;
        // `ProcessGroup` must then NOT swap its `Options` snapshot — so `Options.Limits` never advertises
        // caps that a failed apply did not actually leave in force, and the container (the backend's
        // `InForce`) and the readable snapshot stay consistent.
        let oneGb = 1024L * 1024L * 1024L
        let initial = ResourceLimits.None.WithMemoryMax oneGb

        // The backend refuses any set requesting exactly 999 processes (the injected "poison" that models a
        // pids.max EACCES-style late write failure), restoring the previous set instead.
        let backend = LimitContractBackend(initial, (fun l -> l.MaxProcesses = Some 999))

        use group =
            ProcessGroup.FromBackend(backend :> IContainmentBackend, ProcessGroupOptions().WithMemoryMax oneGb)

        // Baseline: Options and the container agree on the create-time set.
        Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some oneGb))
        Assert.That(backend.InForce.MemoryMax, Is.EqualTo(Some oneGb))

        // A successful update advances BOTH the container and the snapshot to the new set.
        let applied =
            ResourceLimits.None.WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses 8

        match group.UpdateLimits applied with
        | Ok() ->
            Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some(256L * 1024L * 1024L)))
            Assert.That(group.Options.Limits.MaxProcesses, Is.EqualTo(Some 8))
            Assert.That(backend.InForce.MemoryMax, Is.EqualTo(Some(256L * 1024L * 1024L)))
            Assert.That(backend.InForce.MaxProcesses, Is.EqualTo(Some 8))
        | Error error -> Assert.Fail $"the non-poison update should apply: {error}"

        // A partial-failure update (the poison set) is refused with a typed error AND leaves BOTH the
        // container and the Options snapshot on the previous ({256 MB, 8}) set — never the caps it tried
        // and failed to apply.
        let poison =
            ResourceLimits.None.WithMemoryMax(512L * 1024L * 1024L).WithMaxProcesses 999

        match group.UpdateLimits poison with
        | Error(ProcessError.ResourceLimit detail) -> Assert.That(detail, Is.Not.Empty)
        | other -> Assert.Fail $"expected ProcessError.ResourceLimit for the partial-failure update, got {other}"

        Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some(256L * 1024L * 1024L)))
        Assert.That(group.Options.Limits.MaxProcesses, Is.EqualTo(Some 8))
        // The snapshot never advertises the failed 512 MB / 999-process caps, and stays consistent with
        // what the container reports as in force.
        Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo backend.InForce.MemoryMax)
        Assert.That(group.Options.Limits.MaxProcesses, Is.EqualTo backend.InForce.MaxProcesses)

    [<Test>]
    member _.``updateCgroupLimits rolls an already-written controller file back to its prior value when a later write fails``
        ()
        =
        // The cgroup half of the T-207 partial-failure contract, driven directly at the native helper with
        // pure file I/O so it runs on every platform (no real cgroup mount). A cgroup directory with real
        // controller files: `memory.max` is writable, but `pids.max` is a DIRECTORY so writing the pids cap
        // fails partway — AFTER `memory.max` has already been rewritten to the new cap. `updateCgroupLimits`
        // must roll `memory.max` back to exactly its prior content and return `Error`, leaving the cgroup on
        // the PREVIOUS set rather than a silent mix the Options snapshot would misreport.
        let root = Directory.CreateTempSubdirectory("pk-limit-rollback-").FullName

        try
            let cgroupPath = Path.Combine(root, "child")
            Directory.CreateDirectory cgroupPath |> ignore

            let memoryMax = Path.Combine(cgroupPath, "memory.max")
            let priorMemory = "1073741824"
            File.WriteAllText(memoryMax, priorMemory)

            // `pids.max` as a directory: File I/O on it throws, forcing the pids write to fail after
            // `memory.max` was already rewritten to the new cap.
            Directory.CreateDirectory(Path.Combine(cgroupPath, "pids.max")) |> ignore

            let newLimits =
                ResourceLimits.None.WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses 8

            match Native.Cgroup.updateCgroupLimits cgroupPath newLimits with
            | Ok() -> Assert.Fail "the pids.max write should fail, so updateCgroupLimits must return Error"
            | Error detail ->
                Assert.That(detail, Is.Not.Empty)
                // memory.max was rewritten to the new cap, then rolled back to exactly its prior content:
                // the cgroup is left on the previous set, not the partially-applied new one.
                Assert.That(File.ReadAllText memoryMax, Is.EqualTo priorMemory)
        finally
            Directory.Delete(root, true)

    [<Test>]
    member _.``updateCgroupLimits replaces an io.max target with separate writes and rolls back a failed replacement``
        ()
        =
        let root = Directory.CreateTempSubdirectory("pk-io-target-rollback-").FullName

        try
            let cgroupPath = Path.Combine(root, "child")
            Directory.CreateDirectory cgroupPath |> ignore
            File.WriteAllText(Path.Combine(root, "cgroup.subtree_control"), "io")

            let ioMaxFile = Path.Combine(cgroupPath, "io.max")
            let previous = ResourceLimits.None.WithIoMax("8:16", 4096L, 4096L, 12L, 12L)
            let requested = ResourceLimits.None.WithIoMax("8:32", 8192L, 8192L, 24L, 24L)

            let priorContents =
                match previous.IoMax with
                | Some ioMax -> Native.Cgroup.formatIoMax ioMax
                | None -> failwith "the previous I/O limit should contain a target"

            File.WriteAllText(ioMaxFile, priorContents)

            let mutable ioWriteCount = 0

            Native.Cgroup.controllerWriteTestHook <-
                Some(fun file _ ->
                    if Path.GetFileName file = "io.max" then
                        ioWriteCount <- ioWriteCount + 1

                        if ioWriteCount = 2 then
                            raise (IOException("injected second io.max write failure")))

            try
                match Native.Cgroup.updateCgroupLimitsWithPrevious cgroupPath previous requested with
                | Ok() -> Assert.Fail "the injected second io.max write should fail"
                | Error detail -> Assert.That(detail, Does.Contain "injected second io.max write failure")
            finally
                Native.Cgroup.controllerWriteTestHook <- None

            Assert.That(
                File.ReadAllText ioMaxFile,
                Is.EqualTo priorContents,
                "a failed target replacement must restore the old io.max contents"
            )
        finally
            Directory.Delete(root, true)

    [<Test>]
    member _.``live cgroup I/O updates refuse before touching controllers when io is unavailable``() =
        if not isLinux then
            Assert.Ignore "live cgroup controller classification is Linux-only"
        elif not (Native.Cgroup.cgroupV2Available ()) then
            Assert.Ignore "no usable cgroup v2 hierarchy is mounted"
        else
            let root = Directory.CreateTempSubdirectory("pk-io-unsupported-").FullName

            try
                let cgroupPath = Path.Combine(root, "child")
                Directory.CreateDirectory cgroupPath |> ignore
                let subtreeControl = Path.Combine(root, "cgroup.subtree_control")
                File.WriteAllText(subtreeControl, "memory")
                let memoryMax = Path.Combine(cgroupPath, "memory.max")
                File.WriteAllText(memoryMax, "1073741824")

                let previous = ResourceLimits.None.WithMemoryMax(1073741824L)
                let requested = previous.WithIoMax("8:16", 4096L, 4096L, 12L, 12L)
                let backend = CgroupBackend(cgroupPath, previous) :> IContainmentBackend

                use group =
                    ProcessGroup.FromBackend(backend, ProcessGroupOptions().WithMemoryMax 1073741824L)

                Native.Cgroup.cgroupIoAvailableForTests <- Some false

                try
                    match group.UpdateLimits requested with
                    | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "io controller")
                    | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                    | Ok() -> Assert.Fail "an unavailable io controller must refuse the live update"
                finally
                    Native.Cgroup.cgroupIoAvailableForTests <- None

                Assert.That(File.ReadAllText subtreeControl, Is.EqualTo "memory")
                Assert.That(File.ReadAllText memoryMax, Is.EqualTo "1073741824")
                Assert.That(group.Options.Limits.IoMax, Is.EqualTo None)
            finally
                Directory.Delete(root, true)

    [<Test>]
    member _.``updateCgroupLimits toggles memory oom group with replacement semantics``() =
        let root = Directory.CreateTempSubdirectory("pk-oom-group-").FullName

        try
            let cgroupPath = Path.Combine(root, "child")
            Directory.CreateDirectory cgroupPath |> ignore
            File.WriteAllText(Path.Combine(root, "cgroup.subtree_control"), "")
            let oomGroup = Path.Combine(cgroupPath, "memory.oom.group")
            File.WriteAllText(oomGroup, "0")

            match Native.Cgroup.updateCgroupLimits cgroupPath (ResourceLimits.None.WithOomGroupKill()) with
            | Error detail -> Assert.Fail $"enabling memory.oom.group failed: {detail}"
            | Ok() -> Assert.That(File.ReadAllText oomGroup, Is.EqualTo "1")

            match Native.Cgroup.updateCgroupLimits cgroupPath ResourceLimits.None with
            | Error detail -> Assert.Fail $"clearing memory.oom.group failed: {detail}"
            | Ok() -> Assert.That(File.ReadAllText oomGroup, Is.EqualTo "0")
        finally
            Directory.Delete(root, true)

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
    member _.``migrateToCgroup writes the pid and reports success on a writable cgroup.procs``() =
        if isWindows then
            Assert.Ignore "the cgroup migration write is a POSIX libc (open/write/close) path"
        else
            // Exercise the raw open/write/close SUCCESS path deterministically without needing cgroup
            // root: point migrateToCgroup at a writable regular file standing in for cgroup.procs (the
            // function only does open(O_WRONLY) + write(pid) + close on it). The missing/unwritable
            // target — the ENOENT branch — is already covered by the failure-injection tests above.
            let dir = Directory.CreateTempSubdirectory("pk-procs-").FullName

            try
                let procs = Path.Combine(dir, "cgroup.procs")
                File.WriteAllText(procs, "")

                match Native.Cgroup.migrateToCgroup dir 12345 with
                | Ok() -> Assert.That(File.ReadAllText procs, Does.Contain "12345")
                | Error detail ->
                    Assert.Fail $"expected Ok writing a pid to a writable cgroup.procs stand-in, got: {detail}"
            finally
                Directory.Delete(dir, true)

    [<Test>]
    member _.``migrateToCgroup treats a short write to cgroup.procs as a migration failure``() =
        if isWindows then
            Assert.Ignore "the cgroup migration write is a POSIX libc (open/write/close) path"
        else
            // A genuine short write() on cgroup.procs is (per the kernel's atomic per-write handling)
            // effectively unprovokable for a payload this small, so use `migrateWriteTestHook` (the same
            // test-seam pattern as `PipelineRunner.stageSpawnedTestHook`) to force the raw write() return
            // value down by one byte and exercise the classification deterministically.
            let dir = Directory.CreateTempSubdirectory("pk-procs-short-").FullName

            try
                let procs = Path.Combine(dir, "cgroup.procs")
                File.WriteAllText(procs, "")

                Native.Cgroup.migrateWriteTestHook <- Some(fun written -> written - 1n)

                try
                    match Native.Cgroup.migrateToCgroup dir 12345 with
                    | Ok() -> Assert.Fail "a short write to cgroup.procs must not be reported as a successful migration"
                    | Error detail -> Assert.That(detail, Does.Contain "short write")
                finally
                    Native.Cgroup.migrateWriteTestHook <- None
            finally
                Directory.Delete(dir, true)

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

    [<Test>]
    member _.``a synthetic reusable cgroup spawns again after suspended KillAll and Signal Kill``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "the cgroup SpawnInto launcher is Linux-only"
            else
                let directory =
                    Path.Combine(Path.GetTempPath(), $"processkit-reusable-cgroup-{Guid.NewGuid():N}")

                Directory.CreateDirectory directory |> ignore
                File.WriteAllText(Path.Combine(directory, "cgroup.procs"), "")
                File.WriteAllText(Path.Combine(directory, "cgroup.freeze"), "0")

                let originalHook = Native.Cgroup.killCgroupWriteTestHook
                let mutable cleaningUp = false

                Native.Cgroup.killCgroupWriteTestHook <-
                    Some(fun file content ->
                        if
                            cleaningUp
                            && file.EndsWith("cgroup.freeze", StringComparison.Ordinal)
                            && content = "0"
                        then
                            Directory.Delete(directory, true)
                            raise (DirectoryNotFoundException "the synthetic cgroup was removed during final cleanup"))

                try
                    let backend = CgroupBackend directory
                    let nativeBackend = backend :> IContainmentBackend
                    let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                    try
                        let command =
                            Command.create "/bin/sh"
                            |> Command.args [ "-c"; "exit 0" ]
                            |> Command.stdout StdioMode.Null
                            |> Command.stderr StdioMode.Null

                        let runCycle operation kill =
                            task {
                                match group.Suspend() with
                                | Error error -> Assert.Fail $"{operation}: suspend failed: {error}"
                                | Ok() -> ()

                                Assert.That(
                                    File.ReadAllText(Path.Combine(directory, "cgroup.freeze")).Trim(),
                                    Is.EqualTo "1",
                                    $"{operation}: Suspend must leave the synthetic freezer armed"
                                )

                                match kill () with
                                | Error error -> Assert.Fail $"{operation}: hard kill failed: {error}"
                                | Ok() -> ()

                                Assert.That(
                                    File.ReadAllText(Path.Combine(directory, "cgroup.freeze")).Trim(),
                                    Is.EqualTo "0",
                                    $"{operation}: hard kill must explicitly thaw the reusable cgroup"
                                )

                                match group.SpawnInto command with
                                | Error error -> Assert.Fail $"{operation}: the thawed group refused SpawnInto: {error}"
                                | Ok spawned ->
                                    try
                                        let! outcome = nativeBackend.Wait spawned.Handle

                                        Assert.That(
                                            outcome,
                                            Is.EqualTo(Outcome.Exited 0),
                                            $"{operation}: spawned child outcome"
                                        )
                                    finally
                                        nativeBackend.Release spawned
                            }

                        do! runCycle "KillAll" (fun () -> group.KillAll())
                        do! runCycle "Signal Kill" (fun () -> group.Signal Signal.Kill)
                    finally
                        cleaningUp <- true
                        (group :> IDisposable).Dispose()
                finally
                    Native.Cgroup.killCgroupWriteTestHook <- originalHook

                    if Directory.Exists directory then
                        Directory.Delete(directory, true)
        }
        :> Task

    [<Test>]
    member _.``cgroup peak process count grows with concurrency and survives member exit``() : Task =
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

                    match! group.StartAsync(Command.create "/bin/sleep" |> Command.arg "30") with
                    | Error error -> Assert.Fail $"first cgroup member failed to start: {error}"
                    | Ok first ->
                        use first = first

                        let firstPeak =
                            match group.Stats() with
                            | Ok stats ->
                                match stats.PeakProcessCount with
                                | Some peak -> peak
                                | None ->
                                    Assert.Ignore "pids.peak requires Linux 6.6+ and delegated pids controller"
                                    0L
                            | Error error -> raise (AssertionException $"first stats snapshot failed: {error}")

                        match! group.StartAsync(Command.create "/bin/sleep" |> Command.arg "30") with
                        | Error error -> Assert.Fail $"second cgroup member failed to start: {error}"
                        | Ok second ->
                            use second = second

                            let concurrentPeak =
                                match group.Stats() with
                                | Ok stats ->
                                    Assert.That(stats.ActiveProcessCount, Is.GreaterThanOrEqualTo 2)

                                    match stats.PeakProcessCount with
                                    | Some peak -> peak
                                    | None ->
                                        raise (AssertionException "a delegated cgroup v2 pids.peak should be available")
                                | Error error -> raise (AssertionException $"concurrent stats snapshot failed: {error}")

                            Assert.That(concurrentPeak, Is.GreaterThan firstPeak)

                            second.Kill()
                            let! _ = second.WaitAsync()

                            match group.Stats() with
                            | Ok stats ->
                                Assert.That(stats.ActiveProcessCount, Is.EqualTo 1)
                                Assert.That(stats.PeakProcessCount, Is.EqualTo(Some concurrentPeak))
                            | Error error -> Assert.Fail $"post-exit stats snapshot failed: {error}"

                            first.Kill()
                            let! _ = first.WaitAsync()
                            ()
        }
        :> Task

    [<Test>]
    member _.``a cgroup target and a child it forks immediately are both cgroup members``() : Task =
        task {
            if isWindows || isMacOs then
                Assert.Ignore "cgroup v2 is Linux-only"
            else
                // The window this closes: the target must be inside its cgroup BEFORE it runs, so a
                // descendant it forks in its very first instant inherits the cgroup (and its limits)
                // rather than being created in the parent cgroup. The self-migrating launcher guarantees
                // this — the target's pid is a cgroup member before a single instruction of it runs — so
                // a target that forks a child as its first action has BOTH itself and that child listed
                // in cgroup.procs. (This can only be exercised where cgroup enforcement is real: the
                // privileged CI leg at the cgroup root; otherwise it fails fast and is ignored.)
                let options =
                    ProcessGroupOptions().WithMaxProcesses(64).WithMemoryMax(256L * 1024L * 1024L)

                match ProcessGroup.Create options with
                | Error(ProcessError.ResourceLimit _) ->
                    Assert.Ignore "cgroup v2 limits not enforceable here (not at the real cgroup root)"
                | Error other -> Assert.Fail $"{other}"
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    // A target that forks a child as its first action, then both sleep so both stay
                    // observable. With the spawn-into-cgroup launcher the target starts already inside
                    // the cgroup, so the immediately-forked child inherits it — both appear in
                    // cgroup.procs.
                    match! group.StartAsync(shell "sleep 30 & sleep 30") with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok running ->
                        // Poll briefly for both processes to show up as cgroup members.
                        let mutable memberCount = 0
                        let mutable attempts = 0

                        while memberCount < 2 && attempts < 200 do
                            match group.Members() with
                            | Ok m -> memberCount <- m.Count
                            | Error e -> Assert.Fail $"members: {e}"

                            if memberCount < 2 then
                                do! Task.Delay 20
                                attempts <- attempts + 1

                        Assert.That(
                            memberCount,
                            Is.GreaterThanOrEqualTo 2,
                            "the target and the child it forked immediately should both be cgroup members (the spawn-to-migrate window is closed)"
                        )

                        running.Kill()
                        let! _ = running.WaitAsync()
                        ()
        }
        :> Task

    [<Test>]
    member _.``Pty composes with cgroup v2: the child gets a controlling terminal AND is a cgroup member``() : Task =
        task {
            if isWindows || isMacOs then
                Assert.Ignore "cgroup v2 + PTY composition is Linux-only (the ctty helper is util-linux setsid --ctty)"
            else
                // T-139: Command.Pty and ResourceLimits(Mechanism.CgroupV2) at the same time. The self-
                // migrating cgroup launcher `exec`s the `setsid --ctty` shim (with any privilege drop nested
                // inside), so cgroup membership (by the launcher's own pid, written to cgroup.procs before
                // exec) and the controlling-tty setup compose on one unchanged pid. This proves both halves
                // of that composition at once: (a) the child really gets a controlling pty (a single merged
                // stream, never a separate stderr — D3) AND (b) it really runs inside the cgroup (a
                // cgroup.procs member of the limited group, Mechanism.CgroupV2 — never a silent process-group
                // fallback). Requires the real cgroup root: the privileged CI leg sets PROCESSKIT_EXPECT_CGROUP
                // so this *requires* the cgroup path; unprivileged it fails fast and is ignored.
                let expectCgroup =
                    Environment.GetEnvironmentVariable "PROCESSKIT_EXPECT_CGROUP" = "1"

                let options =
                    ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(64)

                match ProcessGroup.Create options with
                | Error(ProcessError.ResourceLimit _) when not expectCgroup ->
                    Assert.Ignore "cgroup v2 limits not enforceable here (not at the real cgroup root)"
                | Error other -> Assert.Fail $"expected a CgroupV2 group (PROCESSKIT_EXPECT_CGROUP set), got {other}"
                | Ok group ->
                    use group = group
                    // (b) the cgroup v2 mechanism actually engaged, not a silent process-group fallback.
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    // A pty child that proves its controlling terminal in its first output, then sleeps so it
                    // stays observable while the parent confirms cgroup.procs membership below. `test -t
                    // 0/1/2` proves all three descriptors are ttys, and opening `/dev/tty` succeeds only when
                    // the session HAS a controlling terminal — the one `setsid --ctty` set on the pty slave,
                    // composed AFTER the launcher's cgroup join.
                    let script =
                        "if test -t 0 && test -t 1 && test -t 2 && : < /dev/tty; then printf 'CTTY-OK\\n'; "
                        + "else printf 'CTTY-NO\\n'; fi; sleep 30"

                    let cmd = Command.create "/bin/sh" |> Command.args [ "-c"; script ] |> Command.pty

                    match! group.StartAsync(cmd, CancellationToken.None) with
                    | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                    | Error other -> Assert.Fail $"unexpected error starting a pty run inside a cgroup: {other}"
                    | Ok running ->
                        // Drain the merged pty stream in the background: the child prints its verdict, then
                        // sleeps, so this completes only once the run is killed below. `verdict` resolves as
                        // soon as that verdict line is framed — the event the kill below waits for. It is
                        // also resolved when the stream ENDS, so a child that never wrote one fails on the
                        // assertions here rather than hanging on a signal that can no longer arrive.
                        let verdict =
                            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                        let collectTask = collectUntil "CTTY-" verdict (running.OutputEventsAsync())

                        // (b) parent-side cgroup.procs membership — the authoritative check the other
                        // LimitsTests use: the pty child is a real member of the limited group's cgroup.
                        let mutable memberCount = 0
                        let mutable attempts = 0

                        while memberCount < 1 && attempts < 200 do
                            match group.Members() with
                            | Ok m -> memberCount <- m.Count
                            | Error e -> Assert.Fail $"members: {e}"

                            if memberCount < 1 then
                                do! Task.Delay 20
                                attempts <- attempts + 1

                        Assert.That(
                            memberCount,
                            Is.GreaterThanOrEqualTo 1,
                            "the pty child must be a real cgroup.procs member of the limited group"
                        )

                        // Wait for the child's own verdict BEFORE killing it. Cgroup membership is NOT
                        // evidence that the target has run: the `/bin/sh` launcher writes its `$$` into
                        // `cgroup.procs` and only THEN `exec`s the `setsid --ctty` shim and the target, so
                        // membership is observable while the exec chain is still in flight. Killing on
                        // membership alone therefore races that chain — and an immediate `Kill()` really
                        // does reach a pre-`setsid()` pty child now (it used to be skipped as if the child
                        // were already gone), so the loser of that race is a child killed before it ever
                        // wrote, leaving this test asserting against an empty stream. Membership stays
                        // asserted above; it is simply not the event the teardown is allowed to trigger on.
                        let! signalled = Task.WhenAny(verdict.Task, Task.Delay(TimeSpan.FromSeconds 30.0))

                        Assert.That(
                            obj.ReferenceEquals(signalled, verdict.Task),
                            Is.True,
                            "the pty child produced no controlling-terminal verdict line before the timeout"
                        )

                        // Tear the run down, then drain the merged stream captured while it ran (the
                        // streaming verb has already consumed the run, so it — not a separate WaitAsync —
                        // observes the child's end; disposing the group reaps the tree).
                        running.Kill()
                        let! events = collectTask

                        // (a) merged terminal stream: every event is a Stdout event, never a separate stderr,
                        // under a PTY (D3).
                        let allStdout =
                            events
                            |> Seq.forall (fun e ->
                                match e with
                                | OutputEvent.Stdout _ -> true
                                | OutputEvent.Stderr _ -> false)

                        Assert.That(allStdout, Is.True, "every event under a PTY must be a Stdout event (D3)")

                        // (a) a real controlling terminal even under the cgroup launcher.
                        let text = events |> Seq.map (fun e -> e.Text) |> String.concat "\n"

                        Assert.That(
                            text,
                            Does.Contain "CTTY-OK",
                            "the pty child must have a real controlling terminal even when spawned via the cgroup launcher"
                        )
        }
        :> Task

    // ---- identity-safe per-member delivery (Native.Cgroup.deliverIdentitySafe) ----
    //
    // These drive the pin -> reconfirm-membership -> send decision logic through injected syscall
    // closures, so the pid-reuse race is exercised deterministically without a real pidfd or cgroup —
    // and are platform-independent (they never touch a real syscall). The production
    // signalCgroup/terminateCgroup wire the same primitive to the real
    // pidfdOpenChecked/pidfdSendSignalChecked; the real-pidfd tests further down cover that live path.

    [<Test>]
    member _.``deliverIdentitySafe skips a pinned pid that is no longer a cgroup member (recycled outside)``() =
        // The pin succeeds, but by the time membership is reconfirmed the original member has exited and
        // its pid was recycled by a process OUTSIDE the cgroup, so `stillMember` reports false. The
        // primitive must skip and never call `send` — the core pid-reuse safety this task adds.
        let mutable sent = false
        let mutable closed = false

        let outcome =
            Native.Cgroup.deliverIdentitySafe
                1234
                Native.Posix.SIGTERM
                (fun _ -> Ok FakeHandle)
                (fun _ -> Ok false)
                (fun _ _ ->
                    sent <- true
                    Ok())
                (fun _ -> closed <- true)

        match outcome with
        | Native.Cgroup.Delivery.Skipped -> ()
        | other -> Assert.Fail $"expected Skipped, got {other}"

        Assert.That(sent, Is.False, "a pid recycled outside the cgroup must never be signalled")
        Assert.That(closed, Is.True, "the pin must be released even when delivery is skipped")

    [<Test>]
    member _.``deliverIdentitySafe delivers the requested signal to a confirmed member``() =
        let mutable sentSignal = None
        let mutable closed = false

        let outcome =
            Native.Cgroup.deliverIdentitySafe
                42
                Native.Posix.SIGTERM
                (fun _ -> Ok FakeHandle)
                (fun _ -> Ok true)
                (fun _ signalNum ->
                    sentSignal <- Some signalNum
                    Ok())
                (fun _ -> closed <- true)

        match outcome with
        | Native.Cgroup.Delivery.Delivered -> ()
        | other -> Assert.Fail $"expected Delivered, got {other}"

        Assert.That(
            sentSignal,
            Is.EqualTo(Some Native.Posix.SIGTERM),
            "the requested signal reaches a confirmed member"
        )

        Assert.That(closed, Is.True, "the pin must be released after a delivery")

    [<Test>]
    member _.``deliverIdentitySafe treats a member gone before the pin as a benign no-op``() =
        // openPin (pidfd_open) fails ESRCH: the member exited before it could be pinned. Benign — the
        // intended end state (gone) already holds — and membership is not even consulted, nor is send.
        let mutable membershipChecked = false
        let mutable sent = false

        let outcome =
            Native.Cgroup.deliverIdentitySafe
                7
                Native.Posix.SIGTERM
                (fun _ -> Error ESRCH)
                (fun _ ->
                    membershipChecked <- true
                    Ok true)
                (fun _ _ ->
                    sent <- true
                    Ok())
                ignore

        match outcome with
        | Native.Cgroup.Delivery.Delivered -> ()
        | other -> Assert.Fail $"expected Delivered, got {other}"

        Assert.That(membershipChecked, Is.False, "membership must not be checked once the pin fails ESRCH")
        Assert.That(sent, Is.False)

    [<Test>]
    member _.``deliverIdentitySafe fails safe (no raw kill) when the kernel lacks pidfd``() =
        // openPin fails ENOSYS (kernel < 5.3 / seccomp): the primitive must surface an honest failure,
        // NOT silently fall back to a racy raw kill by pid number.
        let mutable sent = false

        let outcome =
            Native.Cgroup.deliverIdentitySafe
                7
                Native.Posix.SIGTERM
                (fun _ -> Error ENOSYS)
                (fun _ -> Ok true)
                (fun _ _ ->
                    sent <- true
                    Ok())
                ignore

        match outcome with
        | Native.Cgroup.Delivery.Failed(errno, message) ->
            Assert.That(errno, Is.EqualTo ENOSYS)
            Assert.That(message, Does.Contain "pidfd")
        | other -> Assert.Fail $"a kernel without pidfd must fail safe, not signal; got {other}"

        Assert.That(sent, Is.False, "fail-safe must not send any signal")

    [<Test>]
    member _.``deliverIdentitySafe fails safe without sending when membership is unreadable``() =
        // Reconfirming membership fails (e.g. EACCES on cgroup.procs): unknown membership must not be
        // signalled — fail safe, surface the error, no send.
        let mutable sent = false

        let outcome =
            Native.Cgroup.deliverIdentitySafe
                7
                Native.Posix.SIGTERM
                (fun _ -> Ok FakeHandle)
                (fun _ -> Error "cgroup.procs unreadable (EACCES)")
                (fun _ _ ->
                    sent <- true
                    Ok())
                ignore

        match outcome with
        | Native.Cgroup.Delivery.Failed(_, message) -> Assert.That(message, Does.Contain "unreadable")
        | other -> Assert.Fail $"an unreadable membership must fail safe; got {other}"

        Assert.That(sent, Is.False)

    [<Test>]
    member _.``deliverIdentitySafe treats an ESRCH on send (pinned target exited) as benign``() =
        // Membership is confirmed, but the pinned task exits before the send, so send returns ESRCH. The
        // pidfd guarantees that ESRCH is our own target's exit (never a recycled pid), so it is benign.
        let outcome =
            Native.Cgroup.deliverIdentitySafe
                7
                Native.Posix.SIGTERM
                (fun _ -> Ok FakeHandle)
                (fun _ -> Ok true)
                (fun _ _ -> Error ESRCH)
                ignore

        match outcome with
        | Native.Cgroup.Delivery.Delivered -> ()
        | other -> Assert.Fail $"a pinned target's own exit (ESRCH on send) is benign; got {other}"

    [<Test>]
    member _.``deliverIdentitySafe surfaces a real EPERM delivery failure``() =
        // A confirmed member that changed uid (or a seccomp/container policy) rejects the signal with
        // EPERM — a real delivery failure that must not read as success.
        let outcome =
            Native.Cgroup.deliverIdentitySafe
                7
                Native.Posix.SIGTERM
                (fun _ -> Ok FakeHandle)
                (fun _ -> Ok true)
                (fun _ _ -> Error EPERM)
                ignore

        match outcome with
        | Native.Cgroup.Delivery.Failed(errno, _) -> Assert.That(errno, Is.EqualTo EPERM)
        | other -> Assert.Fail $"EPERM is a real delivery failure and must surface; got {other}"

    // ---- the real pidfd mechanism (Native.Posix.pidfdOpenChecked / pidfdSendSignalChecked) ----
    //
    // Linux integration coverage driving the ACTUAL pidfd syscalls against real child processes (no
    // cgroup mount needed), skipping — rather than failing — where the kernel lacks pidfd. Complements
    // the deterministic seam tests above.

    [<Test>]
    member _.``pidfd pins a child's identity and reports its exit as ESRCH, never a recycled pid``() =
        if not isLinux then
            Assert.Ignore "pidfd (pidfd_open/pidfd_send_signal) is Linux-only"
        elif not (pidfdAvailable ()) then
            Assert.Ignore "pidfd_open unavailable on this kernel/sandbox"
        else
            let child = spawnSleeper ()

            try
                let fd =
                    match Native.Posix.pidfdOpenChecked child.Id with
                    | Ok fd -> fd
                    | Error errno -> failwith $"pidfd_open on a live child failed (errno {errno})"

                try
                    // Signal 0 is a pure existence/permission probe: the child is alive, so Ok.
                    match Native.Posix.pidfdSendSignalChecked fd 0 with
                    | Ok() -> ()
                    | Error errno -> Assert.Fail $"null-signalling a live pinned child failed (errno {errno})"

                    // Kill and reap, then the pinned fd must report the task gone (ESRCH). It can NEVER be
                    // revived by a process that later recycles the pid — the whole point of pinning by pidfd.
                    child.Kill()
                    child.WaitForExit()

                    match Native.Posix.pidfdSendSignalChecked fd 0 with
                    | Error e when e = ESRCH -> ()
                    | Ok() -> Assert.Fail "a reaped, pinned task must not be signallable"
                    | Error errno -> Assert.Fail $"expected ESRCH for a reaped pinned task, got errno {errno}"
                finally
                    Native.Posix.closePidfd fd
            finally
                killAndReap child

    [<Test>]
    member _.``the real pidfd primitive skips (never signals) a live non-member``() =
        if not isLinux then
            Assert.Ignore "pidfd is Linux-only"
        elif not (pidfdAvailable ()) then
            Assert.Ignore "pidfd_open unavailable on this kernel/sandbox"
        else
            let child = spawnSleeper ()

            try
                // Real pidfd_open/pidfd_send_signal, but the membership reconfirm reports "not a member"
                // (modelling a pid recycled by a process outside the cgroup). The primitive must skip: the
                // would-be-fatal SIGKILL is never sent, so the child stays alive.
                let outcome =
                    Native.Cgroup.deliverIdentitySafe
                        child.Id
                        Native.Posix.SIGKILL
                        Native.Posix.pidfdOpenChecked
                        (fun _ -> Ok false)
                        Native.Posix.pidfdSendSignalChecked
                        Native.Posix.closePidfd

                match outcome with
                | Native.Cgroup.Delivery.Skipped -> ()
                | other -> Assert.Fail $"a live non-member must be skipped, got {other}"

                Assert.That(
                    child.HasExited,
                    Is.False,
                    "a non-member must receive no signal — the live child is untouched"
                )
            finally
                killAndReap child

    [<Test>]
    member _.``the real pidfd primitive delivers to a confirmed live member``() =
        if not isLinux then
            Assert.Ignore "pidfd is Linux-only"
        elif not (pidfdAvailable ()) then
            Assert.Ignore "pidfd_open unavailable on this kernel/sandbox"
        else
            let child = spawnSleeper ()

            try
                // Confirmed member + real syscalls: SIGTERM is delivered and the sleeper, which does not
                // trap SIGTERM, exits. Proves the real pidfd send path works end to end, not just the
                // fail-safe branches.
                let outcome =
                    Native.Cgroup.deliverIdentitySafe
                        child.Id
                        Native.Posix.SIGTERM
                        Native.Posix.pidfdOpenChecked
                        (fun _ -> Ok true)
                        Native.Posix.pidfdSendSignalChecked
                        Native.Posix.closePidfd

                match outcome with
                | Native.Cgroup.Delivery.Delivered -> ()
                | other -> Assert.Fail $"a confirmed live member must be delivered to, got {other}"

                Assert.That(
                    child.WaitForExit 5000,
                    Is.True,
                    "the sleeper must exit on the SIGTERM delivered through the pidfd"
                )
            finally
                killAndReap child

    [<Test>]
    member _.``I/O limit builders preserve directional rates and ProcessGroupOptions forwards them``() =
        let limits = ResourceLimits.None.WithIoMax("8:16", Some 4096L, None, Some 12L, None)

        match limits.IoMax with
        | Some ioMax ->
            Assert.That(ioMax.Target, Is.EqualTo "8:16")
            Assert.That(ioMax.ReadBytesPerSecond, Is.EqualTo(Some 4096L))
            Assert.That(ioMax.WriteBytesPerSecond, Is.EqualTo None)
            Assert.That(ioMax.ReadOperationsPerSecond, Is.EqualTo(Some 12L))
            Assert.That(ioMax.WriteOperationsPerSecond, Is.EqualTo None)
        | None -> Assert.Fail "WithIoMax must retain the requested I/O policy"

        let options = ProcessGroupOptions().WithIoMax("8:16", 4096L, 0L, 12L, 0L)

        assertIoMaxEqual limits.IoMax options.Limits.IoMax

    [<Test>]
    member _.``I/O limit builders reject empty targets and invalid rates``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () ->
                ResourceLimits.None.WithIoMax(Unchecked.defaultof<string>, 1L, 0L, 0L, 0L)
                |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(Action(fun () -> ResourceLimits.None.WithIoMax("", 1L, 0L, 0L, 0L) |> ignore))
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> ResourceLimits.None.WithIoMax("8:16", None, None, None, None) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithIoMax("8:16", Some 0L, None, None, None) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> ResourceLimits.None.WithIoMax("8:16", -1L, 0L, 0L, 0L) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Linux io.max rendering uses max for unbounded directions``() =
        let limits = ResourceLimits.None.WithIoMax("8:16", Some 4096L, None, Some 12L, None)

        match limits.IoMax with
        | Some ioMax ->
            Assert.That(Native.Cgroup.formatIoMax ioMax, Is.EqualTo "8:16 rbps=4096 wbps=max riops=12 wiops=max")
        | None -> Assert.Fail "WithIoMax must create an I/O policy"

    [<Test>]
    member _.``ProcessGroup.UpdateLimits carries I/O limits through the backend contract``() =
        let backend = LimitContractBackend(ResourceLimits.None, fun _ -> false)

        use group =
            ProcessGroup.FromBackend(backend :> IContainmentBackend, ProcessGroupOptions())

        let requested =
            ResourceLimits.None.WithIoMax("8:16", Some 4096L, None, Some 12L, None)

        match group.UpdateLimits requested with
        | Error error -> Assert.Fail $"the synthetic backend should accept the I/O policy: {error}"
        | Ok() ->
            assertIoMaxEqual requested.IoMax group.Options.Limits.IoMax
            assertIoMaxEqual requested.IoMax backend.InForce.IoMax

    [<Test>]
    member _.``I/O limits are honestly Unsupported on macOS``() =
        if not isMacOs then
            Assert.Ignore "macOS is the POSIX whole-tree backend without an I/O controller"

        let options = ProcessGroupOptions().WithIoMax("8:16", 4096L, 0L, 12L, 0L)

        match ProcessGroup.Create options with
        | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "disk I/O")
        | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
        | Ok group ->
            (group :> IDisposable).Dispose()
            Assert.Fail "macOS must not claim to enforce a whole-tree I/O rate"

[<TestFixture>]
type IoMaxContractRegressionTests() =

    [<Test>]
    member _.``I/O limit validation rejects a request with no bounded direction``() =
        let error =
            Assert.Throws<ArgumentException>(
                Action(fun () -> ResourceLimits.None.WithIoMax("8:16", None, None, None, None) |> ignore)
            )

        match error with
        | null -> Assert.Fail "expected an ArgumentException"
        | error -> Assert.That(error.Message, Does.Contain "at least one I/O rate")

    [<Test>]
    member _.``unsupported POSIX process-group backends refuse I/O limits``() =
        if
            RuntimeInformation.IsOSPlatform OSPlatform.Windows
            || RuntimeInformation.IsOSPlatform OSPlatform.Linux
        then
            Assert.Ignore "Windows and Linux have dedicated I/O containment backends"

        let options = ProcessGroupOptions().WithIoMax("8:16", 4096L, 0L, 12L, 0L)

        match ProcessGroup.Create options with
        | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "disk I/O")
        | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
        | Ok group ->
            (group :> IDisposable).Dispose()
            Assert.Fail "a POSIX process-group backend must not claim to enforce a whole-tree I/O rate"

[<TestFixture>]
type WindowsIoRateControlTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let ioLimits (target: string) (bandwidth: int64) (iops: int64) =
        ResourceLimits.None.WithIoMax(target, bandwidth, bandwidth, iops, iops)

    let assertIoTarget (expected: ResourceLimits) (actual: ResourceLimits) =
        match expected.IoMax, actual.IoMax with
        | Some expectedIo, Some actualIo ->
            Assert.That(actualIo.Target, Is.EqualTo expectedIo.Target)
            Assert.That(actualIo.ReadBytesPerSecond, Is.EqualTo expectedIo.ReadBytesPerSecond)
            Assert.That(actualIo.WriteBytesPerSecond, Is.EqualTo expectedIo.WriteBytesPerSecond)
            Assert.That(actualIo.ReadOperationsPerSecond, Is.EqualTo expectedIo.ReadOperationsPerSecond)
            Assert.That(actualIo.WriteOperationsPerSecond, Is.EqualTo expectedIo.WriteOperationsPerSecond)
        | _ -> Assert.Fail "the Job's I/O policy presence differs from the requested policy"

    [<Test>]
    member _.``Windows Job I/O rate control creates updates and removes a real volume policy``() =
        if not isWindows then
            Assert.Ignore "Windows Job Object I/O rate control is Windows-only"

        match WindowsIoRateControlTestSupport.volumeTargets () with
        | [] -> Assert.Ignore "could not derive a valid NT volume target"
        | target :: _ ->
            let initial = ioLimits target 1048576L 1L
            let options = ProcessGroupOptions().WithIoMax(target, 1048576L, 1048576L, 1L, 1L)

            match ProcessGroup.Create options with
            | Error(ProcessError.Unsupported detail) when Native.Windows.isIoRateControlUnsupported detail ->
                Assert.Ignore detail
            | Error error -> Assert.Fail $"a real Job Object should accept the I/O rate policy: {error}"
            | Ok group ->
                use group = group
                Assert.That(group.Mechanism, Is.EqualTo Mechanism.JobObject)
                assertIoTarget initial group.Options.Limits

                let updated = ioLimits target 2097152L 2L

                match group.UpdateLimits updated with
                | Error error -> Assert.Fail $"same-volume I/O update failed: {error}"
                | Ok() -> assertIoTarget updated group.Options.Limits

                match group.UpdateLimits ResourceLimits.None with
                | Error error -> Assert.Fail $"removing the Job I/O policy failed: {error}"
                | Ok() -> Assert.That(group.Options.Limits.IoMax, Is.EqualTo None)

    [<Test>]
    member _.``Windows Job I/O rate control replaces a real volume target``() =
        if not isWindows then
            Assert.Ignore "Windows Job Object I/O rate control is Windows-only"

        match WindowsIoRateControlTestSupport.volumeTargets () with
        | first :: second :: _ ->
            let initial = ioLimits first 1048576L 1L

            match ProcessGroup.Create(ProcessGroupOptions().WithIoMax(first, 1048576L, 1048576L, 1L, 1L)) with
            | Error(ProcessError.Unsupported detail) when Native.Windows.isIoRateControlUnsupported detail ->
                Assert.Ignore detail
            | Error error -> Assert.Fail $"a real Job Object should accept the first I/O target: {error}"
            | Ok group ->
                use group = group
                let replacement = ioLimits second 1048576L 1L

                match group.UpdateLimits replacement with
                | Error error -> Assert.Fail $"replacing the Job I/O target failed: {error}"
                | Ok() ->
                    assertIoTarget replacement group.Options.Limits
                    Assert.That(group.Options.Limits.IoMax, Is.Not.EqualTo initial.IoMax)
        | _ -> Assert.Ignore "fewer than two accessible NT volume targets are available for replacement"

    [<Test>]
    member _.``Windows Job I/O rate-control failure restores the prior live limits``() =
        if not isWindows then
            Assert.Ignore "Windows Job Object I/O rate control is Windows-only"

        match WindowsIoRateControlTestSupport.volumeTargets () with
        | [] -> Assert.Ignore "could not derive a valid NT volume target"
        | target :: _ ->
            let initial = ioLimits target 1048576L 1L

            match ProcessGroup.Create(ProcessGroupOptions().WithIoMax(target, 1048576L, 1048576L, 1L, 1L)) with
            | Error(ProcessError.Unsupported detail) when Native.Windows.isIoRateControlUnsupported detail ->
                Assert.Ignore detail
            | Error error -> Assert.Fail $"a real Job Object should accept the initial I/O policy: {error}"
            | Ok group ->
                use group = group

                Assert.That(
                    Native.Windows.ioRateWriteSuccessesForTests (),
                    Is.Empty,
                    "successful native I/O writes must not be retained unless test capture is enabled"
                )

                try
                    // Enable capture before injecting one failure after the extended-limit write. The
                    // one-shot seam must let the rollback write restore the prior Job policy.
                    Native.Windows.enableIoRateWriteSuccessCaptureForTests ()
                    Native.Windows.ioRateWriteErrorForTests <- Some 5
                    let attempted = initial.WithMemoryMax(256L * 1024L * 1024L)

                    match group.UpdateLimits attempted with
                    | Error(ProcessError.ResourceLimit detail) ->
                        Assert.That(detail, Does.Contain "SetIoRateControlInformationJobObject")
                    | Error error ->
                        Assert.Fail $"expected a typed ResourceLimit for the injected native error, got {error}"
                    | Ok() -> Assert.Fail "the injected Job I/O write failure must fail the live update"

                    Assert.That(
                        Native.Windows.ioRateWriteSuccessesForTests (),
                        Does.Contain((target, 1048576L, 1L, true)),
                        "rollback must successfully re-apply the previous Job I/O policy after the late failure"
                    )

                finally
                    Native.Windows.ioRateWriteErrorForTests <- None
                    Native.Windows.disableIoRateWriteSuccessCaptureForTests ()

                assertIoTarget initial group.Options.Limits
                Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo None)

                match group.UpdateLimits ResourceLimits.None with
                | Error error -> Assert.Fail $"the prior I/O policy could not be removed after rollback: {error}"
                | Ok() -> Assert.That(group.Options.Limits.IoMax, Is.EqualTo None)

    // -----------------------------------------------------------------------------------------------
    // LimitEvidence (T-381) — Native.Cgroup.limitEvidence's authoritative counters, pure file I/O
    // against a temp directory (mirroring the `cgroupStats` tests in StatsTests.fs — no real cgroup v2
    // mount required), plus the public ProcessGroup.LimitEvidence() lifecycle and per-backend contracts.
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``CappedAxes.Record tracks CpuTimeMax independently of Cpu (CpuQuota), sticky across calls``() =
        let none = CappedAxes.None
        Assert.That(none.Cpu, Is.False)
        Assert.That(none.CpuTimeMax, Is.False)

        let timeOnly =
            none.Record(ResourceLimits.None.WithCpuTimeMax(TimeSpan.FromSeconds 5.0))

        Assert.That(timeOnly.Cpu, Is.False, "CpuTimeMax must not mark the CpuQuota-derived Cpu axis as capped")
        Assert.That(timeOnly.CpuTimeMax, Is.True)

        // Sticky: a later call naming a different axis keeps the earlier CpuTimeMax record.
        let both = timeOnly.Record(ResourceLimits.None.WithCpuQuota 1.0)
        Assert.That(both.Cpu, Is.True)
        Assert.That(both.CpuTimeMax, Is.True, "CpuTimeMax must stay recorded (sticky) once named")

    [<Test>]
    member _.``CappedAxes.GuardCpuVerdict downgrades NotTripped to Unknown only when CpuTimeMax is recorded``() =
        let noTimeMax = { CappedAxes.None with Cpu = true }

        let withTimeMax =
            { CappedAxes.None with
                Cpu = true
                CpuTimeMax = true }

        Assert.That(noTimeMax.GuardCpuVerdict LimitVerdict.NotTripped, Is.EqualTo LimitVerdict.NotTripped)
        Assert.That(withTimeMax.GuardCpuVerdict LimitVerdict.NotTripped, Is.EqualTo LimitVerdict.Unknown)

        // Tripped and Unknown pass through unchanged regardless of CpuTimeMax.
        Assert.That(withTimeMax.GuardCpuVerdict LimitVerdict.Tripped, Is.EqualTo LimitVerdict.Tripped)
        Assert.That(withTimeMax.GuardCpuVerdict LimitVerdict.Unknown, Is.EqualTo LimitVerdict.Unknown)

    [<Test>]
    member _.``limitEvidence answers NotTripped for an axis never capped, without needing any counter file``() =
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            let evidence = Native.Cgroup.limitEvidence directory CappedAxes.None
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.NotTripped)
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.NotTripped)
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.NotTripped))

    [<Test>]
    member _.``limitEvidence reads Tripped from a positive counter and NotTripped from a present zero one``() =
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            File.WriteAllText(Path.Combine(directory, "memory.events"), "low 0\noom 1\n")
            File.WriteAllText(Path.Combine(directory, "pids.events"), "max 0\n")
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 5\nnr_throttled 3\n")

            let capped: CappedAxes =
                { Memory = true
                  Processes = true
                  Cpu = true
                  CpuTimeMax = false }

            let evidence = Native.Cgroup.limitEvidence directory capped
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Tripped, "oom=1 must read as Tripped")
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.NotTripped, "max=0 must read as NotTripped")
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Tripped, "nr_throttled=3 must read as Tripped"))

    [<Test>]
    member _.``limitEvidence answers Unknown for a capped axis whose counter file is entirely missing``() =
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            let capped: CappedAxes =
                { Memory = true
                  Processes = true
                  Cpu = true
                  CpuTimeMax = false }

            let evidence = Native.Cgroup.limitEvidence directory capped
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.Unknown)
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown))

    [<Test>]
    member _.``limitEvidence answers Unknown, never a fabricated NotTripped, when a present file lacks the key``() =
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            // cpu.stat exists and is readable, but this (synthetic, old-kernel-shaped) file never accounts
            // nr_throttled at all — an honest gap, not "it never throttled".
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 5\n")

            let capped: CappedAxes = { CappedAxes.None with Cpu = true }
            let evidence = Native.Cgroup.limitEvidence directory capped
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown))

    [<Test>]
    member _.``limitEvidence never reports NotTripped on Cpu for a group configured with only CpuTimeMax``() =
        // R-01: `CpuQuota` was never set (`capped.Cpu = false`), so the raw axis verdict short-circuits to
        // `NotTripped` without reading `cpu.stat` at all — but `cpu.stat`'s `nr_throttled` has no bearing on
        // a `CpuTimeMax` (RLIMIT_CPU) trip either way, so an honest `Unknown` is required instead. No
        // cpu.stat file is even written here, proving the guard fires independent of what the file would
        // have said.
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            let capped: CappedAxes =
                { CappedAxes.None with
                    Cpu = false
                    CpuTimeMax = true }

            let evidence = Native.Cgroup.limitEvidence directory capped

            Assert.That(
                evidence.Cpu,
                Is.EqualTo LimitVerdict.Unknown,
                "a group with only CpuTimeMax configured must never read NotTripped on the Cpu axis"
            ))

    [<Test>]
    member _.``limitEvidence downgrades a real NotTripped nr_throttled=0 read to Unknown when CpuTimeMax is also configured``
        ()
        =
        // R-01: CpuQuota WAS configured and cpu.stat honestly reads nr_throttled=0 (the quota mechanism
        // never throttled) — but this group ALSO carries CpuTimeMax, whose own trip nr_throttled cannot see
        // at all, so the honest answer stays Unknown rather than an unqualified "no CPU cap fired".
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 5\nnr_throttled 0\n")

            let capped: CappedAxes =
                { CappedAxes.None with
                    Cpu = true
                    CpuTimeMax = true }

            let evidence = Native.Cgroup.limitEvidence directory capped

            Assert.That(
                evidence.Cpu,
                Is.EqualTo LimitVerdict.Unknown,
                "nr_throttled=0 alone cannot answer for a CpuTimeMax cap also configured on this group"
            ))

    [<Test>]
    member _.``limitEvidence still reports Tripped from a positive nr_throttled even when CpuTimeMax is also configured``
        ()
        =
        // The guard only ever downgrades a NotTripped; real quota-throttle evidence is never suppressed.
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec 5\nnr_throttled 7\n")

            let capped: CappedAxes =
                { CappedAxes.None with
                    Cpu = true
                    CpuTimeMax = true }

            let evidence = Native.Cgroup.limitEvidence directory capped
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Tripped))

    [<Test>]
    member _.``limitEvidence prefers the caller-scoped .local event files over the subtree totals``() =
        LimitEvidenceTestSupport.withTempCgroupDir (fun directory ->
            File.WriteAllText(Path.Combine(directory, "memory.events.local"), "oom 1\n")
            File.WriteAllText(Path.Combine(directory, "memory.events"), "oom 0\n")
            File.WriteAllText(Path.Combine(directory, "pids.events.local"), "max 0\n")
            File.WriteAllText(Path.Combine(directory, "pids.events"), "max 9\n")

            let capped: CappedAxes =
                { Memory = true
                  Processes = true
                  Cpu = false
                  CpuTimeMax = false }

            let evidence = Native.Cgroup.limitEvidence directory capped
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Tripped, "memory.events.local's oom=1 must win")
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.NotTripped, "pids.events.local's max=0 must win"))

    [<Test>]
    member _.``Windows Job Object LimitEvidence is Unknown for a capped axis and NotTripped for one never capped (Windows)``
        ()
        =
        if not isWindows then
            Assert.Ignore "Windows Job Object LimitEvidence contract."

        match Native.Windows.createWindowsJob () with
        | Error error -> Assert.Fail $"could not create a Job Object: {error}"
        | Ok job ->
            let backend = JobObjectBackend job :> IContainmentBackend

            try
                let capped: CappedAxes =
                    { Memory = true
                      Processes = false
                      Cpu = true
                      CpuTimeMax = false }

                let evidence = backend.LimitEvidence capped
                Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
                Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.NotTripped)
                Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown)
            finally
                backend.HardRelease()

    [<Test>]
    member _.``Windows Job Object LimitEvidence is NotTripped-turned-Unknown for a CpuTimeMax-only group (Windows)``() =
        if not isWindows then
            Assert.Ignore "Windows Job Object LimitEvidence contract."

        match Native.Windows.createWindowsJob () with
        | Error error -> Assert.Fail $"could not create a Job Object: {error}"
        | Ok job ->
            let backend = JobObjectBackend job :> IContainmentBackend

            try
                // CpuTimeMax only — CpuQuota was never set, so the raw `Cpu` verdict would be `NotTripped`
                // (never capped by quota). A Job keeps no per-axis job-time evidence, so that must be
                // downgraded to `Unknown` rather than affirmatively claiming the job-time cap never fired
                // (R-01).
                let capped: CappedAxes =
                    { Memory = false
                      Processes = false
                      Cpu = false
                      CpuTimeMax = true }

                let evidence = backend.LimitEvidence capped

                Assert.That(
                    evidence.Cpu,
                    Is.EqualTo LimitVerdict.Unknown,
                    "a group with only CpuTimeMax configured must never read NotTripped on the Cpu axis"
                )
            finally
                backend.HardRelease()

    [<Test>]
    member _.``POSIX process-group LimitEvidence is Unknown on every axis regardless of what was capped``() =
        let backend = ProcessGroupBackend() :> IContainmentBackend

        let capped: CappedAxes =
            { Memory = true
              Processes = true
              Cpu = true
              CpuTimeMax = false }

        let evidence = backend.LimitEvidence capped
        Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown)
        backend.HardRelease()

    [<Test>]
    member _.``POSIX process-group LimitEvidence is Unknown even for an axis this group never capped at all``() =
        // R-02: unlike the Windows Job Object backend, the POSIX process-group backend has no evidence
        // apparatus at all, so it must not read `NotTripped` for a never-capped axis the way the Job Object
        // backend does — every axis stays `Unknown` unconditionally.
        let backend = ProcessGroupBackend() :> IContainmentBackend

        let evidence = backend.LimitEvidence CappedAxes.None
        Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown)
        backend.HardRelease()

    [<Test>]
    member _.``ProcessGroup.LimitEvidence on the POSIX backend is Unknown even for a group that never configured any limit``
        ()
        =
        // R-02, at the public ProcessGroup level (the backend-level test above covers the same contract
        // directly against IContainmentBackend): the real ProcessGroupBackend wired through FromBackend,
        // never capped anything, still answers Unknown on every axis rather than the Job Object's NotTripped.
        let backend = ProcessGroupBackend()

        let group =
            ProcessGroup.FromBackend(backend :> IContainmentBackend, ProcessGroupOptions())

        (group :> IDisposable).Dispose()

        match group.LimitEvidence() with
        | Ok evidence ->
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.Unknown)
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown)
        | Error err -> Assert.Fail $"{err}"

    [<Test>]
    member _.``ProcessGroup.LimitEvidence is unavailable before teardown and returns a cached snapshot after it``() =
        let backend = LimitEvidenceEchoBackend(updateLimitsFails = false)

        let group =
            ProcessGroup.FromBackend(backend :> IContainmentBackend, ProcessGroupOptions())

        match group.LimitEvidence() with
        | Error(ProcessError.Unsupported _) -> ()
        | other -> Assert.Fail $"expected Unsupported before teardown, got {other}"

        (group :> IDisposable).Dispose()

        match group.LimitEvidence(), group.LimitEvidence() with
        | Ok first, Ok second ->
            Assert.That(first.Memory, Is.EqualTo second.Memory)
            Assert.That(first.Processes, Is.EqualTo second.Processes)
            Assert.That(first.Cpu, Is.EqualTo second.Cpu)
        | other -> Assert.Fail $"expected a cached Ok evidence after teardown, got {other}"

    [<Test>]
    member _.``ProcessGroup.LimitEvidence reflects the axes actually capped at Create``() =
        let backend = LimitEvidenceEchoBackend(updateLimitsFails = false)

        let options = ProcessGroupOptions().WithMemoryMax(1024L).WithCpuQuota(1.0)

        let group = ProcessGroup.FromBackend(backend :> IContainmentBackend, options)
        (group :> IDisposable).Dispose()

        match group.LimitEvidence() with
        | Ok evidence ->
            Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Tripped, "MemoryMax was capped at Create")
            Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.NotTripped, "MaxProcesses was never capped")
            Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Tripped, "CpuQuota was capped at Create")
        | Error err -> Assert.Fail $"{err}"

    [<Test>]
    member _.``an axis named by a failed UpdateLimits still joins the sticky cap record``() =
        let backend = LimitEvidenceEchoBackend(updateLimitsFails = true)

        let group =
            ProcessGroup.FromBackend(backend :> IContainmentBackend, ProcessGroupOptions())

        match group.UpdateLimits(ResourceLimits.None.WithMaxProcesses 4) with
        | Error(ProcessError.ResourceLimit _) -> ()
        | other -> Assert.Fail $"expected the simulated update failure, got {other}"

        (group :> IDisposable).Dispose()

        match group.LimitEvidence() with
        | Ok evidence ->
            Assert.That(
                evidence.Processes,
                Is.EqualTo LimitVerdict.Tripped,
                "the axis a FAILED UpdateLimits named must still be recorded as capped — never NotTripped as if it were never asked about"
            )
        | Error err -> Assert.Fail $"{err}"

/// Test support for the per-process rlimit builders (`Command.Rlimit`). Deliberately a top-level private
/// module rather than `let` helpers on a fixture, matching this file's own
/// `WindowsIoRateControlTestSupport`/`LimitEvidenceTestSupport` convention.
module private RlimitTestSupport =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// A fresh, empty temp directory; the caller deletes it.
    let freshDir (label: string) : string =
        let directory =
            Path.Combine(Path.GetTempPath(), $"processkit-rlimit-{label}-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore
        directory

    /// Write a file that the library's OWN resolver accepts as a directly executable program on THIS
    /// host — a `.exe` where PATHEXT decides, a mode 700 file where the execute bit does (the expectation
    /// is derived from the runtime rather than hardcoded for one platform). Never executed by these
    /// tests: they exercise the rewrite that BUILDS a launch, not the launch itself.
    let writeExecutable (directory: string) (name: string) : string =
        let fileName = if isWindows then name + ".exe" else name
        let path = Path.Combine(directory, fileName)
        File.WriteAllText(path, "")

        if not isWindows then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        path

    /// Run `body resolvedHelper target` with the trusted-helper list pointing at a directory that holds a
    /// fake `prlimit` and a fake target program, restoring the real list afterwards. The helper path is
    /// the one the production resolver returns, never a second opinion about it, so the assertions
    /// compare against exactly what a spawn would launch.
    let withFakeHelper (body: string -> string -> unit) : unit =
        let directory = freshDir "helper"

        try
            writeExecutable directory "prlimit" |> ignore
            let target = writeExecutable directory "target"
            Native.Posix.trustedHelperDirectoriesForTests <- Some [ directory ]

            try
                match Native.Posix.trustedHelperPathForTests "prlimit" with
                | None -> Assert.Fail "the fake prlimit must resolve inside the overridden trusted directory"
                | Some resolvedHelper -> body resolvedHelper target
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
        finally
            Directory.Delete(directory, true)

    /// Run `body emptyTrustedDirectory` with the trusted-helper list pointing at a directory that holds
    /// nothing at all — the "this host has no util-linux" case, made deterministic on a host that does.
    let withoutHelper (body: string -> unit) : unit =
        let directory = freshDir "no-helper"

        try
            Native.Posix.trustedHelperDirectoriesForTests <- Some [ directory ]

            try
                body directory
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
        finally
            Directory.Delete(directory, true)

    /// The soft and hard columns `/proc/self/limits` prints for one row, as raw text (so `unlimited`
    /// survives the read instead of being coerced into a number).
    let procLimit (limitsText: string) (label: string) : (string * string) option =
        limitsText.Split '\n'
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> Array.tryPick (fun line ->
            if line.StartsWith(label, StringComparison.Ordinal) then
                let columns =
                    line.Substring(label.Length).Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

                if columns.Length >= 2 then
                    Some(columns[0], columns[1])
                else
                    None
            else
                None)

    /// The arguments of a rewritten command, as a plain list for a whole-value comparison.
    let argsOf (command: Command) : string list = command.Config.Args |> List.ofSeq

/// The typed per-process Unix rlimit builders (T-377): the `RlimitResource`/`Rlimit` value API and its
/// stable name mapping, the `Command.Rlimit` builder, the `prlimit` rewrite that applies the set before
/// the child's program starts, its precedence against the whole-tree `ResourceLimits.CpuTimeMax`, and the
/// honest typed refusals on a host (or platform) that cannot apply it.
[<TestFixture>]
type PerProcessRlimitTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    [<Test>]
    member _.``every resource has a stable name that round-trips through TryFromName and FromName``() =
        Assert.That(RlimitResource.All.Count, Is.EqualTo 6, "every rlimit resource must be enumerable")

        for resource in RlimitResource.All do
            Assert.That(RlimitResource.TryFromName resource.Name, Is.EqualTo(Some resource))
            Assert.That(RlimitResource.FromName resource.Name, Is.EqualTo resource)

        // The exact spellings are a compatibility surface, not an implementation detail: pin them.
        let names =
            RlimitResource.All
            |> Seq.map (fun resource -> resource.Name)
            |> String.concat ", "

        Assert.That(names, Is.EqualTo "cpu, core, data, file_size, no_file, stack")

    [<Test>]
    member _.``an unknown resource name is an honest miss, never a silent default``() =
        // Near misses a config file realistically produces: another spelling, another case, empty.
        for miss in [ "nofile"; "NoFile"; "Cpu"; "filesize"; "unknown"; "" ] do
            Assert.That(RlimitResource.TryFromName miss, Is.EqualTo(None: RlimitResource option), miss)

            match Assert.Throws<ArgumentException>(Action(fun () -> RlimitResource.FromName miss |> ignore)) with
            | null -> Assert.Fail $"'{miss}' must be refused, not resolved to some resource"
            | thrown ->
                // The typed error names every accepted spelling, so a config author can fix it from the
                // message rather than from the source.
                Assert.That(thrown.Message, Does.Contain "no_file")
                Assert.That(thrown.Message, Does.Contain "file_size")

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> RlimitResource.FromName Unchecked.defaultof<string> |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Rlimit renders its stable name and both values``() =
        let command = Command.create "tool" |> Command.rlimit RlimitResource.NoFile 64L 128L

        Assert.That(string command.Rlimits[0], Is.EqualTo "no_file=64:128")

    [<Test>]
    member _.``Command.Rlimit accumulates distinct resources and replaces a repeated one in place``() =
        let command =
            Command.create "tool"
            |> Command.rlimit RlimitResource.Core 1L 1L
            |> Command.rlimit RlimitResource.NoFile 32L 64L
            |> Command.rlimit RlimitResource.Core 0L 0L

        let rendered = command.Rlimits |> Seq.map string |> String.concat " "

        // Last write wins for a repeated resource, and it keeps its original position rather than moving
        // to the end — the same replace-in-place contract the Rust crate's `rlimit` builder has.
        Assert.That(rendered, Is.EqualTo "core=0:0 no_file=32:64")

        // A command that was never given one carries none at all.
        Assert.That(Command.create "tool" |> (fun command -> command.Rlimits) |> Seq.isEmpty, Is.True)

    [<Test>]
    member _.``Command.Rlimit rejects a negative value and a soft limit above its hard limit``() =
        let rejected (soft: int64) (hard: int64) =
            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () ->
                    Command.create "tool"
                    |> Command.rlimit RlimitResource.NoFile soft hard
                    |> ignore)
            )
            |> ignore

        rejected -1L 10L
        rejected 10L -1L
        rejected 65L 64L

        // The boundary itself is valid: soft may equal hard, and zero is a meaningful cap (no core dumps).
        let accepted =
            Command.create "tool"
            |> Command.rlimit RlimitResource.NoFile 64L 64L
            |> Command.rlimit RlimitResource.Core 0L 0L

        Assert.That(accepted.Rlimits.Count, Is.EqualTo 2)

    [<Test>]
    member _.``a command without rlimits or a group CPU-time cap is passed through untouched``() =
        let command = Command.create "tool" |> Command.arg "x"

        match Native.Posix.withProcessLimits None command with
        | Ok passed -> Assert.That(Object.ReferenceEquals(passed, command), Is.True, "no rewrite was needed")
        | Error error -> Assert.Fail $"an unlimited command must not be rewritten at all, got {error}"

    [<Test>]
    member _.``the rewrite launches the trusted prlimit helper with one option per resource, then the target``() =
        RlimitTestSupport.withFakeHelper (fun helper target ->
            let command =
                Command.create target
                |> Command.arg "--flag"
                |> Command.rlimit RlimitResource.Core 0L 0L
                |> Command.rlimit RlimitResource.NoFile 64L 128L
                |> Command.rlimit RlimitResource.FileSize 4096L 8192L
                |> Command.rlimit RlimitResource.Data 134217728L 134217728L
                |> Command.rlimit RlimitResource.Stack 4194304L 8388608L

            match Native.Posix.withProcessLimits None command with
            | Error error -> Assert.Fail $"the rewrite must succeed when the helper resolves, got {error}"
            | Ok rewritten ->
                Assert.That(rewritten.Program, Is.EqualTo helper, "the helper is launched by its trusted path")

                // Byte values reach the helper verbatim (prlimit takes bytes for every size resource), the
                // caller's order is preserved, `--` separates the limits from the target, and the target is
                // the RESOLVED absolute path so no PATH entry can interpose on it.
                let expected =
                    [ "--core=0:0"
                      "--nofile=64:128"
                      "--fsize=4096:8192"
                      "--data=134217728:134217728"
                      "--stack=4194304:8388608"
                      "--"
                      Path.GetFullPath target
                      "--flag" ]
                    |> String.concat "\n"

                Assert.That(RlimitTestSupport.argsOf rewritten |> String.concat "\n", Is.EqualTo expected)

                let spent: string =
                    "the limits are encoded in the argv now, so a second pass must not wrap a second helper"

                Assert.That(rewritten.Config.Rlimits.IsEmpty, Is.True, spent)

                match Native.Posix.withProcessLimits None rewritten with
                | Ok again -> Assert.That(Object.ReferenceEquals(again, rewritten), Is.True, spent)
                | Error error -> Assert.Fail $"the second pass must be a no-op, got {error}")

    [<Test>]
    member _.``a whole-tree CpuTimeMax and a per-process CPU rlimit resolve to the stricter of the two``() =
        RlimitTestSupport.withFakeHelper (fun _ target ->
            let withCpu (soft: int64) (hard: int64) =
                Command.create target |> Command.rlimit RlimitResource.Cpu soft hard

            let cpuOption (cpuTimeMax: TimeSpan option) (command: Command) =
                match Native.Posix.withProcessLimits cpuTimeMax command with
                | Error error -> failwith $"the rewrite must succeed, got {error}"
                | Ok rewritten ->
                    RlimitTestSupport.argsOf rewritten
                    |> List.tryFind (fun arg -> arg.StartsWith("--cpu=", StringComparison.Ordinal))

            // The per-process pair is stricter on both values, so it survives the group's looser cap.
            Assert.That(cpuOption (Some(TimeSpan.FromSeconds 100.0)) (withCpu 5L 6L), Is.EqualTo(Some "--cpu=5:6"))

            // ... and the other way round: a stricter GROUP cap is never relaxed by a looser per-process
            // one. The group's own rounding is unchanged (a soft second count, one extra second of hard
            // budget so the child can observe SIGXCPU first).
            Assert.That(cpuOption (Some(TimeSpan.FromSeconds 3.0)) (withCpu 50L 60L), Is.EqualTo(Some "--cpu=3:4"))

            // Each value is taken on its own, so a crossed pair still yields the stricter of each.
            Assert.That(cpuOption (Some(TimeSpan.FromSeconds 4.0)) (withCpu 2L 90L), Is.EqualTo(Some "--cpu=2:5"))

            // A group cap with no per-process CPU limit is carried by the same rewrite rather than a
            // second shim that could overwrite it — 2.5s rounds up to a 3 second soft budget.
            let other = Command.create target |> Command.rlimit RlimitResource.NoFile 64L 128L

            Assert.That(cpuOption (Some(TimeSpan.FromSeconds 2.5)) other, Is.EqualTo(Some "--cpu=3:4")))

    [<Test>]
    member _.``Arg0 combined with a per-process rlimit is a typed Unsupported, never a misapplied argv0``() =
        RlimitTestSupport.withFakeHelper (fun _ target ->
            let command =
                Command.create target
                |> Command.arg0 "override"
                |> Command.rlimit RlimitResource.NoFile 64L 128L

            match Native.Posix.withProcessLimits None command with
            | Error(ProcessError.Unsupported detail) ->
                Assert.That(detail, Does.Contain "Arg0")
                Assert.That(detail, Does.Contain "prlimit")
            | other -> Assert.Fail $"expected a typed Unsupported for Arg0 with a rlimit, got {other}")

    [<Test>]
    member _.``a host holding prlimit in no trusted directory is refused, never served an unlimited child``() =
        RlimitTestSupport.withoutHelper (fun emptyTrustedDirectory ->
            let command = Command.create "tool" |> Command.rlimit RlimitResource.NoFile 64L 128L

            match Native.Posix.withProcessLimits None command with
            | Error(ProcessError.ResourceLimit detail) ->
                Assert.That(detail, Does.Contain "prlimit")

                let namesSearched: string =
                    "the refusal must name the trusted directories that were actually searched"

                Assert.That(detail, Does.Contain emptyTrustedDirectory, namesSearched)
            | other -> Assert.Fail $"expected a typed ResourceLimit naming the missing helper, got {other}")

    [<Test>]
    member _.``a CpuTimeMax-only run still takes the shell shim and needs no util-linux helper``() =
        if isWindows then
            Assert.Ignore "The RLIMIT_CPU shim is the POSIX path (/bin/sh)."

        // The helper is deliberately absent: the whole-tree CPU-time cap predates the rlimit builders and
        // must keep working on a host without util-linux (macOS/BSD, a minimal image).
        RlimitTestSupport.withoutHelper (fun _ ->
            let command = Command.create "/bin/sh" |> Command.args [ "-c"; "exit 0" ]

            match Native.Posix.withProcessLimits (Some(TimeSpan.FromSeconds 2.0)) command with
            | Ok rewritten -> Assert.That(rewritten.Program, Is.EqualTo "/bin/sh")
            | Error error -> Assert.Fail $"a CPU-time-only run must not depend on prlimit, got {error}")

    [<Test>]
    member _.``Windows refuses a per-process rlimit with a typed Unsupported on both spawn paths``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The Windows refusal is what this asserts."

            let command =
                Command.create "cmd.exe"
                |> Command.args [ "/c"; "exit 0" ]
                |> Command.rlimit RlimitResource.NoFile 64L 128L

            match! command.OutputStringAsync() with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "Rlimit")
            | other -> Assert.Fail $"expected a typed Unsupported on Windows, got {other}"

            // The detached launch is refused on the same terms — an unowned child gets no weaker honesty.
            match command.LaunchDetached() with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "Rlimit")
            | other -> Assert.Fail $"expected a typed Unsupported for the detached launch, got {other}"
        }
        :> Task

    [<Test>]
    member _.``every supported resource reaches the child with the exact value that was asked for``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Reads /proc/self/limits, which only Linux has."

            if (Native.Posix.trustedHelperPathForTests "prlimit").IsNone then
                Assert.Ignore "This host holds no trusted prlimit helper (util-linux is not installed)."

            let command =
                Command.create "cat"
                |> Command.arg "/proc/self/limits"
                |> Command.rlimit RlimitResource.Cpu 60L 61L
                |> Command.rlimit RlimitResource.Core 0L 0L
                |> Command.rlimit RlimitResource.Data 134217728L 268435456L
                |> Command.rlimit RlimitResource.FileSize 4096L 8192L
                |> Command.rlimit RlimitResource.NoFile 64L 128L
                |> Command.rlimit RlimitResource.Stack 4194304L 8388608L

            match! command.OutputStringAsync() with
            | Error error -> Assert.Fail $"the capped child failed to run: {error}"
            | Ok result ->
                let reported (label: string) =
                    match RlimitTestSupport.procLimit result.Stdout label with
                    | Some pair -> pair
                    | None -> failwith $"/proc/self/limits carried no '{label}' row:\n{result.Stdout}"

                // Bytes are bytes: what the builder was given is what the kernel reports, with no block
                // rounding anywhere in between (the reason this path uses prlimit, not a shell ulimit).
                Assert.That(reported "Max cpu time", Is.EqualTo(("60", "61")))
                Assert.That(reported "Max core file size", Is.EqualTo(("0", "0")))
                Assert.That(reported "Max data size", Is.EqualTo(("134217728", "268435456")))
                Assert.That(reported "Max file size", Is.EqualTo(("4096", "8192")))
                Assert.That(reported "Max open files", Is.EqualTo(("64", "128")))
                Assert.That(reported "Max stack size", Is.EqualTo(("4194304", "8388608")))
        }
        :> Task

    [<Test>]
    member _.``the stricter of a group CpuTimeMax and a per-process CPU rlimit is what the child gets``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Reads /proc/self/limits, which only Linux has."

            if (Native.Posix.trustedHelperPathForTests "prlimit").IsNone then
                Assert.Ignore "This host holds no trusted prlimit helper (util-linux is not installed)."

            let cpuTimeOf (groupSeconds: float) (soft: int64) (hard: int64) =
                task {
                    let options =
                        ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromSeconds groupSeconds)

                    match ProcessGroup.Create options with
                    | Error error -> return failwith $"CPU-time limited group creation failed: {error}"
                    | Ok group ->
                        use group = group

                        let command =
                            Command.create "cat"
                            |> Command.arg "/proc/self/limits"
                            |> Command.rlimit RlimitResource.Cpu soft hard

                        match! group.StartAsync command with
                        | Error error -> return failwith $"the capped child failed to start: {error}"
                        | Ok running ->
                            use running = running

                            match! running.OutputStringAsync() with
                            | Error error -> return failwith $"the capped child failed: {error}"
                            | Ok result ->
                                match RlimitTestSupport.procLimit result.Stdout "Max cpu time" with
                                | Some pair -> return pair
                                | None -> return failwith $"no CPU-time row:\n{result.Stdout}"
                }

            // A looser group cap never relaxes the per-process one...
            let! strictPerProcess = cpuTimeOf 100.0 5L 6L
            Assert.That(strictPerProcess, Is.EqualTo(("5", "6")))

            // ... and a looser per-process pair never relaxes the group's cap.
            let! strictGroup = cpuTimeOf 3.0 50L 60L
            Assert.That(strictGroup, Is.EqualTo(("3", "4")))
        }
        :> Task

    [<Test>]
    member _.``a POSIX host without the helper refuses the spawn itself, not just the rewrite``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Windows refuses with Unsupported instead; asserted separately."

            let directory = RlimitTestSupport.freshDir "spawn-no-helper"

            try
                Native.Posix.trustedHelperDirectoriesForTests <- Some [ directory ]

                try
                    let command =
                        Command.create "/bin/sh"
                        |> Command.args [ "-c"; "exit 0" ]
                        |> Command.rlimit RlimitResource.NoFile 64L 128L

                    match! command.OutputStringAsync() with
                    | Error(ProcessError.ResourceLimit detail) -> Assert.That(detail, Does.Contain "prlimit")
                    | Ok result ->
                        Assert.Fail
                            $"the run must be refused when no trusted prlimit exists, but it ran ({result.Outcome})"
                    | Error other -> Assert.Fail $"expected a typed ResourceLimit, got {other}"
                finally
                    Native.Posix.trustedHelperDirectoriesForTests <- None
            finally
                Directory.Delete(directory, true)
        }
        :> Task

    [<Test>]
    member _.``a dry run reports the configured limits instead of dropping them from the preview``() =
        let command =
            Command.create "tool"
            |> Command.arg "build"
            |> Command.rlimit RlimitResource.NoFile 64L 128L
            |> Command.rlimit RlimitResource.Core 0L 0L

        let render = ProcessKit.Testing.DryRunRunner.Render command

        Assert.That(render, Is.EqualTo "tool build (rlimits: no_file=64:128, core=0:0)")

        // A command without limits renders exactly as it always did.
        Assert.That(
            ProcessKit.Testing.DryRunRunner.Render(Command.create "tool" |> Command.arg "build"),
            Is.EqualTo "tool build"
        )

    [<Test>]
    member _.``a command carrying rlimits records and replays through a cassette``() : Task =
        task {
            let path =
                Path.Combine(Path.GetTempPath(), $"processkit-rlimit-cassette-{Guid.NewGuid():N}.json")

            let command =
                Command.create "tool"
                |> Command.arg "build"
                |> Command.rlimit RlimitResource.NoFile 64L 128L

            try
                // The inner runner is the dry run: recording exercises the whole cassette path on every
                // platform without a real spawn (a Windows spawn would be refused, by design).
                let recorder =
                    ProcessKit.Testing.RecordReplayRunner.Record(path, ProcessKit.Testing.DryRunRunner())

                match! (recorder :> IProcessRunner).CaptureStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Does.Contain "no_file=64:128")
                | Error error -> Assert.Fail $"recording a command with rlimits failed: {error}"

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"saving the cassette failed: {error}"

                // Replay serves the recording without spawning anything, so the limits neither refuse the
                // run nor go missing from what was recorded about it.
                match ProcessKit.Testing.RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"loading the cassette failed: {error}"
                | Ok replayer ->
                    match! (replayer :> IProcessRunner).CaptureStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Does.Contain "no_file=64:128")
                    | Error error -> Assert.Fail $"replaying a command with rlimits failed: {error}"
            finally
                if File.Exists path then
                    File.Delete path
        }
        :> Task
