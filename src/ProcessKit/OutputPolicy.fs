namespace ProcessKit

open System

/// How a child's stdout or stderr stream is connected. Set per-stream on `Command` via
/// `Stdout`/`Stderr`; the default is `Piped`.
[<RequireQualifiedAccess; NoComparison>]
type StdioMode =

    /// Capture the stream through a pipe (the default). Required for line streaming,
    /// per-line handlers, and the output-retrieval verbs to see any output.
    | Piped

    /// Let the child share the parent's stream (its output appears on the parent's terminal /
    /// log). Cannot be captured.
    | Inherit

    /// Redirect the stream to the null device, discarding output without tying up a pipe.
    | Null

/// What to drop when a bounded output buffer is full.
[<RequireQualifiedAccess; NoComparison>]
type OverflowMode =

    /// Ring-buffer / "tail" semantics: discard the oldest retained line so the most recent
    /// output survives.
    | DropOldest

    /// "Head" semantics: keep a contiguous prefix of what is already buffered and discard new lines.
    /// Once a byte cap rejects a line, later shorter lines are also discarded rather than filling a
    /// hole in that prefix.
    | DropNewest

    /// Fail-loud ceiling: once the cap would be exceeded the run errors with
    /// `ProcessError.OutputTooLarge` rather than silently dropping. Output exactly equal to a
    /// configured limit is retained; the error is raised on the next line/byte that would push
    /// past it, not on reaching the limit exactly. The pipe is still drained (the child never
    /// blocks); excess lines are counted but not retained. With no cap set (`MaxLines = None`
    /// and `MaxBytes = None`) there is no ceiling to cross, so `Error` behaves like the other
    /// overflow modes on an unbounded buffer: it retains everything and never trips
    /// `OutputTooLarge`.
    | Error

/// Caps how many captured/streamed output lines are retained in memory.
///
/// The pump always drains the OS pipe (the child never blocks on a full buffer); this only
/// bounds the in-memory backlog. Line counters still count every line, so a count greater than
/// the retained amount reveals that lines were dropped. Two independent ceilings — lines and
/// bytes — either or both of which may be set.
[<Sealed>]
type OutputBufferPolicy internal (maxLines: int option, maxBytes: int option, overflow: OverflowMode) =

    /// Maximum retained lines: `None` is unbounded, `Some 0` retains nothing, `Some n` keeps at most `n`.
    /// Applies to the line-capturing paths (the text verbs' stdout/stderr, and a byte verb's line-pumped
    /// stderr). It does **not** apply to a raw byte capture — `OutputBytesAsync`'s stdout and a
    /// pipeline's captured last-stage stdout have no line structure, so only `MaxBytes` bounds those.
    member _.MaxLines = maxLines

    /// Maximum retained bytes: `None` is unbounded. Also bounds the in-flight (not-yet-newline-
    /// terminated) line for the buffered verbs: an unterminated line is force-flushed once its UTF-8
    /// size reaches this many bytes, so a child emitting a newline-free flood can't grow the assembly
    /// buffer past the cap (the flushed segments are dropped/errored per `Overflow`, like any other
    /// over-cap output). An indivisible decoded Unicode scalar can itself be larger than the cap; it is
    /// emitted intact and the retention policy still refuses to retain it when it exceeds the limit.
    ///
    /// For the **line-capturing** paths (the text verbs' stdout/stderr, and a byte verb's line-pumped
    /// stderr — see `Pump.LineBuffer`), each retained line counts its own UTF-8 byte length **plus one
    /// byte** for the `\n` separator reintroduced when the retained lines are joined back into text —
    /// so this cap genuinely bounds the reassembled text's size (not merely the sum of the lines' own
    /// content), and an empty line still costs a non-zero amount (bounding an empty-line flood, which
    /// would otherwise cost `0` bytes per line and defeat the cap).
    ///
    /// This cap is also the ceiling on a **raw byte** capture (`OutputBytesAsync`'s stdout and a
    /// pipeline's captured last-stage stdout — see `Pump.RawBuffer`), which has no line structure and
    /// so no separator surcharge: `Some cap` enforces the literal byte cap per `Overflow` (`Error` ->
    /// `OutputTooLarge`, `DropOldest` -> last `cap` bytes, `DropNewest` -> first `cap` bytes), while
    /// `None` leaves the raw capture unbounded.
    member _.MaxBytes = maxBytes

    /// Which line to drop, or whether to error, when a cap is reached.
    member _.Overflow = overflow

    /// Retain everything (the default).
    static member Unbounded = OutputBufferPolicy(None, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines`, dropping the oldest when full. `maxLines` must be non-negative
    /// (`0` retains nothing; a negative value is rejected with `ArgumentOutOfRangeException`).
    static member Bounded(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines, nameof maxLines)
        OutputBufferPolicy(Some maxLines, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines` and error when the cap is reached — a fail-loud ceiling. `maxLines`
    /// must be non-negative (`0` retains nothing but still tracks totals; negative is rejected).
    static member FailLoud(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines, nameof maxLines)
        OutputBufferPolicy(Some maxLines, None, OverflowMode.Error)

    /// A copy with the retained-line ceiling set, composable with any policy. `maxLines` must be
    /// non-negative (negative is rejected with `ArgumentOutOfRangeException`).
    member _.WithMaxLines(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines, nameof maxLines)
        OutputBufferPolicy(Some maxLines, maxBytes, overflow)

    /// A copy with the retained-byte ceiling set, composable with any policy. `maxBytes` must be
    /// non-negative (negative is rejected with `ArgumentOutOfRangeException`).
    member _.WithMaxBytes(maxBytes: int) =
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes, nameof maxBytes)
        OutputBufferPolicy(maxLines, Some maxBytes, overflow)

    /// A copy with the overflow behaviour set.
    member _.WithOverflow(overflow: OverflowMode) =
        OutputBufferPolicy(maxLines, maxBytes, overflow)

    /// The default policy: retain everything.
    static member Default = OutputBufferPolicy.Unbounded

