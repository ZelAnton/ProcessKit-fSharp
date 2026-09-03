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

    /// Poll a marker until it appears or the bounded deadline expires. The priority fault seam uses this
    /// to let a detached leader create a descendant before cleanup starts.
    let waitForFile (path: string) (timeout: TimeSpan) =
        let deadline = DateTime.UtcNow + timeout
        let mutable found = File.Exists path

        while not found && DateTime.UtcNow < deadline do
            Thread.Sleep 10
            found <- File.Exists path

        found

    let quoteShellPath (path: string) =
        let escaped = path.Replace("'", "'\\''", StringComparison.Ordinal)
        "'" + escaped + "'"

    // `/proc/<pid>/stat` past its `comm` field: the file is `pid (comm) state ppid ...` and `comm` may
    // hold spaces/parens, so the remaining fields are read after the LAST ')' — `[0]` is the state and
    // `[1]` the parent pid. Linux-only; the KillOnParentDeath guard tests below use it to know which pid
    // the guard really compares against instead of assuming one.
    let statFieldsAfterComm (pid: string) =
        let stat = File.ReadAllText $"/proc/{pid}/stat"
        let closeParen = stat.LastIndexOf ')'
        stat.Substring(closeParen + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

    /// This test host's own parent pid.
    let ourParentPid () =
        match Int32.TryParse((statFieldsAfterComm "self")[1]) with
        | true, ppid -> ppid
        | false, _ -> -1

    /// True while `pid` still names a live (not yet reaped, not zombie) process.
    let processLive (pid: int) =
        try
            let fields = statFieldsAfterComm (string pid)
            fields.Length > 0 && fields[0] <> "Z"
        with _ ->
            // No /proc entry (or it vanished mid-read): the process is gone, which is exactly the state
            // the caller is polling for — not a failure to report.
            false

    /// Poll `condition` until it holds or the bounded deadline expires.
    let waitUntil (condition: unit -> bool) (timeout: TimeSpan) =
        let deadline = DateTime.UtcNow + timeout
        let mutable ok = condition ()

        while not ok && DateTime.UtcNow < deadline do
            Thread.Sleep 25
            ok <- condition ()

        ok

    /// The `setpriv` the library itself would load — a trusted-directory match, never a `PATH` copy (see
    /// the pdeathsig test below for why the gate must ask the resolver rather than `Exec.which`).
    let trustedSetpriv () =
        Native.Posix.trustedHelperPathForTests "setpriv"

    // The middle process of the parent-death race harness (see the section comment further down): fork the
    // inner shell, handing it this shell's own pid, then die — immediately, or, in `armed` mode, once the
    // target the inner shell launched is running.
    // Positional: $1 = inner script, $2 = setpriv, $3 = guard script, $4 = reached marker, $5 = mode,
    // $6 = wait marker, $7... = target argv.
    let parentDeathMiddleScript =
        """inner=$1
shift
/bin/sh -c "$inner" sh "$$" "$@" &
if [ "$4" = armed ]; then
    i=0
    while [ ! -f "$5" ] && [ "$i" -lt 300 ]; do
        i=$((i + 1))
        sleep 0.1
    done
fi
exit 0
"""

    // The inner process: in `armed` mode run the chain straight away (the parent is still alive, so the
    // arming is what must protect the target); otherwise spin until the kernel has really reparented this
    // shell away from the middle process, and only then run the chain — with either the dead spawner's pid
    // (`stale`, the pre-arm race) or the pid of the parent it actually has now (`reaper`, the case that
    // must still run, whether that parent is PID 1 or a subreaper). `/proc/$$/stat`'s fourth field is the
    // CURRENT parent pid, unlike `$PPID`, which the shell snapshots once at startup. The pid the chain is
    // given is written to the reached marker first, so a test can tell "the harness never got there" apart
    // from "the guard stopped it".
    // Positional: $1 = middle pid, $2 = setpriv, $3 = guard script, $4 = reached marker, $5 = mode,
    // $6 = wait marker, $7... = target argv.
    let parentDeathInnerScript =
        """mid=$1
sp=$2
g=$3
reached=$4
mode=$5
shift 6
if [ "$mode" = armed ]; then
    e=$mid
else
    i=0
    while [ "$(cut -d' ' -f4 /proc/$$/stat)" = "$mid" ] && [ "$i" -lt 200 ]; do
        i=$((i + 1))
        sleep 0.05
    done
    if [ "$mode" = stale ]; then
        e=$mid
    else
        e=$(cut -d' ' -f4 /proc/$$/stat)
    fi
fi
echo "$e" > "$reached"
exec "$sp" --pdeathsig=SIGKILL /bin/sh -c "$g" sh "$e" "$@"
"""

    /// Launch the parent-death race harness detached — its own session, no containment, no kill-on-drop —
    /// so the inner chain really is orphaned by the middle process's death instead of being torn down with
    /// the run. Returns as soon as the middle process is launched; the assertions poll the markers.
    let startParentDeathHarness
        (setpriv: string)
        (mode: string)
        (reached: string)
        (waitMarker: string)
        (target: string list)
        =
        let args =
            [ "-c"
              parentDeathMiddleScript
              "sh"
              parentDeathInnerScript
              setpriv
              Native.Posix.parentDeathGuardScriptForTests
              reached
              mode
              waitMarker ]
            @ target

        Native.Posix.spawnDetachedPosix (Command.create "/bin/sh" |> Command.args args)

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
                Native.Posix.reapLeader (int spawned.Handle) |> ignore
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
    member _.``detached reaper preparation failure prevents the child from being spawned``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the pre-spawn detached reaper preparation gate"

            let marker =
                Path.Combine(Path.GetTempPath(), $"pk-detached-prepare-{Guid.NewGuid():N}.marker")

            let command =
                Command.create "/bin/sh"
                |> Command.args [ "-c"; $"echo launched > {quoteShellPath marker}" ]

            try
                Native.Posix.detachedReaperPrepareForTests <- Some(fun () -> Error "injected preparation failure")

                match Native.Posix.spawnDetachedPosix command with
                | Error(ProcessError.Spawn(_, detail)) ->
                    Assert.That(detail.Contains("injected preparation failure"), Is.True, detail)
                | Error other -> Assert.Fail $"expected ProcessError.Spawn, got {other}"
                | Ok spawned ->
                    Native.Posix.killProcessGroup spawned.Pid
                    Native.Posix.reapLeader spawned.Pid |> ignore
                    Assert.Fail "detached spawn succeeded despite a failed reaper preparation"

                do! Task.Delay 100
                Assert.That(File.Exists marker, Is.False, "the child ran even though reaper preparation failed")
            finally
                Native.Posix.detachedReaperPrepareForTests <- None

                try
                    File.Delete marker
                with _ ->
                    // Best-effort cleanup: the marker is only a launch proof and must not mask the assertion.
                    ()
        }

    [<Test>]
    member _.``detached reaper handoff failure synchronously kills and reaps the child``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the post-spawn detached reaper handoff unwind"

            let ready =
                Path.Combine(Path.GetTempPath(), $"pk-detached-handoff-{Guid.NewGuid():N}.ready")

            let pidFile =
                Path.Combine(Path.GetTempPath(), $"pk-detached-handoff-{Guid.NewGuid():N}.pid")

            let command =
                Command.create "/bin/sh"
                |> Command.args
                    [ "-c"
                      $"echo $$ > {quoteShellPath pidFile}; echo ready > {quoteShellPath ready}; sleep 10" ]

            let childBefore = if isLinux then ourChildCount () else 0

            let cleanupLeakedChild () =
                match Int32.TryParse(if File.Exists pidFile then File.ReadAllText pidFile else "") with
                | true, pid ->
                    try
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
                    with _ ->
                        // Best-effort cleanup for a deliberately failing path; a correct implementation has
                        // already killed and reaped this pid before returning.
                        ()
                | false, _ -> ()

            try
                Native.Posix.detachedReaperHandoffForTests <-
                    Some(fun _ ->
                        waitForFile ready (TimeSpan.FromSeconds 10.0) |> ignore
                        Error "injected handoff failure")

                match Native.Posix.spawnDetachedPosix command with
                | Error(ProcessError.Spawn(_, detail)) ->
                    Assert.That(detail.Contains("injected handoff failure"), Is.True, detail)
                | Error other -> Assert.Fail $"expected ProcessError.Spawn, got {other}"
                | Ok spawned ->
                    Native.Posix.killProcessGroup spawned.Pid
                    Native.Posix.reapLeader spawned.Pid |> ignore
                    Assert.Fail "detached spawn succeeded despite a failed reaper handoff"

                Assert.That(File.Exists ready, Is.True, "handoff failure was reported before the child ran")

                if isLinux then
                    do! Task.Delay 100

                    Assert.That(
                        ourChildCount (),
                        Is.LessThanOrEqualTo(childBefore),
                        "handoff failure returned with a live or zombie direct child"
                    )
            finally
                cleanupLeakedChild ()
                Native.Posix.detachedReaperHandoffForTests <- None

                for path in [ ready; pidFile ] do
                    try
                        File.Delete path
                    with _ ->
                        // Best-effort test cleanup: marker files do not belong to the lifecycle under test.
                        ()
        }

    [<Test>]
    member _.``detached SIGCHLD handoff fails closed when no replacement waiter starts``() =
        if not isLinux then
            Assert.Ignore "Linux-only: /proc confirms the child exited before the forced SIGCHLD handoff"

        let mutable pid = 0
        let mutable sawZombieBeforeHandoff = false
        let childBefore = ourChildCount ()

        let cleanupLeakedChild () =
            if pid <> 0 then
                try
                    Native.Posix.killProcess pid
                    Native.Posix.reapLeader pid |> ignore
                with _ ->
                    // Best-effort cleanup for a deliberately failing handoff. A correct implementation
                    // has already completed this owner-side reap before returning its Spawn error.
                    ()

        try
            Native.Posix.detachedReaperUseFastPathForTests <- Some false
            Native.Posix.blockingReapThreadStartFailureForTests <- Some(fun candidatePid -> candidatePid = pid)

            Native.Posix.exitWaitFaultForTests <-
                Some(fun operation candidatePid ->
                    if
                        candidatePid = pid
                        && operation = Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid
                    then
                        Some Native.Posix.transientExitWaitErrnoForTests
                    else
                        None)

            Native.Posix.detachedReaperHandoffForTests <-
                Some(fun candidatePid ->
                    pid <- candidatePid
                    Native.Posix.killProcess candidatePid

                    sawZombieBeforeHandoff <-
                        waitUntil
                            (fun () ->
                                try
                                    let fields = statFieldsAfterComm (string candidatePid)
                                    fields.Length > 0 && fields[0] = "Z"
                                with _ ->
                                    false)
                            (TimeSpan.FromSeconds 5.0)

                    Ok())

            match Native.Posix.spawnDetachedPosix (shell "sleep 60") with
            | Error(ProcessError.Spawn(_, detail)) ->
                Assert.That(
                    detail.Contains("could not schedule the blocking exit wait fallback", StringComparison.Ordinal),
                    Is.True,
                    detail
                )
            | Error other -> Assert.Fail $"expected ProcessError.Spawn, got {other}"
            | Ok spawned ->
                pid <- spawned.Pid
                cleanupLeakedChild ()
                Assert.Fail "detached handoff succeeded without a durable exit owner"

            Assert.That(sawZombieBeforeHandoff, Is.True, "the child was not already exited at initial handoff")

            Assert.That(
                ourChildCount (),
                Is.LessThanOrEqualTo(childBefore),
                "failed initial handoff returned with a live or zombie direct child"
            )

            // The owner-side reap is confirmed; do not retain a bare pid number into finally, where a
            // later process could theoretically have recycled it before best-effort cleanup runs.
            pid <- 0
        finally
            Native.Posix.detachedReaperHandoffForTests <- None
            Native.Posix.detachedReaperUseFastPathForTests <- None
            Native.Posix.blockingReapThreadStartFailureForTests <- None
            Native.Posix.exitWaitFaultForTests <- None
            cleanupLeakedChild ()

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

    [<Test>]
    member _.``a detached priority fault kills the whole new session including descendants``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises detached session process-group cleanup"

            let ready =
                Path.Combine(Path.GetTempPath(), $"pk-detached-priority-ready-{Guid.NewGuid():N}.marker")

            let leaked =
                Path.Combine(Path.GetTempPath(), $"pk-detached-priority-leaked-{Guid.NewGuid():N}.marker")

            let quoteShellPath (path: string) =
                let escaped = path.Replace("'", "'\\''", StringComparison.Ordinal)
                "'" + escaped + "'"

            let script =
                $"(sleep 2; echo leaked > {quoteShellPath leaked}) & "
                + $"echo ready > {quoteShellPath ready}; wait"

            let command =
                Command.create "/bin/sh"
                |> Command.args [ "-c"; script ]
                |> Command.priority Priority.Normal

            let install () =
                Native.Posix.setpriorityForTests <-
                    Some(fun _ _ _ ->
                        // Do not let the parent-side fault race the child-side fork: cleanup must be tested
                        // after a descendant is already in the detached session's process group.
                        waitForFile ready (TimeSpan.FromSeconds 10.0) |> ignore
                        -1)

            let reset () =
                Native.Posix.setpriorityForTests <- None

            try
                install ()

                match Native.Posix.spawnDetachedPosix command with
                | Error(ProcessError.Spawn _) ->
                    // The descendant would write this marker after two seconds if killing only the
                    // session leader left it alive. Allow that window to pass before asserting cleanup.
                    do! Task.Delay 3000

                    let readyMessage: string =
                        "the priority fault did not wait for the detached leader to create its descendant"

                    let leakedMessage: string =
                        "a detached descendant survived the priority-failure session cleanup"

                    Assert.That(File.Exists ready, Is.True, readyMessage)

                    Assert.That(File.Exists leaked, Is.False, leakedMessage)
                | Error other -> Assert.Fail $"expected ProcessError.Spawn from the injected fault, got {other}"
                | Ok spawned ->
                    Native.Posix.killProcessGroup spawned.Pid
                    Native.Posix.reapLeader spawned.Pid |> ignore
                    Assert.Fail "the priority fault seam did not fail the detached spawn"
            finally
                reset ()

                for path in [ ready; leaked ] do
                    try
                        File.Delete path
                    with _ ->
                        // Best-effort test cleanup: a marker may still be held by a failed child, and a
                        // leftover temporary file must not mask the process-group assertion.
                        ()
        }

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

    // ---- Command.KillOnParentDeath (reap the child on sudden parent death) --------------------
    //
    // A FULL "parent dies -> child reaped" test is deliberately NOT attempted here: ProcessKit arms the
    // child's parent-death signal relative to THIS process (the test runner is the child's real parent),
    // so actually triggering it would require killing the runner itself. Instead the Linux test below
    // verifies the OBSERVABLE contract the wiring produces — the spawned child really carries
    // pdeath_signal == SIGKILL — which proves the exact primitive KillOnParentDeath relies on
    // (`setpriv --pdeathsig=SIGKILL`, then an `execve` that must PRESERVE the signal) is present and
    // correctly wired on the host. The macOS/BSD and Windows tests pin the honest platform divergence.

    [<Test>]
    member _.``KillOnParentDeath on Linux arms the child's parent-death signal to SIGKILL``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: PR_SET_PDEATHSIG is armed via the util-linux 'setpriv --pdeathsig' helper"

            // The `setpriv` precondition is the library's OWN resolution rule, not an `Exec.which` PATH
            // lookup: since T-317 the helper is loaded only from a trusted system directory (`/usr/bin`,
            // `/bin`, `/usr/sbin`, `/sbin`) and never from `PATH`, so "on PATH" no longer implies usable.
            // A non-FHS host (NixOS/Guix, a custom image with util-linux under /usr/local/bin) resolves
            // `Exec.which "setpriv"` fine and is then refused with a typed `ProcessError.Spawn` — exactly
            // the intended behaviour — which a PATH-based gate would report as a failed test instead of an
            // ignored one. Asking the resolver itself keeps the gate and the contract in lockstep, and
            // keeps the assertion strict wherever the helper really is loadable. `python3` stays a plain
            // `which`: it is the ordinary target program, resolved on PATH like any other.
            match Native.Posix.trustedHelperPathForTests "setpriv", Exec.which "python3" with
            | None, _ ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin) - a PATH copy is deliberately not used"
            | _, Error _ -> Assert.Ignore "requires 'python3' on PATH to read PR_GET_PDEATHSIG"
            | Some _, Ok _ ->
                // The child reads its OWN parent-death signal (PR_GET_PDEATHSIG = 2) and prints it. If
                // ProcessKit wired KillOnParentDeath correctly it must be SIGKILL (9): setpriv armed it and
                // the signal survived setpriv's execve of (non-set-uid) python3.
                let script =
                    "import ctypes,sys; libc=ctypes.CDLL(None,use_errno=True); sig=ctypes.c_int(-1); "
                    + "libc.prctl(2,ctypes.byref(sig),0,0,0); sys.stdout.write(str(sig.value))"

                let cmd =
                    Command.create "python3"
                    |> Command.args [ "-c"; script ]
                    |> Command.killOnParentDeath

                let expected: string = "9"

                match! cmd.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout.Trim(), Is.EqualTo expected)
                | Error err ->
                    Assert.Fail
                        $"KillOnParentDeath spawn failed on Linux although setpriv resolved from a trusted directory: {err.Message}"
        }

    [<Test>]
    member _.``KillOnParentDeath on macOS or BSD is a typed Unsupported, not a silent no-op``() : Task =
        task {
            if isWindows || isLinux then
                Assert.Ignore "macOS/BSD-only: they have no PR_SET_PDEATHSIG analog to reap on parent death"

            let cmd = shell "echo hi" |> Command.killOnParentDeath

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported on macOS/BSD, got {other}"
            | Ok _ -> Assert.Fail "KillOnParentDeath should be Unsupported on macOS/BSD, not silently accepted"
        }

    [<Test>]
    member _.``KillOnParentDeath on Windows leaves the run unaffected (KILL_ON_JOB_CLOSE already covers it)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: KillOnParentDeath is a documented no-op there (the Job Object handles it)"

            // Every Windows child already lives in a Job Object with KILL_ON_JOB_CLOSE whose sole handle
            // the parent owns, so no extra spawn action is needed — the run must be identical to not asking.
            let cmd = shell "echo hi" |> Command.killOnParentDeath

            match! cmd.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout.Trim(), Is.EqualTo "hi")
            | Error err -> Assert.Fail $"KillOnParentDeath must not affect a Windows run: {err.Message}"
        }

    // ---- Arg0 combined with a helper-routing knob (T-376) --------------------------------------
    //
    // `setpriv` (a Uid/Gid/Groups drop or KillOnParentDeath) and the `setsid --ctty` pty shim both
    // re-`exec` the target BY NAME and have no CLI seam for a distinct `argv[0]`, so `Command.Arg0`
    // combined with either is refused with a typed `ProcessError.Unsupported` — never silently applied
    // to the WRONG process (the helper's own `argv[0]`). Checked before the up-front non-root drop
    // precheck, so these do not need root to exercise. A lone `Setsid` does not route through either
    // helper, so it composes with `Arg0` normally — covered by
    // `` `Arg0 composes normally with a lone Setsid (POSIX_SPAWN_SETSID, no privilege drop)` `` in
    // `ArgvEnvRoundTripTests` (T-376/R-03), which pins the same `sh -c` observation trick this file
    // already relies on and actually exercises `Setsid` (a different `posix_spawnattr` flag,
    // `POSIX_SPAWN_SETSID`, from the plain spawn path).

    [<Test>]
    member _.``Arg0 combined with a Uid drop is a typed Unsupported, not a silent misapplication``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Arg0/Uid are both POSIX-only"

            let cmd = shell "echo hi" |> Command.arg0 "override" |> Command.uid 0

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
            | Ok _ -> Assert.Fail "Arg0 combined with a Uid drop should be Unsupported, not silently accepted"
        }

    [<Test>]
    member _.``Arg0 combined with Pty is a typed Unsupported, not a silent misapplication``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Arg0/Pty are both POSIX-only here"

            let cmd = Command.create "/bin/sh" |> Command.arg0 "override" |> Command.pty

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
            | Ok _ -> Assert.Fail "Arg0 combined with Pty should be Unsupported, not silently accepted"
        }

    // ---- The pre-arm window (T-361) -----------------------------------------------------------
    //
    // `setpriv --pdeathsig=SIGKILL` arms `PR_SET_PDEATHSIG` INSIDE the child, so a parent that dies
    // between the spawn and that `prctl` is never covered by the arming: the kernel reparents the orphan
    // first, and the arming then binds the signal to the reaper that adopted it, leaving the child alive
    // after the process that asked for `KillOnParentDeathScope.DirectChildOnly` is gone. `Native.Posix`
    // closes that window with a POSIX-sh guard `setpriv` `exec`s immediately after arming: it compares its
    // own `$PPID` (read at shell startup, i.e. after the arming) with the spawner pid captured BEFORE the
    // spawn, and SIGKILLs itself instead of `exec`ing the target when the two differ.
    //
    // The window cannot be hit by a test that needs its own runner alive, so it is covered from two
    // directions, both deterministic:
    //
    //  * the `parentDeathSpawnerPidForTests` seam substitutes the captured pid, driving the "the parent is
    //    no longer the captured one" branch through the REAL production chain — plain, pty, and cgroup;
    //  * a shell harness reproduces the race for real: a middle process forks a child and dies, the child
    //    waits until the kernel has actually reparented it and only then runs the same
    //    `setpriv --pdeathsig` + guard chain. The same harness also covers the two cases that must NOT
    //    kill anything — a captured pid that matches the current (reaper, PID 1 or subreaper) parent, and
    //    an ordinary armed child whose parent dies AFTER the arming, where the kernel is what kills it.
    //
    // The seam is a process-wide mutable set and reset in a `finally`; this fixture runs sequentially (no
    // `[<Parallelizable>]`), so it never races a concurrent spawn.

    // The three shapes a lost pre-arm race leaves behind, all of them "the child's parent is no longer the
    // pid captured before the spawn": the spawner is simply gone; the orphan was adopted by a subreaper
    // with an ordinary (non-init) pid; the spawner was pid 1 itself. Each is expressed as the captured pid
    // the guard has to reject, since the child's actual parent is always this live test host.
    [<TestCase("vanished-spawner")>]
    [<TestCase("non-init-subreaper")>]
    [<TestCase("captured-pid-1")>]
    member _.``KillOnParentDeath stops the child instead of running the target when the spawner is no longer its parent``
        (shape: string)
        : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the pre-arm guard rides the util-linux 'setpriv --pdeathsig' chain"
            elif shape = "captured-pid-1" && Environment.ProcessId = 1 then
                Assert.Ignore "this test host IS pid 1, so a captured 1 would legitimately match its child's parent"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some _ ->
                let captured =
                    match shape with
                    // A pid that is not this process and names no parent of the child at all — the plain
                    // "the spawner died and its pid went with it" case.
                    | "vanished-spawner" -> Environment.ProcessId + 1
                    // This test host's OWN parent: a real, live, non-init process that simply is not the
                    // child's parent — the shape a reparent to a `PR_SET_CHILD_SUBREAPER` ancestor leaves,
                    // where the new parent keeps an ordinary pid and an "orphans have ppid 1" shortcut
                    // would never fire. Falls back to another non-init pid on the exotic host where this
                    // process is itself a direct child of init, so the case stays a non-init one.
                    | "non-init-subreaper" ->
                        let parent = ourParentPid ()

                        if parent > 1 then parent else Environment.ProcessId + 2
                    // Captured pid 1 while the child's parent is this (non-init) test host: 1 must be
                    // compared like any other pid, not read as a sentinel meaning "fine".
                    | _ -> 1

                Assert.That(
                    captured,
                    Is.Not.EqualTo(Environment.ProcessId),
                    "the captured pid must differ from the child's real parent for this to be the lost-race case"
                )

                let marker =
                    Path.Combine(Path.GetTempPath(), $"pk-pdeath-{shape}-{Guid.NewGuid():N}.marker")

                try
                    Native.Posix.parentDeathSpawnerPidForTests <- Some captured

                    let cmd =
                        Command.create "/bin/sh"
                        |> Command.args [ "-c"; $"echo ran > {quoteShellPath marker}" ]
                        |> Command.killOnParentDeath
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match! cmd.OutputStringAsync() with
                    | Ok result ->
                        Assert.That(
                            result.Outcome,
                            Is.EqualTo(Outcome.Signalled(Some 9)),
                            "a child whose captured spawner is not its parent must end exactly as the armed signal would have ended it"
                        )

                        Assert.That(
                            File.Exists marker,
                            Is.False,
                            $"the target ran although the pid it captured ({captured}) is not its parent"
                        )
                    | Error err -> Assert.Fail $"the guarded KillOnParentDeath spawn failed outright: {err.Message}"
                finally
                    Native.Posix.parentDeathSpawnerPidForTests <- None

                    try
                        File.Delete marker
                    with _ ->
                        // Best-effort cleanup: the marker is only a launch proof and must not mask the
                        // assertion that it was never written.
                        ()
        }

    [<Test>]
    member _.``KillOnParentDeath still runs the target when the spawner is unchanged``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: KillOnParentDeath is armed by the util-linux 'setpriv --pdeathsig' helper"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some _ ->
                // The ordinary case: this process really is the child's parent, so the guard must pass the
                // target through untouched — same exit code, same output, argv intact across both `exec`s.
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf '%s' \"$1\""; "sh"; "guarded-ok" ]
                    |> Command.killOnParentDeath
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.Stdout, Is.EqualTo "guarded-ok")
                    Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                | Error err -> Assert.Fail $"a KillOnParentDeath run with a live spawner must succeed: {err.Message}"
        }

    [<Test>]
    member _.``the parent-death guard is nested between the setpriv arming and the target, and only when asked``() =
        if not isLinux then
            Assert.Ignore "Linux-only: the helper chain is built only where setpriv can be resolved"

        match trustedSetpriv () with
        | None ->
            Assert.Ignore
                "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
        | Some setpriv ->
            // This is the exact argv the `setsid --ctty` pty shim and the `/bin/sh` cgroup launcher `exec`,
            // so pinning it here pins the guard's position in BOTH of those chains — no root, pty, or
            // delegated cgroup needed to prove the composition.
            let guard = Native.Posix.parentDeathGuardScriptForTests
            let self = string Environment.ProcessId

            let armed =
                Command.create "/bin/echo" |> Command.args [ "hi" ] |> Command.killOnParentDeath

            match Native.Posix.setprivWrappedArgvForTests armed with
            | Ok argv ->
                Assert.That(
                    List.toArray argv,
                    Is.EqualTo<string[]>(
                        [| setpriv
                           "--pdeathsig=SIGKILL"
                           "/bin/sh"
                           "-c"
                           guard
                           "sh"
                           self
                           "/bin/echo"
                           "hi" |]
                    )
                )
            | Error err -> Assert.Fail $"a lone KillOnParentDeath must build a chain: {err.Message}"

            // Composed with a uid/gid drop: setpriv still applies the drop flags first and arms the signal
            // after them, and the guard still sits immediately before the target — so the check runs with
            // nothing between it and the program's `exec`, and the drop is not weakened by it.
            match Native.Posix.setprivWrappedArgvForTests (armed |> Command.uid 12345 |> Command.gid 12345) with
            | Ok argv ->
                Assert.That(
                    List.toArray argv,
                    Is.EqualTo<string[]>(
                        [| setpriv
                           "--regid=12345"
                           "--reuid=12345"
                           "--clear-groups"
                           "--pdeathsig=SIGKILL"
                           "/bin/sh"
                           "-c"
                           guard
                           "sh"
                           self
                           "/bin/echo"
                           "hi" |]
                    )
                )
            | Error err -> Assert.Fail $"KillOnParentDeath composed with a drop must build a chain: {err.Message}"

            // A plain privilege drop gains no guard layer at all: nothing is armed, so there is nothing to
            // race, and the extra `exec` would be pure cost.
            match
                Native.Posix.setprivWrappedArgvForTests (
                    Command.create "/bin/echo" |> Command.args [ "hi" ] |> Command.uid 12345
                )
            with
            | Ok argv ->
                Assert.That(
                    List.toArray argv,
                    Is.EqualTo<string[]>([| setpriv; "--reuid=12345"; "--clear-groups"; "/bin/echo"; "hi" |])
                )
            | Error err -> Assert.Fail $"a plain uid drop must build a chain: {err.Message}"

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``a cgroup-launched KillOnParentDeath child joins its cgroup first and only then meets the guard``
        (spawnerGone: bool)
        : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the cgroup launcher path is Linux-only"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some _ ->
                // A plain file stands in for `cgroup.procs`: the launcher's `echo $$ > "$1"` writes to it
                // exactly as it writes to a real one, so the whole launcher -> setpriv -> guard -> target
                // chain is exercised on any host, with the written pid proving the (privileged) join
                // happened BEFORE the guard had its say.
                let procs = Path.Combine(Path.GetTempPath(), $"pk-pdeath-procs-{Guid.NewGuid():N}")

                let marker =
                    Path.Combine(Path.GetTempPath(), $"pk-pdeath-cgroup-{Guid.NewGuid():N}.marker")

                let command =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; $"echo ran > {quoteShellPath marker}" ]
                    |> Command.killOnParentDeath
                    |> Command.stdout StdioMode.Null
                    |> Command.stderr StdioMode.Null

                try
                    if spawnerGone then
                        Native.Posix.parentDeathSpawnerPidForTests <- Some(Environment.ProcessId + 1)

                    match Native.Posix.spawnPosixIntoCgroup command procs with
                    | Error err -> Assert.Fail $"the cgroup launcher spawn failed: {err.Message}"
                    | Ok spawned ->
                        let! outcome = Native.Posix.waitPosix spawned.Handle

                        Assert.That(
                            File.Exists procs && File.ReadAllText(procs).Trim() <> "",
                            Is.True,
                            "the launcher must join the cgroup before the guard runs, in both cases"
                        )

                        if spawnerGone then
                            Assert.That(outcome, Is.EqualTo(Outcome.Signalled(Some 9)))

                            Assert.That(
                                File.Exists marker,
                                Is.False,
                                "the cgroup-launched target ran although its spawner was gone"
                            )
                        else
                            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

                            Assert.That(
                                waitUntil (fun () -> File.Exists marker) (TimeSpan.FromSeconds 10.0),
                                Is.True,
                                "the guard must pass the cgroup-launched target through when the spawner is unchanged"
                            )
                finally
                    Native.Posix.parentDeathSpawnerPidForTests <- None

                    for path in [ procs; marker ] do
                        try
                            File.Delete path
                        with _ ->
                            // Best-effort cleanup of the stand-in cgroup.procs file and the launch marker.
                            ()
        }

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``a Pty KillOnParentDeath child meets the guard inside the ctty chain``(spawnerGone: bool) : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the pty ctty helper is util-linux 'setsid --ctty'"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some _ ->
                let marker =
                    Path.Combine(Path.GetTempPath(), $"pk-pdeath-pty-{Guid.NewGuid():N}.marker")

                try
                    if spawnerGone then
                        Native.Posix.parentDeathSpawnerPidForTests <- Some(Environment.ProcessId + 1)

                    // `setsid --ctty` establishes the controlling terminal and then `exec`s the nested
                    // `setpriv` + guard, so the guard is the last link before the target here too.
                    let cmd =
                        Command.create "/bin/sh"
                        |> Command.args [ "-c"; $"echo ran > {quoteShellPath marker}; printf pty-ran" ]
                        |> Command.killOnParentDeath
                        |> Command.pty
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match! cmd.OutputStringAsync() with
                    | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                    | Error err -> Assert.Fail $"the guarded pty KillOnParentDeath spawn failed: {err.Message}"
                    | Ok result ->
                        if spawnerGone then
                            Assert.That(
                                File.Exists marker,
                                Is.False,
                                "the pty target ran although its spawner was gone"
                            )

                            Assert.That(result.Stdout, Does.Not.Contain "pty-ran")
                        else
                            Assert.That(
                                waitUntil (fun () -> File.Exists marker) (TimeSpan.FromSeconds 10.0),
                                Is.True,
                                "the guard must pass the pty target through when the spawner is unchanged"
                            )
                finally
                    Native.Posix.parentDeathSpawnerPidForTests <- None

                    try
                        File.Delete marker
                    with _ ->
                        // Best-effort cleanup, as above.
                        ()
        }

    [<TestCase("stale")>]
    [<TestCase("reaper")>]
    member _.``a child orphaned before the arming is stopped, while one whose captured pid is its real parent runs``
        (mode: string)
        : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: reproduces the PR_SET_PDEATHSIG pre-arm race"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some setpriv ->
                let directory = Directory.CreateTempSubdirectory($"pk-pdeath-{mode}-").FullName

                let reached = Path.Combine(directory, "reached")
                let ran = Path.Combine(directory, "ran")

                try
                    let target = [ "/bin/sh"; "-c"; $"echo ran > {quoteShellPath ran}" ]

                    match startParentDeathHarness setpriv mode reached ran target with
                    | Error err -> Assert.Fail $"could not launch the parent-death race harness: {err.Message}"
                    | Ok _ ->
                        Assert.That(
                            waitForFile reached (TimeSpan.FromSeconds 30.0),
                            Is.True,
                            "the harness never reached the setpriv chain (the child was not reparented in time)"
                        )

                        let captured = File.ReadAllText(reached).Trim()

                        if mode = "stale" then
                            // The captured spawner really is gone by now, so the arming would bind the
                            // signal to the reaper: the guard must stop the chain instead of running it.
                            Assert.That(
                                waitUntil (fun () -> File.Exists ran) (TimeSpan.FromSeconds 3.0),
                                Is.False,
                                $"the target ran although the spawner it captured ({captured}) had already died"
                            )
                        else
                            // The captured pid IS the process that is now the parent — PID 1 on a container
                            // host, an ordinary subreaper pid elsewhere. Either way nothing may be killed.
                            Assert.That(
                                waitUntil (fun () -> File.Exists ran) (TimeSpan.FromSeconds 30.0),
                                Is.True,
                                $"the guard killed a child whose captured spawner pid ({captured}) is its real parent"
                            )
                finally
                    try
                        Directory.Delete(directory, true)
                    with _ ->
                        // Best-effort cleanup of the harness markers; a leftover temp directory must not
                        // mask the assertions above.
                        ()
        }

    [<Test>]
    member _.``an armed child is killed by the kernel when its parent dies after the arming``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: PR_SET_PDEATHSIG"

            match trustedSetpriv () with
            | None ->
                Assert.Ignore
                    "requires the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)"
            | Some setpriv ->
                let directory = Directory.CreateTempSubdirectory("pk-pdeath-armed-").FullName
                let reached = Path.Combine(directory, "reached")
                let pidFile = Path.Combine(directory, "pid")
                let mutable targetPid = 0

                try
                    // The guard passes (the captured pid IS the live middle process), the signal is armed,
                    // and only THEN does the parent die — the case the guard must not interfere with and
                    // the kernel must handle: the target is SIGKILLed with its long sleep unfinished.
                    let target = [ "/bin/sh"; "-c"; $"echo $$ > {quoteShellPath pidFile}; sleep 30" ]

                    match startParentDeathHarness setpriv "armed" reached pidFile target with
                    | Error err -> Assert.Fail $"could not launch the parent-death race harness: {err.Message}"
                    | Ok _ ->
                        Assert.That(
                            waitForFile pidFile (TimeSpan.FromSeconds 30.0),
                            Is.True,
                            "the armed target never started"
                        )

                        // The file can be observed between create and write, so read the pid itself with a
                        // bounded poll rather than assuming the first read sees it.
                        Assert.That(
                            waitUntil
                                (fun () ->
                                    match Int32.TryParse(File.ReadAllText(pidFile).Trim()) with
                                    | true, pid ->
                                        targetPid <- pid
                                        true
                                    | false, _ -> false)
                                (TimeSpan.FromSeconds 10.0),
                            Is.True,
                            "the armed target never reported its pid"
                        )

                        Assert.That(
                            waitUntil (fun () -> not (processLive targetPid)) (TimeSpan.FromSeconds 20.0),
                            Is.True,
                            $"the armed target ({targetPid}) survived its parent's death"
                        )
                finally
                    if targetPid > 0 && processLive targetPid then
                        try
                            Native.Posix.killProcess targetPid
                        with _ ->
                            // Best-effort cleanup of a target that outlived its parent (the very failure
                            // asserted above): it must not be left behind for the rest of the suite.
                            ()

                    try
                        Directory.Delete(directory, true)
                    with _ ->
                        // Best-effort cleanup of the harness markers.
                        ()
        }
