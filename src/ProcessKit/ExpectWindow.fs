namespace ProcessKit

open System
open System.Text
open System.Threading.Tasks

/// What one look at an `ExpectWindow` tells a pattern waiter (`PtySession.ExpectAsync`): the pattern
/// matched, nothing matched yet, or nothing matched and no more output can come. Deliberately ONE
/// verdict rather than a matched-then-ended pair of questions — see `ExpectWindow.TryConsume`, which
/// decides all three under a single lock so a match arriving with the end of output can never be lost
/// to it.
[<RequireQualifiedAccess; NoComparison>]
type internal ExpectStep =

    /// The pattern matched: the output that preceded it, and the text it matched. Both are consumed
    /// from the window by the same step that reports them.
    | Matched of before: string * text: string

    /// Nothing matched, and the child's output has not ended — the pattern may still arrive.
    | Waiting

    /// Nothing matched, and the child's output has ended: cleanly (`None`), or with the genuine read
    /// fault that ended it (`Some ex`).
    | Ended of fault: exn option

[<RequireQualifiedAccess>]
type private AnsiFilterState =
    | Text = 0
    | Escape = 1
    | EscapeIntermediate = 2
    | Csi = 3
    | Osc = 4
    | OscEscape = 5
    | ControlString = 6
    | ControlStringEscape = 7

/// Incrementally removes ANSI/VT control sequences from decoded terminal text. The mutable state is
/// deliberately retained between calls because an escape sequence may straddle any read boundary.
/// Visible characters are appended straight from the input span to the caller's builder, avoiding a
/// temporary filtered string on every pump chunk.
type internal AnsiEscapeFilter() =

    let mutable state = AnsiFilterState.Text

    member _.Append(input: ReadOnlySpan<char>, output: StringBuilder) =
        for index = 0 to input.Length - 1 do
            let ch = input[index]

            match state with
            | AnsiFilterState.Text ->
                match ch with
                | '\u001b' -> state <- AnsiFilterState.Escape
                | '\u009b' -> state <- AnsiFilterState.Csi
                | '\u009d' -> state <- AnsiFilterState.Osc
                | '\u0090'
                | '\u0098'
                | '\u009e'
                | '\u009f' -> state <- AnsiFilterState.ControlString
                | '\u009c' -> ()
                | _ -> output.Append ch |> ignore
            | AnsiFilterState.Escape ->
                match ch with
                | '[' -> state <- AnsiFilterState.Csi
                | ']' -> state <- AnsiFilterState.Osc
                | 'P'
                | 'X'
                | '^'
                | '_' -> state <- AnsiFilterState.ControlString
                | '\u001b' -> state <- AnsiFilterState.Escape
                | c when c >= '\u0020' && c <= '\u002f' -> state <- AnsiFilterState.EscapeIntermediate
                | c when c >= '\u0030' && c <= '\u007e' -> state <- AnsiFilterState.Text
                | _ ->
                    state <- AnsiFilterState.Text
                    output.Append ch |> ignore
            | AnsiFilterState.EscapeIntermediate ->
                match ch with
                | '\u001b' -> state <- AnsiFilterState.Escape
                | c when c >= '\u0020' && c <= '\u002f' -> ()
                | c when c >= '\u0030' && c <= '\u007e' -> state <- AnsiFilterState.Text
                | _ ->
                    state <- AnsiFilterState.Text
                    output.Append ch |> ignore
            | AnsiFilterState.Csi ->
                match ch with
                | '\u001b' -> state <- AnsiFilterState.Escape
                | c when c >= '\u0040' && c <= '\u007e' -> state <- AnsiFilterState.Text
                | _ -> ()
            | AnsiFilterState.Osc ->
                match ch with
                | '\u0007'
                | '\u009c' -> state <- AnsiFilterState.Text
                | '\u001b' -> state <- AnsiFilterState.OscEscape
                | _ -> ()
            | AnsiFilterState.OscEscape ->
                match ch with
                | '\\' -> state <- AnsiFilterState.Text
                | '\u001b' -> ()
                | '\u009c' -> state <- AnsiFilterState.Text
                | _ -> state <- AnsiFilterState.Osc
            | AnsiFilterState.ControlString ->
                match ch with
                | '\u009c' -> state <- AnsiFilterState.Text
                | '\u001b' -> state <- AnsiFilterState.ControlStringEscape
                | _ -> ()
            | AnsiFilterState.ControlStringEscape ->
                match ch with
                | '\\'
                | '\u009c' -> state <- AnsiFilterState.Text
                | '\u001b' -> ()
                | _ -> state <- AnsiFilterState.ControlString
            | _ -> invalidOp "Unknown ANSI filter state"

