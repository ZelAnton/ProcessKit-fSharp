namespace ProcessKit

open System
open System.Text

/// The one sanitize-and-bound rule every caller-, child-, or peer-controlled fragment goes through
/// before it reaches `ProcessError.Message` (and from there `ProcessError.ToString()`,
/// `ProcessException.Message`, and whatever log line or terminal the consumer prints them to).
///
/// A failure's text routinely carries bytes ProcessKit did not author: a hostile child's stderr, a
/// JSON-RPC peer's own error text, a caller's parser message, an OS error string built around a
/// caller-supplied path. Printed verbatim, those can repaint an operator's terminal with ANSI escapes,
/// ring the bell, split one log record into several with `CR`/`LF`/`U+2028`, or visually reorder the
/// surrounding text with bidirectional-formatting controls (the "Trojan Source" class, CVE-2021-42574)
/// — and an unbounded stream or unparsed dump can flood the log with a single message. This module is
/// the single choke point that neutralizes all of that, so the rule cannot drift between error cases.
///
/// It bounds only the human-readable render. The structured fields (`Detail`, `Stdout`, `Stderr`,
/// `Data`, `Original`) and their accessors keep the caller's bytes exactly as captured — a consumer
/// that wants the full text still has it.
module internal MessageText =

    /// The character budget for ONE embedded fragment of a message. Chosen to clear the longest text
    /// this library writes *by hand* — its wordiest literal diagnostic runs to roughly 400 characters —
    /// while turning a 100 KB stderr line or unparsed dump into a small, stable preview. A message
    /// embeds at most a handful of fragments, so the whole render stays bounded too.
    ///
    /// It is deliberately **not** a promise that every fragment survives whole. A fragment carrying text
    /// this library did not author is cut whenever it exceeds the budget — that is the point — and so is
    /// a *composite* detail the library builds around such text (an OS or exception message, a nested
    /// error's own render; `Supervisor`'s callback failures compose one). A fragment is previewed from
    /// its start, so compose such a detail with its short, actionable part first and the foreign text
    /// last, and the cut falls on the least useful end. Nothing is lost either way: the untruncated text
    /// stays on the structured field the fragment came from. Raising this budget to keep some particular
    /// fragment whole is therefore the wrong lever — order or bound that fragment where it is composed.
    [<Literal>]
    let MaxFragmentChars = 512

    /// Appended when a fragment is cut at `MaxFragmentChars`, so a truncated preview is never mistaken
    /// for the whole text. It is the only character a preview can carry beyond the budget.
    [<Literal>]
    let private Ellipsis = '…'

    /// What an unsafe character becomes: U+FFFD REPLACEMENT CHARACTER — visible, inert, and the
    /// conventional "something was here that could not be represented" marker.
    [<Literal>]
    let private Replacement = '\uFFFD'

    /// Whether `c` must never be emitted verbatim into a one-line log or terminal.
    ///
    /// Control characters (`ESC` and its ANSI sequences, `BEL`, `NUL`, `CR`, cursor moves, …), the
    /// Unicode line/paragraph separators a terminal or log viewer renders as a newline, and the
    /// bidirectional-formatting controls that can visually reorder the text around them.
    ///
    /// **TAB is deliberately exempt**: it is a legitimate column separator in ordinary tool output
    /// (TSV, `git diff`, `ls -l`), it cannot start an escape sequence or reorder text, and mangling it
    /// would corrupt normal output for no security gain.
    let isDisplayUnsafe (c: char) : bool =
        (Char.IsControl c && c <> '\t')
        || c = '\u2028' // LINE SEPARATOR      — not a control character, still breaks the line
        || c = '\u2029' // PARAGRAPH SEPARATOR — likewise
        || c = '\u061C' // ARABIC LETTER MARK
        || c = '\u200E' // LEFT-TO-RIGHT MARK
        || c = '\u200F' // RIGHT-TO-LEFT MARK
        || (c >= '\u202A' && c <= '\u202E') // LRE RLE PDF LRO RLO (embeddings/overrides)
        || (c >= '\u2066' && c <= '\u2069') // LRI RLI FSI PDI (isolates)

    /// The line terminators `String.ReplaceLineEndings` recognizes: `LF`, `CR`, `FF`, `NEL`, `LS`, `PS`.
    /// Used to split a captured stream into lines; every one of them is also display-unsafe, so any that
    /// survives inside a line (a lone `CR` between progress redraws, say) is neutralized by the preview.
    let private isLineBreak (c: char) =
        c = '\n'
        || c = '\r'
        || c = '\u000C'
        || c = '\u0085'
        || c = '\u2028'
        || c = '\u2029'

    /// Sanitize `text[first..last - 1]` (`last` exclusive) into a bounded single-line preview.
    ///
    /// Text that is already display-safe and inside the budget is returned unchanged — the common case
    /// of an ordinary program name or a short OS error string pays nothing and reads exactly as before.
    /// Otherwise each unsafe character becomes `Replacement`, and the copy stops at `MaxFragmentChars`
    /// with an `Ellipsis`. A surrogate pair is copied whole or not at all, so the cap can never split
    /// one (a lone surrogate is not a character and becomes `Replacement`, keeping the preview
    /// well-formed text).
    let private previewRange (text: string) (first: int) (last: int) : string =
        let length = last - first
        let mutable index = first
        let mutable verbatim = length <= MaxFragmentChars

        while verbatim && index < last do
            if isDisplayUnsafe text[index] || Char.IsSurrogate text[index] then
                verbatim <- false
            else
                index <- index + 1

        if verbatim then
            text.Substring(first, length)
        else
            let builder = StringBuilder(min length MaxFragmentChars + 1)
            let mutable index = first
            let mutable truncated = false

            while not truncated && index < last do
                let isPair =
                    Char.IsHighSurrogate text[index]
                    && index + 1 < last
                    && Char.IsLowSurrogate text[index + 1]

                let width = if isPair then 2 else 1

                if builder.Length + width > MaxFragmentChars then
                    truncated <- true
                elif isPair then
                    builder.Append(text[index]).Append(text[index + 1]) |> ignore
                    index <- index + 2
                else
                    let c = text[index]

                    builder.Append(
                        if isDisplayUnsafe c || Char.IsSurrogate c then
                            Replacement
                        else
                            c
                    )
                    |> ignore

                    index <- index + 1

            if truncated then
                builder.Append Ellipsis |> ignore

            builder.ToString()

    /// A whole fragment (a program name, a detail, a searched path, a method name) as a bounded,
    /// display-safe preview. `null`/empty renders as the empty string rather than throwing — every
    /// `ProcessError` case is constructible from C#, where any of these fields can arrive `null`.
    let fragment (text: string) : string =
        if String.IsNullOrEmpty text then
            ""
        else
            previewRange text 0 text.Length

    /// How a probed search path is *reported* in a message: **how many entries it held**, never the
    /// entries themselves — `"84 PATH entries"`, `"1 PATH entry"`, `"an empty PATH"`.
    ///
    /// `NotFound`'s `Searched` is a whole `PATH` value (`Native.Common.resolveWith`, the one resolution
    /// every lookup goes through, passes the effective `PATH` verbatim). Quoting it would put an
    /// environment value — several
    /// thousand characters of it on an ordinary developer machine — into every "not found" log line;
    /// quoting a 512-character prefix of it would be worse, an arbitrary slice that still reads like the
    /// search path. The count is the part of it a one-line message can honestly carry: it separates "the
    /// `PATH` I gave this command was empty/short" from "it had 84 entries and none of them had the
    /// program", which is the question a not-found failure actually raises. The value itself stays on
    /// the `Searched` field, in full, for a caller that wants to print or diff it.
    ///
    /// Entries are counted the way the resolver splits that value — on `Path.PathSeparator`, empty
    /// entries dropped — so the number is how many `PATH` entries the lookup walked. It is not the total
    /// number of directories probed, and `Searched` never was either: a bare name is tried against
    /// `Command.PreferLocal` first, and on Windows against the pre-`PATH` locations `CreateProcessW`
    /// searches. Counted by index rather than by `Split`, so a pathologically long value is never
    /// materialized into an array.
    let searchedPath (searched: string) : string =
        let mutable entries = 0

        if not (String.IsNullOrEmpty searched) then
            let mutable index = 0
            let mutable insideEntry = false

            while index < searched.Length do
                if searched[index] = IO.Path.PathSeparator then
                    insideEntry <- false
                elif not insideEntry then
                    insideEntry <- true
                    entries <- entries + 1

                index <- index + 1

        match entries with
        | 0 -> "an empty PATH"
        | 1 -> "1 PATH entry"
        | count -> $"{count} PATH entries"

    /// The **last non-blank line** of a captured stream, trimmed and previewed — the actionable one
    /// (`git push` ends with `remote: permission denied`, it does not start with it) — or `None` when
    /// the stream holds nothing worth quoting. The line is located and trimmed by index, so a
    /// single-line 100 KB stream is never materialized just to be capped.
    let lastLine (text: string) : string option =
        if String.IsNullOrEmpty text then
            None
        else
            let mutable remaining = text.Length
            let mutable result = None

            while result.IsNone && remaining > 0 do
                // Walk back over the separators that ended the previous segment, then to this one's start.
                let mutable segmentEnd = remaining

                while segmentEnd > 0 && isLineBreak text[segmentEnd - 1] do
                    segmentEnd <- segmentEnd - 1

                let mutable segmentStart = segmentEnd

                while segmentStart > 0 && not (isLineBreak text[segmentStart - 1]) do
                    segmentStart <- segmentStart - 1

                if segmentEnd = 0 then
                    remaining <- 0 // nothing but separators left
                else
                    let mutable trimStart = segmentStart
                    let mutable trimEnd = segmentEnd

                    while trimStart < trimEnd && Char.IsWhiteSpace text[trimStart] do
                        trimStart <- trimStart + 1

                    while trimEnd > trimStart && Char.IsWhiteSpace text[trimEnd - 1] do
                        trimEnd <- trimEnd - 1

                    if trimEnd > trimStart then
                        result <- Some(previewRange text trimStart trimEnd)
                    else
                        remaining <- segmentStart // a blank line: keep looking further back

            result

    /// The `: <last non-blank line>` a stream-carrying failure (`Exit`/`Signalled`/`Timeout`) appends to
    /// its one-line message, or `""` when the stream has nothing to quote. One rule for all three, so
    /// none of them can grow back into a full multi-line dump.
    ///
    /// The quoted stream is `Stderr` — the same stream `Exit` has always quoted; `Stdout` deliberately
    /// stays out of the human-readable render (it is on the `Stdout` accessor in full).
    let diagnosticTail (stderr: string) : string =
        match lastLine stderr with
        | Some line -> ": " + line
        | None -> ""

