namespace ProcessKit.Testing

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open ProcessKit

/// Records the geometry of every `ResizeAsync` call made against a PTY-mode `FakeProcess`'s built
/// handle, so a test can assert the last requested `(cols, rows)` (D10). A reference type shared **by
/// reference** across the fluent `FakeProcess` chain and captured into the built `RunningProcess`'s
/// resize callback, so a resize on the handle is visible through the `FakeProcess.LastResize` accessor
/// on the same fake instance. Thread-safe: the resize callback (invoked from `ResizeAsync`) and the
/// accessor can run on different threads.
[<Sealed>]
type internal PtyResizeRecorder() =
    let gate = obj ()
    let mutable last: (int * int) option = None

    /// Record a resize request's geometry (already range-validated by `RunningProcess.ResizeAsync`).
    member _.Record(cols: int, rows: int) =
        lock gate (fun () -> last <- Some(cols, rows))

    /// The last `(cols, rows)` requested, or `None` if no resize has been requested yet.
    member _.Last = lock gate (fun () -> last)

/// Builds an in-memory `RunningProcess` for unit-testing code that consumes a live handle —
/// `StdoutLinesAsync` / `OutputEventsAsync` / `FinishAsync` / the readiness probes / the buffered verbs — without
/// spawning a real process. Immutable and fluent; `Build()` returns a real `RunningProcess` whose
/// stdout/stderr are `MemoryStream`s of the scripted text, whose wait resolves to the scripted
/// outcome, and whose kill/teardown are no-ops.
///
/// Call `WithPty()` to model a pseudo-terminal (`Command.Pty`) run: the built handle then exposes a
/// **single merged stream** (`OutputEvent.Stderr` is never produced) and `ResizeAsync` is a recorded
/// no-op success — see that method for the merged-stream and `isatty` caveats.
[<Sealed>]
type FakeProcess
    private
    (template: Command, stdout: string, stderr: string, outcome: Outcome, pid: int option, pty: PtyResizeRecorder option)
    =

    /// A fake of `program` that exits 0 with no output.
    static member Create(program: string) =
        ArgumentNullException.ThrowIfNull(program, nameof program)
        FakeProcess(Command.create program, "", "", Outcome.Exited 0, None, None)

    /// A fake (named `"fake"`) that exits 0 with no output.
    static member Create() = FakeProcess.Create "fake"

    /// A fake whose built `RunningProcess` inherits `command`'s config — encodings, `OkCodes`, output
    /// buffer, line handlers — so it behaves like a real run of that command. Internal: `ScriptedRunner`
    /// uses it so `SpawnAsync` and the capture verbs agree on success/encoding semantics.
    static member internal OfCommand(command: Command) =
        FakeProcess(command, "", "", Outcome.Exited 0, None, None)

    /// The captured stdout the fake replays (split on `\n` into lines for the streaming verbs).
    member _.WithStdout(text: string) =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        FakeProcess(template, text, stderr, outcome, pid, pty)

    /// The captured stdout as a sequence of lines (joined with `\n`).
    member _.WithStdoutLines(lines: seq<string>) =
        ArgumentNullException.ThrowIfNull(lines, nameof lines)
        FakeProcess(template, String.Join('\n', lines), stderr, outcome, pid, pty)

    /// The captured stderr. On a PTY fake (see `WithPty`) there is no separate stderr stream: this text
    /// is folded into the single merged stdout stream rather than surfaced as `OutputEvent.Stderr`.
    member _.WithStderr(text: string) =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        FakeProcess(template, stdout, text, outcome, pid, pty)

    /// Make the fake exit with `code`.
    member _.WithExit(code: int) =
        FakeProcess(template, stdout, stderr, Outcome.Exited code, pid, pty)

    /// Make the fake conclude with an explicit `Outcome` (e.g. `Outcome.TimedOut` or `Signalled`).
    member _.WithOutcome(value: Outcome) =
        ArgumentNullException.ThrowIfNull(value, nameof value)
        FakeProcess(template, stdout, stderr, value, pid, pty)

    /// Set the pid the handle reports.
    member _.WithPid(value: int) =
        FakeProcess(template, stdout, stderr, outcome, Some value, pty)

    /// Model a pseudo-terminal (`Command.Pty`) run, so the built handle mirrors the observable
    /// merged-stream contract (ADR D3/D10):
    ///
    /// - **One merged stream.** A real PTY gives the child a single terminal device, so stdout and
    ///   stderr are physically one stream. The fake therefore exposes **no separate stderr**:
    ///   `OutputEventsAsync()` yields only `OutputEvent.Stdout` — `OutputEvent.Stderr` is never
    ///   produced — and `ProcessResult.Stderr` is empty. Any text set via `WithStderr` is folded into
    ///   the merged stdout stream (a fake cannot reproduce real OS interleaving, so folded stderr simply
    ///   follows the stdout text) rather than being dropped or surfaced as a separate stderr event.
    /// - **`ResizeAsync` is a recorded no-op success.** On the built handle `ResizeAsync(cols, rows)`
    ///   returns `Ok ()` (not the typed `Unsupported` a non-PTY fake returns) and stores the geometry —
    ///   read the last requested `(cols, rows)` back through `LastResize` for assertions.
    ///
    /// **Inherent limitation (not papered over).** A double has no real terminal, so it **cannot** make
    /// the child observe `isatty = true`: any behaviour that depends on the *child* seeing a tty (a tool
    /// switching from line-buffered "dumb" output to full-screen TUI mode, a shell enabling colour) is
    /// not reproducible here — only the *observable merged-stream shape* is. Test that child-tty
    /// behaviour against a real `Command.Pty` run. See `docs/testing.md`.
    member _.WithPty() =
        FakeProcess(template, stdout, stderr, outcome, pid, Some(PtyResizeRecorder()))

    /// The last `(cols, rows)` requested via `ResizeAsync` on this fake's built PTY handle, or `None`
    /// if this is not a PTY fake (see `WithPty`) or no resize has been requested. Shared across the
    /// fluent chain, so read it from the same instance `Build()` was called on.
    member _.LastResize: (int * int) option =
        match pty with
        | Some recorder -> recorder.Last
        | None -> None

    /// Build a real `RunningProcess` over in-memory streams.
    member _.Build() : RunningProcess =
        let config = template.Config
        let isPty = pty.IsSome

        // Under a PTY there is ONE physical terminal stream (D3): the child's stdout and stderr are the
        // same device. A PTY fake therefore folds any scripted stderr into the single merged stdout
        // stream (rather than surfacing a separate `OutputEvent.Stderr`), joining on a newline so the
        // folded text forms its own line. A fake cannot reproduce real OS interleaving, so folded stderr
        // simply follows the stdout text.
        let stdoutText =
            if isPty && stderr.Length > 0 then
                match stdout with
                | "" -> stderr
                | s when s.EndsWith '\n' -> s + stderr
                | s -> s + "\n" + stderr
            else
                stdout

        let stdoutStream =
            new MemoryStream(config.StdoutEncoding.GetBytes stdoutText) :> Stream

        // A PTY has no separate stderr channel (D3), exactly like a real `Command.Pty` spawn whose
        // `Spawned.Stderr` is `None`; a non-PTY fake keeps its own stderr stream.
        let stderrStream =
            if isPty then
                None
            else
                Some(new MemoryStream(config.StderrEncoding.GetBytes stderr) :> Stream)

        let host: RunningHost =
            { Config = config
              Pid = pid
              Stdout = Some stdoutStream
              Stderr = stderrStream
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              // No real process backs a fake's pid (arbitrary via `WithPid`, or none), so there is no
              // genuine identity to capture — `None` defers `processMetrics`'s gate (T-097) to the raw
              // read, leaving this fake's existing behaviour unchanged.
              StartTimeIdentity = None
              Wait = fun () -> Task.FromResult outcome
              // A fake process feeds no stdin, so it never has a source failure to surface, and there is
              // no source feeder for `TakeStdin` to wait on.
              StdinError = fun () -> None
              StdinFeedComplete = ignore
              StartKill = fun () -> ()
              GracefulKill = fun _ -> Task.CompletedTask
              // A PTY fake models `ResizeAsync` as a RECORDED no-op success (D10): it has no real pty, so
              // there is nothing to resize, but the verb succeeds (`Ok ()`, not the typed `Unsupported`)
              // and the geometry — already range-validated by `RunningProcess.ResizeAsync` — is stored
              // for assertions via `LastResize`. A non-PTY fake has no pseudo-terminal, so `ResizePty` is
              // `None` and `ResizeAsync` reports a typed `Unsupported` (D6).
              ResizePty =
                match pty with
                | Some recorder ->
                    Some(fun (cols, rows) ->
                        recorder.Record(cols, rows)
                        Ok())
                | None -> None
              Teardown =
                fun () ->
                    stdoutStream.Dispose()
                    stderrStream |> Option.iter (fun s -> s.Dispose())
                    ValueTask.CompletedTask }

        new RunningProcess(host)
