namespace ProcessKit

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// What `PtySession.SendLineAsync` appends to the text it sends — the "Enter key" of an interactive
/// session. A real terminal sends a carriage return when Enter is pressed, which is why `Auto` picks
/// it for a `Command.Pty` run; a plain pipe has no line discipline to translate it, so `Auto` picks a
/// line feed there instead. Override it when a particular child disagrees.
[<RequireQualifiedAccess; NoComparison>]
type PtyLineEnding =

    /// `Cr` on a `Command.Pty` run, `Lf` otherwise — the default, and correct for both. A POSIX pty's
    /// line discipline maps the carriage return to a newline for the child (`ICRNL`, on by default) and
    /// Windows ConPTY maps it to a VK_RETURN key event, so a terminal child sees a completed line
    /// either way; a child reading a plain pipe sees a literal `\r` instead and would keep waiting, so
    /// a non-PTY run gets `\n`.
    | Auto

    /// A carriage return (`\r`) — what a terminal sends for Enter.
    | Cr

    /// A line feed (`\n`).
    | Lf

    /// A carriage return + line feed pair (`\r\n`).
    | CrLf

/// Tuning for a `PtySession`: how much output a pattern may be matched against, whether the session
/// keeps a transcript for diagnostics, and what `SendLineAsync` sends as the line ending. Both sizes
/// are hard memory bounds in **characters**, so a long-lived session over a chatty child can never grow
/// without limit; both bound by dropping the OLDEST text, since the tail is what the next pattern will
/// match and what a failed session's diagnosis needs.
[<NoComparison>]
type PtySessionOptions =
    {
        /// How many characters of not-yet-matched output the sliding match window holds (must be
        /// positive; the default is 65536). Output older than this is evicted, so a pattern that would
        /// need more context than the window holds simply cannot match — raise it for a child that
        /// emits a lot between prompts, lower it to cap memory harder.
        WindowChars: int

        /// Keep a transcript of everything the child emits this session (`true`, the default) or keep
        /// none at all (`false`). Turn it OFF for a session that handles credentials: a terminal echoes
        /// typed input back into its own output stream by default, so a password sent through a PTY
        /// with `PtyConfig.Echo = true` would otherwise be recorded in `Transcript` — see
        /// `PtyConfig`'s secret-safety note.
        CaptureTranscript: bool

        /// How many characters of transcript to retain when `CaptureTranscript` is on (must be
        /// positive; the default is 1048576). Once reached, the oldest text is dropped and
        /// `TranscriptTruncated` reports it.
        TranscriptChars: int

        /// What `SendLineAsync` appends to the text it sends (the default is `PtyLineEnding.Auto`).
        LineEnding: PtyLineEnding
    }

    /// The default session tuning: a 65536-character match window, a transcript capped at 1048576
    /// characters, and `PtyLineEnding.Auto`.
    static member Default =
        { WindowChars = 65536
          CaptureTranscript = true
          TranscriptChars = 1048576
          LineEnding = PtyLineEnding.Auto }

/// What a `PtySession.ExpectAsync` pattern matched, plus everything the child emitted before it — the
/// output a prompt was preceded by (a banner, a menu, an error line), which is usually what the script
/// wants to inspect or log. Both are consumed from the session's window, so the next `ExpectAsync`
/// starts after this match and can never match the same prompt twice.
[<Sealed; NoComparison>]
type ExpectMatch internal (before: string, text: string) =

    /// Everything received before the match, since the previous `ExpectAsync` consumed its own match.
    /// Capped by the session's `WindowChars`: on a child that emitted more than that between the two
    /// patterns, the oldest part has already been evicted (`Transcript` still has the fuller record).
    member _.Before = before

    /// The text the pattern matched.
    member _.Text = text

