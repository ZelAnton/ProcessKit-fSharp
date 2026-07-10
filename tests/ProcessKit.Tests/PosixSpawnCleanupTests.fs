namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Regression tests for the POSIX `spawnPosix` hardening (T-029): a requested `CurrentDir` is honored
/// (never silently dropped to the parent's working directory), and the spawn path — success and honest
/// failure alike — leaks no file descriptors.
///
/// Direct P/Invoke fault injection into `posix_spawn_file_actions_*` is not available from a test, so
/// the `addchdir_np`-absent branch (glibc < 2.29 / macOS < 10.15 raising `EntryPointNotFoundException`
/// → typed `ProcessError.Unsupported`) cannot be exercised on a modern CI libc; it is covered by the
/// code path's own local handler and documented there. What is testable here — and is — is the positive
/// contract (CurrentDir actually takes effect) and the fd hygiene the fix guarantees.
///
/// T-074 extends this with exception-safety on both sides of `posix_spawnp`, driven through
/// `Native.Posix`'s internal fault-injection seams (`marshalCStringFaultForTests`,
/// `setpriorityForTests`, `streamWrapFaultForTests`): a fault while marshalling argv/envp frees every
/// partially built unmanaged block; a fault AFTER the spawn (a refused priority, or any per-stream wrap
/// failure) kills and reaps the already-running child and closes every parent/child fd exactly once,
/// returning the original `ProcessError.Spawn` — never a raw exception, a leaked descriptor, or a
/// stranded child. The seams are process-wide mutables set and reset in a `finally`; the fixture runs
/// sequentially (no `[Parallelizable]`), so they never race a concurrent spawn.
[<TestFixture>]
type PosixSpawnCleanupTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Number of open fds this process currently holds. Linux-only (reads /proc/self/fd); the enumeration
    // opens a transient dirfd that is closed again before this returns, so it does not skew the count.
    let openFdCount () =
        Directory.GetFileSystemEntries("/proc/self/fd").Length

    // Count our own live/zombie child processes (Linux-only, via /proc). A child spawned directly through
    // `Native.Posix.spawnPosix` has this test host as its parent until it is reaped, so a fault path that
    // failed to kill+reap it would surface here as an extra child — a live `sleep`, or its unreaped zombie.
    let ourChildCount () =
        let self = string (Environment.ProcessId)
        let mutable count = 0

        for dir in Directory.GetDirectories "/proc" do
            match Int32.TryParse(Path.GetFileName dir) with
            | true, _ ->
                try
                    // /proc/<pid>/stat is `pid (comm) state ppid ...`; comm may hold spaces/parens, so read
                    // ppid as the second field AFTER the last ')'.
                    let stat = File.ReadAllText(Path.Combine(dir, "stat"))
                    let closeParen = stat.LastIndexOf ')'

                    if closeParen >= 0 then
                        let fields =
                            stat.Substring(closeParen + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

                        if fields.Length >= 2 && fields[1] = self then
                            count <- count + 1
                with _ ->
                    // The process exited between the directory listing and the read (or /proc denied it): it
                    // is not a leaked child of ours, so skip it rather than fail the scan.
                    ()
            | false, _ -> ()

        count

    // Run `command` through `Native.Posix.spawnPosix` with a fault seam installed (always reset in a
    // `finally`) and assert it comes back as an honest `ProcessError.Spawn` — never a raw exception, and
    // never `Ok` (which would mean the fault did not fire and a child may have leaked). Cross-platform:
    // this alone is the macOS-observable contract.
    let expectFaultedSpawn (install: unit -> unit) (reset: unit -> unit) (command: Command) =
        install ()

        try
            match Native.Posix.spawnPosix command with
            | Error(ProcessError.Spawn _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Spawn from the injected fault, got {other}"
            | Ok spawned ->
                // The fault did not fail the spawn: a live child leaked. Tear it down so the test host is
                // not polluted, then fail.
                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                Native.Posix.killProcess (int spawned.Handle)
                Native.Posix.reapLeader (int spawned.Handle)
                Assert.Fail "the fault seam did not fail the spawn — a child may have leaked"
        finally
            reset ()

    // Repeatedly run a faulted spawn and assert it strands neither a file descriptor nor a child process
    // (both Linux-only, via /proc). A single warm-up (also the cross-platform honest-error check) lets
    // one-time fds/children settle into the baseline, then N faulted spawns are bracketed with counts.
    let assertFaultedSpawnIsClean (install: unit -> unit) (reset: unit -> unit) (command: Command) : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the posix_spawn exception-safety path"

            // Warm up once and confirm the fault path returns an honest error on every POSIX host.
            expectFaultedSpawn install reset command

            if isLinux then
                GC.Collect()
                GC.WaitForPendingFinalizers()
                do! Task.Delay 50
                let fdBefore = openFdCount ()
                let childBefore = ourChildCount ()

                for _ in 1..30 do
                    expectFaultedSpawn install reset command

                GC.Collect()
                GC.WaitForPendingFinalizers()
                do! Task.Delay 100
                let fdAfter = openFdCount ()
                let childAfter = ourChildCount ()

                Assert.That(
                    fdAfter,
                    Is.LessThanOrEqualTo(fdBefore + 8),
                    $"file descriptors grew from {fdBefore} to {fdAfter} across 30 faulted spawns — an fd leak on the failure path"
                )

                Assert.That(
                    childAfter,
                    Is.LessThanOrEqualTo(childBefore + 2),
                    $"live/zombie child processes grew from {childBefore} to {childAfter} across 30 faulted spawns \
                      — a child was not killed+reaped on the failure path"
                )
        }

    [<Test>]
    member _.``a CurrentDir set on POSIX runs the child in that directory (not silently the parent's)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises posix_spawn_file_actions_addchdir_np"

            let dir = Directory.CreateTempSubdirectory("pk-cwd-").FullName

            try
                // A marker file inside `dir`, read via a relative name: the child can only find it if its
                // working directory really is `dir`. This proves CurrentDir took effect without any host
                // path normalization / symlink-resolution (macOS /tmp -> /private/tmp) getting in the way.
                let markerName = "pk-marker.txt"
                let markerContent = $"cwd-ok-{Guid.NewGuid():N}"
                File.WriteAllText(Path.Combine(dir, markerName), markerContent)

                let cmd = (shell $"cat {markerName}") |> Command.currentDir dir

                match! cmd.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout.Trim(), Is.EqualTo markerContent)
                | Error err -> Assert.Fail $"CurrentDir run failed: {err.Message}"
            finally
                Directory.Delete(dir, true)
        }

    [<Test>]
    member _.``a spawn that fails to launch (unknown program) still yields an honest error, not a hang``() : Task =
        task {
            // The child-side pipes/fds are created before posix_spawnp even runs; an unknown program makes
            // it fail with ENOENT. The error path must close every fd it opened and report honestly.
            let cmd =
                Command.create "pk-definitely-not-a-program-xyz"
                |> Command.currentDir (Path.GetTempPath())
                |> Command.stdout StdioMode.Piped

            match! cmd.OutputStringAsync() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected an error spawning an unknown program"
        }

    [<Test>]
    member _.``repeated POSIX spawns (success and failure) do not leak file descriptors``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: counts open descriptors via /proc/self/fd"

            let runOnce () : Task =
                task {
                    // Success path: three piped std streams created, dup2'd, then closed/handed off.
                    let! _ = (shell "exit 0" |> Command.stdout StdioMode.Piped).OutputStringAsync()

                    // Honest-failure path: pipes created, then posix_spawnp fails (ENOENT) and the error
                    // path must close every fd it opened (the exact cleanup this fix hardened).
                    let! _ =
                        (Command.create "pk-definitely-not-a-program-xyz"
                         |> Command.currentDir "/"
                         |> Command.stdout StdioMode.Piped)
                            .OutputStringAsync()

                    ()
                }

            // Warm up so one-time fds (the shared SIGCHLD registration, thread-pool eventfds, JIT) are
            // already open and counted in the baseline rather than mistaken for a leak.
            do! runOnce ()
            GC.Collect()
            GC.WaitForPendingFinalizers()
            do! Task.Delay 50
            let before = openFdCount ()

            for _ in 1..40 do
                do! runOnce ()

            // Force finalization of any SafeFileHandle-backed stream that has already been dropped, so the
            // count reflects settled state rather than pending-GC fds.
            GC.Collect()
            GC.WaitForPendingFinalizers()
            do! Task.Delay 50
            let after = openFdCount ()

            // A real fd leak over 40 iterations (each opening several pipe/devnull fds) would blow far past
            // this; the small slack tolerates benign runtime jitter (thread-pool growth, timers).
            Assert.That(
                after,
                Is.LessThanOrEqualTo(before + 8),
                $"file descriptors grew from {before} to {after} across 40 spawns — likely an fd leak"
            )
        }

    [<Test>]
    member _.``a marshalling fault after the first argv string is allocated frees every partial block and leaks no fd``
        ()
        : Task =
        // The fault throws once at least one unmanaged argv string has been allocated, so the partial-free
        // path in `marshalCStringArray` runs. This is before `posix_spawnp`, so no child is ever created —
        // the check is that the honest error surfaces and the stdio fds opened up front are all closed.
        let install () =
            Native.Posix.marshalCStringFaultForTests <-
                Some(fun allocated ->
                    if allocated >= 1 then
                        failwith "injected marshalling fault (partial argv allocated)")

        let reset () =
            Native.Posix.marshalCStringFaultForTests <- None
        // `/bin/sh -c "exit 0"` is 3 argv items, so at least one is allocated before the fault fires.
        assertFaultedSpawnIsClean install reset (shell "exit 0")

    [<Test>]
    member _.``a priority fault after posix_spawnp kills and reaps the child and leaks no fd``() : Task =
        // Force `setpriority` to report failure regardless of privilege, so the just-spawned leader must be
        // killed+reaped rather than left running at an unintended priority.
        let install () =
            Native.Posix.setpriorityForTests <- Some(fun _ _ _ -> -1)

        let reset () =
            Native.Posix.setpriorityForTests <- None

        let command =
            shell "sleep 10" |> Command.priority Priority.Normal |> Command.keepStdinOpen

        assertFaultedSpawnIsClean install reset command

    [<TestCase("stdout")>]
    [<TestCase("stderr")>]
    [<TestCase("stdin")>]
    member _.``a per-stream wrap fault after posix_spawnp kills and reaps the child and leaks no fd``
        (slot: string)
        : Task =
        // Throw while wrapping the named parent-side stream into its Socket/NetworkStream. Streams are
        // wrapped in stdout, stderr, stdin order, so faulting "stderr"/"stdin" also exercises disposing the
        // stream(s) already built. The child (a live `sleep`) must be killed+reaped and every parent/child
        // fd released exactly once. All three streams exist: stdout/stderr default to Piped, and
        // `keepStdinOpen` adds the stdin pipe.
        let install () =
            Native.Posix.streamWrapFaultForTests <-
                Some(fun label ->
                    if label = slot then
                        failwith $"injected stream-wrap fault for {label}")

        let reset () =
            Native.Posix.streamWrapFaultForTests <- None

        assertFaultedSpawnIsClean install reset (shell "sleep 10" |> Command.keepStdinOpen)
