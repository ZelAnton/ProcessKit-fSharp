namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type TimeoutTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let sleeper () =
        if isWindows then
            shell "ping 127.0.0.1 -n 10 >NUL"
        else
            shell "sleep 8"

    [<Test>]
    member _.``Timeout kills the run promptly and reports Timeout``() : Task =
        task {
            let command = sleeper () |> Command.timeout (TimeSpan.FromMilliseconds 400.0)
            let stopwatch = Stopwatch.StartNew()
            let! result = command.RunAsync()
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected Timeout, got {other}"

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
        }
        :> Task

    [<Test>]
    member _.``Timeout surfaces as Outcome.TimedOut on outputString``() : Task =
        task {
            let command = sleeper () |> Command.timeout (TimeSpan.FromMilliseconds 400.0)

            match! command.OutputStringAsync() with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``TimeoutGrace also times out``() : Task =
        task {
            let command =
                sleeper ()
                |> Command.timeout (TimeSpan.FromMilliseconds 300.0)
                |> Command.timeoutGrace (TimeSpan.FromMilliseconds 200.0)

            match! command.OutputStringAsync() with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Retry re-runs a failing command the configured number of times``() : Task =
        let id = Guid.NewGuid().ToString("N")
        let marker = Path.Combine(Path.GetTempPath(), $"pk-retry-{id}.txt")

        task {
            try
                let script =
                    if isWindows then
                        $"echo x>>{marker}&exit 1"
                    else
                        $"echo x >> {marker}; exit 1"

                let command =
                    shell script |> Command.retry 2 (TimeSpan.FromMilliseconds 50.0) (fun _ -> true)

                match! command.RunAsync() with
                | Error _ -> ()
                | Ok _ -> Assert.Fail "expected the command to fail"

                let attempts =
                    if File.Exists marker then
                        File.ReadAllLines(marker).Length
                    else
                        0

                Assert.That(attempts, Is.EqualTo 3) // initial + 2 retries
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``CancelOn cancels the run when its token fires``() : Task =
        task {
            use cts = new CancellationTokenSource()
            let command = sleeper () |> Command.cancelOn cts.Token
            let runTask = command.RunAsync()
            do! Task.Delay 300
            cts.Cancel()

            match! runTask with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task