/// How a bounded *streaming* channel behaves once its capacity is reached — the streaming analogue of
/// `OverflowMode`, but with a genuine backpressure option that only makes sense against a live
/// consumer (a buffered one-shot verb has no such consumer to pace, which is why `OutputBufferPolicy`
/// has no equivalent case).
[<RequireQualifiedAccess; NoComparison>]
type StreamFullMode =

    /// Slow the producer down instead of dropping anything: the pump stops draining the OS pipe until
    /// the consumer catches up, so the child itself observably blocks writing to a full stdout/stderr
    /// pipe. Bounds memory losslessly, at the cost of the child's timing — opt in only when you intend
    /// to pace a trusted producer against your consumer (see the deadlock note in
    /// <a href="https://zelanton.github.io/ProcessKit-fSharp/streaming.html">Streaming</a> before using it: a
    /// `Command.Timeout` kills the *child* but does not by itself free a writer parked here if you abandon
    /// reading — dispose the `RunningProcess` to release it).
    | Backpressure

    /// Ring-buffer / "tail" semantics: drop the oldest queued item to make room for the newest.
    /// Lossy but bounded; sets `RunningProcess.DroppedStreamLineCount`. Refused by
    /// `ContentLengthSession`, whose items are protocol messages rather than log lines.
    | DropOldest

    /// "Head" semantics: keep what is already queued and drop the newest incoming item.
    /// Lossy but bounded; sets `RunningProcess.DroppedStreamLineCount`. Refused by
    /// `ContentLengthSession`, whose items are protocol messages rather than log lines.
    | DropNewest

    /// Fail-loud ceiling: once the cap is reached, fault the stream with `ProcessError.OutputTooLarge`
    /// (observed by the consumer as the streaming enumerator throwing, the same fault path a throwing
    /// per-line handler already uses).
    | Error

