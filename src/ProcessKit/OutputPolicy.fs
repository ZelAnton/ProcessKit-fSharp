namespace ProcessKit

/// How a child's stdout or stderr stream is connected. Set per-stream on `Command` via
/// `Stdout`/`Stderr`; the default is `Piped`.
[<RequireQualifiedAccess>]
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
[<RequireQualifiedAccess>]
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
    member _.MaxLines = maxLines

    /// Maximum retained bytes (sum of the retained lines' UTF-8 lengths): `None` is unbounded.
    member _.MaxBytes = maxBytes

    /// Which line to drop, or whether to error, when a cap is reached.
    member _.Overflow = overflow

    /// Retain everything (the default).
    static member Unbounded() =
        OutputBufferPolicy(None, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines`, dropping the oldest when full.
    static member Bounded(maxLines: int) =
        OutputBufferPolicy(Some maxLines, None, OverflowMode.DropOldest)

    /// Retain at most `maxLines` and error when the cap is reached — a fail-loud ceiling.
    static member FailLoud(maxLines: int) =
        OutputBufferPolicy(Some maxLines, None, OverflowMode.Error)

    /// A copy with the retained-byte ceiling set, composable with any policy.
    member _.WithMaxBytes(maxBytes: int) =
        OutputBufferPolicy(maxLines, Some maxBytes, overflow)

    /// A copy with the overflow behaviour set.
    member _.WithOverflow(overflow: OverflowMode) =
        OutputBufferPolicy(maxLines, maxBytes, overflow)

    /// The default policy: retain everything.
    static member Default = OutputBufferPolicy.Unbounded()
