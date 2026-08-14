namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// What a given `IProcessRunner` implementation (or mode) can honestly be driven through, for the
/// shared conformance fixture below (`RunnerConformanceFixtureBase`). Every flag names a capability the
/// *seam contract* documents as varying between implementations — never a bug to fix, and never a
/// silent skip: a fixture that reports `false` for a flag explains why in the gated test itself
/// (`Assert.Ignore` with a reason), and this type's own doc comment is the capability matrix the T-369
/// criteria ask for.
///
/// ## Capability matrix
///
/// | Implementation                              | Spawn | StdinFeed | TimeoutEnforcement | DistinctStreams | ScriptedOutcome | OsBackedPty |
/// |----------------------------------------------|:-----:|:---------:|:-------------------:|:----------------:|:----------------:|:-----------:|
/// | `JobRunner` / `ProcessGroup` (real subprocess)|  Y    |    Y      |         Y            |        Y         |        Y         | Windows/Linux only (see below) |
/// | `ScriptedRunner`                              |  Y    |    N      |         N            |        Y         |        Y         |     Y       |
/// | `DryRunRunner`                                |  Y    |    N      |         N            |        N         |        N         |     Y       |
/// | `FaultInjectingRunner` (delegate mode)        |  Y    |    N      |         N            |        Y         |        Y         |     Y       |
/// | `RecordReplayRunner` (`Record` mode)          |  N    |    Y      |         Y            |        Y         |        Y         | Windows/Linux only (see below) |
///
/// `JobRunner` and `ProcessGroup` share one row rather than one fixture each: `ProcessGroup` is itself
/// an `IProcessRunner` whose capture verbs route through the exact same `RunningProcess` primitives as
/// `JobRunner` (see `ProcessGroup.fs`'s `interface IProcessRunner` and its doc comment), and
/// `ApiParityTests.fs` ("ProcessGroup runner captures identically to JobRunner (same normalization)",
/// "ProcessGroup as a runner honours the command timeout") already exercises that identity directly
/// against a real spawn — re-deriving it here through this fixture would duplicate that proof, not add
/// coverage, at the cost of a second live per-test container to create and tear down. `JobRunner` is the
/// representative of the row.
///
/// `RecordReplayRunner`'s `Replay`/`Auto` modes and its cassette-only knobs (`RecordReplayOptions`,
/// argument/`cwd` matching, redaction) are exercised exhaustively in `CassetteTests.fs` /
/// `CassetteRobustnessTests.fs`; this fixture only exercises `Record` mode (wrapping a real `JobRunner`),
/// because that is the one mode whose *seam contract* differs in a way this shared fixture can express
/// uniformly: `SpawnAsync` is a documented `Unsupported` in `Record` mode ("a live stream cannot be
/// captured without racing the consumer" — see `RecordReplayRunner.Spawn`), the one genuinely
/// cassette-only exclusion in this matrix.
///
/// - **Spawn** — a live handle (`StartAsync`/`SpawnAsync`) is obtainable. `false` only for
///   `RecordReplayRunner` in `Record` mode (see above); every other implementation here can produce one.
/// - **StdinFeed** — a `Command.Stdin(source)` feeder is actually delivered to what the runner "runs".
///   `false` for the in-memory doubles (`ScriptedRunner`/`DryRunRunner`, and `FaultInjectingRunner`
///   wrapping one): `FakeProcess` never reads a command's `Stdin` source (see `FakeProcess.BuildCore`'s
///   doc comment — "A fake does not feed `Command.Stdin` sources"), so a fed payload is silently
///   unread rather than round-tripped. Interactive `KeepStdinOpen`/`TakeStdin` writing IS supported by
///   every double (already covered directly against `FakeProcess`/`ScriptedRunner` in
///   `TestabilityTests.fs`), which is a distinct capability from *feeding* a configured source.
/// - **TimeoutEnforcement** — `Command.Timeout` is enforced by watching a real wall-clock deadline.
///   `false` for every double that never spawns a real child: `Seam.complete` (shared by
///   `ScriptedRunner`/`DryRunRunner`) tracks only `CancellationToken`/`Command.CancelOn`, never
///   `Command.Timeout` — a double can only be told to *report* `Outcome.TimedOut` (`Reply.TimedOut`,
///   `FaultInjection.Outcome Outcome.TimedOut`), never made to time out on its own clock.
/// - **DistinctStreams** — the implementation can be driven to produce genuinely different stdout and
///   stderr content, so a merge/PTY fold-into-stdout assertion has something non-trivial to check.
///   `false` only for `DryRunRunner`: its render is one deterministic string computed from
///   program/args/cwd with no stderr concept at all (`DryRunRunner.Render`) — its `ProcessResult.Stderr`
///   is always empty, PTY or not, which the shared PTY/merge tests still assert as the structural case.
/// - **ScriptedOutcome** — the implementation can be driven to conclude with an `Outcome` other than a
///   clean `Exited 0`. `false` only for `DryRunRunner`, whose `resolve` always builds
///   `FakeProcess.OfCommand(command).WithStdout(render)` with no way to script a different outcome.
/// - **OsBackedPty** — a `Command.Pty` run against this implementation is expected to be *architecturally*
///   capable of the merged-stream contract, i.e. not statically excluded by OS family, so the PTY test
///   drives a real `Command.Pty` run rather than skipping up front. It does NOT assert the concrete host
///   has every runtime prerequisite: for the two real-subprocess rows below, the PTY test itself catches
///   a typed `ProcessError.Unsupported` from the run and skips with `Assert.Ignore`, exactly like
///   `PtyTests.fs`'s existing real PTY tests already do for this same prerequisite gap (e.g. its
///   "requires Windows 10 1809" / missing-ctty-helper cases) — a documented fallback, not a silent
///   pass/fail flip. `true` unconditionally for every in-memory double (`ScriptedRunner`/`DryRunRunner`,
///   and `FaultInjectingRunner` wrapping one): `FakeProcess.WithPty()` simulates the merged-stream
///   contract with no host dependency at all — see `ScriptedRunner.fs`/`DryRunRunner.fs`'s
///   `if command.Config.Pty.IsSome then fake.WithPty()`, so no `ProcessError.Unsupported` can occur there
///   and the fallback skip never triggers. For the two real-subprocess rows (`JobRunner`/`ProcessGroup`,
///   and `RecordReplayRunner` in `Record` mode, which delegates straight to a real `JobRunner`) `true`
///   means only the OS family supports a PTY at all: Windows needs ConPTY (Windows 10 1809+ — an older,
///   still-in-support Windows host fails the spawn with `ProcessError.Unsupported`, caught by the
///   fallback above, never a silent downgrade) and POSIX needs the `setsid --ctty` controlling-terminal
///   helper from util-linux in a trusted directory plus `/dev/ptmx` (same fallback if either is missing)
///   — present on Linux, absent entirely on macOS/BSD (no pty devfs at all — see `Command.Pty`'s own doc
///   comment for the full platform matrix). This fixture makes the same static "Windows or Linux"
///   OS-family assumption `PtyTests.fs`'s real
///   POSIX pty tests already make (their "Linux-only" gate) rather than probing a live spawn: `false`
///   on macOS/BSD, `true` on Windows/Linux, and the PTY test is skipped up front with `Assert.Ignore`
///   when it is `false` — a documented matrix exclusion, distinct from (but paired with) the documented,
///   reason-carrying `ProcessError.Unsupported` fallback skip described above for a `true`-but-still-
///   missing-a-prerequisite host; neither path is a silent per-test error-path catch.
type ConformanceCapabilities =
    { SupportsSpawn: bool
      SupportsStdinFeed: bool
      SupportsTimeoutEnforcement: bool
      SupportsDistinctStreams: bool
      SupportsScriptedOutcome: bool
      SupportsOsBackedPty: bool }

