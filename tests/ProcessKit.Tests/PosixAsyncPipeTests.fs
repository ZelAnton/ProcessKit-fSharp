namespace ProcessKit.Tests

open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// POSIX-only: the genuinely-async socketpair replacement for the piped-stdio pipes
/// (`Native.Posix.createSocketPair` / `spawnPosix`). The parent side of each piped stdio stream is
/// one end of an `AF_UNIX` `SOCK_STREAM` socketpair wrapped in a `Socket`/`NetworkStream`, so its
/// reads/writes complete through the runtime's epoll/kqueue event loop instead of a plain
/// `FileStream` parking a thread-pool thread per piped stream. This verifies the observable contract
/// stays intact under that native change — byte-exact captured output — and that a concurrent fan-out
/// of piped children no longer parks one thread-pool thread per stream. The direct POSIX analogue of
/// `WindowsOverlappedPipeTests` (which asserts the same for Windows overlapped named pipes over IOCP).
[<TestFixture>]
type PosixAsyncPipeTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    [<Test>]
    member _.``captured stdout is byte-exact through the async socketpair``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the socketpair stdio replacement"

            // A payload spanning several hundred lines and comfortably larger than a single OS read
            // chunk (8 KiB, per `Pump.readLines`), so any silent truncation/duplication/reordering at
            // a read boundary through the socketpair stream would show up.
            let lines = [ for i in 1..500 -> sprintf "line-%d-................" i ]

            let script = lines |> List.map (sprintf "echo %s") |> String.concat "\n"

            match! (shell script).OutputStringAsync() with
            | Error error -> Assert.Fail $"{error.Message}"
            | Ok result ->
                let captured = result.Stdout.TrimEnd([| '\n' |]).Split('\n') |> Array.toList

                CollectionAssert.AreEqual(lines, captured)
        }
        :> Task

    [<Test>]
    member _.``reading piped output does not park a thread-pool thread per concurrent child``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the socketpair stdio replacement"

            let concurrency = 100
            let baselineThreadPoolCount = ThreadPool.ThreadCount

            // Each child sleeps a couple of seconds so all `concurrency` of them are alive together, with
            // their stdout socketpairs open and being read (the read stays pending, with no data, for the
            // child's whole lifetime), at the sampling point below.
            let sleeper = shell "sleep 2"
            let runs = [ for _ in 1..concurrency -> sleeper.OutputStringAsync() ]

            // Sample mid-flight. The old plain-`FileStream` path (no `FileOptions.Asynchronous`) fell
            // back to a blocking pool read per stream, parking roughly one thread-pool thread per
            // concurrent child; the socketpair/`NetworkStream` path completes reads through the epoll/
            // kqueue event loop instead, so thread-pool growth here stays far below `concurrency`, not
            // tracking it 1:1.
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
                  per socketpair read"
            )
        }
        :> Task
