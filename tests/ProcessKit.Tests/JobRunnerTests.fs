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
    member _.``Start with an already-cancelled token reports Cancelled without spawning``() : Task =
        task {
            use cts = new CancellationTokenSource()
            cts.Cancel()

            match! runner.StartAsync(shell "echo hi", cts.Token) with
            | Error(ProcessError.Cancelled _) -> ()
            | Error other -> Assert.Fail $"expected Cancelled, got {other}"
            | Ok running ->
                do! (running :> IAsyncDisposable).DisposeAsync()
                Assert.Fail "expected Cancelled, but a process was started"
        }
        :> Task

    [<Test>]
    member _.``a token that fires after StartAsync does not terminate the caller-driven live handle``() : Task =
        // Locks in the documented `StartAsync`/`SpawnAsync` contract: the token is checked exactly once,
        // before the spawn, and is NOT tracked once the child is running. A live handle is caller-driven
        // (unlike the completion verbs, which watch the token for the whole run — see `cancelling
        // terminates the contained process promptly` below). If this behaviour ever regresses into a
        // post-spawn watchdog, the cancelled token would kill the sleeper and the exit wait would complete
        // well within the window below.
        task {
            use cts = new CancellationTokenSource()

            match! runner.StartAsync(sleeper, cts.Token) with
            | Error error -> Assert.Fail $"expected a live handle, got {error}"
            | Ok running ->
                // Fire the token after the spawn; the caller-driven handle must ignore it.
                cts.Cancel()
                let exitWait = RunningProcess.WaitAnyAsync [| running |]
                let! _ = Task.WhenAny(exitWait :> Task, Task.Delay(TimeSpan.FromMilliseconds 400.0))

                Assert.That(
                    exitWait.IsCompleted,
                    Is.False,
                    "a token that fires after StartAsync must not terminate the live handle"
                )

                // The caller — not the token — reaps it.
                running.Kill()
                let! _ = exitWait
                do! (running :> IAsyncDisposable).DisposeAsync()
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