[<RequireQualifiedAccess>]
module internal PtyExpectLoop =

    let invalidPatternError: Result<ExpectMatch, ProcessError> =
        Error(ProcessError.Unsupported "expect patterns must match at least one character")

    let private readFaultError (fault: exn) : Result<ExpectMatch, ProcessError> =
        match fault with
        | :? ProcessException as ex -> Error ex.Error
        | ex -> Error(ProcessError.Io ex.Message)

    let run
        (window: ExpectWindow)
        (program: string)
        (timeProvider: TimeProvider)
        (matcher: string -> (int * int) option)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<ExpectMatch, ProcessError>> =
        task {
            // Clamp so an out-of-range timeout can't throw out of the CTS constructor, and report the
            // CLAMPED value in `NotReady` — the same rule `WaitForLineAsync`/`ReadinessProbe` follow, so
            // a reported budget is always the one actually enforced.
            let armed = Timeouts.clampArmable timeout
            use deadline = new CancellationTokenSource(armed, timeProvider)

            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken)

            let interruptionOutcome () =
                if cancellationToken.IsCancellationRequested then
                    Some(Error(ProcessError.Cancelled program))
                elif deadline.IsCancellationRequested then
                    Some(Error(ProcessError.NotReady(program, armed)))
                else
                    None

            let matcherFailureOutcome (fault: exn) =
                match interruptionOutcome () with
                | Some outcome -> outcome
                | None ->
                    match fault with
                    | :? RegexMatchTimeoutException as ex -> Error(ProcessError.NotReady(program, ex.MatchTimeout))
                    | ex -> Error(ProcessError.Io $"expect pattern matching failed: {ex.Message}")

            let mutable result = ValueNone

            while result.IsNone do
                // Capture the change signal BEFORE testing the window: output landing between the two
                // either shows up in this very test or completes the captured task, so a wake-up can
                // never be lost (see `ExpectWindow.Changed`).
                let changed = window.Changed

                // Matching runs over an immutable snapshot outside the window gate. The conditional
                // consume below returns `None` when output or a competing waiter changed that snapshot;
                // retrying is what keeps a stale match from consuming different text while letting the
                // output pumps continue during an expensive regular expression.
                let step =
                    try
                        Choice1Of2(window.TryConsume matcher)
                    with
                    | :? RegexMatchTimeoutException as ex -> Choice2Of2(matcherFailureOutcome ex)
                    | ex -> Choice2Of2(matcherFailureOutcome ex)

                match step with
                | Choice2Of2 outcome -> result <- ValueSome outcome
                | Choice1Of2 None ->
                    // A stale snapshot is not a terminal verdict. Before retrying, observe the same
                    // caller-first cancellation/deadline ordering used by the parked-wait path so a
                    // continuously changing window cannot postpone an already-fired limit forever.
                    match interruptionOutcome () with
                    | Some outcome -> result <- ValueSome outcome
                    | None -> ()
                | Choice1Of2(Some(ExpectStep.Matched(before, matched))) ->
                    result <- ValueSome(Ok(ExpectMatch(before, matched)))
                | Choice1Of2(Some ExpectStep.InvalidPattern) -> result <- ValueSome invalidPatternError
                // The child's output has ended (it closed the terminal, or exited) and the pattern
                // never arrived: report it now rather than burning the rest of the timeout on output
                // that can no longer come.
                | Choice1Of2(Some(ExpectStep.Ended(Some fault))) -> result <- ValueSome(readFaultError fault)
                | Choice1Of2(Some(ExpectStep.Ended None)) ->
                    result <- ValueSome(Error(ProcessError.NotReady(program, armed)))
                | Choice1Of2(Some ExpectStep.Waiting) ->
                    try
                        do! changed.WaitAsync linked.Token
                    with
                    | :? OperationCanceledException ->
                        // The caller's token wins over the deadline: a cancelled wait is an error, a
                        // timed-out one is "the pattern has not arrived yet". Either way the window is
                        // left untouched, so a retry — or the next pattern — still sees this output.
                        match interruptionOutcome () with
                        | Some outcome -> result <- ValueSome outcome
                        | None ->
                            invalidOp "A linked expect wait was cancelled without either source token being cancelled"
                    | ex -> raise ex

            match result with
            | ValueSome outcome -> return outcome
            | ValueNone ->
                return invalidOp "Loop invariant violated: result should always be ValueSome after exiting the loop"
        }

