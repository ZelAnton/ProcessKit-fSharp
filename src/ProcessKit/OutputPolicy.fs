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

    /// "Head" semantics: keep what is already buffered and discard new lines.
    | DropNewest

    /// Fail-loud ceiling: once the cap is reached the run errors with
    /// `ProcessError.OutputTooLarge` rather than silently dropping. The pipe is still drained
    /// (the child never blocks); excess lines are counted but not retained. On an *unbounded*
    /// buffer this is zero-tolerance — any line-pumped output errors.
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

    /// Maximum retained bytes (sum of the retained lines' UTF-8 lengths): `None` is unbounded. Also
    /// bounds the in-flight (not-yet-newline-terminated) line for the buffered verbs: an unterminated
    /// line is force-flushed once it reaches this many characters, so a child emitting a newline-free
    /// flood can't grow the assembly buffer past the cap (the flushed segments are dropped/errored per
    /// `Overflow`, like any other over-cap output). This cap is also the ceiling on a **raw byte**
    /// capture (`OutputBytesAsync`'s stdout and a pipeline's captured last-stage stdout): `Some cap`
    /// enforces it per `Overflow` (`Error` -> `OutputTooLarge`, `DropOldest` -> last `cap` bytes,
    /// `DropNewest` -> first `cap` bytes), while `None` leaves the raw capture unbounded.
    member _.MaxBytes = maxBytes

    /// Which line to drop, or whether to error, when a cap is reached.
    member _.Overflow = overflow

    /// Retain everything (the default).
    static member Unbounded = OutputBufferPolicy(None, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines`, dropping the oldest when full. `maxLines` must be non-negative
    /// (`0` retains nothing; a negative value is rejected with `ArgumentOutOfRangeException`).
    static member Bounded(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative maxLines
        OutputBufferPolicy(Some maxLines, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines` and error when the cap is reached — a fail-loud ceiling. `maxLines`
    /// must be non-negative (`0` retains nothing but still tracks totals; negative is rejected).
    static member FailLoud(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative maxLines
        OutputBufferPolicy(Some maxLines, None, OverflowMode.Error)

    /// A copy with the retained-line ceiling set, composable with any policy. `maxLines` must be
    /// non-negative (negative is rejected with `ArgumentOutOfRangeException`).
    member _.WithMaxLines(maxLines: int) =
        ArgumentOutOfRangeException.ThrowIfNegative maxLines
        OutputBufferPolicy(Some maxLines, maxBytes, overflow)

    /// A copy with the retained-byte ceiling set, composable with any policy. `maxBytes` must be
    /// non-negative (negative is rejected with `ArgumentOutOfRangeException`).
    member _.WithMaxBytes(maxBytes: int) =
        ArgumentOutOfRangeException.ThrowIfNegative maxBytes
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
    /// [Streaming](../../docs/streaming.md) before using it: a `Command.Timeout` kills the *child* but
    /// does not by itself free a writer parked here if you abandon reading — dispose the
    /// `RunningProcess` to release it).
    | Backpressure

    /// Ring-buffer / "tail" semantics: drop the oldest queued item to make room for the newest.
    /// Lossy but bounded; sets `RunningProcess.DroppedStreamLineCount`.
    | DropOldest

    /// "Head" semantics: keep what is already queued and drop the newest incoming item.
    /// Lossy but bounded; sets `RunningProcess.DroppedStreamLineCount`.
    | DropNewest

    /// Fail-loud ceiling: once the cap is reached, fault the stream with `ProcessError.OutputTooLarge`
    /// (observed by the consumer as the streaming enumerator throwing, the same fault path a throwing
    /// per-line handler already uses).
    | Error

/// An opt-in bounded/backpressure policy for the streaming verbs (`StdoutLinesAsync` /
/// `OutputEventsAsync` / `WaitForLineAsync`), set via `Command.StreamBuffer`. Unlike
/// `OutputBufferPolicy` — which bounds an in-memory *buffer* a one-shot verb assembles — this bounds
/// the *channel* between the background pump and your live consumer. Leaving it unset keeps today's
/// unbounded channel: an unbounded, uncapped in-flight backlog, exactly as before this policy existed.
[<Sealed>]
type StreamBufferPolicy internal (capacity: int, fullMode: StreamFullMode) =

    /// The bounded channel capacity, in lines/events not yet read by the consumer.
    member _.Capacity = capacity

    /// What happens once the channel reaches `Capacity`.
    member _.FullMode = fullMode

    /// A channel bounded to `capacity` items that backpressures the producer once full — the safest
    /// default for an opt-in cap: lossless, at the cost of the child's observable timing.
    static member Bounded(capacity: int) =
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1)
        StreamBufferPolicy(capacity, StreamFullMode.Backpressure)

    /// A channel bounded to `capacity` items with an explicit `fullMode`.
    static member Bounded(capacity: int, fullMode: StreamFullMode) =
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1)
        StreamBufferPolicy(capacity, fullMode)

    /// A copy with the full-mode changed, composable with any policy.
    member _.WithFullMode(fullMode: StreamFullMode) = StreamBufferPolicy(capacity, fullMode)