/// The shared NUnit conformance fixture: the minimal consumer-visible contract of `IProcessRunner` that
/// every implementation in the capability matrix above must honour where its capabilities say it can.
/// A concrete subclass supplies the runner instance, its capabilities, and a handful of command builders
/// (`EchoCommand`, `ExitCodeCommand`, ...) that let the SAME test bodies drive wildly different backends
/// (a real subprocess, an in-memory fake, a decorator, a record/replay wrapper) through one vocabulary.
///
/// Every `[<Test>]` here is a concrete member (not `abstract`/`override`), so a concrete subclass need
/// only implement the small abstract configuration surface below — NUnit discovers and runs the
/// inherited tests against each subclass exactly as if they were declared there directly.
[<AbstractClass>]
type RunnerConformanceFixtureBase() =

    /// The runner under test for this fixture.
    abstract member Runner: IProcessRunner

    /// What this runner/mode can honestly be driven through — see `ConformanceCapabilities`.
    abstract member Capabilities: ConformanceCapabilities

    /// A command that, run through `Runner`, exits cleanly (an accepted code) with exactly `text` as its
    /// captured stdout. `text` never contains whitespace, quotes, or a newline, so every implementation
    /// (a real shell `echo`, a scripted reply, a dry-run render of `text` as the bare program name) can
    /// reproduce it verbatim.
    abstract member EchoCommand: text: string -> Command

    /// A command that, run through `Runner`, exits with exactly `code` (a non-zero/non-accepted code).
    /// Only called when `Capabilities.SupportsScriptedOutcome` is true.
    abstract member ExitCodeCommand: code: int -> Command

    /// A command that, run through `Runner`, concludes with `Outcome.TimedOut`. Only called when
    /// `Capabilities.SupportsTimeoutEnforcement` or `Capabilities.SupportsScriptedOutcome` is true.
    abstract member TimedOutCommand: unit -> Command

    /// A command that, run through `Runner`, exits cleanly with `stdoutText` on stdout and `stderrText`
    /// on stderr as two genuinely distinct streams. Only called when `Capabilities.SupportsDistinctStreams`
    /// is true.
    abstract member DistinctStreamsCommand: stdoutText: string * stderrText: string -> Command

    /// A command that, run through `Runner` with a `Command.Stdin` source carrying `text`, exits cleanly
    /// with stdout containing `text` (a stdin round-trip). Only called when `Capabilities.SupportsStdinFeed`
    /// is true.
    abstract member StdinRoundTripCommand: text: string -> Command

    // --- text/bytes capture, outcome/duration, command metadata --------------------------------------

    [<Test>]
    member this.``captures stdout as text with an accepted exit and the command's Program metadata``() : Task =
        task {
            let command = this.EchoCommand "conformance-echo-marker"

            match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "conformance-echo-marker")
                Assert.That(result.IsSuccess, Is.True)
                Assert.That(result.Code, Is.EqualTo(Some 0))
                Assert.That(result.Program, Is.EqualTo command.Program, "ProcessResult.Program must name the run")
                Assert.That(result.Duration, Is.GreaterThanOrEqualTo TimeSpan.Zero)
            | Error error -> Assert.Fail $"expected Ok, got {error.Message}"
        }

    [<Test>]
    member this.``OutputBytesAsync captures the same content as the text verb``() : Task =
        task {
            let command = this.EchoCommand "conformance-bytes-marker"

            match! this.Runner.OutputBytesAsync(command, CancellationToken.None) with
            | Ok result ->
                let decoded = System.Text.Encoding.UTF8.GetString result.Stdout
                Assert.That(decoded, Does.Contain "conformance-bytes-marker")
                Assert.That(result.Program, Is.EqualTo command.Program)
            | Error error -> Assert.Fail $"expected Ok, got {error.Message}"
        }

    [<Test>]
    member this.``a non-zero/non-accepted exit is reported as data (Outcome.Exited), not as an error``() : Task =
        task {
            if not this.Capabilities.SupportsScriptedOutcome then
                Assert.Ignore
                    "this runner cannot be driven to a non-zero exit (capability matrix: SupportsScriptedOutcome=false)"
            else
                let command = this.ExitCodeCommand 3

                match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
                | Ok result ->
                    Assert.That(result.Code, Is.EqualTo(Some 3))
                    Assert.That(result.IsSuccess, Is.False)

                    match result.Outcome with
                    | Outcome.Exited 3 -> ()
                    | other -> Assert.Fail $"expected Outcome.Exited 3, got {other}"
                | Error error -> Assert.Fail $"expected a completed (non-success) result, got {error.Message}"
        }

    [<Test>]
    member this.``Outcome.TimedOut is reported as data, whether wall-clock-enforced or scripted``() : Task =
        task {
            if
                not (
                    this.Capabilities.SupportsTimeoutEnforcement
                    || this.Capabilities.SupportsScriptedOutcome
                )
            then
                Assert.Ignore
                    "neither a real Command.Timeout deadline nor a scripted TimedOut outcome is available here (capability matrix)"
            else
                let command = this.TimedOutCommand()

                match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.IsTimedOut, Is.True)
                | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    // --- truncation ------------------------------------------------------------------------------------

    [<Test>]
    member this.``a bounded OutputBufferPolicy sets Truncated when output exceeds the cap``() : Task =
        task {
            let command =
                this.EchoCommand "conformance-overflow-marker"
                |> Command.outputBuffer (
                    OutputBufferPolicy.Unbounded.WithMaxBytes(1).WithOverflow OverflowMode.DropOldest
                )

            match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(
                    result.Truncated,
                    Is.True,
                    "output far exceeding a 1-byte cap must be reported as truncated"
                )
            | Error error -> Assert.Fail $"expected a completed (truncated) result, got {error.Message}"
        }

    [<Test>]
    member this.``a fail-loud OutputBufferPolicy reports OutputTooLarge instead of silently truncating``() : Task =
        task {
            let command =
                this.EchoCommand "conformance-overflow-marker"
                |> Command.outputBuffer (OutputBufferPolicy.FailLoud 0)

            match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
            | Error(ProcessError.OutputTooLarge _) -> ()
            | Error other -> Assert.Fail $"expected OutputTooLarge, got {other.Message}"
            | Ok result -> Assert.Fail $"expected OutputTooLarge, got a completed result: {result.Stdout}"
        }

    // --- spawn / streaming -------------------------------------------------------------------------------

    [<Test>]
    member this.``StartAsync returns a live handle that streams the same text a capture verb would``() : Task =
        task {
            if not this.Capabilities.SupportsSpawn then
                Assert.Ignore
                    "this runner cannot produce a live handle in this mode (capability matrix: SupportsSpawn=false — RecordReplayRunner in Record mode cannot capture a live stream without racing the consumer; record the call through a capture verb, then replay it as a stream)"
            else
                let command = this.EchoCommand "conformance-spawn-marker"

                match! this.Runner.StartAsync(command, CancellationToken.None) with
                | Error error -> Assert.Fail $"expected a live handle, got {error.Message}"
                | Ok proc ->
                    use proc = proc

                    match! proc.OutputStringAsync() with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "conformance-spawn-marker")
                    | Error error -> Assert.Fail $"{error.Message}"
        }

    // --- stdin ------------------------------------------------------------------------------------------

    [<Test>]
    member this.``Command.Stdin content is fed to the child and round-trips through stdout``() : Task =
        task {
            if not this.Capabilities.SupportsStdinFeed then
                Assert.Ignore
                    "this runner never feeds a Command.Stdin source into what it \"runs\" (capability matrix: SupportsStdinFeed=false — a test double has no real child to read one; see FakeProcess.BuildCore's doc comment)"
            else
                let payload = "conformance-stdin-payload"
                let command = this.StdinRoundTripCommand payload

                match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Does.Contain payload)
                | Error error -> Assert.Fail $"{error.Message}"
        }

    // --- cancellation / timeout ---------------------------------------------------------------------------

    [<Test>]
    member this.``an already-cancelled token reports Cancelled without completing the run``() : Task =
        task {
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let command = this.EchoCommand "conformance-should-not-run"

            match! this.Runner.OutputStringAsync(command, cts.Token) with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }

    [<Test>]
    member this.``an already-cancelled Command.CancelOn cancels a completion verb``() : Task =
        task {
            use cancelOnSource = new CancellationTokenSource()
            cancelOnSource.Cancel()

            let command =
                this.EchoCommand "conformance-cancel-on-marker"
                |> Command.cancelOn cancelOnSource.Token

            match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled via Command.CancelOn, got {other}"
        }

    // --- PTY / merge --------------------------------------------------------------------------------------

    [<Test>]
    member this.``Command.MergeStderr folds stderr into the single observed stdout stream``() : Task =
        task {
            let command =
                if this.Capabilities.SupportsDistinctStreams then
                    this.DistinctStreamsCommand("merge-out-marker", "merge-err-marker")
                else
                    this.EchoCommand "merge-out-marker"
                |> Command.mergeStderr

            match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Stderr, Is.Empty, "MergeStderr leaves no separate stderr channel")
                Assert.That(result.Stdout, Does.Contain "merge-out-marker")

                if this.Capabilities.SupportsDistinctStreams then
                    Assert.That(
                        result.Stdout,
                        Does.Contain "merge-err-marker",
                        "stderr must fold into the merged stdout"
                    )
            | Error error -> Assert.Fail $"{error.Message}"
        }

    [<Test>]
    member this.``Command.Pty runs as a single merged stream with no separate stderr``() : Task =
        task {
            if not this.Capabilities.SupportsOsBackedPty then
                Assert.Ignore
                    "this host/implementation has no OS-backed Command.Pty support (capability matrix: SupportsOsBackedPty=false — see ConformanceCapabilities's doc comment, OsBackedPty row, for the platform requirement)"
            else
                let command =
                    if this.Capabilities.SupportsDistinctStreams then
                        this.DistinctStreamsCommand("pty-out-marker", "pty-err-marker")
                    else
                        this.EchoCommand "pty-out-marker"
                    |> Command.pty

                match! this.Runner.OutputStringAsync(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported message) ->
                    // SupportsOsBackedPty=true is an OS-family assumption (Windows or Linux — see
                    // `ConformanceCapabilities`'s `OsBackedPty` row), not a probed host fact: a real host
                    // can still lack the concrete prerequisite (ConPTY needs Windows 10 1809+; POSIX
                    // needs the trusted `setsid --ctty` helper plus `/dev/ptmx` — see `Command.Pty`'s own
                    // doc comment). `ProcessError.Unsupported` is exactly the typed, honest failure
                    // `Command.Pty` promises for that gap, never a silent downgrade — skip here rather
                    // than fail, mirroring the same prerequisite-gap handling `PtyTests.fs`'s real PTY
                    // tests already do (e.g. its "requires Windows 10 1809" / missing-ctty-helper cases).
                    Assert.Ignore $"host lacks a Command.Pty prerequisite despite OS-family support: {message}"
                | Error error -> Assert.Fail $"unexpected error under Command.Pty: {error.Message}"
                | Ok result ->
                    Assert.That(result.Stderr, Is.Empty, "a PTY run must have no separate stderr channel (D3)")
                    Assert.That(result.Stdout, Does.Contain "pty-out-marker")

                    if this.Capabilities.SupportsDistinctStreams then
                        Assert.That(
                            result.Stdout,
                            Does.Contain "pty-err-marker",
                            "stderr must fold into the merged pty stream"
                        )
        }

