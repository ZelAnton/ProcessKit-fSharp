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
