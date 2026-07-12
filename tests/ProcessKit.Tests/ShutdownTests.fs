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
type ShutdownTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let createGroup () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    let tempMarker (prefix: string) =
        let id = Guid.NewGuid().ToString("N")
        Path.Combine(Path.GetTempPath(), $"pk-{prefix}-{id}.marker")

    // Start a live handle over the DEFAULT runner's private, handle-owned group (`Command.StartAsync()`),
    // which — unlike the shared-group `group.StartAsync` above — gets the full graceful stop on Unix.
    let startProc (command: Command) : Task<RunningProcess> =
        task {
            match! command.StartAsync() with
            | Ok proc -> return proc
            | Error error -> return failwith $"StartAsync failed: {error}"
        }

    // A child that sleeps far longer than any test needs — killed well before it wakes.
    let longSleeper =
        if isWindows then
            shell "ping 127.0.0.1 -n 30 >nul"
        else
            shell "sleep 30"

    let assertTerminal (outcome: Outcome) =
        match outcome with
        | Outcome.Exited _
        | Outcome.Signalled _ -> ()
        | other -> Assert.Fail $"expected a terminal Exited/Signalled outcome, got {other}"

    [<Test>]
    member _.``Shutdown reaps a running process promptly``() : Task =
        task {
            use group = createGroup ()

            let sleeper =
                if isWindows then
                    shell "ping 127.0.0.1 -n 30"
                else
                    shell "sleep 30"

            let stopwatch = Stopwatch.StartNew()
            // Start the run but do not await it; Shutdown must kill the still-running child.
            let capture =
                (group :> IProcessRunner).OutputStringAsync(sleeper, CancellationToken.None)

            do! Task.Delay 300
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            let! result = capture
            stopwatch.Stop()

            match result with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"capture failed: {error}"

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 15.0))
        }
        :> Task

    [<Test>]
    member _.``Shutdown is idempotent with itself and Dispose``() : Task =
        task {
            let group = createGroup ()
            // The second Shutdown and the Dispose must be no-ops, not signal a released handle/pgid.
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            (group :> IDisposable).Dispose()
            Assert.Pass()
        }
        :> Task

    [<Test>]
    member _.``ShutdownAsync rejects negative grace and accepts zero and positive grace``() : Task =
        task {
            use zeroGraceGroup = createGroup ()

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> zeroGraceGroup.ShutdownAsync(TimeSpan.FromMilliseconds -1.0) |> ignore)
            )
            |> ignore

            do! zeroGraceGroup.ShutdownAsync(TimeSpan.Zero)

            use positiveGraceGroup = createGroup ()
            do! positiveGraceGroup.ShutdownAsync(TimeSpan.FromMilliseconds 1.0)
        }
        :> Task

    [<Test>]
    member _.``Shutdown reaps a lingering grandchild``() : Task =
        if isWindows then
            Assert.Ignore "Deep-tree reaping uses POSIX shell job control; exercised on Unix."

        // The parent backgrounds a grandchild (detached stdio, so the parent's pipe still EOFs)
        // that writes a marker after a delay. Containment + Shutdown must reap it first.
        task {
            let marker = tempMarker "reap"

            try
                use group = createGroup ()
                let script = $"( sleep 3 && touch {marker} ) </dev/null >/dev/null 2>&1 &"

                match! (group :> IProcessRunner).OutputStringAsync(shell script, CancellationToken.None) with
                | Error error -> Assert.Fail $"spawn failed: {error}"
                | Ok _ -> ()

                do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
                do! Task.Delay 4000
                Assert.That(File.Exists marker, Is.False, "grandchild escaped the group and wrote its marker")
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``a setsid child escapes the group (documented weakness)``() : Task =
        if not isLinux then
            Assert.Ignore "The setsid-escape demonstration needs the Linux `setsid` binary."

        // A POSIX process group is escapable: a child that calls setsid() starts a new session
        // and is no longer reached by killpg. This is the documented weakness cgroup v2 closes.
        task {
            let marker = tempMarker "setsid"

            try
                use group = createGroup ()

                let script =
                    $"( setsid sh -c 'sleep 3 && touch {marker}' ) </dev/null >/dev/null 2>&1 &"

                match! (group :> IProcessRunner).OutputStringAsync(shell script, CancellationToken.None) with
                | Error error -> Assert.Fail $"spawn failed: {error}"
                | Ok _ -> ()

                // Let setsid finish establishing the new session before teardown, so we test an
                // *established* escapee rather than racing the detach.
                do! Task.Delay 1000
                do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
                do! Task.Delay 4000

                Assert.That(
                    File.Exists marker,
                    Is.True,
                    "a setsid child should escape killpg — the documented pgroup weakness"
                )
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``StopAsync stops a live handle and returns an honest terminal outcome``() : Task =
        task {
            let! proc = startProc longSleeper

            let stopwatch = Stopwatch.StartNew()
            let! outcome = proc.StopAsync(TimeSpan.FromSeconds 1.0)
            stopwatch.Stop()

            // The child concluded for real — never left running, never a fabricated clean exit; and a
            // killed/non-zero exit is returned as data, not raised.
            assertTerminal outcome
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 15.0), "StopAsync did not return promptly")
        }
        :> Task

    [<Test>]
    member _.``StopAsync rejects negative grace and accepts zero and positive grace``() : Task =
        task {
            let! zeroGraceProcess = startProc longSleeper

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> zeroGraceProcess.StopAsync(TimeSpan.FromMilliseconds -1.0) |> ignore)
            )
            |> ignore

            let! zeroGraceOutcome = zeroGraceProcess.StopAsync(TimeSpan.Zero)
            assertTerminal zeroGraceOutcome

            let! positiveGraceProcess = startProc longSleeper
            let! positiveGraceOutcome = positiveGraceProcess.StopAsync(TimeSpan.FromMilliseconds 1.0)
            assertTerminal positiveGraceOutcome
        }
        :> Task

    [<Test>]
    member _.``StopAsync lets a signal-handling child exit cleanly within the grace window``() : Task =
        if isWindows then
            Assert.Ignore "A soft SIGTERM has no Windows equivalent; StopAsync is the atomic Job kill there."

        task {
            // Trap SIGTERM and exit 42, announcing "ready" only AFTER the trap is installed. The
            // `sleep & wait` loop makes the shell act on the trap promptly (a bare foreground `sleep`
            // would defer the handler until the sleep returned).
            let trapping =
                shell "trap 'exit 42' TERM; echo ready; while :; do sleep 0.2 & wait $!; done"

            let! proc = startProc trapping

            // Gate on that readiness line before signalling. `posix_spawn` returns the instant the child
            // has exec'd — BEFORE the shell has run `trap` — so a SIGTERM sent immediately would race the
            // handler's installation and land on the default disposition (a bare Signalled 15), never the
            // trap. Waiting for "ready" makes the soft signal deterministically hit an installed trap.
            match! proc.WaitForLineAsync((fun line -> line = "ready"), TimeSpan.FromSeconds 10.0) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"child never signalled readiness before StopAsync: {error}"

            let! outcome = proc.StopAsync(TimeSpan.FromSeconds 5.0)

            // Exited 42 (not a Signalled SIGKILL) proves the soft signal reached the child AND the grace
            // window let it shut itself down cleanly, rather than being force-killed after the grace.
            match outcome with
            | Outcome.Exited 42 -> Assert.Pass()
            | other -> Assert.Fail $"expected a clean Exited 42 from the SIGTERM handler, got {other}"
        }
        :> Task

    [<Test>]
    member _.``StopAsync hard-kills a child that ignores the soft signal after the grace window``() : Task =
        if isWindows then
            Assert.Ignore "Ignoring SIGTERM to force the SIGKILL escalation is a POSIX scenario."

        task {
            // Ignore SIGTERM entirely, so the soft signal cannot stop it — only the post-grace SIGKILL can.
            let stubborn = shell "trap '' TERM; while :; do sleep 0.2 & wait $!; done"
            let! proc = startProc stubborn

            let stopwatch = Stopwatch.StartNew()
            let! outcome = proc.StopAsync(TimeSpan.FromMilliseconds 300.0)
            stopwatch.Stop()

            // It ignored SIGTERM, so it was force-killed after the grace window — an honest Signalled.
            match outcome with
            | Outcome.Signalled _ -> ()
            | other -> Assert.Fail $"expected Signalled (SIGKILL) after the grace window, got {other}"

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 10.0),
                "the escalation to a hard kill did not happen promptly after the grace window"
            )
        }
        :> Task

    [<Test>]
    member _.``parameterless StopAsync uses the default grace and stops the process``() : Task =
        task {
            let! proc = startProc longSleeper

            let stopwatch = Stopwatch.StartNew()
            let! outcome = proc.StopAsync() // default 2s grace, matching ProcessGroupOptions.ShutdownTimeout
            stopwatch.Stop()

            assertTerminal outcome

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 15.0),
                "StopAsync() did not return promptly"
            )
        }
        :> Task

    [<Test>]
    member _.``StopAsync composes with an in-flight streaming FinishAsync``() : Task =
        task {
            // Streams one line, then blocks; StopAsync must reuse the streaming session's own wait (not
            // race a second reader on the pipe) and stay consistent with a concurrent FinishAsync.
            let streamer =
                if isWindows then
                    shell "echo ready& ping 127.0.0.1 -n 30 >nul"
                else
                    shell "echo ready; sleep 30"

            let! proc = startProc streamer

            // Begin the stdout streaming session (starts the pumps + the shared streamOutcome).
            let lines = proc.StdoutLinesAsync()
            let enumerator = lines.GetAsyncEnumerator()
            let! _ = enumerator.MoveNextAsync() // pull the first line so streaming is genuinely underway

            // A terminal FinishAsync in flight, then StopAsync concurrently — both reuse streamOutcome.
            let finishTask = proc.FinishAsync()
            let! stopOutcome = proc.StopAsync(TimeSpan.FromSeconds 1.0)
            let! finished = finishTask
            do! enumerator.DisposeAsync()

            assertTerminal stopOutcome

            match finished with
            | Ok f -> Assert.That(f.Outcome, Is.EqualTo stopOutcome, "FinishAsync and StopAsync saw different outcomes")
            | Error error -> Assert.Fail $"FinishAsync failed: {error}"
        }
        :> Task

    [<Test>]
    member _.``StopAsync is idempotent with a repeat call and a following Dispose``() : Task =
        task {
            let! proc = startProc longSleeper

            let! first = proc.StopAsync(TimeSpan.FromSeconds 1.0)
            // A repeat StopAsync is a no-op that returns the same conclusion — never a second teardown,
            // nor a native kill re-entered on the already-released container.
            let! second = proc.StopAsync(TimeSpan.FromSeconds 1.0)
            // And a following Dispose is a harmless no-op.
            do! (proc :> IAsyncDisposable).DisposeAsync()

            assertTerminal first
            Assert.That(second, Is.EqualTo first, "a repeat StopAsync returned a different outcome")
        }
        :> Task

    [<Test>]
    member _.``StopAsync alongside Kill returns an honest outcome without throwing``() : Task =
        task {
            let! proc = startProc longSleeper

            proc.Kill() // fire-and-forget hard kill
            let! outcome = proc.StopAsync(TimeSpan.FromSeconds 1.0) // stop on top of the in-flight kill

            assertTerminal outcome
        }
        :> Task
