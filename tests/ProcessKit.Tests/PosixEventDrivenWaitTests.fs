namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// POSIX-only: the event-driven replacement for `Native.waitPosix`'s old `Task.Run` + blocking
/// `waitpid` (a shared SIGCHLD registration + pid -> `TaskCompletionSource` registry, plus an
/// auxiliary `pidfd_open` on Linux — see `Native.fs`). Verifies the observable contract holds under
/// this native change: no fd/pidfd leak under load, and the thread-pool no longer parking one thread
/// per live POSIX child.
[<TestFixture>]
type PosixEventDrivenWaitTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Race `work` against a deadline so a regression that strands a `TaskCompletionSource` fails this
    // test in `deadlineMs` instead of hanging the whole run.
    let withDeadline (deadlineMs: int) (work: Task<'T>) =
        task {
            let! winner = Task.WhenAny((work :> Task), Task.Delay deadlineMs)
            Assert.That(obj.ReferenceEquals(winner, work), Is.True, "timed out waiting for the task to complete")
            return! work
        }

    // Spawn a short-lived child directly through `Native.spawnPosix` (bypassing the containment/verb
    // layer entirely) and call `Native.waitPosix` on its pid TWICE — the exact double-registration
    // scenario a caller could hit before this handle's own single wait has settled. Returns both
    // outcomes so a caller can assert they agree.
    let spawnAndDoubleWait () =
        task {
            match Native.spawnPosix (shell "true") with
            | Error e -> return Error e
            | Ok spawned ->
                let first = Native.waitPosix spawned.Handle
                let second = Native.waitPosix spawned.Handle
                let! firstOutcome = withDeadline 5000 first
                let! secondOutcome = withDeadline 5000 second
                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                return Ok(firstOutcome, secondOutcome)
        }

    [<Test>]
    member _.``waitPosix is idempotent for a repeated pid and does not leak a pidfd``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises Native.waitPosix directly"

            match! spawnAndDoubleWait () with
            | Error e -> Assert.Fail $"spawn failed: {e.Message}"
            | Ok(firstOutcome, secondOutcome) ->
                Assert.That(secondOutcome, Is.EqualTo firstOutcome, "the two waitPosix calls disagreed on the outcome")

                match firstOutcome with
                | Outcome.Exited 0 -> ()
                | other -> Assert.Fail $"expected a clean exit, got {other}"

            if isLinux then
                // Warm up (JIT, the lazy shared SIGCHLD registration) before the baseline, then repeat
                // the double-registration scenario under load: an unconditional `pendingWaits`
                // overwrite would leak the first call's pidfd every time, showing up as fd growth
                // proportional to the iteration count.
                for _ in 1..5 do
                    match! spawnAndDoubleWait () with
                    | Ok _ -> ()
                    | Error e -> Assert.Fail $"{e.Message}"

                let fdCount () =
                    Directory.GetFileSystemEntries("/proc/self/fd").Length

                let baseline = fdCount ()

                for _ in 1..50 do
                    match! spawnAndDoubleWait () with
                    | Ok _ -> ()
                    | Error e -> Assert.Fail $"{e.Message}"

                let after = fdCount ()

                Assert.That(
                    after,
                    Is.LessThan(baseline + 20),
                    $"open fd count grew from {baseline} to {after} after 50 duplicate-registration \
                      spawns — looks like a pidfd leak"
                )
        }
        :> Task

    [<Test>]
    member _.``no fd leak after many piped spawns``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix/pidfd replacement"

            if not isLinux then
                Assert.Ignore "open fd count is observable portably via /proc on Linux only"

            let echo = shell "echo warmup"

            // Warm up (JIT, the lazy shared SIGCHLD registration, first-spawn one-offs) before
            // establishing the baseline, so the load below is measured against a settled steady state.
            for _ in 1..5 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            let fdCount () =
                Directory.GetFileSystemEntries("/proc/self/fd").Length

            let baseline = fdCount ()

            for _ in 1..200 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            let after = fdCount ()

            // A per-spawn leak of even one fd/pidfd would show up as growth proportional to the 200
            // runs; a generous absolute slack (`+20`) absorbs incidental steady-state noise without
            // masking a real leak.
            Assert.That(
                after,
                Is.LessThan(baseline + 20),
                $"open fd count grew from {baseline} to {after} after 200 piped spawns — looks like a leak"
            )
        }
        :> Task

    [<Test>]
    member _.``reaping a POSIX child does not park a thread-pool thread per concurrent child``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix/pidfd replacement"

            let concurrency = 100
            let baselineThreadPoolCount = ThreadPool.ThreadCount

            // Each child sleeps briefly so all `concurrency` of them are alive together at the
            // sampling point below.
            let sleeper = shell "sleep 0.3"
            let runs = [ for _ in 1..concurrency -> sleeper.RunAsync() ]

            // Sample mid-flight. The old, unconditionally-blocking `waitPosix` parked one dedicated
            // thread-pool thread per concurrent child (one `Task.Run` each, blocked in `waitpid`); the
            // event-driven replacement shares one SIGCHLD registration process-wide, so thread-pool
            // growth here should be far below `concurrency`, not track it 1:1.
            do! Task.Delay 150
            let midFlightThreadPoolCount = ThreadPool.ThreadCount

            let! results = Task.WhenAll runs

            for result in results do
                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            Assert.That(
                midFlightThreadPoolCount,
                Is.LessThan(baselineThreadPoolCount + concurrency / 2),
                $"thread-pool grew from {baselineThreadPoolCount} to {midFlightThreadPoolCount} threads \
                  with {concurrency} concurrent POSIX children in flight — looks like one thread parked \
                  per child"
            )
        }
        :> Task

    [<Test>]
    member _.``exit code, clean exit, and signal all decode correctly through the event-driven wait``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix/pidfd replacement"

            match! (shell "exit 0").RunAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"clean exit: {error.Message}"

            match! Runner.exitCode (JobRunner()) CancellationToken.None (shell "exit 42") with
            | Ok 42 -> ()
            | other -> Assert.Fail $"expected exit code 42, got {other}"

            match! (shell "kill -TERM $$").RunAsync() with
            | Error(ProcessError.Signalled _) -> ()
            | other -> Assert.Fail $"expected a Signalled outcome, got {other}"
        }
        :> Task

    [<Test>]
    member _.``waitPosix resolves to the real outcome when reapLeader wins the reap race``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the reformulated ECHILD race in tryReapPending/reapLeader"

            match Native.spawnPosix (shell "exit 0") with
            | Error e -> Assert.Fail $"spawn failed: {e.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let waitTask = Native.waitPosix spawned.Handle

                // Race a direct `reapLeader` call — the exact "some concurrent caller already reaped
                // this pid" scenario `tryReapPending`'s ECHILD branch exists for (in real use, this is
                // a group's own teardown racing a run's own wait) — against `waitPosix`'s own
                // SIGCHLD-driven reap. Whichever side actually wins the `waitpid` race, the wait must
                // still resolve to the REAL decoded status promptly: never a fabricated clean exit
                // (the old behaviour), and never a hang (the removed blocking spin's replacement must
                // not silently swallow a result either).
                let reapTask = Task.Run(fun () -> Native.reapLeader pid)

                let! outcome = withDeadline 5000 waitTask
                do! reapTask

                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())

                match outcome with
                | Outcome.Exited 0 -> ()
                | other -> Assert.Fail $"expected a clean exit despite the concurrent reap race, got {other}"
        }
        :> Task
