namespace ProcessKit

/// How the output pump decides where one captured or streamed line ends — set per stream on
/// `Command` via `Command.LineTerminator` (both streams at once) or `Command.StdoutLineTerminator` /
/// `Command.StderrLineTerminator`. The default is `Lf`, which reproduces ProcessKit's original
/// line-splitting behaviour exactly.
///
/// This is one shared definition of "a line" for the whole line-pumped path: what
/// `RunningProcess.StdoutLinesAsync` / `OutputEventsAsync` yield, what a `WaitForLineAsync` predicate
/// sees, what the per-line handlers (`Command.OnStdoutLine` / `OnStderrLine`) receive, and what
/// `OutputStringAsync` joins. Choosing a mode moves all of them together — there is never a per-sink
/// disagreement about what a line is. It is orthogonal to the raw byte path: `OutputBytesAsync` and
/// the tees (`Command.StdoutTee` / `StderrTee`) stay byte-exact and are unaffected by the mode.
///
/// A `\r\n` pair is always treated as a **single** terminator in every mode (it never emits a
/// spurious empty line between the `\r` and the `\n`), so ordinary CRLF text reads identically across
/// the modes; the modes differ only in whether a *lone* `\n` or a *lone* `\r` ends a line.
[<RequireQualifiedAccess; NoComparison>]
type LineTerminator =

    /// Unix / line-feed framing — the default, and ProcessKit's original behaviour: split on `\n`
    /// only. A `\r` immediately before a `\n` is a CRLF terminator and is stripped; every other `\r`
    /// (a lone one, or a run of them) is line **content**. Carriage-return progress output — a bar
    /// redrawn in place with `\r` and no `\n` until the very end — therefore accumulates as one
    /// ever-growing line: nothing is delivered until the final `\n`, and under a byte cap
    /// (`OutputBufferPolicy.WithMaxBytes`) that single over-cap line is dropped whole. Reach for `Cr`
    /// or `Any` when you need such output live.
    | Lf

    /// Classic carriage-return framing (old Mac OS ≤ 9, and the shape of `curl`/`pip`/`apt` progress
    /// output): split on a lone `\r`. A `\r\n` pair is a single terminator; a lone `\n` is **content**.
    /// Each carriage-return frame is delivered as its own line the instant it is seen, so
    /// `Progress: 50%\rProgress: 100%` streams as the frames `Progress: 50%` then `Progress: 100%`
    /// live, instead of piling up as one line that surfaces only at EOF.
    | Cr

    /// Strict Windows framing: split only on the two-character sequence `\r\n`. A lone `\n` or a lone
    /// `\r` is line **content**, so `\n`-only (Unix) or `\r`-only (progress) output is not split.
    | CrLf

    /// Universal framing: split on any of `\n`, `\r`, or `\r\n` (a `\r\n` pair staying a single
    /// terminator). Both a lone `\n` and a lone `\r` end a line, so this both reads ordinary text and
    /// splits carriage-return progress output into live per-frame lines. The `\r`-aware choice for
    /// output of unknown or mixed line endings.
    | Any

/// Internal per-mode splitting rules the output pump reads to decide, character by character, whether
/// a lone terminator ends a line. Kept in one place (rather than inlined into the pump) so the
/// mode-to-behaviour mapping is directly unit-testable and lives beside the `LineTerminator` type it
/// interprets. A `\r\n` pair is always a single terminator in every mode and is handled directly by
/// the pump scan, so it needs no predicate here.
module internal LineTerminatorRules =

    /// Does a lone `\n` (one not preceded by a `\r`) end a line in this mode? True for `Lf`/`Any`.
    let splitsOnLf (terminator: LineTerminator) : bool =
        match terminator with
        | LineTerminator.Lf
        | LineTerminator.Any -> true
        | LineTerminator.Cr
        | LineTerminator.CrLf -> false

    /// Does a lone `\r` (one not part of a `\r\n` pair) end a line in this mode? True for `Cr`/`Any`.
    let splitsOnCr (terminator: LineTerminator) : bool =
        match terminator with
        | LineTerminator.Cr
        | LineTerminator.Any -> true
        | LineTerminator.Lf
        | LineTerminator.CrLf -> false