/// The shared state behind an interactive expect-style session (`PtySession`): a bounded sliding
/// window of the child's merged output that pattern waits are matched against, plus the optional
/// session transcript. The view is raw by default and ANSI-filtered for the explicit filtered-session
/// factory. It is always unframed — a terminal prompt (`Password: `, `> `) carries no line terminator,
/// so the line pumps' framing is exactly what a pattern waiter must NOT go through.
///
/// Filled by the session's raw pumps (`Pump.readTextUntilDone`, one per piped stream) and drained by
/// `TryConsume` on the caller's thread, so every operation runs under one `gate` — including a
/// waiter's whole verdict, which `TryConsume` answers as a single `ExpectStep` rather than as
/// separately locked questions a producer could slip between. Waiters are woken
/// through `Changed`, a `TaskCompletionSource` replaced on every publish: capture it BEFORE testing
/// the window, and an append landing between the capture and the test either shows up in that test or
/// completes the captured task, so a wake-up can never be lost. Continuations run asynchronously, so
/// no waiter's continuation executes while `gate` is held.
///
/// Both buffers are bounded, and both bound by DROPPING THE OLDEST text — the tail is what an expect
/// script is about to match, and what a failed session's diagnosis needs. Neither ever grows past its
/// cap, so a chatty child cannot turn a long-lived session into an unbounded memory leak; a pattern
/// that needs more context than `maxWindowChars` simply cannot match, which `PtySession` documents
/// rather than papering over.
type internal ExpectWindow(maxWindowChars: int, maxTranscriptChars: int option) =

    let gate = obj ()
    let window = StringBuilder()
    let transcript = StringBuilder()
    let mutable transcriptTruncated = false
    let mutable windowTruncated = false
    let mutable completed = false
    let mutable readFault: exn option = None

    // Completed (and replaced) on every append and once the readers finish. `RunContinuationsAsynchronously`
    // keeps a woken waiter's continuation off this thread, so it never runs while `gate` is held below.
    let mutable changed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    // Callers hold `gate`.
    let publish () =
        let current = changed
        changed <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        current.TrySetResult() |> ignore

    /// Completes the next time the window changes — an append, or the readers finishing. Capture this
    /// BEFORE testing the window, then await it, so no change is missed between the two.
    member _.Changed: Task = lock gate (fun () -> changed.Task)

    /// Append freshly decoded child output, evicting the oldest text past either cap.
    member _.Append(text: string) =
        if text.Length > 0 then
            lock gate (fun () ->
                window.Append text |> ignore

                if window.Length > maxWindowChars then
                    window.Remove(0, window.Length - maxWindowChars) |> ignore
                    windowTruncated <- true

                match maxTranscriptChars with
                | Some cap ->
                    transcript.Append text |> ignore

                    if transcript.Length > cap then
                        transcript.Remove(0, transcript.Length - cap) |> ignore
                        transcriptTruncated <- true
                | None -> ()

                publish ())

    /// Filter a freshly decoded chunk straight into both bounded buffers. Copying the newly appended
    /// range between builders avoids allocating a second, cleaned string for each raw pump read.
    member _.AppendFiltered(filter: AnsiEscapeFilter, text: string) =
        if text.Length > 0 then
            lock gate (fun () ->
                let start = window.Length
                filter.Append(text.AsSpan(), window)
                let appended = window.Length - start

                if appended > 0 then
                    match maxTranscriptChars with
                    | Some cap ->
                        transcript.Append(window, start, appended) |> ignore

                        if transcript.Length > cap then
                            transcript.Remove(0, transcript.Length - cap) |> ignore
                            transcriptTruncated <- true
                    | None -> ()

                    if window.Length > maxWindowChars then
                        window.Remove(0, window.Length - maxWindowChars) |> ignore
                        windowTruncated <- true

                    publish ())

    /// Mark the child's output as ended — cleanly (`None`) or with a genuine read fault (`Some ex`) —
    /// and wake every waiter. Idempotent: the first completion wins, so a second reader finishing (or a
    /// late teardown) can't overwrite a genuine fault with a clean end.
    member _.Complete(fault: exn option) =
        lock gate (fun () ->
            if not completed then
                completed <- true
                readFault <- fault
                publish ())

    /// The one step a pattern waiter takes: test `matcher` against the whole current window and, on a
    /// match, CONSUME everything up to and including it (so the next wait starts after this match, and a
    /// prompt is never matched twice), reporting `Matched(before, text)`. `before` is the output that
    /// preceded the match, which the sliding window may already have truncated (see `WindowTruncated`).
    /// With no match, the same step reports whether more output can still come (`Waiting`) or the
    /// readers have already finished (`Ended`, carrying any genuine read fault).
    ///
    /// Match and end-of-output are decided under ONE `gate` acquisition, and that is load-bearing: the
    /// last chunk of a conversation and the readers finishing arrive back to back (a child prints its
    /// final answer, then closes the terminal), so a waiter that asked the two questions separately
    /// could be preempted between them and report "ended, no match" for a match already sitting in the
    /// window. A match always wins over the end that came with it — including over a read fault, since
    /// output the child did produce is still honest output.
    member _.TryConsume(matcher: string -> (int * int) option) : ExpectStep =
        lock gate (fun () ->
            let text = window.ToString()

            match matcher text with
            | Some(start, length) ->
                window.Remove(0, start + length) |> ignore
                ExpectStep.Matched(text.Substring(0, start), text.Substring(start, length))
            | None when completed -> ExpectStep.Ended readFault
            | None -> ExpectStep.Waiting)

    /// The buffered output no pattern has consumed yet.
    member _.Pending = lock gate (fun () -> window.ToString())

    /// Everything the child has emitted this session (empty when the transcript is off), oldest text
    /// dropped once the cap is reached.
    member _.Transcript = lock gate (fun () -> transcript.ToString())

    /// Whether the transcript has dropped any output to stay within its cap.
    member _.TranscriptTruncated = lock gate (fun () -> transcriptTruncated)

    /// Whether the match window has dropped any output to stay within its cap.
    member _.WindowTruncated = lock gate (fun () -> windowTruncated)