/// An expect-style conversation with a live child: wait for a pattern in its terminal output, send it
/// input, repeat — the classic automation loop for an interactive program (`ssh`, a REPL, an
/// installer, a credential prompt). Built over a started `RunningProcess`, and designed for one from a
/// `Command.Pty` run, which is what makes an interactive child prompt at all.
///
/// **Why not `WaitForLineAsync`.** A terminal prompt is not a line: `Password: `, `> `, `(y/N) ` carry
/// no line terminator, so a line-framed wait cannot see them until a newline finally arrives — often
/// never, because the child is waiting for the very input the prompt is asking for. A session
/// therefore reads the child's merged terminal output as **raw text** and matches patterns against a
/// sliding window of it, framing nothing.
///
/// **One conversation, in order.** The verbs are meant to be called sequentially, the way the script
/// they automate reads: expect, send, expect. Two `ExpectAsync` calls racing on one session are safe
/// (the window is guarded, so exactly one of them consumes a given match) but arbitrary — which of the
/// two sees the prompt is a coin toss, the same as concurrently driving any other single handle.
///
/// **This session owns the output pipes.** Creating it claims the handle exactly like
/// `OutputEventsAsync`/`StdoutLinesAsync` do, so a capturing or streaming verb on the same handle
/// afterwards is refused ("already consumed by another verb"), and constructing a session over a
/// handle another verb already claimed throws `InvalidOperationException`. Use `WaitForExitAsync` for
/// the child's outcome, and dispose the `RunningProcess` (or its owning `ProcessGroup`) to reap the
/// tree — do not layer a second consuming verb on top.
///
/// **Line handlers do not fire here.** `Command.LineTerminator`, `OnStdoutLine`/`OnStderrLine` and the
/// per-stream line counters describe a framed stream, and this session deliberately does not frame
/// one; the byte-exact tees (`Command.StdoutTee`/`StderrTee`) are still fed, exactly as they are on
/// the line paths. On a plain (non-PTY) run a session still works, but the child decides whether you
/// ever see a prompt: without a terminal most programs switch their stdout to block buffering, so the
/// prompt sits in the child's own buffer — that is the child's behaviour, and precisely what
/// `Command.Pty` exists to change.
///
/// **`StreamBuffer` is inapplicable here.** A session has no queued line/frame backlog: raw output
/// feeds its sliding match window directly. `PtySessionOptions.WindowChars` and `TranscriptChars`
/// are its explicit bounded-memory policy, so a `Command.StreamBuffer` setting has no effect.
///
/// **Secret-safety.** Sent input is never logged, traced, or added to `Transcript` — but a terminal
/// echoes input back into its OUTPUT by default, so with `PtyConfig.Echo = true` a sent password
/// arrives in the child's output stream and therefore in the transcript. For a credential exchange use
/// `Echo = false` (POSIX), or turn `CaptureTranscript` off, or both.
[<Sealed>]
type PtySession private (running: RunningProcess, options: PtySessionOptions, filterAnsi: bool) =

    do
        ArgumentNullException.ThrowIfNull(running, nameof running)
        ArgumentNullException.ThrowIfNull(options, nameof options)
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WindowChars, 1, "options.WindowChars")

        if options.CaptureTranscript then
            ArgumentOutOfRangeException.ThrowIfLessThan(options.TranscriptChars, 1, "options.TranscriptChars")

    let config = running.Config
    let program = config.Program

    // The terminal has one encoding: what the session decodes the child's output with is what it
    // encodes sent input with, so a round-trip through the pty can't disagree with itself.
    let encoding = config.StdoutEncoding

    let lineEnding =
        match options.LineEnding with
        | PtyLineEnding.Auto -> Pump.defaultInputLineTerminator running.HasPseudoTerminal
        | PtyLineEnding.Cr -> Pump.defaultInputLineTerminator true
        | PtyLineEnding.Lf -> Pump.defaultInputLineTerminator false
        | PtyLineEnding.CrLf -> "\r\n"

    // Claim the pipes and start the raw readers. A handle another verb already owns is a programmer
    // error (two readers on one pipe would split the child's output), reported the same way
    // `StdoutLinesAsync`/`OutputEventsAsync` report it: an exception, not a `Result` a script would
    // have to thread through every step of the conversation.
    let window =
        let transcriptChars =
            if options.CaptureTranscript then
                Some options.TranscriptChars
            else
                None

        match running.StartInteractiveSession(options.WindowChars, transcriptChars, filterAnsi) with
        | Ok w -> w
        | Error error -> raise (InvalidOperationException error.Message)

    // Claim interactive stdin once, but do not wait here for a `Command.Stdin` source feeder. The
    // session constructor must return even when a child is not reading and the feeder is blocked; the
    // send and close verbs await this task before touching the pipe, preserving the single-writer rule.
    let stdin: Task<ProcessStdin option> = running.TakeStdinAsync()

    let expectCore
        (matcher: string -> (int * int) option)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<ExpectMatch, ProcessError>> =
        PtyExpectLoop.run window program config.TimeProvider matcher timeout cancellationToken

    let sendBytes (bytes: byte[]) (cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        task {
            try
                let! claimed = stdin.WaitAsync cancellationToken

                match claimed with
                | None ->
                    return
                        Error(
                            ProcessError.Unsupported
                                "Send (this run has no interactive stdin - build the command with Command.KeepStdinOpen)"
                        )
                | Some pipe ->
                    do! pipe.WriteAsync(bytes, cancellationToken)
                    // Flush explicitly: an unflushed write can sit in a buffered stdin stream while the
                    // child waits for the very input this call was supposed to deliver - a deadlock the
                    // session's own per-pattern timeout would then report as a spurious `NotReady`.
                    do! pipe.FlushAsync cancellationToken
                    return Ok()
            with
            | :? OperationCanceledException -> return Error(ProcessError.Cancelled program)
            | :? IOException as ex ->
                // The child closed its end (it exited, or stopped reading) - an honest typed failure,
                // never a silently dropped write.
                return Error(ProcessError.Io ex.Message)
            | :? ObjectDisposedException as ex -> return Error(ProcessError.Io ex.Message)
        }

    /// A raw-output session over `running` with explicit tuning. ANSI/VT escape sequences remain in
    /// pattern matching, `Pending`, and `Transcript`; use `WithAnsiFiltering` to remove them.
    new(running: RunningProcess, options: PtySessionOptions) = PtySession(running, options, false)

    /// A raw-output session over `running` with the default tuning (`PtySessionOptions.Default`).
    new(running: RunningProcess) = PtySession(running, PtySessionOptions.Default, false)

    /// Create a session that removes ANSI/VT escape sequences before matching or retaining terminal
    /// output. Filtering is incremental across read boundaries and covers CSI sequences, OSC strings
    /// terminated by BEL or ST, and single ESC forms. Byte-exact output tees remain unfiltered.
    static member WithAnsiFiltering(running: RunningProcess, options: PtySessionOptions) =
        PtySession(running, options, true)

    /// Create an ANSI-filtered session with the default tuning (`PtySessionOptions.Default`).
    static member WithAnsiFiltering(running: RunningProcess) =
        PtySession(running, PtySessionOptions.Default, true)

    /// Wait until `pattern` appears in the child's terminal output, or fail with `NotReady` after
    /// `timeout` — a budget for THIS pattern alone, entirely separate from the run-wide
    /// `Command.Timeout`/`IdleTimeout` (which kill the child; this one does not). `Cancelled` if
    /// `cancellationToken` fires first.
    ///
    /// The match is an ordinal substring search over the unframed session view (raw by default,
    /// control-free from `WithAnsiFiltering`), so a prompt that never ends its line (`Password: `) is
    /// found the moment it arrives. Note that a terminal ends its own lines with `\r\n`: match on the
    /// prompt text itself rather than on a trailing `\n`. Everything up to and including the match is
    /// consumed, so the next call starts after it; a pattern that has not arrived by the deadline leaves
    /// the window untouched, so a longer retry still sees what did arrive. `pattern` must be non-null;
    /// an empty pattern returns a typed `Unsupported` result because it cannot advance the session
    /// window.
    ///
    /// Ends promptly with `NotReady` if the child's output ends first (it exited, or closed the
    /// terminal) rather than waiting out the whole `timeout`, and with `ProcessError.Io` if reading it
    /// genuinely failed.
    member _.ExpectAsync
        (pattern: string, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<ExpectMatch, ProcessError>> =
        ArgumentNullException.ThrowIfNull(pattern, nameof pattern)

        if pattern.Length = 0 then
            Task.FromResult PtyExpectLoop.invalidPatternError
        else
            expectCore
                (fun text ->
                    match text.IndexOf(pattern, StringComparison.Ordinal) with
                    | -1 -> None
                    | index -> Some(index, pattern.Length))
                timeout
                cancellationToken

    /// Wait until `pattern` matches somewhere in the child's terminal output — the regular-expression
    /// form of the overload above, with the identical timeout, consumption, early-end, and error
    /// contract. The `Regex` is used exactly as given, including its own options and `MatchTimeout`.
    ///
    /// Matching runs over the same sliding, unframed session view as the string overload, so `^`/`$`
    /// anchor against the window's bounds rather than the child's line structure unless you pass
    /// `RegexOptions.Multiline`. Matching runs over an immutable window snapshot without blocking new
    /// output. The snapshot is consumed only if it is still current, so concurrent expects cannot both
    /// consume one match. A `RegexMatchTimeoutException` becomes `NotReady` with the regex's own
    /// `MatchTimeout`, and any other matcher exception becomes `Io`; neither faults the returned task.
    /// If caller cancellation or the pattern deadline fires while matching a snapshot, that limit wins
    /// before a stale snapshot is retried or a matcher failure is classified. A successful current
    /// match still wins and is consumed atomically.
    /// A zero-width match is rejected with a typed `Unsupported` result, because it cannot advance the
    /// session window.
    member _.ExpectAsync
        (pattern: Regex, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<ExpectMatch, ProcessError>> =
        ArgumentNullException.ThrowIfNull(pattern, nameof pattern)

        expectCore
            (fun text ->
                let m = pattern.Match text
                if m.Success then Some(m.Index, m.Length) else None)
            timeout
            cancellationToken

    /// Send `text` to the child exactly as given — no line ending appended — encoded with the
    /// command's terminal encoding (`Command.StdoutEncoding`, UTF-8 by default). Use it for a control
    /// character (U+0003 is Ctrl+C to a terminal) or to answer a prompt that reads a single
    /// keystroke rather than a line. On Windows ConPTY, U+0003 does not interrupt the child by default:
    /// ProcessKit's unconditional `CREATE_NEW_PROCESS_GROUP` isolation disables default CTRL+C handling.
    ///
    /// Returns a typed `Unsupported` when the run has no interactive stdin (build the command with
    /// `Command.KeepStdinOpen`), `Cancelled` if `cancellationToken` fires, and `Io` if the child has
    /// closed its input — never a silently dropped write. With `Command.Stdin(source)` plus
    /// `Command.KeepStdinOpen`, the source feeder is awaited here before the first interactive byte;
    /// construction itself does not wait for that source. As with any cancellable stream write, a
    /// cancelled send may already have delivered *some* of its bytes, so recover by abandoning the
    /// conversation rather than resending (which would duplicate the delivered prefix). Sent bytes are
    /// never logged, traced, or added to `Transcript`; a terminal with echo on will nevertheless reflect
    /// them back into the child's output (see the type-level secret-safety note).
    member _.SendAsync
        (text: string, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        sendBytes (encoding.GetBytes text) cancellationToken

    /// Send `text` followed by the session's line ending (`PtySessionOptions.LineEnding`, by default a
    /// carriage return on a PTY run — what a terminal sends for Enter — and a line feed otherwise).
    /// The answer to a prompt. Same encoding, refusal, and secret-safety contract as `SendAsync`.
    member _.SendLineAsync
        (text: string, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(text, nameof text)
        sendBytes (encoding.GetBytes(text + lineEnding)) cancellationToken

    /// Close the child's stdin, so it sees end-of-file — how a conversation that feeds input ends for a
    /// child that reads until EOF. Idempotent; returns the same typed `Unsupported` as the send verbs
    /// when the run has no interactive stdin. A `Command.Stdin` source is awaited before closing so its
    /// bytes cannot be truncated by the interactive close. `cancellationToken` bounds only that
    /// pre-delivery wait; once stdin is claimed, end-of-input delivery is not cancellable.
    ///
    /// On a **PTY** there is no stdin pipe to close — input goes into the same terminal the conversation's
    /// output comes from — so the end of input is delivered as that terminal's own end-of-input gesture
    /// instead (see `ProcessStdin.FinishAsync`): on POSIX the pty's configured end-of-input character
    /// (`termios.c_cc[VEOF]`, Ctrl-D on a default terminal), on Windows the console's Ctrl-Z followed by
    /// Enter. The terminal itself stays open either way, so the child keeps its output — and, on Windows,
    /// its console session — for the rest of the run. Being a gesture the terminal interprets, it ends the
    /// input only of a child still reading in cooked mode (POSIX canonical mode, or a Windows console its
    /// own `CONIN$` mode has not switched to raw); one that reads its terminal raw receives those bytes as
    /// ordinary input, which is the terminal's contract rather than something this verb can paper over.
    ///
    /// A genuine failure to deliver it is a typed `Io` — never a silently dropped close that would leave the
    /// child waiting — while a child that has already closed its terminal is reported as the `Ok` it is:
    /// there is nothing left to tell it.
    member _.CloseStdinAsync(cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        task {
            try
                let! claimed = stdin.WaitAsync cancellationToken

                match claimed with
                | None ->
                    return
                        Error(
                            ProcessError.Unsupported
                                "CloseStdin (this run has no interactive stdin - build the command with Command.KeepStdinOpen)"
                        )
                | Some pipe ->
                    do! pipe.FinishAsync()
                    return Ok()
            with
            | :? IOException as ex ->
                // The end of input could not be delivered (the terminal refused the write) - the same
                // honest typed failure the send verbs report, never a close that silently did nothing.
                return Error(ProcessError.Io ex.Message)
            | :? ObjectDisposedException as ex -> return Error(ProcessError.Io ex.Message)
            | :? OperationCanceledException -> return Error(ProcessError.Cancelled program)
        }

    /// Close the child's stdin without cancellation. Equivalent to `CloseStdinAsync(CancellationToken.None)`.
    member this.CloseStdinAsync() : Task<Result<unit, ProcessError>> =
        this.CloseStdinAsync(CancellationToken.None)

    /// The output received but not yet consumed by a pattern — what the next `ExpectAsync` will match
    /// against. Reading it consumes nothing; it is for diagnosing a pattern that did not arrive
    /// ("what did the child actually print?") without having to keep a transcript.
    member _.Pending = window.Pending

    /// Everything the child has emitted this session, in order — empty when `CaptureTranscript` is off,
    /// and holding at most `TranscriptChars` characters (oldest dropped first, see
    /// `TranscriptTruncated`). Readable at any point, during the conversation as well as after it.
    /// Contains only the child's output; input sent through this session is never recorded here,
    /// though a terminal with echo on reflects it into the child's output anyway.
    member _.Transcript = window.Transcript

    /// Whether the transcript has already dropped output to stay within `TranscriptChars` — so a
    /// diagnosis reading `Transcript` knows it is looking at the tail of the session, not all of it.
    member _.TranscriptTruncated = window.TranscriptTruncated

    /// Whether the match window has already dropped output to stay within `WindowChars` — a pattern
    /// expecting context that far back can no longer match, and `ExpectMatch.Before` is correspondingly
    /// incomplete.
    member _.WindowTruncated = window.WindowTruncated

    /// Wait for the child to exit and for this session's readers to finish draining its terminal, then
    /// report how it concluded. A non-zero or killed exit is data, not a raised error.
    ///
    /// Does **not** reap: dispose the `RunningProcess` (or its owning `ProcessGroup`) for that, exactly
    /// as after `OutputEventsAsync`. It shares this handle's one exit wait, so it never starts a second
    /// wait racing the session's own readers.
    member _.WaitForExitAsync() : Task<Outcome> = running.ExitTask
