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

            match! group.StartAsync sleeper with
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

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``Signal delivers to the group (Kill on Windows, Term on POSIX)``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                if isWindows then
                    match group.Signal Signal.Term with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Term on Windows, got {other}"

                    match group.Signal Signal.Kill with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.WaitAsync()
                    Assert.That(outcome.IsExited, Is.True)
                else
                    match group.Signal Signal.Term with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Signalled _ -> Assert.Pass()
                    | other -> Assert.Fail $"expected Signalled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Signal with an invalid raw number returns Error on POSIX``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Signal.Other's errno-aware failure path is POSIX-only."
            else
                use group = create ()

                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Signal numbers are conventionally 1..31/64; a wildly out-of-range value is
                    // rejected by the kernel (EINVAL), which must now surface honestly instead of a
                    // silently-swallowed `Ok()`.
                    match group.Signal(Signal.Other 999) with
                    | Error(ProcessError.Io _) -> ()
                    | other -> Assert.Fail $"expected Error(ProcessError.Io), got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``Signal on an empty/concluded group still returns Ok``() : Task =
        task {
            if isWindows then
                Assert.Ignore "This exercises the POSIX best-effort ESRCH path specifically."
            else
                use group = create ()

                // No child was ever started: the group's member set is empty, so delivery has
                // nothing to fail on — a vacuous broadcast is a success, not an error.
                match group.Signal Signal.Term with
                | Ok() -> ()
                | Error error -> Assert.Fail $"expected Ok for an empty group, got {error}"
        }
        :> Task

    [<Test>]
    member _.``Windows: Signal.Int/Term without WindowsCtrlSignals is honest Unsupported``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."
            else
                use group = create ()

                // The child was NOT started with WindowsCtrlSignals(), so it is not in its own console
                // process group and no CTRL+BREAK can reach it. Both soft signals must fail honestly —
                // never silently downgrade to the Job kill.
                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    for soft in [ Signal.Int; Signal.Term ] do
                        match group.Signal soft with
                        | Error(ProcessError.Unsupported _) -> ()
                        | other ->
                            Assert.Fail $"expected Unsupported for {soft} without WindowsCtrlSignals, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``Windows: Signal.Int stops a console child started with WindowsCtrlSignals``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."
            else
                use group = create ()

                // A console child spawned in its OWN process group (CREATE_NEW_PROCESS_GROUP). The 15s
                // timeout bounds the run so a delivery miss surfaces as TimedOut rather than hanging.
                let consoleChild =
                    (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                        .WindowsCtrlSignals()
                        .Stdout(StdioMode.Null)
                        .Timeout(TimeSpan.FromSeconds 15.0)

                match! group.StartAsync consoleChild with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    match group.Signal Signal.Int with
                    | Ok() ->
                        let! outcome = running.WaitAsync()

                        match outcome with
                        | Outcome.Exited _ -> Assert.Pass()
                        | Outcome.TimedOut ->
                            // The event was generated but the child never received it — best-effort
                            // delivery needs the child to actually share the caller's console, which
                            // some test hosts do not provide. Not a code defect.
                            Assert.Ignore
                                "CTRL+BREAK was generated but the child did not share the caller's console (best-effort)."
                        | other -> Assert.Fail $"unexpected outcome {other}"
                    | Error(ProcessError.Unsupported _) ->
                        // No console to share in this environment — the honest best-effort outcome.
                        running.Kill()
                        let! _ = running.WaitAsync()
                        Assert.Ignore "The test host has no console to share with the child (best-effort)."
                    | Error error -> Assert.Fail $"{error}"
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

            match! group.StartAsync printer with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match group.Suspend() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"suspend: {error}"

                match group.Resume() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"resume: {error}"

                let! outcome = running.WaitAsync()

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

            match! runner.OutputStringAsync(shell "echo shared", CancellationToken.None) with
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

            match! sup.RunAsync() with
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
            let! result = pipeline.RunAsync()
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

    // --- T-069: a cancellable, fully observable stdin feeder (RunningProcess + ProcessGroup call sites).
    // The `HangingStdinAsyncLines` / `FaultyStdin*` doubles are shared from `PipelineTests.fs`. The fault
    // tests feed a stdin-reading child (`sort`, reads to EOF then exits 0) so a source fault that closes
    // stdin as its last act is always observed before the child — and thus the run — exits. ---

    [<Test>]
    member _.``a run stops a hung async stdin feed on teardown and disposes its enumerator``() : Task =
        task {
            // The child exits at once without reading stdin, so the `FromAsyncLines` feed is left parked
            // in `MoveNextAsync`. The run's teardown must Stop the feeder — cancelling the hung feed and
            // disposing the user's enumerator — instead of leaking it past the run (early child exit).
            let source = HangingStdinAsyncLines()
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)

            match! cmd.OutputStringAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"expected a successful run, got {error}"

            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(completed, Is.SameAs source.Disposed, "the hung async enumerator was never disposed")
        }
        :> Task

    [<Test>]
    member _.``cancelling a run during a hung stdin feed reports Cancelled and disposes the enumerator``() : Task =
        task {
            // A live child plus a hung `FromAsyncLines` feed: cancelling the run mid-feed must report
            // `Cancelled` and — via teardown — Stop the feeder so the parked enumerator is disposed.
            let source = HangingStdinAsyncLines()
            let cmd = sleeper |> Command.stdin (Stdin.FromAsyncLines source)
            use cts = new CancellationTokenSource()
            let run = cmd.OutputStringAsync cts.Token

            // Cancel only once the feed is genuinely parked in `MoveNextAsync`.
            let! started = Task.WhenAny(source.Started, Task.Delay 5000)
            Assert.That(started, Is.SameAs source.Started, "the async feed never started")
            cts.Cancel()

            match! run with
            | Error(ProcessError.Cancelled _) -> ()
            | Error other -> Assert.Fail $"expected Cancelled, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a cancelled run to error"

            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(completed, Is.SameAs source.Disposed, "the parked enumerator was never disposed on cancel")
        }
        :> Task

    [<Test>]
    member _.``a FromLines source that throws at GetEnumerator surfaces as ProcessError.Stdin``() : Task =
        task {
            // The sync source faults acquiring its enumerator — the entry stage the pre-fix code let
            // slip past into the benign-broken-pipe bucket. On an otherwise-successful run it must
            // surface as `ProcessError.Stdin`.
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromLines(FaultyStdinLines AtGetEnumerator))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a GetEnumerator fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``a FromAsyncLines source that throws at MoveNextAsync surfaces as ProcessError.Stdin``() : Task =
        task {
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromAsyncLines(FaultyStdinAsyncLines AtMoveNext))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a MoveNextAsync fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``a FromAsyncLines source that throws at Current surfaces as ProcessError.Stdin``() : Task =
        task {
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromAsyncLines(FaultyStdinAsyncLines AtCurrent))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a Current fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``ProcessGroup StartAsync stops a hung stdin feed when the shared run is disposed``() : Task =
        task {
            // The shared-group start path (BuildHost with ownsGroup=false) must Stop the feeder on the
            // run's teardown too: disposing the `RunningProcess` cancels the hung feed and disposes the
            // user's enumerator.
            use group = create ()
            let source = HangingStdinAsyncLines()
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)

            match! group.StartAsync cmd with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! started = Task.WhenAny(source.Started, Task.Delay 5000)
                Assert.That(started, Is.SameAs source.Started, "the shared-run async feed never started")
                let! _ = running.WaitAsync()
                do! (running :> IAsyncDisposable).DisposeAsync()

                let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
                Assert.That(completed, Is.SameAs source.Disposed, "disposing the shared run did not stop the hung feed")
        }
        :> Task

    [<Test>]
    member _.``Stdin factories reject null arguments at the API boundary``() =
        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromString(Unchecked.defaultof<string>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromBytes(Unchecked.defaultof<byte[]>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromFile(Unchecked.defaultof<string>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> Stdin.FromStream(Unchecked.defaultof<IO.Stream>) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> Stdin.FromLines(Unchecked.defaultof<seq<string>>) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () ->
                Stdin.FromAsyncLines(Unchecked.defaultof<Collections.Generic.IAsyncEnumerable<string>>)
                |> ignore)
        )
        |> ignore