/// Shared command-building over a real subprocess shell, reused by every real-runner-backed conformance
/// fixture (`JobRunnerConformanceTests`, `RecordReplayRunnerConformanceTests`). Mirrors the `shell`
/// helper already established by `JobRunnerTests`/`CorrectnessBugTests`.
[<AbstractClass>]
type RealRunnerConformanceFixtureBase() =
    inherit RunnerConformanceFixtureBase()

    // Static, shared across every subclass: the "Windows or Linux" platform assumption documented on
    // `ConformanceCapabilities`'s `OsBackedPty` row — the same one `PtyTests.fs`'s real POSIX pty tests
    // already make (their "Linux-only" gate). macOS/BSD has no pty devfs at all (`Command.Pty`'s own
    // doc comment), so it is the one platform excluded here.
    static let osBackedPtySupported =
        RuntimeInformation.IsOSPlatform OSPlatform.Windows
        || RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    override _.Capabilities =
        { SupportsSpawn = true
          SupportsStdinFeed = true
          SupportsTimeoutEnforcement = true
          SupportsDistinctStreams = true
          SupportsScriptedOutcome = true
          SupportsOsBackedPty = osBackedPtySupported }

    override _.EchoCommand(text) = shell $"echo {text}"

    override _.ExitCodeCommand(code) = shell $"exit {code}"

    override _.TimedOutCommand() =
        let sleeper =
            if isWindows then
                shell "ping 127.0.0.1 -n 30"
            else
                shell "sleep 30"

        sleeper |> Command.timeout (TimeSpan.FromMilliseconds 300.0)

    override _.DistinctStreamsCommand(stdoutText, stderrText) =
        if isWindows then
            shell $"echo {stdoutText}& echo {stderrText} 1>&2"
        else
            shell $"printf '{stdoutText}\\n'; printf '{stderrText}\\n' >&2"

    override _.StdinRoundTripCommand(text) =
        (shell (if isWindows then "more" else "cat"))
        |> Command.stdin (Stdin.FromString text)