/// An opt-in bounded/backpressure policy for the streaming verbs (`StdoutLinesAsync` /
/// `OutputEventsAsync` / `WaitForLineAsync`) and `ContentLengthSession.FramesAsync` (which honours the
/// lossless full modes and refuses the two lossy ones), set via
/// `Command.StreamBuffer`. It is inapplicable to `PtySession`, whose raw match window and transcript
/// have their own character bounds. Unlike
/// `OutputBufferPolicy` — which bounds an in-memory *buffer* a one-shot verb assembles — this bounds
/// the *channel* between the background pump and your live consumer. Leaving it unset keeps today's
/// unbounded channel: an unbounded, uncapped in-flight backlog, exactly as before this policy existed.
[<Sealed>]
type StreamBufferPolicy internal (capacity: int, fullMode: StreamFullMode) =

    /// The bounded channel capacity, in lines/events/frames not yet read by the consumer.
    member _.Capacity = capacity

    /// What happens once the channel reaches `Capacity`.
    member _.FullMode = fullMode

    /// A channel bounded to `capacity` items that backpressures the producer once full — the safest
    /// default for an opt-in cap: lossless, at the cost of the child's observable timing.
    static member Bounded(capacity: int) =
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1, nameof capacity)
        StreamBufferPolicy(capacity, StreamFullMode.Backpressure)

    /// A channel bounded to `capacity` items with an explicit `fullMode`.
    static member Bounded(capacity: int, fullMode: StreamFullMode) =
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1, nameof capacity)
        StreamBufferPolicy(capacity, fullMode)

    /// A copy with the full-mode changed, composable with any policy.
    member _.WithFullMode(fullMode: StreamFullMode) = StreamBufferPolicy(capacity, fullMode)

/// Which of a child's two captured streams a decoded line came from.
///
/// Handed to `ICapturePolicy.OnCapture` so one policy can treat the two streams differently (redact
/// only the stream a passphrase prompt is echoed on, say). It mirrors `OutputEvent`'s stdout/stderr
/// split, but is a bare discriminator carrying no line: the capture seam should not force a consumer
/// that only wants to shape text to depend on the streaming event type.
///
/// Deliberately carries no stable machine identifier (no `Name`/`FromName`, and no entry in
/// `spec/identifiers.json`), unlike `RlimitResource` or `IoPriorityClass`: nothing serializes it — it
/// is an argument passed to a callback within one process, never a value ProcessKit writes to a
/// report, a log field, or a cassette.
[<RequireQualifiedAccess; NoComparison>]
type CaptureStream =

    /// The child's standard output.
    | Stdout

    /// The child's standard error.
    | Stderr

/// A consumer-supplied, typed seam that shapes each decoded line **just before it enters the capture
/// backlog** — the redaction-at-capture extension point, set with `Command.CapturePolicy`.
///
/// For every framed line handed to a capture, `OnCapture` runs *before* the line is retained, so the
/// text it returns — never the raw line — is what lands in the backlog, and therefore in
/// `ProcessResult.Stdout`/`Stderr` and `Finished.Stderr`. That is what the observing
/// `Command.OnStdoutLine`/`OnStderrLine` handlers cannot do: they run *alongside* capture and can see
/// a line, not shape what is kept.
///
/// **Scope and boundaries (read before relying on it for secret hygiene).** This seam shapes the
/// in-memory capture backlog and nothing else. It deliberately does **not** reach the independent
/// observation sinks, each of which keeps its existing contract and sees the line **unshaped**:
///
///  - the per-line handlers `Command.OnStdoutLine`/`OnStderrLine` and the tees
///    `Command.StdoutTee`/`StderrTee` — they exist to observe the real output; if you also write to a
///    log or a file there, redact in that sink too;
///  - the **streaming** verbs, which hand each line to a live consumer rather than retaining it:
///    `RunningProcess.StdoutLinesAsync`, `OutputEventsAsync`, `WaitForLineAsync`, the byte-chunk
///    streams, `PtySession`'s window/transcript, `ContentLengthSession`'s frames, and the stderr
///    readiness probes. (`FinishAsync`'s retained *stderr* on those same sessions **is** backlog, and
///    is shaped.)
///  - a **raw byte** capture, which has no decoded line to shape: `OutputBytesAsync`'s stdout. A bytes
///    run's line-pumped **stderr** is still shaped.
///
/// A `Pipeline` captures nothing but raw bytes — its final stdout and every stage's stderr — so it
/// cannot shape anything at all; a stage carrying a policy is therefore **rejected** by `Pipe`
/// (`ArgumentException`) rather than run with the seam quietly inactive. Run such a command on its own.
///
/// **A failing policy fails closed.** If `OnCapture` throws — or returns `null` — the offending line
/// is retained **empty**, never the raw line it was meant to scrub, and the policy stays active for
/// the lines that follow. A redactor that throws blanks its output rather than leaking it. Nothing
/// else reports that failure, so prefer a policy that cannot throw.
///
/// **It must be thread-safe.** One policy instance serves both of a run's streams, whose pumps are
/// independent tasks, so `OnCapture` can be called for a stdout line and a stderr line at the same
/// time (and for several concurrent runs sharing one policy). A pure transform of its argument — the
/// shape this seam is for — needs nothing extra; a policy that keeps mutable state has to guard it.
///
/// **What it does not decide.** *How much* is retained, and what is evicted on overflow, remain
/// `OutputBufferPolicy`'s job; the two compose orthogonally — this decides each retained line's
/// content, that decides how many survive. Retention bookkeeping (the retained-byte total, the
/// `DropNewest` seal, `Truncated`/`TooLarge`) is computed from the text you return, while the
/// cumulative line counters and the raw-pipe byte counters (`RunningProcess.StdoutBytesSeen`) are
/// taken before this seam and are untouched by it.
type ICapturePolicy =

    /// A short, stable, human-readable name for this policy (`"redact-tokens"`, say), surfaced by
    /// `Command.ConfiguredCapturePolicyName` so a configured policy is introspectable in a test or a
    /// diagnostic dump rather than an anonymous callback.
    abstract Name: string

    /// Shape one decoded `line` from `stream` just before it enters the capture backlog, returning the
    /// text to retain: the line itself to keep it unchanged, a rewrite to redact it, or `""` to blank
    /// it while keeping its slot (and the line/byte counters). The line arrives with its terminator
    /// already stripped — the shape `StdoutLinesAsync` yields. Keep it cheap: it runs on the capture
    /// pump, in front of every retained line.
    abstract OnCapture: stream: CaptureStream * line: string -> string

