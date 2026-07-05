namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// Windows-only: the async-capable named-pipe replacement for the piped-stdio anonymous pipes
/// (`Native.createAsyncPipePair` / `spawnWindowsCore`). Verifies the observable contract stays intact
/// under this native change — byte-exact captured output, no handle leak under load — and that reads
/// no longer park a dedicated thread-pool thread per piped stream.
[<TestFixture>]
type WindowsOverlappedPipeTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    [<Test>]
    member _.``captured stdout is byte-exact through the async pipe``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: exercises the named-pipe stdio replacement"

            // A payload spanning several hundred lines and comfortably larger than a single OS read
            // chunk (8 KiB, per `Pump.readLines`), so any silent truncation/duplication/reordering at
            // a read-boundary would show up.
            let lines = [ for i in 1..500 -> sprintf "line-%d-................" i ]
            // `GetTempFileName` both names AND creates an empty file at `tempFile`; the runnable
            // script needs a `.cmd` extension, so it's written to a second path alongside it — both
            // get cleaned up below.
            let tempFile = Path.GetTempFileName()
            let scriptFile = tempFile + ".cmd"

            let script =
                "@echo off\r\n"
                + (lines |> List.map (sprintf "echo %s") |> String.concat "\r\n")
                + "\r\n"

            File.WriteAllText(scriptFile, script)

            try
                match! (Command.create scriptFile).OutputStringAsync() with
                | Error error -> Assert.Fail $"{error.Message}"
                | Ok result ->
                    let captured =
                        result.Stdout.Replace("\r\n", "\n").TrimEnd([| '\n' |]).Split('\n')
                        |> Array.toList

                    CollectionAssert.AreEqual(lines, captured)
            finally
                File.Delete scriptFile
                File.Delete tempFile
        }
        :> Task

    [<Test>]
    member _.``no process handle leak after many piped spawns``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: exercises the named-pipe stdio replacement"

            use proc = Process.GetCurrentProcess()
            let echo = Command.create "cmd.exe" |> Command.args [ "/c"; "echo warmup" ]

            // Warm up (JIT, thread-pool ramp-up, first-spawn one-offs) before establishing the
            // baseline, so the load below is measured against a settled steady state.
            for _ in 1..5 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            proc.Refresh()
            let baseline = proc.HandleCount

            for _ in 1..200 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            proc.Refresh()
            let after = proc.HandleCount

            // A per-spawn leak of even one handle would show up as growth proportional to the 200
            // runs; a generous absolute slack (`+50`) absorbs incidental steady-state noise (GC/JIT/
            // thread-pool handles) without masking a real leak.
            Assert.That(
                after,
                Is.LessThan(baseline + 50),
                $"handle count grew from {baseline} to {after} after 200 piped spawns — looks like a leak"
            )
        }
        :> Task

    [<Test>]
    member _.``reading piped output does not park a thread-pool thread per concurrent child``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: exercises the named-pipe stdio replacement"

            let concurrency = 100
            let baselineThreadPoolCount = ThreadPool.ThreadCount

            // Each child sleeps briefly so all `concurrency` of them are alive together, with their
            // stdout pipes open and being read, at the sampling point below.
            let sleeper =
                Command.create "cmd.exe" |> Command.args [ "/c"; "ping 127.0.0.1 -n 3 >nul" ]

            let runs = [ for _ in 1..concurrency -> sleeper.OutputStringAsync() ]

            // Sample mid-flight. The old, unconditionally-synchronous `AnonymousPipeServerStream` path
            // fell back to a blocking pool read per pipe, parking roughly one thread-pool thread per
            // concurrent child; the overlapped path completes reads via IOCP instead, so thread-pool
            // growth here should be far below `concurrency`, not track it 1:1.
            do! Task.Delay 800
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
                  with {concurrency} concurrent piped children in flight — looks like one thread parked \
                  per pipe read"
            )
        }
        :> Task
