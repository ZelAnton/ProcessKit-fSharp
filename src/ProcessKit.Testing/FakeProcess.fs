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

/// A `MemoryStream` handed out to exactly one built `RunningProcess` handle. Disposing it (via
/// `ProcessStdin.FinishAsync` or process teardown) tears down only this stream — it never touches any
/// other stream the owning `FakeStdinRecorder` has handed out to a sibling handle. Every write is also
/// mirrored to `onWrite` so the recorder's aggregate log survives this stream's disposal.
[<Sealed>]
type private RecordingStream(onWrite: byte[] -> int -> int -> unit) =
    inherit MemoryStream()

    override _.Write(buffer: byte[], offset: int, count: int) =
        base.Write(buffer, offset, count)
        onWrite buffer offset count

    override _.Write(buffer: ReadOnlySpan<byte>) =
        base.Write buffer
        // `Stream.WriteAsync(ReadOnlyMemory<byte>, _)` routes here rather than through the byte[]
        // overload above; a copy is the price of mirroring that path into the byte[]-shaped callback.
        let copy = buffer.ToArray()
        onWrite copy 0 copy.Length

/// Records the bytes written to a `FakeProcess`'s interactive stdin across every handle built from it
/// (`Build()` on a `KeepStdinOpen` fake may be called more than once — see `FakeProcess.Build`). Each
/// call to `NewStream` hands out its own independent `MemoryStream`, so tearing down one built handle's
/// stdin (`ProcessStdin.FinishAsync`, process teardown) never disturbs another handle built from the same
/// fake. Every byte written through any of those streams is also appended to this recorder's own
/// aggregate log, so `Bytes` stays a readable snapshot of the fake's *total* stdin activity — across all
/// built handles, in write order — even after any one handle's stream has since been disposed.
[<Sealed>]
type internal FakeStdinRecorder() =
    let gate = obj ()
    let aggregate = new MemoryStream()

    let append (buffer: byte[]) (offset: int) (count: int) =
        lock gate (fun () -> aggregate.Write(buffer, offset, count))

    /// A new, independent writable stdin stream for one `Build()`'s handle.
    member _.NewStream() : Stream = new RecordingStream(append) :> Stream

    /// A snapshot of all bytes written through every stream this recorder has handed out, in write
    /// order, including after the writing stream(s) have been disposed.
    member _.Bytes = lock gate (fun () -> aggregate.ToArray())

[<Sealed>]
type internal FakeSignalRecorder() =
    let gate = obj ()
    let signals = ResizeArray<Signal>()

    member _.Record(signal: Signal) =
        lock gate (fun () -> signals.Add signal)

    member _.Snapshot =
        lock gate (fun () -> signals.ToArray() :> Collections.Generic.IReadOnlyList<Signal>)

