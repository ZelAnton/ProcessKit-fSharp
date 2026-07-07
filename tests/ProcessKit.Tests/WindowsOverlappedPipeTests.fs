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

/// Deterministic Windows-only fault injection for `Native.Windows`'s `StdioMode.Inherit` path
/// (`inheritableStdHandle` → `GetStdHandle`): `SetStdHandle` lets a test force `GetStdHandle` to
/// return an invalid handle for the current process, the same condition a detached/no-console
/// process would see for real — without touching production code or relying on a genuinely
/// console-less host (which CI runners are not, reliably).
module private WindowsStdHandleFaultInjection =

    [<Literal>]
    let STD_ERROR_HANDLE = -12

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint GetStdHandle(int nStdHandle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool SetStdHandle(int nStdHandle, nativeint hHandle)

/// Windows-only: the async-capable named-pipe replacement for the piped-stdio anonymous pipes
/// (`Native.Windows.createAsyncPipePair` / `spawnWindowsCore`). Verifies the observable contract stays intact
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

    [<Test>]
    member _.``spawn honestly fails instead of handing the child a broken Inherit std handle``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: exercises GetStdHandle/SetStdHandle fault injection"

            let original =
                WindowsStdHandleFaultInjection.GetStdHandle WindowsStdHandleFaultInjection.STD_ERROR_HANDLE

            try
                // Force `GetStdHandle(STD_ERROR_HANDLE)` to return NULL for the duration of the spawn —
                // the same condition `inheritableStdHandle` sees for a detached/no-console process whose
                // stderr is not otherwise redirected. Before the fix, `spawnWindowsCore` put this
                // unchecked (NULL) value straight into `STARTUPINFO.hStdError`, handing the child a
                // broken std handle instead of failing the spawn.
                WindowsStdHandleFaultInjection.SetStdHandle(
                    WindowsStdHandleFaultInjection.STD_ERROR_HANDLE,
                    IntPtr.Zero
                )
                |> ignore

                let command =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "echo hi" ]
                    |> Command.stderr StdioMode.Inherit

                match! command.OutputStringAsync() with
                | Ok result ->
                    Assert.Fail
                        $"expected an honest spawn failure with an invalid stderr std handle, got a \
                          successful run instead (stdout: {result.Stdout})"
                | Error error -> StringAssert.Contains("spawn", error.Message.ToLowerInvariant())
            finally
                WindowsStdHandleFaultInjection.SetStdHandle(WindowsStdHandleFaultInjection.STD_ERROR_HANDLE, original)
                |> ignore
        }
        :> Task

    [<Test>]
    member _.``a failed stderr std-handle setup does not leak the stdout Inherit handle already created``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: exercises GetStdHandle/SetStdHandle fault injection"

            use proc = Process.GetCurrentProcess()

            let original =
                WindowsStdHandleFaultInjection.GetStdHandle WindowsStdHandleFaultInjection.STD_ERROR_HANDLE

            try
                WindowsStdHandleFaultInjection.SetStdHandle(
                    WindowsStdHandleFaultInjection.STD_ERROR_HANDLE,
                    IntPtr.Zero
                )
                |> ignore

                // Stdout is `Inherit` — the FIRST `setupOut` call succeeds and duplicates the parent's
                // (untouched) STD_OUTPUT_HANDLE, registering that duplicate in the unwind list via the
                // `createdPipes.Add(disposableHandle handle)` line this task adds to the `Inherit` branch.
                // Stderr is also `Inherit`, but its std handle is forced invalid above, so the SECOND
                // `setupOut` call always throws. Before the fix, that new `Add` call did not exist, so
                // the duplicated stdout handle from the first call was never registered in the unwind list
                // and leaked on every one of these failed attempts. `Piped` mode is deliberately NOT used
                // here: its pipe handles were already registered in `createdPipes` before this task, so it
                // would not exercise the `Null`/`Inherit` fix at all.
                let command =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "echo hi" ]
                    |> Command.stdout StdioMode.Inherit
                    |> Command.stderr StdioMode.Inherit

                // Warm up once so the baseline reflects steady state, not first-call one-offs.
                match! command.OutputStringAsync() with
                | Ok _ -> Assert.Fail "expected the forced invalid stderr std handle to fail the spawn"
                | Error _ -> ()

                proc.Refresh()
                let baseline = proc.HandleCount

                for _ in 1..50 do
                    match! command.OutputStringAsync() with
                    | Ok _ -> Assert.Fail "expected the forced invalid stderr std handle to fail the spawn"
                    | Error _ -> ()

                proc.Refresh()
                let after = proc.HandleCount

                // A per-attempt leak of the duplicated stdout Inherit handle would show up as growth
                // roughly proportional to the 50 failed attempts; a generous absolute slack absorbs
                // incidental steady-state noise without masking a real leak.
                Assert.That(
                    after,
                    Is.LessThan(baseline + 20),
                    $"handle count grew from {baseline} to {after} after 50 spawn attempts that fail \
                      during stdio setup — looks like the stdout Inherit handle from the first setupOut \
                      call is leaking"
                )
            finally
                WindowsStdHandleFaultInjection.SetStdHandle(WindowsStdHandleFaultInjection.STD_ERROR_HANDLE, original)
                |> ignore
        }
        :> Task
