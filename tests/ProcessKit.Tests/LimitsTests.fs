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

[<TestFixture>]
type LimitsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

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