/// Real subprocess conformance: `JobRunner`, the default `IProcessRunner`. Represents the
/// "JobRunner/ProcessGroup" row of the capability matrix — see `ConformanceCapabilities`'s doc comment
/// for why `ProcessGroup` is not separately exercised here.
[<TestFixture>]
type JobRunnerConformanceTests() =
    inherit RealRunnerConformanceFixtureBase()

    let runner: IProcessRunner = JobRunner()

    override _.Runner = runner

/// `RecordReplayRunner.Record` conformance: wraps a real `JobRunner` and delegates every call to it, so
/// this fixture proves Record mode is a faithful pass-through of the same real-runner contract —
/// EXCEPT `SpawnAsync`, the one cassette-only exclusion in this matrix (see
/// `ConformanceCapabilities`'s doc comment). The cassette is never saved (no `Save()`/`Dispose()` call),
/// so this fixture never touches disk.
[<TestFixture>]
type RecordReplayRunnerConformanceTests() =
    inherit RealRunnerConformanceFixtureBase()

    let recorder =
        RecordReplayRunner.Record(
            Path.Combine(Path.GetTempPath(), $"pk-conformance-record-{Guid.NewGuid():N}.json"),
            JobRunner()
        )

    override _.Runner = recorder :> IProcessRunner

    override _.Capabilities =
        { base.Capabilities with
            SupportsSpawn = false }