/// Applies a configured `ICapturePolicy` at the one seam that owns the capture backlog. The whole
/// point of routing every capture write through here is that the fail-closed rule cannot be
/// implemented differently — or forgotten — at one of the several places a line reaches a backlog.
[<RequireQualifiedAccess>]
module internal CapturePolicy =

    /// What a failing policy retains in place of the line it was asked to shape.
    [<Literal>]
    let FailClosedLine = ""

    /// Shape one framed line for the backlog. `None` (no policy configured — the default) returns the
    /// line unchanged and costs one match.
    let shapeLine (policy: ICapturePolicy option) (stream: CaptureStream) (line: string) : string =
        match policy with
        | None -> line
        | Some configured ->
            try
                let shaped = configured.OnCapture(stream, line)

                // A policy declared to return a string handed back `null` — a bug in it, and one that
                // must not retain the raw line: a consumer compiled without nullable reference types
                // can return null where Rust's `Cow<str>` could not, so this is the .NET-specific half
                // of the same fail-closed rule the `with` handler below implements for a throwing
                // policy. `ReferenceEquals` because the declared type is non-nullable here, so a `null`
                // pattern would not compile against it.
                if obj.ReferenceEquals(shaped, null) then
                    FailClosedLine
                else
                    shaped
            with _ ->
                // FAIL CLOSED, deliberately catching everything a consumer's policy can raise. The
                // policy exists to keep a secret out of the retained capture, so the one thing the
                // failure path must never do is fall back to the raw line it was handed. Retaining the
                // blank line instead keeps the run going (a buggy redactor does not fail the command)
                // with the secret still scrubbed, and leaves the policy active for later lines. This
                // is the deliberate opposite of a throwing `OnStdoutLine` handler, which faults the
                // run: a handler's failure has nothing to hide, this one does.
                FailClosedLine

    /// Shape an already-assembled capture — the newline-joined text a `Pump.LineBuffer` produces — by
    /// running `shapeLine` over each of its lines. Used where a retained capture is reconstructed
    /// rather than pumped (a `RecordReplayRunner` cassette hit), so that path shapes its retained text
    /// exactly as the live pump would have shaped the same lines. `'\n'` is the separator because it is
    /// the one `Pump.LineBuffer.Text` joins retained lines with, whatever `LineTerminator` framed them.
    let shapeText (policy: ICapturePolicy option) (stream: CaptureStream) (text: string) : string =
        match policy with
        | None -> text
        | Some _ ->
            // An empty capture has no framed line, so a live run would never have called the policy at
            // all; returning it unchanged keeps the reconstructed path faithful to that (and covers the
            // null a malformed cassette entry could carry).
            if String.IsNullOrEmpty text then
                text
            else
                text.Split '\n' |> Array.map (shapeLine policy stream) |> String.concat "\n"
