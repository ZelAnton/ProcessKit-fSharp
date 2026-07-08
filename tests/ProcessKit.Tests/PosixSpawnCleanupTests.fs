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