/// `ScriptedRunner` conformance: a subprocess-free double. Each command-builder method (re)scripts the
/// runner just before returning the command it built the script for, so every test drives an
/// independently-scripted `ScriptedRunner` instance without any test depending on another's script.
[<TestFixture>]
type ScriptedRunnerConformanceTests() =
    inherit RunnerConformanceFixtureBase()

    let mutable current: ScriptedRunner = ScriptedRunner()

    override _.Runner = current :> IProcessRunner

    override _.Capabilities =
        { SupportsSpawn = true
          SupportsStdinFeed = false
          SupportsTimeoutEnforcement = false
          SupportsDistinctStreams = true
          SupportsScriptedOutcome = true
          SupportsOsBackedPty = true }

    override _.EchoCommand(text) =
        let program = "conformance-echo"
        current <- ScriptedRunner().On([ program ], Reply.Ok text)
        Command.create program

    override _.ExitCodeCommand(code) =
        let program = "conformance-exit"
        current <- ScriptedRunner().On([ program ], Reply.Exit code)
        Command.create program

    override _.TimedOutCommand() =
        let program = "conformance-timeout"
        current <- ScriptedRunner().On([ program ], Reply.TimedOut)
        Command.create program

    override _.DistinctStreamsCommand(stdoutText, stderrText) =
        let program = "conformance-distinct-streams"
        current <- ScriptedRunner().On([ program ], (Reply.Ok stdoutText).WithStderr stderrText)
        Command.create program

    override _.StdinRoundTripCommand(_text) =
        raise (
            NotSupportedException
                "ScriptedRunner never feeds a Command.Stdin source (see FakeProcess.BuildCore); guarded by Capabilities.SupportsStdinFeed = false"
        )