/// Structured failure type for ProcessKit operations.
///
/// Named `ProcessError` (rather than just `Error`) to avoid colliding with the `Result.Error`
/// constructor in F#. Honest-result verbs — `outputString`, `outputBytes`, `exitCode`, `probe` —
/// return their value; only genuine failures surface as a `ProcessError` in the `Result` channel.
[<RequireQualifiedAccess; NoComparison>]
type ProcessError =

    /// The process could not be spawned (a failure before or during launch).
    | Spawn of Program: string * Detail: string

    /// The program could not be found. `Searched` is the search path that was probed, when known — the
    /// whole `PATH` value the lookup walked, `None` when no `PATH` search applied (a path-form program
    /// is resolved against its own directory instead).
    ///
    /// `Message` reports only **how many entries** that path held, not the entries: a `PATH` is an
    /// environment value, and a several-thousand-character one has no place in a log line. Read this
    /// field when you want to name the directories that were searched.
    | NotFound of Program: string * Searched: string option

    /// A success-requiring verb (`run`) observed a non-zero exit code.
    | Exit of Program: string * Code: int * Stdout: string * Stderr: string

    /// The process was terminated by a signal (Unix) or otherwise killed without a code.
    | Signalled of Program: string * Signal: int option * Stdout: string * Stderr: string

    /// The run exceeded its configured timeout.
    | Timeout of Program: string * Timeout: TimeSpan * Stdout: string * Stderr: string

    /// The process concluded but its actual exit status could not be observed (see
    /// `Outcome.Unobserved`) — a native API failure or an unresolved POSIX reap race. `Detail` carries
    /// the reason. Always a failure; never fabricated as a clean exit.
    | Unobserved of Program: string * Detail: string

    /// The run was cancelled through its `CancellationToken`. A cancellation is always an error.
    ///
    /// One further producer exists, and it is not a token: a supervision session
    /// (`SupervisionSession.StopAsync` / `Supervisor`) whose graceful stop landed before its very first
    /// incarnation was ever started ends here, because it has neither a `SupervisionOutcome` to report
    /// nor a failure that kept the child from starting — and it will not launch a child just to
    /// manufacture one. A stop that lands any later reports the honest result of the incarnation it
    /// stopped, or the honest failure of the ones that never started. Code that must tell "my token
    /// fired" apart from "I asked for a graceful stop" cannot rely on this case alone; on a session, ask
    /// the token (`CancellationToken.IsCancellationRequested`).
    | Cancelled of Program: string

    /// A readiness probe (`WaitForLineAsync` / `WaitForPortAsync` / `WaitForHttpAsync` / `WaitForAsync`) did not succeed within its timeout.
    | NotReady of Program: string * Timeout: TimeSpan

    /// Parsing the captured output into a typed value failed.
    | Parse of Program: string * Detail: string

    /// The retry predicate threw while classifying a failed attempt. `Original` is the typed failure
    /// from that attempt; this error is terminal and is never itself eligible for another retry.
    | RetryPredicate of Program: string * Original: ProcessError * Detail: string

    /// A JSON-RPC peer (`JsonRpcSession`) answered a request with an `error` object instead of a
    /// `result` — the protocol's own way of saying "I understood you and I refuse", so it is a failure
    /// of that call, never a successful result. `Method` is the request it answers, `Code` and `Detail`
    /// are the peer's own `code`/`message` (`-32601` "method not found", `-32602` "invalid params", and
    /// the rest of the reserved range are conventional), and `Data` is the raw JSON text of the
    /// optional `data` member when the peer attached one. A transport failure of the same call — a
    /// truncated frame, the peer's output ending, a timeout — is reported by its own case
    /// (`Parse`/`Io`/`Timeout`), never folded in here.
    | JsonRpc of Program: string * Method: string * Code: int * Detail: string * Data: string option

    /// Captured or streamed output exceeded a configured fail-loud ceiling. Metrics are populated only
    /// when their unit applies to that channel (lines, bytes, merged events, or protocol frames).
    | OutputTooLarge of
        Program: string *
        LineLimit: int option *
        ByteLimit: int option *
        TotalLines: int *
        TotalBytes: int

    /// The child's stdin source could not be read (e.g. a missing `FromFile` path) on an otherwise-
    /// successful run. A routine broken pipe — the child closed stdin early — is never reported here.
    | Stdin of Program: string * Detail: string

    /// A `ResourceLimits` cap was requested but could not be enforced — the platform has no
    /// whole-tree limit primitive (macOS / the Linux process-group fallback), or the Linux cgroup v2
    /// controllers could not be enabled (this process is not at the real cgroup root).
    | ResourceLimit of Detail: string

    /// An external process could not be adopted into a `ProcessGroup` (`ProcessGroup.Adopt`). This is
    /// the honest, typed refusal for a *runtime* adoption failure of a specific process — as opposed to
    /// `Unsupported`, which reports a mechanism that cannot adopt at all (the POSIX process group). The
    /// distinguishable causes all live in `Detail`: the target had already exited or its pid does not
    /// exist (a TOCTOU race — never a silent success), the caller lacks the rights to place a foreign
    /// process into the container, or the process is already assigned to a Job that does not permit
    /// nesting on this Windows configuration. `Pid` is the target's pid, or `0` when no live process was
    /// associated with the argument.
    | Adopt of Pid: int * Detail: string

    /// A `RecordReplayRunner` in replay mode found no recorded entry matching the invocation.
    | CassetteMiss of Program: string

    /// An underlying I/O failure not attributable to a specific exit.
    | Io of Detail: string

    /// The requested operation is unsupported on this platform or in this configuration.
    | Unsupported of Operation: string

    /// A short, human-readable description for logs and diagnostics — always **one line**, and always
    /// bounded.
    ///
    /// Every fragment this render embeds that ProcessKit did not author — the program name, a captured
    /// stream, a detail, a peer's method name — goes through `MessageText`: terminal and
    /// bidirectional-formatting controls, `CR`/`LF`, and the Unicode line/paragraph separators
    /// become `U+FFFD` (an ordinary TAB is kept), and anything past 512 characters per fragment is cut
    /// with a trailing `…`. So a hostile child, a JSON-RPC peer, or a caller's own parser cannot repaint
    /// an operator's terminal, forge extra log lines, or flood the log through this string — and a
    /// message stays the same small size whether the child wrote 20 bytes of stderr or 100 KB.
    ///
    /// A stream-carrying failure (`Exit`/`Signalled`/`Timeout`) quotes at most the **last non-blank line
    /// of `Stderr`**, never the whole stream. A `NotFound` reports how many entries the `PATH` it
    /// searched held, never the `PATH` itself — an environment value stays out of the message (read
    /// `Searched` for it).
    ///
    /// This is the *render* only. `Detail`, `Stdout`, `Stderr`, `Data`, `Original` and their accessors
    /// still carry the full, unmodified text — read them when you need the whole thing.
    member this.Message =
        match this with
        | ProcessError.Spawn(program, detail) ->
            $"failed to spawn '{MessageText.fragment program}': {MessageText.fragment detail}"
        | ProcessError.NotFound(program, searched) ->
            match searched with
            | Some path ->
                $"program '{MessageText.fragment program}' was not found (searched {MessageText.searchedPath path})"
            | None -> $"program '{MessageText.fragment program}' was not found"
        | ProcessError.Exit(program, code, _, stderr) ->
            $"'{MessageText.fragment program}' exited with code {code}{MessageText.diagnosticTail stderr}"
        | ProcessError.Signalled(program, signal, _, stderr) ->
            let tail = MessageText.diagnosticTail stderr

            match signal with
            | Some s -> $"'{MessageText.fragment program}' was terminated by signal {s}{tail}"
            | None -> $"'{MessageText.fragment program}' was killed{tail}"
        | ProcessError.Timeout(program, timeout, _, stderr) ->
            $"'{MessageText.fragment program}' timed out after {timeout.TotalSeconds}s{MessageText.diagnosticTail stderr}"
        | ProcessError.Unobserved(program, detail) ->
            $"'{MessageText.fragment program}' concluded, but its exit status is unknown: {MessageText.fragment detail}"
        | ProcessError.Cancelled program -> $"'{MessageText.fragment program}' was cancelled"
        | ProcessError.NotReady(program, timeout) ->
            $"'{MessageText.fragment program}' was not ready within {timeout.TotalSeconds}s"
        | ProcessError.Parse(program, detail) ->
            $"failed to parse output of '{MessageText.fragment program}': {MessageText.fragment detail}"
        | ProcessError.RetryPredicate(program, original, detail) ->
            // The nested original is previewed as a whole, so nesting cannot walk around the bound: the
            // inner message is already bounded in its own right, and this caps it again as one fragment.
            $"retry predicate for '{MessageText.fragment program}' threw: {MessageText.fragment detail}; original attempt: {MessageText.fragment original.Message}"
        | ProcessError.JsonRpc(program, methodName, code, detail, _) ->
            let head =
                $"'{MessageText.fragment program}' answered '{MessageText.fragment methodName}' with JSON-RPC error {code}"

            if System.String.IsNullOrEmpty detail then
                head
            else
                $"{head}: {MessageText.fragment detail}"
        | ProcessError.OutputTooLarge(program, lineLimit, byteLimit, totalLines, totalBytes) ->
            let name = MessageText.fragment program

            match lineLimit, byteLimit, totalLines with
            | Some _, _, _ -> $"'{name}' produced too much line output ({totalLines} lines / {totalBytes} bytes)"
            | None, Some _, _ -> $"'{name}' produced too much byte output ({totalBytes} bytes)"
            | None, None, events when events > 0 ->
                $"'{name}' produced too many events ({events} events / {totalBytes} bytes)"
            | None, None, _ -> $"'{name}' produced too much output ({totalBytes} bytes)"
        | ProcessError.Stdin(program, detail) ->
            $"could not read the stdin source for '{MessageText.fragment program}': {MessageText.fragment detail}"
        | ProcessError.ResourceLimit detail -> $"resource limit could not be enforced: {MessageText.fragment detail}"
        | ProcessError.Adopt(pid, detail) ->
            $"could not adopt process {pid} into the group: {MessageText.fragment detail}"
        | ProcessError.CassetteMiss program -> $"no recorded cassette entry for '{MessageText.fragment program}'"
        | ProcessError.Io detail -> $"I/O error: {MessageText.fragment detail}"
        | ProcessError.Unsupported operation -> $"unsupported: {MessageText.fragment operation}"

    override this.ToString() = this.Message

    /// True for errors that may succeed on a retry (a spawn race or transient I/O). The instance
    /// form of `ProcessError.isTransient`, so it reads cleanly from C# as `err.IsTransient` (the
    /// not-found classifier already has the generated `err.IsNotFound` tester).
    member this.IsTransient =
        match this with
        | ProcessError.Spawn _
        | ProcessError.Io _ -> true
        | _ -> false

    // The read-without-destructure accessors below let a consumer read a failure's fields off the base
    // `ProcessError` without pattern-matching each case — the only practical way to do it from C#, which
    // can't destructure an F# union. Each returns the field for the cases that carry it, `None` otherwise.

    /// The program the error is about, when it carries one — `None` for `Adopt` (which carries a pid
    /// rather than a program) / `ResourceLimit` / `Io` / `Unsupported`, which are not tied to a specific
    /// program.
    member this.Program: string option =
        match this with
        | ProcessError.Spawn(program, _)
        | ProcessError.NotFound(program, _)
        | ProcessError.Exit(program, _, _, _)
        | ProcessError.Signalled(program, _, _, _)
        | ProcessError.Timeout(program, _, _, _)
        | ProcessError.Unobserved(program, _)
        | ProcessError.Cancelled program
        | ProcessError.NotReady(program, _)
        | ProcessError.Parse(program, _)
        | ProcessError.RetryPredicate(program, _, _)
        | ProcessError.JsonRpc(program, _, _, _, _)
        | ProcessError.OutputTooLarge(program, _, _, _, _)
        | ProcessError.Stdin(program, _)
        | ProcessError.CassetteMiss program -> Some program
        | ProcessError.Adopt _
        | ProcessError.ResourceLimit _
        | ProcessError.Io _
        | ProcessError.Unsupported _ -> None

    /// The captured stdout when the error carries it (`Exit` / `Signalled` / `Timeout`, or the original
    /// attempt inside `RetryPredicate`); `None` otherwise.
    member this.Stdout: string option =
        match this with
        | ProcessError.Exit(_, _, stdout, _)
        | ProcessError.Signalled(_, _, stdout, _)
        | ProcessError.Timeout(_, _, stdout, _) -> Some stdout
        | ProcessError.RetryPredicate(_, original, _) -> original.Stdout
        | _ -> None

    /// The captured stderr when the error carries it (`Exit` / `Signalled` / `Timeout`, or the original
    /// attempt inside `RetryPredicate`); `None` otherwise.
    member this.Stderr: string option =
        match this with
        | ProcessError.Exit(_, _, _, stderr)
        | ProcessError.Signalled(_, _, _, stderr)
        | ProcessError.Timeout(_, _, _, stderr) -> Some stderr
        | ProcessError.RetryPredicate(_, original, _) -> original.Stderr
        | _ -> None

    /// The captured stdout and stderr joined (stdout, then stderr on a new line when both are non-empty)
    /// for the stream-carrying cases (`Exit` / `Signalled` / `Timeout`, or the original attempt inside
    /// `RetryPredicate`); `None` otherwise.
    member this.Combined: string option =
        match this with
        | ProcessError.Exit(_, _, stdout, stderr)
        | ProcessError.Signalled(_, _, stdout, stderr)
        | ProcessError.Timeout(_, _, stdout, stderr) -> Some(ProcessError.CombineStreams(stdout, stderr))
        | ProcessError.RetryPredicate(_, original, _) -> original.Combined
        | _ -> None

    /// The exit code when the error is an `Exit`, or when its `RetryPredicate` original is an `Exit`;
    /// `None` otherwise (a signal kill or timeout has none).
    member this.Code: int option =
        match this with
        | ProcessError.Exit(_, code, _, _) -> Some code
        | ProcessError.RetryPredicate(_, original, _) -> original.Code
        | _ -> None

    /// The terminating signal number when the error is a `Signalled` with a known number, or when its
    /// `RetryPredicate` original is one; `None` otherwise.
    member this.Signal: int option =
        match this with
        | ProcessError.Signalled(_, signal, _, _) -> signal
        | ProcessError.RetryPredicate(_, original, _) -> original.Signal
        | _ -> None

    /// The shared combined-output join: both streams non-empty → `stdout` + newline + `stderr`; else the
    /// non-empty one (or `""` when both are empty). One rule for `ProcessError.Combined` and
    /// `ProcessResult.Combined`, so the two views can't drift. Internal.
    static member internal CombineStreams(stdout: string, stderr: string) : string =
        match String.IsNullOrEmpty stdout, String.IsNullOrEmpty stderr with
        | false, false -> stdout + "\n" + stderr
        | false, true -> stdout
        | true, false -> stderr
        | true, true -> ""

[<RequireQualifiedAccess>]
module ProcessError =

    /// True when the error is a program-not-found failure.
    let isNotFound (error: ProcessError) =
        match error with
        | ProcessError.NotFound _ -> true
        | _ -> false

    /// True for errors that may succeed on a retry (spawn races, transient I/O). Delegates to the
    /// instance `ProcessError.IsTransient` so the two never drift.
    let isTransient (error: ProcessError) = error.IsTransient