/// Builds an in-memory `RunningProcess` for unit-testing code that consumes a live handle —
/// `StdoutLinesAsync` / `OutputEventsAsync` / `FinishAsync` / the readiness probes / the buffered verbs — without
/// spawning a real process. Immutable and fluent; `Build()` returns a real `RunningProcess` whose
/// observable stdout/stderr are `MemoryStream`s of scripted text when the command pipes that stream;
/// file redirects and `Null`/`Inherit` destinations expose no parent-side stream, just like a real
/// spawn. Its wait resolves to the scripted outcome, and kill/teardown are no-ops. `Build()` may be
/// called more than once on the same instance — the type stays immutable (`With*` never mutates the
/// receiver), and each built handle gets its own independent stdout/stderr/stdin streams (see `Build`),
/// while `StdinBytes`/`Signals` report the combined activity of every handle built so far.
///
/// Call `WithPty()` to model a pseudo-terminal (`Command.Pty`) run: the built handle then exposes a
/// **single merged stream** (`OutputEvent.Stderr` is never produced) and `ResizeAsync` is a recorded
/// no-op success — see that method for the merged-stream and `isatty` caveats. A fake built from a
/// `Command.MergeStderr()` configuration likewise exposes a single merged stream, without PTY resizing.
[<Sealed>]
type FakeProcess
    private
    (
        template: Command,
        stdout: string,
        stdoutBytes: byte[] option,
        stderr: string,
        outcome: Outcome,
        pid: int option,
        pty: PtyResizeRecorder option,
        stdin: FakeStdinRecorder,
        signals: FakeSignalRecorder
    ) =

    /// A fake of `program` that exits 0 with no output.
    static member Create(program: string) =
        ArgumentNullException.ThrowIfNull(program, nameof program)

        FakeProcess(
            Command.create program,
            "",
            None,
            "",
            Outcome.Exited 0,
            None,
            None,
            FakeStdinRecorder(),
            FakeSignalRecorder()
        )

    /// A fake (named `"fake"`) that exits 0 with no output.
    static member Create() = FakeProcess.Create "fake"

    /// A fake whose built `RunningProcess` inherits `command`'s config — encodings, `OkCodes`, output
    /// buffer, line handlers — so it behaves like a real run of that command. Internal: `ScriptedRunner`
    /// uses it so `SpawnAsync` and the capture verbs agree on success/encoding semantics.
    static member internal OfCommand(command: Command) =
        FakeProcess(command, "", None, "", Outcome.Exited 0, None, None, FakeStdinRecorder(), FakeSignalRecorder())

    /// The captured stdout the fake replays (split on `\n` into lines for the streaming verbs).
    member _.WithStdout(text: string) =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        FakeProcess(template, text, None, stderr, outcome, pid, pty, stdin, signals)

    /// The captured stdout as a sequence of lines (joined with `\n`).
    member _.WithStdoutLines(lines: seq<string>) =
        ArgumentNullException.ThrowIfNull(lines, nameof lines)
        FakeProcess(template, String.Join('\n', lines), None, stderr, outcome, pid, pty, stdin, signals)

    /// Script byte-exact Content-Length frames on stdout. The fake writes canonical CRLF headers and
    /// preserves each payload verbatim, including non-UTF-8 bytes, for `ContentLengthSession` tests.
    member _.WithContentLengthFrames(payloads: seq<byte[] | null>) =
        ArgumentNullException.ThrowIfNull(payloads, nameof payloads)
        use framed = new MemoryStream()

        for payload in payloads do
            match payload with
            | null -> raise (ArgumentException("payloads must not contain null", nameof payloads))
            | payload ->
                let header =
                    Text.Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n")

                framed.Write(header, 0, header.Length)
                framed.Write(payload, 0, payload.Length)

        FakeProcess(template, "", Some(framed.ToArray()), stderr, outcome, pid, pty, stdin, signals)

    /// The captured stderr. On a PTY fake (see `WithPty`) or a `Command.MergeStderr()` fake there is no
    /// separate stderr stream: this text is folded into the single merged stdout stream rather than
    /// surfaced as `OutputEvent.Stderr`.
    member _.WithStderr(text: string) =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        FakeProcess(template, stdout, stdoutBytes, text, outcome, pid, pty, stdin, signals)

    /// Make the fake exit with `code`.
    member _.WithExit(code: int) =
        FakeProcess(template, stdout, stdoutBytes, stderr, Outcome.Exited code, pid, pty, stdin, signals)

    /// Make the fake conclude with an explicit `Outcome` (e.g. `Outcome.TimedOut` or `Signalled`).
    member _.WithOutcome(value: Outcome) =
        ArgumentNullException.ThrowIfNull(value, nameof value)
        FakeProcess(template, stdout, stdoutBytes, stderr, value, pid, pty, stdin, signals)

    /// Set the pid the handle reports.
    member _.WithPid(value: int) =
        FakeProcess(template, stdout, stdoutBytes, stderr, outcome, Some value, pty, stdin, signals)

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
    /// **Expect-style sessions work against it.** A `PtySession` over the built handle reads the same
    /// merged stream raw, so `ExpectAsync` finds a scripted prompt that carries no line terminator
    /// (`"Password: "`) exactly as it would against a real PTY, and `SendAsync`/`SendLineAsync` record
    /// their bytes into `StdinBytes` (pair this with `WithStdinOpen()`, or a `Command.KeepStdinOpen`
    /// command through `ScriptedRunner`). Within the merged-stream limits above: the scripted text is
    /// replayed from a buffer that is complete before the first `ExpectAsync` runs, so a fake cannot
    /// model a child that *reacts* to what was sent — script the whole conversation's output up front
    /// and expect its parts in order.
    ///
    /// **Inherent limitation (not papered over).** A double has no real terminal, so it **cannot** make
    /// the child observe `isatty = true`: any behaviour that depends on the *child* seeing a tty (a tool
    /// switching from line-buffered "dumb" output to full-screen TUI mode, a shell enabling colour) is
    /// not reproducible here — only the *observable merged-stream shape* is. Test that child-tty
    /// behaviour against a real `Command.Pty` run. See `docs/testing.md`.
    member _.WithPty() =
        FakeProcess(template, stdout, stdoutBytes, stderr, outcome, pid, Some(PtyResizeRecorder()), stdin, signals)

    /// Keep the built handle's stdin open, exactly as `Command.KeepStdinOpen()` does on a real run, so
    /// `TakeStdin()` hands back a writable pipe — and so a `PtySession` built over this fake can
    /// actually send. Everything written lands in `StdinBytes` for assertions.
    ///
    /// Needed only for a fake built through `FakeProcess.Create`; one built from a caller's own
    /// `Command` (through `ScriptedRunner`) already inherits that command's `KeepStdinOpen`. Applying
    /// it twice is harmless.
    member _.WithStdinOpen() =
        FakeProcess(template.KeepStdinOpen(), stdout, stdoutBytes, stderr, outcome, pid, pty, stdin, signals)

    /// The last `(cols, rows)` requested via `ResizeAsync` on this fake's built PTY handle, or `None`
    /// if this is not a PTY fake (see `WithPty`) or no resize has been requested. Shared across the
    /// fluent chain, so read it from the same instance `Build()` was called on.
    member _.LastResize: (int * int) option =
        match pty with
        | Some recorder -> recorder.Last
        | None -> None

    /// A snapshot of bytes written through `TakeStdin()` on built `KeepStdinOpen` handles. The fake
    /// records raw bytes so tests can assert a command's exact stdin payload without an encoding assumption.
    /// The recorder is shared across this fake's fluent chain, so this aggregates writes from **every**
    /// handle `Build()` has produced from this instance, in write order — including a handle whose own
    /// stdin stream has since been closed by `FinishAsync` or process teardown (that only ends *that*
    /// handle's writes; it does not truncate what it already wrote here, and it does not affect any other
    /// built handle's own, independent stdin stream — see `Build`).
    member _.StdinBytes: byte[] = stdin.Bytes

    /// Signals delivered through built handles, in call order. Aggregated the same way as `StdinBytes`:
    /// every `Build()` on this instance shares the one recorder, so a signal sent to any built handle
    /// shows up here regardless of which handle received it.
    member _.Signals = signals.Snapshot

    /// Build a real `RunningProcess` over in-memory streams.
    ///
    /// Calling `Build()` more than once on the same `FakeProcess` is supported and produces independent
    /// handles: each gets its own stdout/stderr/stdin `MemoryStream`s, so disposing one handle's stdin
    /// (`ProcessStdin.FinishAsync`, or the handle's own teardown) never raises `ObjectDisposedException`
    /// on a write to a different, still-live handle built from the same fake. `StdinBytes` and `Signals`
    /// still read as a single combined log across every built handle (see their docs) — that plural
    /// "handles" wording describes exactly this multi-`Build()` scenario, not just distinct restart
    /// incarnations each with its own `FakeProcess`. Concurrent writers on different handles do not
    /// interleave within a single handle's own stream, but the combined `StdinBytes` log's ordering across
    /// *different* handles is whatever order the underlying writes happened to land in — script one
    /// handle's conversation before starting the next if the test asserts an exact combined byte sequence.
    member private _.BuildCore(recordedCompletion: (TimeSpan * bool) option) : RunningProcess =
        let config = template.Config
        let isPty = pty.IsSome
        let hasMergedStderr = isPty || config.MergeStderr
        let hasStdout = config.StdoutFile.IsNone && config.StdoutMode = StdioMode.Piped

        let hasStderr =
            not hasMergedStderr
            && config.StderrFile.IsNone
            && config.StderrMode = StdioMode.Piped

        // A PTY or MergeStderr has one observable stdout stream: the child's stdout and stderr share a
        // terminal device or the OS folds stderr into stdout. The fake joins scripted stderr on a newline
        // so it forms its own line. A fake cannot reproduce real OS interleaving, so folded stderr simply
        // follows the stdout text.
        let stdoutPayload =
            match stdoutBytes with
            | Some bytes when hasMergedStderr && stderr.Length > 0 ->
                let suffix = config.StdoutEncoding.GetBytes("\n" + stderr)
                Array.append bytes suffix
            | Some bytes -> Array.copy bytes
            | None ->
                let text =
                    if hasMergedStderr && stderr.Length > 0 then
                        match stdout with
                        | "" -> stderr
                        | s when s.EndsWith '\n' -> s + stderr
                        | s -> s + "\n" + stderr
                    else
                        stdout

                config.StdoutEncoding.GetBytes text

        let stdoutStream =
            if hasStdout then
                Some(new MemoryStream(stdoutPayload) :> Stream)
            else
                None

        // PTY and MergeStderr runs have no separate stderr channel, exactly like a real spawn whose
        // `Spawned.Stderr` is `None`; a normal fake keeps its own stderr stream.
        let stderrStream =
            if hasStderr then
                Some(new MemoryStream(config.StderrEncoding.GetBytes stderr) :> Stream)
            else
                None

        // Match the real spawn's capability boundary: `TakeStdin` has a writable pipe exactly for a
        // KeepStdinOpen run. A fake does not feed `Command.Stdin` sources, so this in-memory sink is ready
        // immediately and `StdinFeedComplete` below remains a no-op.
        let stdinStream =
            if config.KeepStdinOpen then
                Some(stdin.NewStream())
            else
                None

        let host: RunningHost =
            { Config = config
              Pid = pid
              Stdout = stdoutStream
              Stderr = stderrStream
              Stdin = stdinStream
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              // No real process backs a fake's pid (arbitrary via `WithPid`, or none), so there is no
              // genuine identity to capture — `None` defers `processMetrics`'s gate (T-097) to the raw
              // read, leaving this fake's existing behaviour unchanged.
              StartTimeIdentity = None
              Wait = fun () -> Task.FromResult outcome
              // A fake process feeds no stdin source, so it never has a source failure to surface. Its
              // keep-open in-memory sink is ready immediately, with no feeder for `TakeStdin` to wait on.
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = fun () -> ()
              Signal =
                fun signal ->
                    signals.Record signal
                    Ok()
              GracefulKill =
                fun _ ->
                    signals.Record config.StopSignal
                    Task.CompletedTask
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
              TreeStats = None
              Teardown =
                fun () ->
                    stdoutStream |> Option.iter (fun s -> s.Dispose())
                    stderrStream |> Option.iter (fun s -> s.Dispose())
                    stdinStream |> Option.iter (fun s -> s.Dispose())
                    ValueTask.CompletedTask }

        match recordedCompletion with
        | Some(duration, truncated) -> new RunningProcess(host, duration, truncated)
        | None -> new RunningProcess(host)

    member this.Build() : RunningProcess = this.BuildCore None

    member internal this.Build(recordedDuration: TimeSpan, recordedTruncated: bool) : RunningProcess =
        this.BuildCore(Some(recordedDuration, recordedTruncated))
