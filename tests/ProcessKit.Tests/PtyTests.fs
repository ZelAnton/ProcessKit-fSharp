namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Tests for the opt-in PTY (pseudo-terminal) mode: Stage 1 (T-137) — `PtyConfig`, the `Command.Pty` /
/// `Command.pty` builders, the build-time guards (D4/D8 + the pipeline guard), the Windows ConPTY spawn;
/// and Stage 2 (T-138) — the real POSIX `openpty` (`posix_openpt`) + `setsid --ctty` spawn: a merged
/// terminal stream (no separate stderr, D3), a real controlling terminal (`isatty` true, `/dev/tty`
/// openable), and an honest typed `Unsupported` where the ctty helper is absent (D9).
///
/// Live-ConPTY round-trip note (documented skip-gate, per the ADR/task): a ConPTY child's *text* output
/// is only captured through the pseudoconsole when the parent process has **no** inherited interactive
/// console; a console-attached parent (a developer terminal, some test hosts) makes a console-subsystem
/// child attach to that console instead — a well-known ConPTY caveat, not a defect (verified: the spawn,
/// containment, merged-stream shape, and conhost teardown are all correct, and full text capture works
/// from a console-less parent). So the Windows test below asserts the robust, environment-independent
/// contract — the run spawns, produces a single merged stream (no stderr), and exits cleanly — rather
/// than a specific captured string.
///
/// The POSIX spawn tests are Linux-gated (the Stage-2 ctty helper is util-linux `setsid --ctty`, absent
/// on macOS/BSD — an honest `Unsupported` there, asserted deterministically via the internal
/// `ptyCttyHelperAvailableForTests` seam). They run sequentially (NUnit's default within a fixture), so
/// that forced-missing-helper seam never races the real-pty tests.
[<TestFixture>]
type PtyTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    // Collect an async sequence (the streaming event/line verbs) into a list for assertions.
    let collect (items: IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let e = items.GetAsyncEnumerator()
            let mutable more = true

            while more do
                match! e.MoveNextAsync() with
                | true -> acc.Add e.Current
                | false -> more <- false

            do! e.DisposeAsync()
            return acc
        }

    // ----------------------------------------------------------------------------------
    // Config + builders
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``PtyConfig.Default is 80x24 with echo on``() =
        let d = PtyConfig.Default
        Assert.That(d.Cols, Is.EqualTo 80)
        Assert.That(d.Rows, Is.EqualTo 24)
        Assert.That(d.Echo, Is.True)

    [<Test>]
    member _.``Pty builders accept valid geometry without throwing``() =
        // Member overloads and the module function all build a Command without error.
        (Command.create "cmd").Pty() |> ignore
        (Command.create "cmd").Pty(100, 40) |> ignore
        (Command.create "cmd").Pty({ Cols = 120; Rows = 30; Echo = false }) |> ignore
        (Command.create "cmd" |> Command.pty) |> ignore

    [<Test>]
    member _.``Pty rejects a non-positive number of columns``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> (Command.create "cmd").Pty(0, 24) |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty rejects a non-positive number of rows``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> (Command.create "cmd").Pty(80, 0) |> ignore))
        |> ignore

    // ----------------------------------------------------------------------------------
    // Build-time guards (D4: no separate stderr observers; D8: not with Setsid; pipeline guard)
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Pty then StderrTee is rejected (D4)``() =
        use sink = new MemoryStream()

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "cmd" |> Command.pty).StderrTee(sink) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``StderrTee then Pty is rejected (D4, reverse order)``() =
        use sink = new MemoryStream()

        Assert.Throws<ArgumentException>(Action(fun () -> ((Command.create "cmd").StderrTee sink).Pty() |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty then OnStderrLine is rejected (D4)``() =
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                (Command.create "cmd" |> Command.pty).OnStderrLine(Action<string>(ignore))
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``OnStderrLine then Pty is rejected (D4, reverse order)``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> ((Command.create "cmd").OnStderrLine(Action<string>(ignore))).Pty() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Pty then Setsid is rejected (D8)``() =
        Assert.Throws<ArgumentException>(Action(fun () -> (Command.create "cmd" |> Command.pty).Setsid() |> ignore))
        |> ignore

    [<Test>]
    member _.``Setsid then Pty is rejected (D8, reverse order)``() =
        Assert.Throws<ArgumentException>(Action(fun () -> ((Command.create "cmd").Setsid()).Pty() |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty on a non-last pipeline stage is rejected``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "a" |> Command.pty).Pipe(Command.create "b") |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Pty combined with MergeStderr is allowed (redundant, not rejected)``() =
        // A PTY already implies merge semantics, so pairing them is redundant but not a conflict (ADR).
        (Command.create "cmd" |> Command.mergeStderr |> Command.pty) |> ignore
        (Command.create "cmd" |> Command.pty |> Command.mergeStderr) |> ignore

    // ----------------------------------------------------------------------------------
    // Spawn behaviour: POSIX honest-Unsupported; Windows ConPTY merged stream
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Pty on POSIX merges stdout and stderr onto the single terminal stream (D3)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the Stage-2 POSIX ctty helper is util-linux setsid --ctty"
            else
                // A tty is one device, so the child's fd 1 and fd 2 both write the pty slave: OUT and ERR
                // land, in order, on the single captured master stream — and there is no separate stderr.
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf 'OUT-marker\\n'; printf 'ERR-marker\\n' >&2" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "OUT-marker")
                    Assert.That(result.Stdout, Does.Contain "ERR-marker", "stderr is merged into the pty stream (D3)")
                    Assert.That(result.Stderr, Is.Empty, "a PTY produces no separate stderr (D3)")

                    match result.Outcome with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child, got {other}"
        }

    [<Test>]
    member _.``Pty on POSIX gives the child a real controlling terminal (isatty + /dev/tty)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `test -t 0/1/2` proves all three descriptors are ttys (a plain pipe run prints NOTATTY);
                // opening `/dev/tty` succeeds only when the session HAS a controlling terminal — which
                // `setsid --ctty` established on the pty slave. Together they prove the controlling-tty
                // (session) invariant, not merely that a device is attached.
                let script =
                    "if test -t 0 && test -t 1 && test -t 2; then printf ALLTTY; else printf NOTATTY; fi; "
                    + "if : < /dev/tty; then printf =HASCTTY; else printf =NOCTTY; fi"

                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; script ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "ALLTTY", "the child's stdin/stdout/stderr must be a tty")

                    Assert.That(
                        result.Stdout,
                        Does.Contain "HASCTTY",
                        "the pty must be the child's controlling terminal"
                    )
        }

    [<Test>]
    member _.``Pty on POSIX feeds the streaming verbs, emitting only Stdout events (D3)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf 'evt-out\\n'; printf 'evt-err\\n' >&2" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    let! events = collect (running.OutputEventsAsync())

                    // Under a PTY there is no separate stderr stream to tag: every event is a Stdout event.
                    let allStdout =
                        events
                        |> Seq.forall (fun e ->
                            match e with
                            | OutputEvent.Stdout _ -> true
                            | OutputEvent.Stderr _ -> false)

                    Assert.That(allStdout, Is.True, "every event must be a Stdout event under a PTY (D3)")

                    let text = events |> Seq.map (fun e -> e.Text) |> String.concat "\n"
                    Assert.That(text, Does.Contain "evt-out")
                    Assert.That(text, Does.Contain "evt-err", "the merged stderr line arrives as a Stdout event")
        }

    [<Test>]
    member _.``Pty on POSIX applies the configured winsize (Cols/Rows) to the child terminal``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `stty size` reads its controlling terminal's window size (TIOCGWINSZ) and prints
                // "rows cols". A pty opened WITHOUT applying PtyConfig.Cols/Rows carries the kernel's 0x0
                // default and would print "0 0"; a 30x120 result proves the initial geometry is honoured
                // via ioctl(TIOCSWINSZ) — parity with the Windows CreatePseudoConsole path, and no silent
                // cross-platform downgrade of a validated user field.
                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "stty size" ]).Pty(120, 30)
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout.Trim(),
                        Does.Contain "30 120",
                        "the pty must carry the configured 30 rows x 120 cols winsize, not the kernel 0x0 default"
                    )
        }

    [<Test>]
    member _.``Pty on POSIX feeds an interactive stdin to the child terminal and exits cleanly``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // A fed/interactive stdin under a PTY is written to the SINGLE pty master (there is no
                // `dup` of it — a dup would drop the master's O_CLOEXEC and leak a writable master into a
                // concurrent spawn, keeping the child from ever seeing its stdin EOF). `read` returns on
                // the fed newline (it does not need EOF), so the child consumes the line and exits
                // cleanly; a hang would trip the timeout. This exercises the interactive-stdin pty path
                // end-to-end (previously untested).
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "read line; printf 'GOT=%s' \"$line\"" ]
                    |> Command.stdin (Stdin.FromString "pty-stdin-marker\n")
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain "GOT=pty-stdin-marker",
                        "the fed stdin line must reach the pty child"
                    )

                    match result.Outcome with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child that read stdin, got {other}"
        }

    [<Test>]
    member _.``Pty on a host without the ctty helper is a typed Unsupported, never a fake tty (D9)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: forces the missing-ctty-helper path a real macOS/BSD host takes"
            else
                // Force the "no setsid --ctty helper" verdict (the macOS/BSD / old-util-linux case)
                // deterministically on a host that DOES carry setsid, so the honest typed Unsupported is
                // exercised — never a socketpair silently standing in for a tty.
                Native.Posix.ptyCttyHelperAvailableForTests <- Some(fun () -> false)

                try
                    let cmd =
                        Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ] |> Command.pty

                    match! cmd.OutputStringAsync() with
                    | Error(ProcessError.Unsupported msg) ->
                        Assert.That(msg, Does.Contain "Pty")
                        Assert.That(msg, Does.Contain "setsid")
                    | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                    | Ok _ ->
                        Assert.Fail "a PTY without the ctty helper must fail Unsupported, not succeed with a fake tty"
                finally
                    Native.Posix.ptyCttyHelperAvailableForTests <- None
        }

    [<Test>]
    member _.``Pty on Windows spawns a ConPTY child, one merged stream, clean exit``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "echo pty-stage1" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                | Ok result ->
                    // D3: a PTY is one merged terminal stream, so there is never a separate stderr.
                    Assert.That(result.Stderr, Is.Empty, "a PTY must produce no separate stderr (D3)")
                    // The pseudoconsole output stream was captured (conhost emits terminal setup at least),
                    // proving the merged pty stream is wired through the normal capture path.
                    Assert.That(result.Stdout.Length, Is.GreaterThan 0, "the merged pty stream should be captured")
                    // A clean exit proves spawn + Job containment + pseudoconsole teardown all worked and
                    // the capture did not deadlock (the ConPTY output-EOF coordination on child exit).
                    match result.Outcome with
                    | Outcome.Exited _ -> ()
                    | other -> Assert.Fail $"expected a clean exit from the ConPTY child, got {other}"
        }

    // ----------------------------------------------------------------------------------
    // Stage 4 (T-140): RunningProcess.ResizeAsync + PtyConfig.Echo effect
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``ResizeAsync off a PTY validates geometry, reports Unsupported, and never consumes the handle (D6, K-031/K-016)``
        ()
        : Task =
        task {
            // A plain (non-PTY) run — cross-platform. `ResizeAsync` must reject bad geometry up front,
            // return a typed `Unsupported` (never a silent no-op), and — crucially — NOT be a consuming
            // verb: an exit-consuming `WaitAsync` still succeeds afterward (KB K-031), and the shared
            // reap-once exit wait is untouched (KB K-016).
            let baseCmd =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo hi" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ]

            let cmd = baseCmd |> Command.timeout (TimeSpan.FromSeconds 30.0)

            match! runner.StartAsync(cmd, CancellationToken.None) with
            | Error e -> Assert.Fail $"spawn failed: {e}"
            | Ok running ->
                use _running = running

                // Programmer-error geometry is rejected synchronously, matching the Command.Pty builder.
                Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> running.ResizeAsync(0, 24) |> ignore))
                |> ignore

                Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> running.ResizeAsync(80, 0) |> ignore))
                |> ignore

                // Non-PTY run: a typed Unsupported, never a silent/garbled resize (D6).
                match! running.ResizeAsync(120, 40) with
                | Error(ProcessError.Unsupported msg) -> Assert.That(msg, Does.Contain "Resize")
                | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                | Ok() -> Assert.Fail "ResizeAsync on a non-PTY run must not succeed"

                // The consuming verb still runs — proving ResizeAsync claimed no consumption (K-031) and
                // did not touch the reap-once wait path (K-016): no "already consumed by another verb".
                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.Exited _ -> ()
                | other -> Assert.Fail $"expected a clean exit after ResizeAsync, got {other}"
        }

    [<Test>]
    member _.``ResizeAsync resizes the POSIX pty and the child observes the new geometry (D6)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The child blocks on `read _`, THEN prints its terminal size (`stty size` → "rows cols").
                // We `ResizeAsync` to 120x40 BEFORE unblocking `read`, so the size it reports is the
                // POST-resize winsize (ioctl(TIOCSWINSZ) on the master, shared with the slave, + SIGWINCH):
                // a "40 120" line proves the live resize actually reached the child's terminal.
                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read _; stty size" ]).Pty(80, 24)
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use _running = running

                    match! running.ResizeAsync(120, 40) with
                    | Ok() -> ()
                    | Error e -> Assert.Fail $"ResizeAsync on a live pty run failed: {e}"

                    // Unblock `read` so the child proceeds to `stty size` (its output — a few bytes — sits
                    // in the pty buffer until the drain below).
                    match running.TakeStdin() with
                    | Some stdin ->
                        do! stdin.WriteLineAsync ""
                        do! stdin.FlushAsync()
                    | None -> Assert.Fail "expected an interactive stdin on a KeepStdinOpen pty run"

                    let! events = collect (running.OutputEventsAsync())
                    let text = events |> Seq.map (fun e -> e.Text) |> String.concat "\n"

                    Assert.That(
                        text,
                        Does.Contain "40 120",
                        "the child's terminal must report the resized 40 rows x 120 cols, not the initial 24x80"
                    )
        }

    [<Test>]
    member _.``Pty with Echo=false keeps a fed credential out of the captured output (secret-safety)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The secret-invariant round-trip: `read pw` consumes the fed line SILENTLY (the child
                // itself never prints it — it only prints "done"), so the ONLY path by which `secret` could
                // reach the CAPTURED merged output is the terminal's cooked-mode ECHO. Echo=false clears the
                // pty slave's termios ECHO bit at spawn, so the fed credential must NOT appear.
                let secret = "hunter2-SECRET-should-not-echo"

                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read pw; printf 'done\\n'" ])
                        .Pty({ Cols = 80; Rows = 24; Echo = false })
                    |> Command.stdin (Stdin.FromString(secret + "\n"))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "done", "the child must have read the fed credential line")

                    Assert.That(
                        result.Stdout,
                        Does.Not.Contain secret,
                        "Echo=false must keep the fed credential out of the captured merged output (secret-safety)"
                    )
        }

    [<Test>]
    member _.``Pty with the default cooked echo reflects fed input into the captured output``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The deliberate contrast to the Echo=false secret test: with the OS cooked-mode default
                // (echo on), the pty line discipline DOES echo fed input back into the captured output —
                // proving the terminal really echoes by default, so Echo=false's suppression above is a
                // genuine effect and not a coincidental no-op.
                let marker = "echoed-marker-xyz"

                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read line; printf 'done\\n'" ]).Pty(80, 24) // echo on (the ratified default)
                    |> Command.stdin (Stdin.FromString(marker + "\n"))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain marker,
                        "with cooked-mode echo on (the default), fed input is echoed into the captured output"
                    )
        }

    // ----------------------------------------------------------------------------------
    // The default (no PTY) path is unchanged: separate stdout/stderr, exactly as before.
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Without Pty the default path keeps stdout and stderr separate (D1/D2)``() : Task =
        task {
            let script =
                if isWindows then
                    "echo out-marker&echo err-marker 1>&2"
                else
                    "echo out-marker; echo err-marker >&2"

            let cmd =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; script ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; script ]

            match! cmd.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stdout, Does.Contain "out-marker")
                Assert.That(result.Stderr, Does.Contain "err-marker")
            | Error e -> Assert.Fail $"a plain (no-PTY) run should still capture separate streams: {e}"
        }
