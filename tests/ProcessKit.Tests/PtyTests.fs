namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Tests for the opt-in PTY (pseudo-terminal) mode — Stage 1 (T-137): `PtyConfig`, the `Command.Pty` /
/// `Command.pty` builders, the build-time guards (D4/D8 + the pipeline guard), the Windows ConPTY spawn,
/// and the honest typed `Unsupported` on POSIX (the `openpty` mechanism is a later stage).
///
/// Live-ConPTY round-trip note (documented skip-gate, per the ADR/task): a ConPTY child's *text* output
/// is only captured through the pseudoconsole when the parent process has **no** inherited interactive
/// console; a console-attached parent (a developer terminal, some test hosts) makes a console-subsystem
/// child attach to that console instead — a well-known ConPTY caveat, not a defect (verified: the spawn,
/// containment, merged-stream shape, and conhost teardown are all correct, and full text capture works
/// from a console-less parent). So the Windows test below asserts the robust, environment-independent
/// contract — the run spawns, produces a single merged stream (no stderr), and exits cleanly — rather
/// than a specific captured string.
[<TestFixture>]
type PtyTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

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
    member _.``Pty on POSIX returns a typed Unsupported (openpty is a later stage)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: Windows uses ConPTY (covered by the ConPTY test)"
            else
                let cmd =
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ] |> Command.pty

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.That(msg, Does.Contain "Pty")
                | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                | Ok _ -> Assert.Fail "a PTY on POSIX must fail with Unsupported at this stage, not succeed"
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
