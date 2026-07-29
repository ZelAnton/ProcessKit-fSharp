namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

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

        member _.Release(_spawned) = ()
        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(_spawned) = None
        member _.KillChild(_spawned) = ()
        member _.KillTree() = ()
        member _.GracefulKillTree (_signal) (_grace) = Task.CompletedTask
        member _.SignalChild(_spawned, _signal) = Ok()
        member _.Members() = Ok []
        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(0, None, None, None))

        member _.UpdateLimits(limits) =
            if shouldFail limits then
                // Model a limit-capable backend whose native apply failed partway and then best-effort
                // restored the previous set: nothing net changed (InForce stays put), and it surfaces the
                // honest typed refusal — exactly what the real Windows/cgroup backends now do (T-207).
                Error(ProcessError.ResourceLimit "simulated partial apply failure (previous set restored)")
            else
                inForce <- limits
                Ok()

        member _.HardRelease() = ()

[<TestFixture>]
type LimitsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

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

    // Drain an async sequence (the streaming event verbs) into a list for assertions.
    let collect (items: IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let e = items.GetAsyncEnumerator()
            let mutable more = true

            while more do
                match! e.MoveNextAsync() with
                | true -> acc.Add e.Current
                | false -> more <- false

            do! e.DisposeAsync()
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
                        // Drain the merged pty stream in the background: the child prints CTTY-OK, then
                        // sleeps, so this completes only once the run is killed below.
                        let collectTask = collect (running.OutputEventsAsync())

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
