namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type JobRunnerTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // ~30s if left alone; a prompt return proves the container killed it.
    let sleeper =
        if isWindows then
            shell "ping 127.0.0.1 -n 30"
        else
            shell "sleep 30"

    [<Test>]
    member _.``run captures stdout and trims the trailing newline``() : Task =
        task {
            let! result = shell "echo hello" |> Runner.run runner CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "hello")
            | Error error -> Assert.Fail $"expected Ok, got {error}"
        }
        :> Task

    [<Test>]
    member _.``outputString reports a non-zero exit as data, not an error``() : Task =
        task {
            let! result = shell "exit 3" |> Runner.outputString runner CancellationToken.None

            match result with
            | Ok value ->
                Assert.That(value.Code, Is.EqualTo(Some 3))
                Assert.That(value.IsSuccess, Is.False)
            | Error error -> Assert.Fail $"expected Ok, got {error}"
        }
        :> Task

    [<Test>]
    member _.``exitCode returns the real exit code``() : Task =
        task {
            let! result = shell "exit 7" |> Runner.exitCode runner CancellationToken.None
            Assert.That(result, Is.EqualTo(Ok 7: Result<int, ProcessError>))
        }
        :> Task

    [<Test>]
    member _.``a missing program yields NotFound``() : Task =
        task {
            let! result =
                Command.create "processkit-no-such-program-xyz"
                |> Runner.outputString runner CancellationToken.None

            match result with
            | Error(ProcessError.NotFound _) -> Assert.Pass()
            | other -> Assert.Fail $"expected NotFound, got {other}"
        }
        :> Task

    [<Test>]
    member _.``an env override is visible to the child``() : Task =
        task {
            let script =
                if isWindows then
                    "echo %PK_TEST_VAR%"
                else
                    "echo $PK_TEST_VAR"

            let command = shell script |> Command.env "PK_TEST_VAR" "pk-value"
            let! result = command |> Runner.run runner CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "pk-value")
            | Error error -> Assert.Fail $"expected Ok, got {error}"
        }
        :> Task

    [<Test>]
    member _.``cancelling terminates the contained process promptly``() : Task =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)
            let stopwatch = Stopwatch.StartNew()
            let! result = sleeper |> Runner.run runner cts.Token
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 15.0))
        }
        :> Task
