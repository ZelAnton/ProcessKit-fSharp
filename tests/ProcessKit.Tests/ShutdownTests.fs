namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// A minimal `IContainmentBackend` fault-injection seam for the T-132 regression below, local to this
/// file: `ContainmentBugTests.SyntheticBackend` compiles AFTER `ShutdownTests.fs` in the `.fsproj`'s
/// dependency order, so it is not visible here. Every verb is a no-op except `GracefulKillTree`, which
/// always throws synchronously (simulating a faulting graceful-teardown stage), and `HardRelease`,
/// whose call count this test asserts on directly.
type private GracefulFaultBackend() =
    let mutable hardReleaseCount = 0

    /// How many times teardown (`HardRelease`) ran — must be exactly one, even after the graceful
    /// stage faults, and must never run a second time on a later idempotent call.
    member _.HardReleaseCount = hardReleaseCount

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(_command) =
            Ok
                { Native.Common.Spawned.Handle = 0n
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  WindowsCtrlGroup = false
                  PtyControl = None }

        member _.Track(_spawned) = Ok()
        member _.Adopt(_pid) = Ok()
        member _.Release(_spawned) = ()
        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(_spawned) = None
        member _.KillChild(_spawned) = ()
        member _.KillTree() = ()

        member _.GracefulKillTree(_grace) : Task =
            // The fault this backend exists to inject: `ShutdownAsync`'s graceful stage must still
            // guarantee `hardRelease()` runs, and must still propagate this exception to the caller.
            raise (InvalidOperationException "synthetic graceful-kill failure")

        member _.Members() = Ok []
        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()
        member _.Stats() = Ok(ProcessGroupStats(0, None, None))
        member _.UpdateLimits(_limits) = Ok()

        member _.HardRelease() =
            hardReleaseCount <- hardReleaseCount + 1

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

    // A Windows child that owns a REAL top-level GUI window (a WinForms form pumping a message loop),
    // for the best-effort WM_CLOSE soft-close path (T-146). It prints `ready` from the form's `Shown`
    // handler — so the window provably exists before the test posts WM_CLOSE — then blocks in
    // `Application.Run`. A WM_CLOSE closes the form, `Application.Run` returns, and PowerShell exits
    // cleanly with code 0; a hard `TerminateJobObject` would instead give a non-zero exit. So a later
    // `Exited 0` proves the soft close reached the window AND the grace window let it act, exactly as
    // the POSIX SIGTERM test asserts a clean `Exited 42`. `-Sta` gives the required apartment for a
    // message pump; `-NoProfile`/`-NonInteractive` keep stdout to just the `ready` line.
    let windowedChildScript =
        "Add-Type -AssemblyName System.Windows.Forms; "
        + "$f = New-Object System.Windows.Forms.Form; "
        + "$f.ShowInTaskbar = $false; "
        + "$f.WindowState = 'Minimized'; "
        + "$f.Add_Shown({ [Console]::Out.WriteLine('ready'); [Console]::Out.Flush() }); "
        + "[System.Windows.Forms.Application]::Run($f)"

    let windowedChild =
        Command.create "powershell.exe"
        |> Command.args [ "-NoProfile"; "-NonInteractive"; "-Sta"; "-Command"; windowedChildScript ]

    // Gate a windowed-child test on its window actually coming up: return true once `ready` is seen, or
    // false (after reaping the child) when it never arrives — a host with no interactive desktop that
    // WinForms can render on, where the WM_CLOSE scenario simply cannot be exercised. Callers
    // `Assert.Ignore` on false rather than fail, mirroring the other best-effort platform gates here.
    //
    // `WaitForLineAsync` starts the stdout STREAMING session, so from here the outcome must be observed
    // through `FinishAsync`/`StopAsync` (which reuse that session), never `WaitAsync` (a second,
    // conflicting consumption). `finishOutcome` below is the shared way to read it.
    let awaitWindowReady (proc: RunningProcess) : Task<bool> =
        task {
            match! proc.WaitForLineAsync((fun line -> line = "ready"), TimeSpan.FromSeconds 20.0) with
            | Ok _ -> return true
            | Error _ ->
                proc.Kill()
                let! _ = proc.FinishAsync() // reuses the streaming session to reap the killed child
                return false
        }

    // The exit outcome of a windowed child whose stdout streaming session is already underway (started
    // by `awaitWindowReady`): `FinishAsync` reuses that session's wait instead of claiming a second one.
    let finishOutcome (proc: RunningProcess) : Task<Outcome> =
        task {
            match! proc.FinishAsync() with
            | Ok finished -> return finished.Outcome
            | Error error -> return failwith $"FinishAsync failed: {error}"
        }

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
    member _.``ShutdownAsync still hard-releases the container when the graceful kill throws``() : Task =
        task {
            let backend = GracefulFaultBackend()
            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

            // The graceful stage faults; ShutdownAsync's `finally` must still run `hardRelease()`
            // (never a forever-unreleased Job handle/cgroup/process group) AND propagate the original
            // exception to this call's Task rather than swallow it.
            let mutable thrown: exn option = None

            try
                do! group.ShutdownAsync(TimeSpan.FromMilliseconds 1.0)
            with ex ->
                thrown <- Some ex

            match thrown with
            | Some ex -> Assert.That(ex, Is.InstanceOf<InvalidOperationException>())
            | None -> Assert.Fail "expected the synthetic GracefulKillTree failure to propagate"

            Assert.That(
                backend.HardReleaseCount,
                Is.EqualTo 1,
                "hardRelease() must still run once after a graceful-kill fault"
            )

            // A follow-up ShutdownAsync/Dispose must be a harmless no-op — the container really was
            // released by the winner's `finally`, so the losers must not hang or double-release.
            do! group.ShutdownAsync(TimeSpan.FromMilliseconds 1.0)
            (group :> IDisposable).Dispose()

            Assert.That(
                backend.HardReleaseCount,
                Is.EqualTo 1,
                "a later idempotent Shutdown/Dispose call re-ran hardRelease()"
            )
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

    [<Test>]
    member _.``Windows: StopAsync closes a windowed child with WM_CLOSE inside the grace window``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The best-effort WM_CLOSE soft close is a Windows-only concern."

            let! proc = startProc windowedChild
            let! ready = awaitWindowReady proc

            if not ready then
                Assert.Ignore "the test host could not bring up a WinForms window (no interactive desktop)."
            else
                let stopwatch = Stopwatch.StartNew()
                // The soft phase posts WM_CLOSE to the form; a 5s grace is far more than the form needs to
                // close, so it should never reach the post-grace hard kill.
                let! outcome = proc.StopAsync(TimeSpan.FromSeconds 5.0)
                stopwatch.Stop()

                // Exited 0 = the form closed itself in response to WM_CLOSE and PowerShell returned cleanly,
                // NOT a TerminateJobObject hard kill (which exits non-zero). This is the whole point of the
                // soft phase for GUI children.
                match outcome with
                | Outcome.Exited 0 -> Assert.Pass()
                | Outcome.Exited code ->
                    Assert.Fail $"expected a clean Exited 0 from the WM_CLOSE close, got a non-zero Exited {code}"
                | other -> Assert.Fail $"expected Exited 0 from the WM_CLOSE close, got {other}"

                Assert.That(
                    stopwatch.Elapsed,
                    Is.LessThan(TimeSpan.FromSeconds 5.0),
                    "the child closed only at/after the grace deadline, so WM_CLOSE did not close it early"
                )
        }
        :> Task

    [<Test>]
    member _.``Windows: ShutdownAsync closes a windowed child with WM_CLOSE inside the grace window``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The best-effort WM_CLOSE soft close is a Windows-only concern."

            use group = createGroup ()

            match! group.StartAsync windowedChild with
            | Error error -> Assert.Fail $"StartAsync failed: {error}"
            | Ok running ->
                let! ready = awaitWindowReady running

                if not ready then
                    Assert.Ignore "the test host could not bring up a WinForms window (no interactive desktop)."
                else
                    // ShutdownAsync's graceful stage posts WM_CLOSE to every member's windows, polls up to
                    // the grace, then hard-kills survivors. The form should close well before the deadline.
                    let stopwatch = Stopwatch.StartNew()
                    do! group.ShutdownAsync(TimeSpan.FromSeconds 5.0)
                    let! outcome = finishOutcome running
                    stopwatch.Stop()

                    match outcome with
                    | Outcome.Exited 0 -> Assert.Pass()
                    | Outcome.Exited code ->
                        Assert.Fail $"expected a clean Exited 0 from the WM_CLOSE close, got a non-zero Exited {code}"
                    | other -> Assert.Fail $"expected Exited 0 from the WM_CLOSE close, got {other}"

                    Assert.That(
                        stopwatch.Elapsed,
                        Is.LessThan(TimeSpan.FromSeconds 5.0),
                        "the tree drained only at/after the grace deadline, so WM_CLOSE did not close the child early"
                    )
        }
        :> Task

    [<Test>]
    member _.``Windows: Signal.Term posts WM_CLOSE to a windowed child and returns Ok``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The best-effort WM_CLOSE soft close is a Windows-only concern."

            use group = createGroup ()

            match! group.StartAsync windowedChild with
            | Error error -> Assert.Fail $"StartAsync failed: {error}"
            | Ok running ->
                let! ready = awaitWindowReady running

                if not ready then
                    Assert.Ignore "the test host could not bring up a WinForms window (no interactive desktop)."
                else
                    // A windowed member turns Signal.Int/Term from the pre-T-146 honest Unsupported into a
                    // best-effort Ok: the WM_CLOSE was posted to a top-level window even though this child
                    // was NOT started with WindowsCtrlSignals() (so there is no CTRL+BREAK path).
                    match group.Signal Signal.Term with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"expected Ok for Signal.Term against a windowed child, got {error}"

                    // And the delivery is real: the form closes and PowerShell exits cleanly on its own.
                    let! outcome = finishOutcome running

                    match outcome with
                    | Outcome.Exited 0 -> Assert.Pass()
                    | other -> Assert.Fail $"expected the windowed child to close cleanly on WM_CLOSE, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Windows: a windowless child is still hard-killed under StopAsync (WM_CLOSE is a no-op)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "This pins the Windows windowless-child regression specifically."

            // A console child with no top-level window: WM_CLOSE finds nothing to post to (a no-op, not an
            // error), so the soft phase changes nothing and the post-grace hard kill still reaps it — the
            // exact pre-T-146 behaviour, unregressed.
            let! proc = startProc longSleeper

            let stopwatch = Stopwatch.StartNew()
            let! outcome = proc.StopAsync(TimeSpan.FromMilliseconds 300.0)
            stopwatch.Stop()

            assertTerminal outcome

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 10.0),
                "the windowless child was not hard-killed promptly after the grace window"
            )
        }
        :> Task

    [<Test>]
    member _.``Windows: StopAsync racing a concurrent Dispose in the grace window is use-after-close-safe``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The Windows Job-handle use-after-close race (T-162) is a Windows-only concern."

            // T-162 regression. `StopAsync(grace)`'s graceful poll on the OWNED Job tree runs OFF the
            // group's lifecycle lock, so a concurrent Dispose can win the release and close the Job handle
            // while that poll is still in flight. Before the fix the poll's liveness query + final
            // `TerminateJobObject` then ran on the just-closed handle — a use-after-close, and a potential
            // wrong-target terminate on a handle value the OS had recycled. Now the poll owns its OWN
            // duplicate Job handle, so the concurrent close can neither invalidate nor divert it. Pin the
            // documented public contract: `StopAsync` stays race-safe with `Dispose` — no invalid-handle
            // exception escapes either call, the tree is still reaped, and both return promptly. A
            // windowless child (no WM_CLOSE target, ignores the soft phase) keeps the poll looping for the
            // whole grace window, so the mid-poll close lands squarely inside it; loop to widen the window.
            for _ in 1..5 do
                let! proc = startProc longSleeper

                // Kick off the graceful stop: its duplicate handle is taken and the poll enters its wait
                // loop synchronously as this call returns. Then, well inside the grace window, close the
                // Job handle underneath the in-flight poll via a concurrent Dispose.
                let stop = proc.StopAsync(TimeSpan.FromSeconds 1.0)
                do! Task.Delay 150
                let dispose = (proc :> IAsyncDisposable).DisposeAsync().AsTask()

                // Neither public call may throw an invalid-handle error; the child must conclude terminally.
                let! outcome = stop
                do! dispose
                assertTerminal outcome
        }
        :> Task
