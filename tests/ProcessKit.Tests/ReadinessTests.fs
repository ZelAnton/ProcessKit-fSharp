namespace ProcessKit.Tests

open System
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type ReadinessTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Stays alive a few seconds without producing the awaited signal.
    let lingering () =
        if isWindows then
            shell "ping 127.0.0.1 -n 5 >NUL"
        else
            shell "sleep 4"

    [<Test>]
    member _.``WaitForLine matches a stdout line, then dispose reaps the rest``() : Task =
        task {
            let command =
                if isWindows then
                    shell "echo ready&ping 127.0.0.1 -n 5 >NUL"
                else
                    shell "echo ready; sleep 4"

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match! running.WaitForLineAsync((fun line -> line.Contains "ready"), TimeSpan.FromSeconds 5.0) with
                | Ok line -> Assert.That(line, Does.Contain "ready")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForLine times out with NotReady when the line never appears``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match!
                    running.WaitForLineAsync((fun line -> line.Contains "never"), TimeSpan.FromMilliseconds 300.0)
                with
                | Error(ProcessError.NotReady _) -> Assert.Pass()
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPort connects to a listening port``() : Task =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        task {
            try
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running
                    let endpoint = IPEndPoint(IPAddress.Loopback, port)

                    match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 3.0) with
                    | Ok() -> Assert.Pass()
                    | Error error -> Assert.Fail $"{error}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.``WaitFor polls a custom predicate``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let started = DateTime.UtcNow

                let probe () =
                    Task.FromResult((DateTime.UtcNow - started).TotalMilliseconds > 200.0)

                match! running.WaitForAsync(probe, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitAny returns the first process to exit``() : Task =
        task {
            match! runner.StartAsync(shell "exit 0", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok fast ->
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok slow ->
                    use fast = fast
                    use slow = slow

                    let! result = RunningProcess.WaitAnyAsync [| fast; slow |]
                    Assert.That(result.Index, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``WaitAll waits for every process``() : Task =
        task {
            match! runner.StartAsync(shell "exit 3", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok first ->
                match! runner.StartAsync(shell "exit 0", CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok second ->
                    use first = first
                    use second = second
                    let! outcomes = RunningProcess.WaitAllAsync [| first; second |]
                    Assert.That(outcomes.Length, Is.EqualTo 2)
        }
        :> Task