/// `DryRunRunner` conformance: a deterministic, subprocess-free render — the strictest double in the
/// matrix (no stderr, no scriptable exit/outcome). `EchoCommand`/the truncation tests use the program
/// name alone (no args, no cwd) as the marker text, since `DryRunRunner.Render` of such a command is
/// exactly that program name.
[<TestFixture>]
type DryRunRunnerConformanceTests() =
    inherit RunnerConformanceFixtureBase()

    let runner = DryRunRunner()

    override _.Runner = runner :> IProcessRunner

    override _.Capabilities =
        { SupportsSpawn = true
          SupportsStdinFeed = false
          SupportsTimeoutEnforcement = false
          SupportsDistinctStreams = false
          SupportsScriptedOutcome = false
          SupportsOsBackedPty = true }

    override _.EchoCommand(text) = Command.create text

    override _.ExitCodeCommand(_code) =
        raise (
            NotSupportedException
                "DryRunRunner always synthesizes a clean Exited 0 preview; guarded by Capabilities.SupportsScriptedOutcome = false"
        )

    override _.TimedOutCommand() =
        raise (
            NotSupportedException
                "DryRunRunner never times out and cannot be scripted to; guarded by Capabilities.SupportsTimeoutEnforcement/SupportsScriptedOutcome = false"
        )

    override _.DistinctStreamsCommand(_stdoutText, _stderrText) =
        raise (
            NotSupportedException
                "DryRunRunner's render has no separate stderr concept; guarded by Capabilities.SupportsDistinctStreams = false"
        )

    override _.StdinRoundTripCommand(_text) =
        raise (
            NotSupportedException
                "DryRunRunner never reads a Command.Stdin source; guarded by Capabilities.SupportsStdinFeed = false"
        )

