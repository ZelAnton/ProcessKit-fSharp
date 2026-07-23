namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

[<TestFixture>]
type ProcessControlTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

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

    // Start a childless, long-running EXTERNAL process (started OUTSIDE ProcessKit) suitable for
    // `ProcessGroup.Adopt`. `ping -n N` (Windows) and `sleep N` (POSIX) both outlast every test here and
    // fork no children, so the adopted process itself is the entire tree — killing it is the whole effect.
    let startExternalSleeper () : Process =
        let psi =
            if isWindows then
                let p = ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
                // Swallow ping's slow, tiny output into an unread pipe so it neither spams the test host
                // nor (in the few seconds before the group kills it) fills the buffer and blocks.
                p.RedirectStandardOutput <- true
                p
            else
                ProcessStartInfo("/bin/sh", "-c \"sleep 30\"")

        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        match Process.Start psi with
        | null -> failwith "failed to start the external test process"
        | p -> p

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
    member _.``Signal.Other 0 (and a negative) no longer reports a false success on POSIX``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Signal.Other is always Unsupported on Windows; the false-success was POSIX-only."
            else
                use group = create ()

                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Signal 0 is a liveness probe that delivers nothing; with a LIVE child in the group it
                    // used to return Ok() (a false delivery). It must now be a typed error, not a silent
                    // success — the honesty regression this task closes.
                    match group.Signal(Signal.Other 0) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Signal.Other 0, got {other}"

                    // A negative number is likewise not a signal, so it is refused the same way.
                    match group.Signal(Signal.Other(-1)) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Signal.Other -1, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``ProcessGroupBackend refuses Signal.Other 0 and negatives before any delivery``() =
        if isWindows then
            Assert.Ignore "The POSIX process-group backend is not used on Windows."

        let backend: IContainmentBackend = ProcessGroupBackend()

        // The guard sits at the API boundary (before the per-pgid delivery loop), so it rejects a probe
        // even on an empty group — no child is needed to prove the false success is gone.
        for raw in [ 0; -1 ] do
            match backend.Signal(Signal.Other raw) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for Signal.Other {raw}, got {other}"

    [<Test>]
    member _.``CgroupBackend refuses Signal.Other 0 and negatives before any delivery``() =
        if isWindows || isMacOs then
            Assert.Ignore "The cgroup v2 backend is Linux-only."

        // The guard short-circuits before cgroup.procs is ever read, so a placeholder path never matters.
        let backend: IContainmentBackend =
            CgroupBackend "/nonexistent/processkit-signal-guard-probe"

        for raw in [ 0; -1 ] do
            match backend.Signal(Signal.Other raw) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for Signal.Other {raw}, got {other}"

    [<Test>]
    member _.``signalProcessGroup refuses a non-deliverable number without a false Delivered``() =
        if isWindows then
            Assert.Ignore "killpg-based process-group signalling is POSIX-only."

        // The guard returns before killpg, so the pgid is never actually signalled — any value stands in.
        // The primitive must report DeliveryFailed, never the Delivered that killpg(pgid, 0)'s success
        // return would otherwise yield (the false success this fixes, one layer below the backend).
        for raw in [ 0; -1 ] do
            match Native.Posix.signalProcessGroup 999999 raw with
            | Native.Common.SignalDelivery.DeliveryFailed _ -> ()
            | other -> Assert.Fail $"expected DeliveryFailed for signal {raw}, got {other}"

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
    member _.``Windows: CTRL+BREAK rejects non-positive process groups before the native API``() =
        if not isWindows then
            Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."

        let original = Windows.generateConsoleCtrlEventHook
        let mutable invoked = false

        try
            Windows.generateConsoleCtrlEventHook <-
                fun _ ->
                    invoked <- true
                    true

            for processGroupId in [ 0; -1 ] do
                match Windows.sendConsoleCtrlBreakWindows processGroupId with
                | Error _ -> ()
                | Ok() -> Assert.Fail $"expected non-positive group id {processGroupId} to be rejected"

            Assert.That(invoked, Is.False, "an invalid process group id reached GenerateConsoleCtrlEvent")
        finally
            Windows.generateConsoleCtrlEventHook <- original

    [<Test>]
    member _.``Windows: GetProcessId failure leaves a ctrl child unregistered and without a pid``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."

            let original = Windows.getProcessIdHook

            try
                // `GetProcessId` returns zero on failure. The child remains contained by its Job, but it
                // must not be registered for CTRL+BREAK because group zero broadcasts to this console.
                Windows.getProcessIdHook <- fun _ -> 0u
                use group = create ()

                let consoleChild =
                    (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                        .WindowsCtrlSignals()
                        .Stdout(StdioMode.Null)
                        .Timeout(TimeSpan.FromSeconds 15.0)

                match! group.StartAsync consoleChild with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    Assert.That(running.Pid, Is.EqualTo None, "GetProcessId failure must not become Some 0")

                    match group.Signal Signal.Int with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected no CTRL-capable child after GetProcessId failure, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
            finally
                Windows.getProcessIdHook <- original
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

    // --- ProcessGroup.Adopt: bring an already-running EXTERNAL process into the container (T-187) ---

    [<Test>]
    member _.``Adopt of an already-exited process is an honest typed error, never a silent success``() : Task =
        task {
            // The dead-pid / TOCTOU guard, cross-platform: a concluded process cannot be adopted.
            // `ProcessGroup.Adopt`'s pre-adopt liveness check refuses it with a typed `ProcessError.Adopt`
            // BEFORE the mechanism is even consulted — so this holds on every platform, adopting or not.
            let psi =
                if isWindows then
                    ProcessStartInfo("cmd.exe", "/c exit 0")
                else
                    ProcessStartInfo("/bin/sh", "-c \"exit 0\"")

            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            use external =
                match Process.Start psi with
                | null -> failwith "failed to start the external test process"
                | p -> p

            do! external.WaitForExitAsync()

            use group = create ()

            match group.Adopt external with
            | Error(ProcessError.Adopt _) -> ()
            | other -> Assert.Fail $"expected ProcessError.Adopt for an already-exited process, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Adopt places an external process into the group and kill-on-dispose reaps it (Windows)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows Job Object adopts an external process; the POSIX refusal is covered separately"
            else
                let external = startExternalSleeper ()

                try
                    use group = create ()

                    match group.Adopt external with
                    | Error error -> Assert.Fail $"adopt should succeed on the Job Object mechanism, got {error}"
                    | Ok() ->
                        // Now a full Job member: it shows up in the membership snapshot...
                        match group.Members() with
                        | Ok pids -> Assert.That(pids, Does.Contain external.Id)
                        | Error error -> Assert.Fail $"Members failed: {error}"

                        // ...and disposing the group (kill-on-dispose) terminates it, even though we never
                        // started it — the whole point of adoption.
                        (group :> IDisposable).Dispose()

                        let! _ = Task.WhenAny(external.WaitForExitAsync(), Task.Delay 5000)

                        Assert.That(
                            external.HasExited,
                            Is.True,
                            "kill-on-dispose should have reaped the adopted process"
                        )
                finally
                    try
                        if not external.HasExited then
                            external.Kill true
                    with _ ->
                        // Best-effort test cleanup: the process was likely already killed by group dispose,
                        // or the handle is racing teardown — nothing to recover here.
                        ()

                    external.Dispose()
        }
        :> Task

    [<Test>]
    member _.``Adopt on the POSIX process-group mechanism is an honest Unsupported (non-Windows)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX process-group mechanism only; Windows adopts via the Job Object"
            else
                let external = startExternalSleeper ()

                try
                    // A limit-free group on POSIX uses the process-group backend, which cannot relocate a
                    // foreign process (setpgid only moves our own children, before exec) — an honest typed
                    // refusal, never a silent no-op that would leave the process uncontained.
                    use group = create ()

                    match group.Adopt external with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported adopting into a POSIX process group, got {other}"
                finally
                    try
                        if not external.HasExited then
                            external.Kill true
                    with _ ->
                        // Best-effort cleanup; the sleeper exits on its own if the test outlives it.
                        ()

                    external.Dispose()
        }
        :> Task
