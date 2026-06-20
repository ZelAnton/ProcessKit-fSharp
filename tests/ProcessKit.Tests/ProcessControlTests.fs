namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type ProcessControlTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Sleeps ~3s; killed well before that by every test here.
    let sleeper =
        if isWindows then
            shell "ping -n 4 127.0.0.1 >nul"
        else
            shell "sleep 3"

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    [<Test>]
    member _.``Members lists a child started into the group``() : Task =
        task {
            use group = create ()

            match! group.Start sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let members =
                    match group.Members() with
                    | Ok pids -> pids
                    | Error error -> failwith $"Members failed: {error}"

                Assert.That(members, Is.Not.Empty)

                match running.Pid with
                | Some pid -> Assert.That(members, Does.Contain pid)
                | None -> Assert.Fail "expected a pid"

                running.StartKill()
                let! _ = running.Wait()
                ()
        }
        :> Task

    [<Test>]
    member _.``Signal delivers to the group (Kill on Windows, Term on POSIX)``() : Task =
        task {
            use group = create ()

            match! group.Start sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                if isWindows then
                    match group.Signal Signal.Term with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Term on Windows, got {other}"

                    match group.Signal Signal.Kill with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.Wait()
                    Assert.That(outcome.IsExited, Is.True)
                else
                    match group.Signal Signal.Term with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.Wait()

                    match outcome with
                    | Outcome.Signalled _ -> Assert.Pass()
                    | other -> Assert.Fail $"expected Signalled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Suspend then Resume leaves the process able to complete``() : Task =
        task {
            use group = create ()

            // Sleeps briefly then prints; a 10s timeout turns a failed Resume (process stuck frozen)
            // into a TimedOut outcome the assertion catches, instead of hanging the test.
            let printer =
                if isWindows then
                    shell "ping -n 2 127.0.0.1 >nul & echo done"
                else
                    shell "sleep 0.5; echo done"
                |> Command.timeout (TimeSpan.FromSeconds 10.0)

            match! group.Start printer with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match group.Suspend() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"suspend: {error}"

                match group.Resume() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"resume: {error}"

                let! outcome = running.Wait()

                match outcome with
                | Outcome.Exited _ -> Assert.Pass()
                | Outcome.TimedOut -> Assert.Fail "process stayed suspended — Resume did not thaw it"
                | other -> Assert.Fail $"{other}"
        }
        :> Task

    [<Test>]
    member _.``a ProcessGroup is an IProcessRunner that runs into the shared group``() : Task =
        task {
            use group = create ()
            let runner: IProcessRunner = group

            match! runner.OutputString(shell "echo shared", CancellationToken.None) with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "shared")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Supervisor can restart into a shared ProcessGroup``() : Task =
        task {
            use group = create ()

            let sup =
                Supervisor(shell "echo supervised").Restart(RestartPolicy.Never).WithRunner(group :> IProcessRunner)

            match! sup.Run() with
            | Ok outcome -> Assert.That(outcome.FinalResult.Stdout, Does.Contain "supervised")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout reaps a long-running first stage``() : Task =
        task {
            // The sleeper is the FIRST stage (its own pgid on POSIX); the consumer exits at once.
            // Only the multi-child containment fix kills the first stage promptly — without it the
            // pipeline would block on the 30s sleeper's natural exit.
            let longSleeper =
                if isWindows then
                    shell "ping -n 31 127.0.0.1 >nul"
                else
                    shell "sleep 30"

            let pipeline =
                longSleeper.Pipe(shell "echo done").Timeout(TimeSpan.FromMilliseconds 300.0)

            let stopwatch = Stopwatch.StartNew()
            let! result = pipeline.Run()
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected Timeout, got {other}"

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 10.0),
                "the long-running first stage was not reaped promptly"
            )
        }
        :> Task