/// `FaultInjectingRunner` conformance, in "always delegate" mode (an empty injection sequence, so
/// `nextInjection()` never selects one and every call forwards to the wrapped `ScriptedRunner`) — the
/// decorator's own contract is "forward unless a scripted injection intercepts", so a fully-delegating
/// instance must be a faithful pass-through of its inner runner's own capabilities. Mirrors
/// `ScriptedRunnerConformanceTests`'s per-call (re)scripting, wrapped fresh alongside its inner runner.
[<TestFixture>]
type FaultInjectingRunnerConformanceTests() =
    inherit RunnerConformanceFixtureBase()

    let mutable current: FaultInjectingRunner =
        FaultInjectingRunner(ScriptedRunner() :> IProcessRunner, ([]: FaultInjection list))

    let delegateTo (scripted: ScriptedRunner) =
        current <- FaultInjectingRunner(scripted :> IProcessRunner, ([]: FaultInjection list))

    override _.Runner = current :> IProcessRunner

    override _.Capabilities =
        { SupportsSpawn = true
          SupportsStdinFeed = false
          SupportsTimeoutEnforcement = false
          SupportsDistinctStreams = true
          SupportsScriptedOutcome = true
          SupportsOsBackedPty = true }

    override _.EchoCommand(text) =
        let program = "fi-conformance-echo"
        delegateTo (ScriptedRunner().On([ program ], Reply.Ok text))
        Command.create program

    override _.ExitCodeCommand(code) =
        let program = "fi-conformance-exit"
        delegateTo (ScriptedRunner().On([ program ], Reply.Exit code))
        Command.create program

    override _.TimedOutCommand() =
        let program = "fi-conformance-timeout"
        delegateTo (ScriptedRunner().On([ program ], Reply.TimedOut))
        Command.create program

    override _.DistinctStreamsCommand(stdoutText, stderrText) =
        let program = "fi-conformance-distinct-streams"
        delegateTo (ScriptedRunner().On([ program ], (Reply.Ok stdoutText).WithStderr stderrText))
        Command.create program

    override _.StdinRoundTripCommand(_text) =
        raise (
            NotSupportedException
                "the wrapped ScriptedRunner never feeds a Command.Stdin source; guarded by Capabilities.SupportsStdinFeed = false"
        )
