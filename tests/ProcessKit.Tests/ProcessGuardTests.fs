namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// The assembly-wide safety net (`GlobalProcessGuard`, T-394): no process this run spawns may outlive the
/// test host, including the two classes of child the library deliberately does not contain — a
/// `Command.LaunchDetached` opt-out and the ConPTY sidecar.
///
/// The guarantee is proven in three parts, because a test process cannot watch its own death: every child
/// the host spawns is a member of the guard's Job (asserted directly, on a genuinely stranded child),
/// closing the last handle to a Job built by that same code path kills whatever is still in it (asserted
/// directly, on a Job this fixture owns), and the kernel closes a dying process's handles — the OS
/// guarantee the library itself already leans on for `Command.KillOnParentDeath` on Windows.
[<TestFixture>]
type ProcessGuardTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// A single-process sleeper that spawns no grandchildren, so a membership assertion sees exactly the
    /// process this fixture started. Windows `ping` sends one packet per second after the first, so
    /// `-n (seconds + 1)` lives roughly `seconds`.
    let sleeper (seconds: int) =
        if isWindows then
            Command.create "ping"
            |> Command.args [ "-n"; string (seconds + 1); "127.0.0.1" ]
            |> Command.stdout StdioMode.Null
        else
            Command.create "sleep" |> Command.args [ string seconds ]

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    let isAlive (pid: int) =
        try
            use proc = Process.GetProcessById pid
            not proc.HasExited
        with :? ArgumentException ->
            // `GetProcessById` throws `ArgumentException` for a pid that is not running — the "gone"
            // answer this predicate reports, not an error to propagate.
            false

    let killQuietly (pid: int) =
        try
            use proc = Process.GetProcessById pid
            proc.Kill()
        with _ ->
            // Best-effort cleanup: the child may already have exited (`ArgumentException`) or be exiting
            // concurrently (`InvalidOperationException`/`Win32Exception`). A cleanup failure must never
            // fail the assertion under test.
            ()

    let waitUntil (timeout: TimeSpan) (predicate: unit -> bool) =
        let deadline = DateTime.UtcNow + timeout
        let mutable ok = predicate ()

        while not ok && DateTime.UtcNow < deadline do
            Thread.Sleep 50
            ok <- predicate ()

        ok

    /// The live guard Job, or an ignored test off Windows. A guard that Windows refused to install is a
    /// real loss of the guarantee this fixture exists for, so it fails here (loudly, in one diagnostic
    /// test) rather than being skipped — installing it never fails the rest of the suite.
    let guardJob () =
        match GlobalProcessGuard.state () with
        | ProcessGuardState.Guarded -> GlobalProcessGuard.guardJob ()
        | ProcessGuardState.NotApplicable reason ->
            Assert.Ignore $"the process guard is Windows-only: {reason}"
            IntPtr.Zero
        | ProcessGuardState.Unavailable reason ->
            Assert.Fail $"the test host is running unguarded: {reason}"
            IntPtr.Zero
        | ProcessGuardState.NotInstalled ->
            Assert.Fail "the [<SetUpFixture>] that installs the process guard did not run"
            IntPtr.Zero

    [<Test>]
    member _.``the test host itself is enrolled in the guard, or honestly reports why not``() =
        match GlobalProcessGuard.state () with
        | ProcessGuardState.NotInstalled ->
            Assert.Fail "the [<SetUpFixture>] that installs the process guard did not run"
        | ProcessGuardState.NotApplicable reason ->
            // Off Windows the guard is a documented no-op, not a silent one: it says so, and it says why.
            Assert.That(isWindows, Is.False, "Windows must not report the guard as inapplicable")
            Assert.That(String.IsNullOrWhiteSpace reason, Is.False, "a no-op guard must explain itself")
            Assert.That(GlobalProcessGuard.guardJob (), Is.EqualTo IntPtr.Zero)
        | ProcessGuardState.Unavailable reason -> Assert.Fail $"the test host is running unguarded: {reason}"
        | ProcessGuardState.Guarded ->
            Assert.That(isWindows, Is.True, "only Windows has a Job Object to be guarded by")
            let job = GlobalProcessGuard.guardJob ()
            Assert.That(job, Is.Not.EqualTo IntPtr.Zero, "a guarded host must hold its Job handle")

            match GlobalProcessGuard.isPidInJob job (Environment.ProcessId) with
            | Ok true -> ()
            | Ok false -> Assert.Fail "the test host is not a member of the Job it is supposed to be guarded by"
            | Error reason -> Assert.Fail $"could not ask whether the host is in the guard Job: {reason}"

    [<Test>]
    member _.``the guard Job carries kill-on-close and no other limit``() =
        let job = guardJob ()

        // The whole cost argument for wrapping the host rests on this: an ancestor Job with no resource
        // limits of its own has nothing to intersect with the caps `LimitsTests` sets on the library's
        // nested Jobs. A limit added here would silently tighten every group in the suite.
        match GlobalProcessGuard.jobLimitFlags job with
        | Error reason -> Assert.Fail $"could not read the guard Job's limits: {reason}"
        | Ok flags ->
            Assert.That(
                flags,
                Is.EqualTo GlobalProcessGuard.expectedLimitFlags,
                "the guard Job must carry KILL_ON_JOB_CLOSE and nothing else"
            )

    [<Test>]
    member _.``a child stranded by a test that never reaches its own cleanup is still in the guard Job``() =
        let job = guardJob ()
        let mutable strandedPid = 0

        // Exactly the scenario the guard exists for: a test spawns the one child nothing in the library
        // owns (a detached launch — no group, no handle, no teardown) and then fails before its `finally`.
        try
            match (sleeper 300).LaunchDetached() with
            | Error error -> Assert.Fail $"detached launch failed: {error}"
            | Ok detached ->
                strandedPid <- detached.Pid
                failwith "simulated test failure between the spawn and the cleanup"
        with
        | :? AssertionException ->
            // `Assert.Fail` above already recorded the real failure; let it stand.
            reraise ()
        | _ ->
            // The simulated failure. Nothing has killed the child, and by design nothing can — that is
            // the point of the assertions below.
            ()

        Assert.That(strandedPid, Is.Not.Zero, "the simulated failure must leave a real pid behind")
        Assert.That(isAlive strandedPid, Is.True, "the stranded child should still be running")

        match GlobalProcessGuard.isPidInJob job strandedPid with
        | Ok true -> ()
        | Ok false ->
            killQuietly strandedPid

            Assert.Fail
                "a child of the test host escaped the guard Job and would outlive the run — the usual cause is an ancestor Job carrying JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK, which makes the kernel create this host's children outside the whole job chain"
        | Error reason ->
            killQuietly strandedPid
            Assert.Fail $"could not ask whether the stranded child is in the guard Job: {reason}"

        // Deliberately NOT killed here. Cleaning it up would make this test prove nothing: the guard is
        // now the only thing that will ever reap it, and it does so when the host exits. The pid is
        // reported so the end-to-end claim can be checked from outside the run — after the host exits,
        // this process must be gone, even though it was started with minutes left to live. (It is bounded
        // at ~5 minutes so that a host that somehow ran unguarded cannot leak it indefinitely either.)
        TestContext.Out.WriteLine $"stranded pid {strandedPid}: the guard must reap it when this test host exits"

    [<Test>]
    member _.``closing the last handle to a guard-shaped Job kills the survivors``() =
        if not isWindows then
            Assert.Ignore "Windows-only: the guard is a Job Object"
        else

            // The other half of the proof. The guard's own Job cannot be closed to demonstrate this (it would
            // kill the test host), so this builds one through the SAME creation path, models a run that ends
            // with a survivor still in it, and closes the last handle — the exact event the kernel performs on
            // the guard's handle while tearing the host down.
            let job =
                match GlobalProcessGuard.createKillOnCloseJob () with
                | Ok job -> job
                | Error reason ->
                    Assert.Fail $"could not create the Job: {reason}"
                    IntPtr.Zero

            let mutable hostStandInPid = 0
            let mutable survivorPid = 0
            let mutable jobClosed = false

            try
                match (sleeper 300).LaunchDetached(), (sleeper 300).LaunchDetached() with
                | Error error, _
                | _, Error error -> Assert.Fail $"detached launch failed: {error}"
                | Ok hostStandIn, Ok survivor ->
                    hostStandInPid <- hostStandIn.Pid
                    survivorPid <- survivor.Pid

                    for pid in [ hostStandInPid; survivorPid ] do
                        match GlobalProcessGuard.assignPidToJob job pid with
                        | Ok() -> ()
                        | Error reason -> Assert.Fail $"could not put {pid} into the Job: {reason}"

                    // A member's death is not the trigger: the "host" goes, the survivor stays.
                    killQuietly hostStandInPid

                    Assert.That(
                        waitUntil (TimeSpan.FromSeconds 30.0) (fun () -> not (isAlive hostStandInPid)),
                        Is.True,
                        "the stand-in host did not exit"
                    )

                    Assert.That(
                        isAlive survivorPid,
                        Is.True,
                        "the survivor must outlive the process that left it behind"
                    )

                    // ...and then the last handle goes, as it does when the kernel tears down the real host.
                    GlobalProcessGuard.closeJob job
                    jobClosed <- true

                    Assert.That(
                        waitUntil (TimeSpan.FromSeconds 30.0) (fun () -> not (isAlive survivorPid)),
                        Is.True,
                        "a survivor outlived the closing of the kill-on-close Job it was in"
                    )
            finally
                if not jobClosed then
                    GlobalProcessGuard.closeJob job

                killQuietly hostStandInPid
                killQuietly survivorPid

    [<Test>]
    member _.``a detached child is still outside every ProcessGroup, and inside the guard Job``() : Task =
        task {
            let job = guardJob ()
            use group = create ()
            let mutable detachedPid = 0

            try
                match! group.StartAsync(sleeper 30) with
                | Error error -> Assert.Fail $"{error}"
                | Ok contained ->
                    match (sleeper 30).LaunchDetached() with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok detached ->
                        detachedPid <- detached.Pid

                        let members =
                            match group.Members() with
                            | Ok pids -> Set.ofSeq pids
                            | Error error -> failwith $"Members failed: {error}"

                        // The claim `DetachedLaunchTests` makes is about the library's containment, and the
                        // harness Job does not make it false: the detached child is in no group, and it
                        // still outlives the disposal that reaps a contained sibling. What changed is only
                        // that it can no longer outlive the HOST.
                        match contained.Pid with
                        | Some pid ->
                            Assert.That(
                                members.Contains pid,
                                Is.True,
                                "the contained child is missing from the group's membership"
                            )
                        | None -> Assert.Fail "expected a pid for the contained child"

                        Assert.That(
                            members.Contains detachedPid,
                            Is.False,
                            "a detached child must not be a member of any ProcessGroup"
                        )

                        for pid in Set.add detachedPid members do
                            match GlobalProcessGuard.isPidInJob job pid with
                            | Ok true -> ()
                            | Ok false -> Assert.Fail $"{pid} was spawned by the test host but escaped the guard Job"
                            | Error reason -> Assert.Fail $"could not ask whether {pid} is in the guard Job: {reason}"

                        (group :> IDisposable).Dispose()

                        Assert.That(
                            isAlive detachedPid,
                            Is.True,
                            "the detached child was killed by a ProcessGroup disposal it is not contained by"
                        )
            finally
                if detachedPid <> 0 then
                    killQuietly detachedPid
        }
        :> Task

    [<Test>]
    member _.``the ConPTY sidecar the library cannot contain is inside the guard Job``() : Task =
        task {
            let job = guardJob ()
            let runner: IProcessRunner = JobRunner()

            let members () =
                match GlobalProcessGuard.jobMemberPids job with
                | Ok pids -> Set.ofList pids
                | Error reason -> failwith $"could not enumerate the guard Job: {reason}"

            // Fixtures in this assembly run sequentially (nothing here is `[<Parallelizable>]`), so the
            // processes that join the Job between this snapshot and the assertion below are the ones this
            // test started: the ConPTY child, its `ping` grandchild, and the console host under test.
            let before = members ()

            let isConsoleHost pid =
                try
                    use proc = Process.GetProcessById pid
                    let name = proc.ProcessName

                    name.StartsWith("conhost", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("OpenConsole", StringComparison.OrdinalIgnoreCase)
                with _ ->
                    // The pid was read from a point-in-time job snapshot and can be gone (or briefly
                    // unreadable) by the time it is inspected; a member we cannot name is simply not the
                    // console host this poll is looking for.
                    false

            let cmd =
                Command.create "cmd.exe"
                |> Command.args [ "/c"; "ping -n 6 127.0.0.1 >NUL" ]
                |> Command.pty
                |> Command.timeout (TimeSpan.FromSeconds 60.0)

            match! runner.StartAsync(cmd, CancellationToken.None) with
            | Error(ProcessError.Unsupported message) when message.Contains "1809" ->
                // Pre-1809 host without ConPTY — the documented typed-Unsupported path.
                Assert.Ignore $"host lacks ConPTY: {message}"
            | Error error -> Assert.Fail $"ConPTY spawn failed: {error}"
            | Ok running ->
                use running = running

                // `CreatePseudoConsole` spins the console host up OUTSIDE the Job the library places the
                // child in — the documented containment divergence (`Native.Windows.fs`). It is still a
                // process this host spawned, so the harness Job above it does contain it, and it cannot
                // outlive the run.
                let contained =
                    waitUntil (TimeSpan.FromSeconds 30.0) (fun () ->
                        Set.difference (members ()) before |> Set.exists isConsoleHost)

                running.Kill()
                let! _ = running.WaitAsync()

                Assert.That(
                    contained,
                    Is.True,
                    "the ConPTY console host escaped the guard Job and would outlive the test run"
                )
        }
        :> Task
