namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The closures and state a `RunningProcess` is built from. Internal — `ProcessGroup.StartAsync`
/// constructs it, so `RunningProcess` need not reference `ProcessGroup` (no compile cycle).
type internal RunningHost =
    {
        Config: CommandConfig
        Pid: int option
        Stdout: Stream option
        Stderr: Stream option
        Stdin: Stream option
        StartTime: DateTime
        StartedTimestamp: int64
        /// The child's own OS-reported creation time (`Process.GetProcessById(pid).StartTime`),
        /// captured once right after spawn — the identity token `processMetrics` (T-097) re-checks
        /// before trusting a later `Process.GetProcessById pid` read, so a pid the OS recycled for an
        /// unrelated process after the child was reaped can't be mistaken for it. `None` when the pid
        /// is unknown, the identity read failed at spawn time (the child already exited, or the
        /// platform/timing raced it), or a synthetic host (tests/fakes) has no real process behind the
        /// pid — an unknown token is never proof either way, so the gate defers to the raw read exactly
        /// like the POSIX pgid identity check (`Native.Posix.processGroupStillTracked`) already does.
        StartTimeIdentity: DateTime option
        /// Wait for the process to exit and report how it concluded.
        Wait: unit -> Task<Outcome>
        /// The BOUNDED FINAL observation of the background stdin feeder's genuine source failure, made at
        /// the one moment a Result-producing verb decides an otherwise-successful run's result (the child
        /// exited with an accepted code and the output drains have finished). A feed that already finished
        /// answers immediately; a feed still reading its source gets a bounded window to conclude — so a
        /// slow source that only fails AFTER a fast child exited is reported as the real cause instead of
        /// being torn down unread — and is stopped, not awaited, once that window runs out. Never blocks
        /// past the budget, and never faults. A synthetic host with no feed to observe uses
        /// `RunningHost.NoStdinError`.
        StdinError: unit -> Task<exn option>
        /// Block until the background stdin feeder has finished draining the source, so `TakeStdin` never
        /// hands the caller a stream the feeder is still writing to (two concurrent writers on one pipe is
        /// forbidden). A no-op — returns immediately — when the command kept stdin open with **no** source
        /// (interactive from the start) or when there is nothing to feed, so only a `Stdin(source)` +
        /// `KeepStdinOpen` run actually waits. `TakeStdin` calls this OUTSIDE `stateLock`.
        StdinFeedComplete: unit -> unit
        /// Signal the tree to die without waiting (start_kill).
        StartKill: unit -> unit
        /// Deliver a signal to this run's own contained process tree.
        Signal: Signal -> Result<unit, ProcessError>
        /// Gracefully kill the tree (configured soft signal, then SIGKILL after the grace period) without
        /// releasing the container — for timeouts.
        GracefulKill: TimeSpan -> Task
        /// Resize the child's pseudo-terminal to `(cols, rows)` — `Some` only for a `Command.Pty` run
        /// (the retained pseudoconsole handle / pty master fd from spawn), `None` otherwise. Backs
        /// `RunningProcess.ResizeAsync`, which returns a typed `ProcessError.Unsupported` when it is
        /// `None` (a non-PTY run — D6). A pure resize: it never touches the exit-wait/consumption state.
        ResizePty: (int * int -> Result<unit, ProcessError>) option
        /// Whole-tree stats for a run that owns a private containment group. Shared runs leave this
        /// `None`: the group's counters include siblings and must not be attributed to one profile.
        TreeStats: (unit -> ProcessGroupStats option) option
        /// Reap the tree and release the container.
        Teardown: unit -> ValueTask
    }

    /// The `StdinError` observer for a host with NO background stdin feed behind it: there is nothing to
    /// observe, so it answers "no fault" immediately and can never delay a verb. Used by the pipeline
    /// session's inner handle (stage 0's feed is observed by the chain itself, and reaches the session
    /// through the stashed capture's `Stdin0Error`) and by every synthetic host in the fakes and tests.
    static member NoStdinError() : Task<exn option> = Task.FromResult<exn option> None

module internal ReadinessRace =

    let preferCancellation
        (program: string)
        (cancellationToken: CancellationToken)
        (result: Result<unit, ProcessError>)
        : Result<unit, ProcessError> =
        match result with
        | Ok() when cancellationToken.IsCancellationRequested -> Error(ProcessError.Cancelled program)
        | other -> other

    let preferCancellationAndDeadline
        (program: string)
        (cancellationToken: CancellationToken)
        (deadlineHasElapsed: unit -> bool)
        (notReady: ProcessError)
        (result: Result<unit, ProcessError>)
        : Result<unit, ProcessError> =
        match result with
        | Ok() ->
            if cancellationToken.IsCancellationRequested then
                Error(ProcessError.Cancelled program)
            elif deadlineHasElapsed () then
                Error notReady
            elif cancellationToken.IsCancellationRequested then
                // A caller cancellation can race the elapsed-time read; check again so the deadline gate
                // cannot let a success through after cancellation has taken effect.
                Error(ProcessError.Cancelled program)
            else
                result
        | other -> other

/// The result of `RunningProcess.WaitAnyAsync`: which started process finished first and how it
/// concluded. A named type (rather than a tuple) so the fields read clearly from C#.
[<Sealed; NoComparison>]
type WaitAnyResult internal (index: int, outcome: Outcome) =

    /// The index, into the array passed to `WaitAnyAsync`, of the process that finished first.
    member _.Index = index

    /// How that process concluded.
    member _.Outcome = outcome

/// The single output consumption a `RunningProcess` has been claimed for. Its output pipes are
/// pumped exactly once: a buffered one-shot verb, a stdout-streaming session, a byte-chunk session, or
/// an event-streaming session — never two readers on the same pipe.
[<RequireQualifiedAccess>]
type internal Consumption =
    | Fresh
    | Buffered
    | StdoutStreaming
    | StdoutChunkStreaming
    | EventStreaming
    | Interactive

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

/// The `IAsyncEnumerator<'T>` behind `RunningProcess.StdoutJsonLinesAsync`: projects a line-based
/// `IAsyncEnumerator<string>` (the shape `StdoutLinesAsync` already returns) into a typed
/// NDJSON/JSON-Lines sequence. An empty line (after the line-terminator policy the pump already
/// applied) is skipped silently — never handed to `deserialize` — and a line that fails to
/// deserialize raises `ProcessException(ProcessError.Parse(program, ...))`, so a malformed line
/// surfaces the same typed-error signal every other streaming verb already uses for a pump/handler
/// fault (`reportedPumpFault`, `StreamChannel.writeItem`'s `StreamFullMode.Error` case) rather than a
/// raw, undocumented exception escaping the `IAsyncEnumerable`. Hand-written rather than an
/// `async seq { }`/`taskSeq { }` builder because neither ships in this project's dependencies
/// (FSharp.Core has no async-enumerable computation expression, and `FSharp.Control.TaskSeq` is not
/// referenced) — this is the plain `IAsyncEnumerator<'T>` shape the BCL itself expects.
type internal JsonLinesEnumerator<'T>(program: string, source: IAsyncEnumerator<string>, deserialize: string -> 'T) =

    let mutable current = Unchecked.defaultof<'T>

    interface IAsyncEnumerator<'T> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            let body =
                task {
                    let mutable result = ValueNone

                    while result.IsNone do
                        let! moved = source.MoveNextAsync()

                        if not moved then
                            result <- ValueSome false
                        else
                            let line = source.Current

                            if String.IsNullOrEmpty line then
                                // Empty (post-line-terminator) line - skip silently, never deserialized;
                                // loop for the next one instead of ending the sequence early.
                                ()
                            else
                                try
                                    current <- deserialize line
                                    result <- ValueSome true
                                with ex ->
                                    // `return` can't escape a `while` loop body here (its body type must
                                    // unify with `unit`, not the enclosing `Task<bool>`) — `raise` alone
                                    // still faults `body` (and, through it, `MoveNextAsync`'s `ValueTask`)
                                    // exactly like a `return raise` would elsewhere in this file.
                                    raise (ProcessException(ProcessError.Parse(program, ex.Message)))

                    return
                        match result with
                        | ValueSome v -> v
                        | ValueNone ->
                            invalidOp
                                "Loop invariant violated: result should always be ValueSome after exiting the loop"
                }

            ValueTask<bool>(body)

        member _.DisposeAsync() = source.DisposeAsync()

/// The `IAsyncEnumerable<'T>` that `RunningProcess.StdoutJsonLinesAsync` returns — wraps the
/// underlying line stream (`StdoutLinesAsync()`) with `JsonLinesEnumerator` above.
type internal JsonLinesEnumerable<'T>(program: string, source: IAsyncEnumerable<string>, deserialize: string -> 'T) =

    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) : IAsyncEnumerator<'T> =
            JsonLinesEnumerator<'T>(program, source.GetAsyncEnumerator cancellationToken, deserialize)
            :> IAsyncEnumerator<'T>

/// A live handle to a started process: stream its output, feed its stdin, wait for it, or
/// collect it to completion. Disposing it reaps the whole process tree (kill-on-drop).
[<Sealed>]
type RunningProcess internal (host: RunningHost, extraFdStreams: (int * Stream) list) =

    let config = host.Config
    let hasPseudoTerminal = config.Pty.IsSome || host.ResizePty.IsSome
    let stdinTarget = ProcessStdinTarget.forRun hasPseudoTerminal

    // The one lock that serializes every transition of this handle's consumption state machine —
    // claiming the pipes (`claimBuffered`, the streaming-session setup), memoizing the single exit
    // wait (`ensureBufferedWait`, `ExitTask`), and handing out the interactive stdin (`TakeStdin`).
    // These are once-per-handle setup steps, never a hot path, so a single Monitor keeps their
    // check-then-act pairs atomic under concurrent verbs without the subtlety of a field-by-field
    // lock-free scheme. No genuine `await` is ever held across it: a `task { }` built inside the lock
    // returns to the builder (releasing the Monitor) at its first real suspension, so only synchronous
    // setup runs under it.
    let stateLock = obj ()

    let mutable stdinTaken = false
    let extraFds = Dictionary<int, Stream>()

    do
        for targetFd, stream in extraFdStreams do
            extraFds.Add(targetFd, stream)
    // Whether `StdoutLinesAsync()` — directly, or transitively through either `StdoutJsonLinesAsync`
    // overload, which both fold into it — has already handed out its one enumerator. Deliberately a
    // SEPARATE flag from `consumption`/`StartStdoutStreaming()`'s reentrant-by-design gate below: that
    // gate must stay reentrant so `FinishAsync`/`WaitForLineAsync` can rejoin an already-claimed stdout
    // session as companions, but the enumerator-producing call itself — `StdoutLinesAsync`/
    // `StdoutJsonLinesAsync` — still must not silently hand out a second, redundant reader over the
    // same channel (calling one after the other, or the same one twice). Set only from inside
    // `StdoutLinesAsync()` itself, and only AFTER `StartStdoutStreaming()` has already succeeded, so a
    // handle that never gets past that gate (a buffered/event-streaming verb claimed first) never has
    // this flag poisoned by an attempt that was refused for an unrelated reason.
    let mutable stdoutLinesClaimed = false
    // The chunk-streaming analogue of `stdoutLinesClaimed`: the session setup is deliberately
    // reentrant for `FinishAsync`/`ExitTask`, but the public enumerator is handed out only once.
    let mutable stdoutChunksClaimed = false
    // The event-streaming analogue of `stdoutLinesClaimed`/`stdoutChunksClaimed`. Unlike stdout
    // line/chunk streaming, `StartEventStreaming()` has no companion verb that needs to rejoin an
    // already-claimed session (`ExitTask`/`StopAsync` reuse `eventOutcome` directly, never
    // `StartEventStreaming()` itself), so this flag lives right in the claim gate below instead of
    // a separate public verb. Set only once, the moment `StartEventStreaming()` first succeeds.
    let mutable eventStreamClaimed = false
    let mutable stdoutLineCount = 0L
    let mutable stdoutChunkCount = 0
    let mutable stderrLineCount = 0L
    let mutable droppedStreamLineCount = 0
    // Cumulative bytes actually pumped into the stdout LINE streaming channel / the raw stdout CHUNK
    // channel, tracked only to feed an honest `ProcessError.OutputTooLarge.TotalBytes` on
    // `StreamFullMode.Error` overflow (T-297) — neither channel's own consumption path needs them.
    // `int64` because a long-running stream can plausibly exceed `Int32.MaxValue` bytes before the cap
    // (if any) ever trips; `readStdoutStreamedByteCount`/`readStdoutChunkStreamedByteCount` below
    // saturate the read back down to `int` the same way `Pump.LineBuffer.TotalBytes` does.
    let mutable stdoutStreamedByteCount = 0L
    let mutable stderrStreamedByteCount = 0L
    let mutable stdoutChunkStreamedByteCount = 0L
    let mutable stderrStreamBuffer = Unchecked.defaultof<Pump.LineBuffer>
    let mutable streamOutcome = Unchecked.defaultof<Task<Outcome>>
    let mutable chunkOutcome = Unchecked.defaultof<Task<Outcome>>

    let bumpDroppedStreamLine () =
        Interlocked.Increment(&droppedStreamLineCount) |> ignore

    // `stdoutLineCount`/`stderrLineCount` are written by a background pump task and read from the
    // consumer's thread via `StdoutLineCount`/`StderrLineCount` and the `OutputTooLarge`-building
    // closures below — `Interlocked.Increment` to publish each write, `Volatile.Read` (see the two
    // members) to read a fresh value, the same atomic approach `droppedStreamLineCount` already uses.
    let bumpStdoutLine () =
        Interlocked.Increment(&stdoutLineCount) |> ignore

    let bumpStdoutChunk () =
        Interlocked.Increment(&stdoutChunkCount) |> ignore

    let bumpStderrLine () =
        Interlocked.Increment(&stderrLineCount) |> ignore

    let saturateInt64ToInt (value: int64) = int (min value (int64 Int32.MaxValue))

    let readStdoutLineCount64 () = Volatile.Read(&stdoutLineCount)

    let readStdoutLineCount () =
        readStdoutLineCount64 () |> saturateInt64ToInt

    let readStderrLineCount64 () = Volatile.Read(&stderrLineCount)

    let readStderrLineCount () =
        readStderrLineCount64 () |> saturateInt64ToInt

    let readCombinedLineCount () =
        let stdout = readStdoutLineCount64 ()
        let stderr = readStderrLineCount64 ()

        if stdout >= int64 Int32.MaxValue || stderr >= int64 Int32.MaxValue then
            Int32.MaxValue
        else
            int (min (stdout + stderr) (int64 Int32.MaxValue))

    let bumpStdoutStreamedBytes (delta: int64) =
        Interlocked.Add(&stdoutStreamedByteCount, delta) |> ignore

    let bumpStderrStreamedBytes (delta: int64) =
        Interlocked.Add(&stderrStreamedByteCount, delta) |> ignore

    let bumpStdoutChunkStreamedBytes (delta: int) =
        Interlocked.Add(&stdoutChunkStreamedByteCount, int64 delta) |> ignore

    // Saturating reads, mirroring `Pump.LineBuffer.TotalBytes` — these only ever feed an `int`-typed
    // `ProcessError.OutputTooLarge.TotalBytes`.
    let readStdoutStreamedByteCount () =
        Volatile.Read(&stdoutStreamedByteCount) |> saturateInt64ToInt

    let readCombinedStreamedByteCount () =
        let stdout = Volatile.Read(&stdoutStreamedByteCount)
        let stderr = Volatile.Read(&stderrStreamedByteCount)

        if stdout >= int64 Int32.MaxValue || stderr >= int64 Int32.MaxValue then
            Int32.MaxValue
        else
            int (min (stdout + stderr) (int64 Int32.MaxValue))

    let readStdoutChunkStreamedByteCount () =
        Volatile.Read(&stdoutChunkStreamedByteCount) |> saturateInt64ToInt

    // One sequence domain for both event pumps. The atomic increment records the order in which the
    // two independently-drained streams reach ProcessKit's line-framing boundary.
    let mutable outputEventSequence = 0L

    // Idle-timeout (`Command.IdleTimeout`, opt-in): a resettable "no output" watchdog, plus thin
    // activity-tracking wrappers around the stdout/stderr pipes that reset it on every non-empty read.
    // Byte granularity — honest and uniform across every verb (line pumps, byte drains, raw captures
    // all reset it), and independent of the line counters above. Unset (the default): no timer, and the
    // raw pipe streams pass straight through with zero overhead, keeping the idle path entirely opt-in.
    // Armed by `waitWithTimeout` (via `Timeouts.raceTimeout`) when the exit wait begins; disposed with
    // this handle.
    let idleTimer: Timeouts.IdleTimer option =
        match config.IdleTimeout with
        | Some idle when Timeouts.isArmable idle -> Some(new Timeouts.IdleTimer(idle))
        | _ -> None

    let watchActivity (stream: Stream option) : Stream option =
        match idleTimer with
        | Some timer ->
            stream
            |> Option.map (fun s -> new Timeouts.ActivityStream(s, timer.Reset) :> Stream)
        | None -> stream

    let stdoutStream = watchActivity host.Stdout
    let stderrStream = watchActivity host.Stderr

    // Cancels a writer parked on a bounded stream's `StreamFullMode.Backpressure` (`WriteAsync`) once
    // this handle is torn down, so an abandoned bounded stream can't leave its pump running forever: a
    // `Command.Timeout` kills the CHILD but does not by itself free a writer waiting here if nothing
    // ever reads again (see the deadlock note in docs/streaming.md). No `CancelAfter` is ever armed on
    // it, so it owns no timer — there is nothing to release, and skipping `Dispose` is safe.
    let disposalCts = new CancellationTokenSource()

    // Bounded line/event/frame writers use a token separate from `disposalCts`: terminal verbs cancel
    // this token BEFORE awaiting their shared outcome, while `disposalCts` remains the marker that host
    // teardown has actually started for genuine-vs-teardown I/O classification. This distinction lets an
    // abandoned Backpressure writer wake without turning a real pump fault into a routine cancellation.
    let backpressureCts = new CancellationTokenSource()

    // Only the stdout chunk channel's bounded writer uses this token. StopAsync must be able to
    // release an abandoned chunk consumer before awaiting `chunkOutcome`, without cancelling the
    // general lifecycle token that line/event pumps use for teardown-fault classification.
    let chunkBackpressureCts = new CancellationTokenSource()

    let cancelBackpressureWriters () =
        backpressureCts.Cancel()
        chunkBackpressureCts.Cancel()

    // ---- the bounded post-kill reap window for THIS handle (see `PostKillReap`) ------------------
    //
    // Completed once the post-kill budget has elapsed after a hard kill delivered THROUGH this handle
    // — `Kill()` (which is what a cancelled run fires, via the token registration in
    // `CaptureVerbs.runToCompletion`), the pump-fault kill, and `StopAsync`'s soft->hard escalation.
    // Until one of those arms it, this task never completes and the exit wait behaves exactly as
    // before: an ordinary child is reaped synchronously and reports its REAL outcome, with no budget
    // and no artificial delay anywhere on the normal path.
    //
    // It bounds the waits that are IN FLIGHT when the kill lands. A wait that starts later cannot use
    // it — this is a one-shot latch that stays completed for the life of the handle, so racing it
    // would answer instantly and read nothing (see `boundedExitWait`, which gives such a wait its own
    // window instead).
    let postKillDeadline =
        // RunContinuationsAsynchronously: the arming timer's callback must not run the exit wait's
        // continuation inline on the timer thread.
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    // 0 until a hard kill armed the budget; `Interlocked.Exchange` flips it for the first one, so a
    // second kill (a `Kill()` after a `StopAsync`, a pump fault racing either) extends nothing and
    // cannot restart the window.
    let mutable postKillArmed = 0

    // Start the one-shot post-kill reap window. Called at the exact points a hard kill has been
    // delivered, never at the points one is merely intended: arming it before the kill would cut a
    // still-legitimate graceful grace window short.
    let armPostKillReap () =
        if Interlocked.Exchange(&postKillArmed, 1) = 0 then
            Task
                .Delay(PostKillReap.budget ())
                .ContinueWith(
                    Action<Task>(fun _ -> postKillDeadline.TrySetResult() |> ignore),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
            |> ignore

    // The streaming channels and their policy-aware writer live in `StreamChannel`: the stdout channel
    // is written by exactly one pump, the event channel by two (stdout + stderr), and either is bounded
    // when `config.StreamBuffer` opts in (else unbounded, as before).
    let stdoutChannel: StreamChannel.Channel<string> =
        StreamChannel.create config.StreamBuffer true

    // The byte-chunk session has its own channel type but shares the same configured capacity and
    // full-mode policy. It remains dormant unless `StdoutChunksAsync` claims this handle.
    let stdoutChunkChannel: StreamChannel.Channel<ReadOnlyMemory<byte>> =
        StreamChannel.create config.StreamBuffer true

    let eventChannel: StreamChannel.Channel<OutputEvent> =
        StreamChannel.create config.StreamBuffer false

    // Write one item to a line/event streaming channel per `config.StreamBuffer` (see
    // `StreamChannel.writeItem`): unbounded `TryWrite` when unset, else backpressure / drop / fail-loud.
    // Bound `Backpressure` to `backpressureCts.Token` so terminal/shared-exit paths can release an
    // abandoned writer before they await its outcome; the separate `disposalCts` remains the teardown
    // marker used by the pump fault classifier.
    let writeStreamItem
        (channel: StreamChannel.Channel<'T>)
        (buildOverflowError: StreamBufferPolicy -> ProcessError)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        let pending =
            StreamChannel.writeItem config.StreamBuffer backpressureCts.Token channel buildOverflowError onDrop item

        if pending.IsCompletedSuccessfully then
            pending
        else
            ValueTask(
                task {
                    try
                        do! pending
                    with :? OperationCanceledException when backpressureCts.Token.IsCancellationRequested ->
                        // Only the bounded writer's own terminal cancellation is routine; an OCE from
                        // the surrounding pump or a user callback remains visible to the outer handler.
                        ()
                }
            )

    // Raw stdout byte chunks (`Pump.readBytesUntilDone`'s items) have no line structure — one item is
    // whatever the OS handed back on a single read — so neither `LineLimit` nor `TotalLines` means
    // anything for this channel; T-297's bug reported the channel's item capacity as a fabricated line
    // limit regardless of that. `TotalBytes` is the one honest total this site can offer: the real
    // cumulative size, in bytes, of every chunk pumped into the channel so far.
    let writeChunkItem (item: ReadOnlyMemory<byte>) : ValueTask =
        StreamChannel.writeItem
            config.StreamBuffer
            chunkBackpressureCts.Token
            stdoutChunkChannel
            (fun _policy ->
                ProcessError.OutputTooLarge(config.Program, None, None, 0, readStdoutChunkStreamedByteCount ()))
            bumpDroppedStreamLine
            item

    let mutable exitStarted = false
    let mutable exitTaskValue = Unchecked.defaultof<Task<Outcome>>

    // 0 = no `StopAsync` has fired the soft-kill yet; `Interlocked.Exchange` flips it to 1 for the
    // first one. A repeat `StopAsync` (or one racing a `Dispose` that already reaped the container)
    // then skips re-entering the native graceful kill on an already-released container and only awaits
    // the same exit outcome — the once-guard that makes `StopAsync` idempotent.
    let mutable stopStarted = 0

    // The event-streaming session's single combined outcome (waiting for exit + draining both pipes via
    // the two pumps). ExitTask reuses it for an `EventStreaming` handle so it does not start a second,
    // racing set of drains on the same streams.
    let mutable eventOutcome = Unchecked.defaultof<Task<Outcome>>

    // An interactive raw session's single combined outcome — the exit wait plus both readers draining.
    // `PtySession` owns unframed text; `ContentLengthSession` owns framed stdout plus a stderr drain.
    // `ExitTask` reuses it for either `Interactive` handle so it never starts racing readers.
    let mutable interactiveOutcome = Unchecked.defaultof<Task<Outcome>>

    // A buffered verb's single exit wait (`OutputStringAsync`/`OutputBytesAsync`/`WaitAsync`/
    // `ProfileAsync`, via `ensureBufferedWait`). ExitTask reuses it for an already-`Buffered` handle —
    // the "verb, then WaitAny/WaitAll" order — so it does not start a second `host.Wait()` racing the
    // verb's own, mirroring `streamOutcome`/`eventOutcome` above for the streaming sessions.
    let mutable bufferedOutcome = Unchecked.defaultof<Task<Outcome>>

    // Set by the winning total/idle deadline before teardown begins, then threaded into buffered
    // ProcessResult construction. Duration remains the complete wall-clock elapsed time.
    let mutable configuredTimeoutDuration: TimeSpan option = None

    // Single-consumption guard: the output pipes are pumped exactly once. A buffered one-shot verb
    // (OutputString/OutputBytes/Wait/Profile) consumes them whole; the streaming verbs form one
    // session (`StdoutStreaming`: StdoutLines/WaitForLine/Finish share the stdout channel;
    // `EventStreaming`: OutputEvents owns the event channel; `Interactive`: a `PtySession` owns the
    // pipes as UNFRAMED text, matched through `ExpectWindow`). A second, different consumer would race
    // two readers on the same pipe — splitting/losing output — so it is refused. Every transition of
    // this field runs under `stateLock`, so concurrent verbs (or a verb racing `ExitTask`) resolve to
    // exactly one winning consumer rather than both observing `Fresh` and double-pumping.
    let mutable consumption = Consumption.Fresh

    // Claim the pipes for a one-shot buffered verb — atomically, only from fresh (no re-entry: a
    // second buffered verb would re-pump already-torn-down streams). Two concurrent buffered verbs
    // therefore resolve to exactly one winner; the loser is refused (`alreadyConsumedError`).
    let claimBuffered () =
        lock stateLock (fun () ->
            if consumption = Consumption.Fresh then
                consumption <- Consumption.Buffered
                true
            else
                false)

    let alreadyConsumedMessage =
        "this RunningProcess has already been consumed by another verb"

    let alreadyConsumedError () =
        ProcessError.Unsupported alreadyConsumedMessage

    // Hand `stdoutStream`/`stderrStream` to a readiness probe (`WaitForPortAsync`/`WaitForAsync`) for
    // its background drain — but only a still-`Fresh` handle's pipes: if a buffered verb or a
    // streaming session already claimed them, that consumer's own pump already drains them, and
    // handing the same streams to the probe as well would start a second, racing reader on the same
    // pipe. A snapshot read (not a claim: `consumption` is left untouched, so a real verb can still
    // claim the pipes normally once the probe stops draining) taken once, before the probe's first
    // attempt — the same narrow race window every other snapshot-then-act check in this class
    // accepts (concurrently calling two verbs on one handle from different threads without
    // WaitAny/WaitAll is already undefined elsewhere in this API).
    let probeDrainStreams () : Stream option * Stream option =
        lock stateLock (fun () ->
            if consumption = Consumption.Fresh then
                stdoutStream, stderrStream
            else
                None, None)

    let elapsed () =
        Stopwatch.GetElapsedTime host.StartedTimestamp

    // The per-run correlation id: the verb layer stamps one (shared across a run's retries); a direct
    // spawn with none gets a fresh per-incarnation id. Carried on every run-scoped log/trace event.
    let runId =
        match config.RunId with
        | Some id -> id
        | None -> Diag.newRunId ()

    // Count the run as started + in-flight, and capture the ambient `Activity` now (at spawn) so the
    // backdated completion span nests under it. Runs once, at construction (like the spawn log). Defined
    // before the timeout arming below, which carries `runId` into the timeout log. The once-guarded
    // conclude/abandon paths (formerly `conclude`/`markAbandoned` with a hand-rolled `concludedFlag`)
    // now live in the shared `RunTelemetryScope` (T-041) — single-consumption already means one terminal
    // verb runs, but its once-guard makes that bulletproof, so metrics can't double-count and a run
    // never yields two spans. An abandoned run (spawned, never driven to a terminal verb) simply isn't
    // counted as completed.
    let telemetry = RunTelemetryScope.Start(config.Program, runId, host.StartTime)

    let conclude (outcome: Outcome) =
        telemetry.Conclude(config.Logger, outcome, host.Pid, elapsed ())

    // Clear the `runs.active` mark for a run whose handle is being disposed without ever having reached
    // a terminal verb (a streaming/event-driven handle the caller only consumed and dropped) — a no-op
    // once a terminal verb has already run (`telemetry`'s own once-guard).
    let markAbandoned () = telemetry.Abandon()

    // Per-process CPU / peak-memory via the BCL `Process` (reads /proc on Linux, the OS APIs
    // elsewhere) — no metrics once the child has exited or where the platform does not report them.
    //
    // Gated by pid identity (T-097): `waitPosix` reaps the child as soon as it exits, before a verb
    // that later reads `CpuTime`/`PeakMemoryBytes`/`ProfileAsync`'s sampler necessarily observes that —
    // the OS is then free to recycle the pid for an unrelated process, and a raw `Process.GetProcessById
    // pid` read would silently hand back THAT stranger's metrics. `host.StartTimeIdentity` is this
    // child's own OS-reported creation time, captured once right after spawn; re-reading `proc.StartTime`
    // here and comparing catches a recycled pid (its own process, by definition, was created at a
    // different time) before any metric is read. An unknown identity on either side (no captured token,
    // or this platform's `Process.StartTime` throws) is never proof of a mismatch — the gate then defers
    // to the raw read, exactly like the POSIX pgid identity check already does for the tree-level
    // liveness probes.
    let processMetrics (pid: int) : TimeSpan option * int64 option =
        try
            use proc = Process.GetProcessById pid

            let identityMatches =
                match host.StartTimeIdentity with
                | Some captured ->
                    try
                        proc.StartTime = captured
                    with _ ->
                        // `StartTime` unreadable on this platform/timing — no current token to compare
                        // against; defer to the raw read rather than spuriously withholding real metrics.
                        true
                | None -> true

            if not identityMatches then
                // The pid answers, but it is not our child anymore — a recycled pid. Withhold the
                // stranger's metrics rather than silently misattributing them to this run.
                None, None
            else

                let cpu =
                    try
                        Some proc.TotalProcessorTime
                    with _ ->
                        // Not reported on this platform (e.g. denied / unsupported); omit it.
                        None

                let memory =
                    try
                        let peak = proc.PeakWorkingSet64
                        if peak > 0L then Some peak else None
                    with _ ->
                        // Peak working set unavailable (some platforms report 0 / throw); omit it.
                        None

                cpu, memory
        with _ ->
            // The process has already exited or is inaccessible — no metrics to read.
            None, None

    // Invoke a per-line callback without allocating a closure per line (which `Option.iter (fun cb ->
    // cb.Invoke line)` would, capturing `line`). On the hot per-line path.
    let invokeLine (callback: Action<string> option) (line: string) =
        match callback with
        | Some cb -> cb.Invoke line
        | None -> ()

    // True once THIS handle's own teardown has begun — `disposalCts` is cancelled (synchronously) by
    // `reapGuard`/`DisposeAsync` immediately before `host.Teardown()` disposes the pipe streams (the
    // same happens-before the streaming pumps' `isTearingDown` relies on, see `Pump.genuineReadFault`).
    // The buffered pumps below poll it before reclassifying a caught `IOException`/`ObjectDisposedException`:
    // one caught while this reports `true` is the routine dispose/broken-pipe race a CONCURRENT
    // `StopAsync`/`Dispose` sharing this handle triggers by design — it disposes the pipes a still
    // in-flight buffered verb's pumps are draining — not a genuine OS read failure.
    let isTearingDown () =
        disposalCts.Token.IsCancellationRequested

    // Reclassify a fault escaping a stdout/stderr pump into a typed `ProcessError.Io` when it is one of
    // the two exception types a genuine OS read fault surfaces as. Only ever reached once the routine
    // teardown-race case has already been excluded: the streaming pumps route through
    // `Pump.readLinesUntilDone`'s `genuineReadFault` (`isTearingDown` by `disposalCts`) first, and the
    // buffered pumps (`pumpToBuffer` / the discard drains in `WaitAsync`/`ProfileAsync` / the raw stdout
    // capture in `OutputBytesAsync`) now gate on `isTearingDown ()` themselves before calling this. That
    // gate is load-bearing: a buffered verb awaits its OWN pumps before its `reapGuard` tears down, but a
    // CONCURRENT `StopAsync`/`Dispose` on the same handle can dispose the pipes while those pumps are
    // still draining a large tail — reclassifying that routine race as a genuine `ProcessError.Io` used
    // to falsely fault the verb (and, through the supervision layer, `SupervisionSession.Completion`).
    // Any other pump fault (a throwing line handler, a decoder failure, an already-typed
    // `ProcessException` from `StreamChannel`'s fail-loud bounded-channel mode) passes through unchanged
    // — T-087.
    let reportedPumpFault (ex: exn) : exn =
        match ex with
        | :? IOException
        | :? ObjectDisposedException -> ProcessException(ProcessError.Io ex.Message) :> exn
        | _ -> ex

    let pumpToBuffer (stream: Stream) encoding terminator tee (callback: Action<string> option) counter =
        task {
            let buffer = Pump.LineBuffer(config.OutputBuffer)

            let onLine (line: string) : ValueTask =
                invokeLine callback line
                counter ()
                buffer.Add line
                ValueTask.CompletedTask

            // Pass the buffer's byte cap as the in-flight line ceiling too, so a newline-free flood
            // can't grow the assembly buffer past it (the forced segments go through `buffer`'s policy).
            //
            // A genuine OS read fault here (`IOException`/`ObjectDisposedException`) is reclassified
            // into `ProcessError.Io` (via `reportedPumpFault`) so the caller reports an honest,
            // incomplete-capture failure instead of a silently truncated success — T-087.
            try
                do!
                    Pump.readLines
                        stream
                        encoding
                        terminator
                        tee
                        onLine
                        config.OutputBuffer.MaxBytes
                        CancellationToken.None
            with
            | (:? IOException | :? ObjectDisposedException) when isTearingDown () ->
                // A concurrent `StopAsync`/`Dispose` on this handle disposed the pipe streams while this
                // pump was still draining the tail — the buffered-pump teardown race. Stop quietly and
                // return what was captured so far, rather than misreporting the routine race as a genuine
                // `ProcessError.Io` that would fault the verb (and, via supervision, the session). A real
                // mid-run read fault (teardown not begun) still surfaces below — T-087.
                ()
            | :? IOException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
            | :? ObjectDisposedException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)

            return buffer
        }

    // Drain a stream to EOF discarding output (`WaitAsync`/`ProfileAsync`), reclassifying a genuine
    // OS read fault into `ProcessError.Io` exactly like `pumpToBuffer` above — T-087.
    let drainDiscardReporting (stream: Stream option) : Task =
        task {
            try
                do! Pump.drainDiscardOrEmpty stream CancellationToken.None
            with
            | (:? IOException | :? ObjectDisposedException) when isTearingDown () ->
                // Same buffered-pump teardown race as `pumpToBuffer`: a concurrent `StopAsync`/`Dispose`
                // disposed the pipe mid-drain — stop quietly instead of surfacing a false `ProcessError.Io`.
                ()
            | :? IOException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
            | :? ObjectDisposedException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
        }
        :> Task

    // The raw stdout capture backing `OutputBytesAsync` shares the buffered-pump teardown race above, but
    // through the shared `Pump.captureRawOrEmpty`/`drainRaw` primitive, which discards its in-flight buffer
    // on ANY fault. So the bytes read before a concurrent `StopAsync`/`Dispose` disposed the pipe can't be
    // recovered here — on that routine teardown race report an honest, incomplete empty capture rather than
    // faulting the verb. A genuine mid-run read fault (teardown not begun) is left to propagate unchanged,
    // exactly as before — T-087.
    let captureRawStdout () : Task<Pump.RawCapture> =
        task {
            try
                return! Pump.captureRawOrEmpty stdoutStream config.StdoutTee config.OutputBuffer CancellationToken.None
            with (:? IOException | :? ObjectDisposedException) when isTearingDown () ->
                return
                    { Pump.RawCapture.Bytes = Array.empty<byte>
                      Truncated = false
                      TooLarge = false
                      TotalBytes = 0 }
        }

    let pumpStdoutBuffer () =
        match stdoutStream with
        | Some s ->
            pumpToBuffer
                s
                config.StdoutEncoding
                config.StdoutLineTerminator
                config.StdoutTee
                config.OnStdoutLine
                bumpStdoutLine
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    let pumpStderrBuffer () =
        match stderrStream with
        | Some s ->
            pumpToBuffer
                s
                config.StderrEncoding
                config.StderrLineTerminator
                config.StderrTee
                config.OnStderrLine
                bumpStderrLine
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    let tooLargeError (totalLines: int) (totalBytes: int) =
        ProcessError.OutputTooLarge(
            config.Program,
            config.OutputBuffer.MaxLines,
            config.OutputBuffer.MaxBytes,
            totalLines,
            totalBytes
        )

    // A genuine stdin-source failure surfaces as `ProcessError.Stdin` only on an otherwise-successful
    // run — an accepted exit code. A non-zero/unaccepted exit, a signal, or a timeout is the "realer"
    // failure and wins: the outcome passes through unchanged so the caller's own classifier sees it. (A
    // cancelled run is already turned into `ProcessError.Cancelled` upstream, before this is reached.)
    //
    // Called by each Result-producing verb at its ONE classification point, after the exit outcome AND
    // the output drains have been awaited. Only then is the feeder observed, and only on the success
    // branch — which is both the correct precedence and why a failing/timed-out run pays nothing for the
    // bounded window: `host.StdinError` waits (bounded) for a source still reading when the child exited,
    // instead of peeking once and calling a lost race a success. A feed that already finished answers with
    // no wait at all — a synchronous source failure, e.g. a missing `FromFile`, has nothing left to read and
    // is finished once it has ended the child's stdin.
    let stdinErrorOnSuccess (outcome: Outcome) : Task<ProcessError option> =
        if outcome.IsAcceptedBy config.OkCodes then
            task {
                let! fault = host.StdinError()
                return fault |> Option.map (fun ex -> ProcessError.Stdin(config.Program, ex.Message))
            }
        else
            Task.FromResult<ProcessError option> None

    // Observe any fault on an otherwise fire-and-forget outcome task, so it can never surface as an
    // unobserved task exception at finalization when nothing awaits it (a streaming-only consumer that
    // abandons `FinishAsync`, or a readiness probe that races — and never awaits — the memoized buffered
    // exit wait, see `ensureBufferedWait` below). A consumer that *does* await (`FinishAsync`/
    // `WaitAnyAsync`/`WaitAllAsync`/`awaitBufferedOutcome`) still re-throws it. Used by both streaming
    // sessions and the buffered exit wait.
    let observeFault (outcomeTask: Task<Outcome>) =
        outcomeTask.ContinueWith(Action<Task<Outcome>>(fun t -> t.Exception |> ignore))
        |> ignore

    // The reason an exit wait reports when a hard kill was delivered but the reap never landed inside
    // the post-kill budget. Deliberately `Unobserved` and not a fabricated `Exited`/`Signalled`: we
    // genuinely did not see how the tree concluded, and `Unobserved` is never accepted as success.
    let postKillUnobservedReason =
        "the tree was hard-killed, but its exit status was not observed within the bounded post-kill reap window; a background reaper owns the remaining wait"

    // The same honesty for `StopAsync`, whose window also covers the graceful grace period and the
    // pipe drains its shared `ExitTask` may include.
    let stopUnobservedReason =
        "the graceful stop hard-killed the tree, but its conclusion was not observed within the grace period plus the bounded post-kill reap window; a background reaper owns the remaining wait"

    // The one `host.Wait()` for this handle (see `ensureBufferedWait`/`ExitTask` for why there is
    // exactly one), bounded once a hard kill has been delivered through this handle. A delivered
    // SIGKILL/`TerminateProcess` is not a promise that the child is reapable now — a child wedged in
    // uninterruptible sleep defers even SIGKILL — so a kill-then-wait caller (a cancelled run's
    // `Kill()`, the pump-fault kill) would otherwise wait forever on a tree it has already killed. When
    // the budget elapses the native wait keeps running as the SINGLE eventual reaper, adopted by the
    // `PostKillReap` ledger (which observes its fault; on POSIX it is the same shared `waitPosix` group,
    // so nothing starts a second reap — K-016), and this wait resolves to an honest `Unobserved`.
    //
    // The budget runs from whichever came LAST: the kill, or this wait's own start. That distinction is
    // the whole point of the two branches below — the window exists to stop a CALLER from blocking
    // unboundedly after the answer is decided, so a caller that only starts waiting later must get a
    // window of its own rather than inherit a spent one. Time between the kill and the first verb
    // (`Kill()`, then any work, then `WaitAsync`/`OutputStringAsync`/... — the exit wait is created
    // lazily by the first of them) would otherwise leave `postKillDeadline` already completed and
    // report `Unobserved` for a perfectly ordinary killed child whose status was there for the asking.
    // Either way the caller blocks at most one budget, and either way a wait that genuinely does not
    // land inside its window hands ownership over instead of being dropped. With no kill delivered at
    // all — the normal path — this is a straight pass-through, with no budget and no timer.
    let boundedExitWait () : Task<Outcome> =
        let wait = host.Wait()
        let waitBase = wait :> Task

        if Volatile.Read(&postKillArmed) = 1 then
            // The kill preceded this wait: give it a full budget measured from here. `awaitWithin` also
            // answers an already-completed wait without arming anything, and adopts on expiry.
            task {
                match! PostKillReap.awaitWithin (PostKillReap.budget ()) wait with
                | ValueSome outcome -> return outcome
                | ValueNone -> return Outcome.Unobserved postKillUnobservedReason
            }
        else
            // No kill yet: the handle-wide latch is what bounds this wait, one budget after a kill
            // delivered while it is in flight (and never at all if none is).
            task {
                let! winner = Task.WhenAny(waitBase, postKillDeadline.Task)

                if obj.ReferenceEquals(winner, waitBase) then
                    return! wait
                else
                    PostKillReap.adoptWait waitBase
                    return Outcome.Unobserved postKillUnobservedReason
            }

    // Wait for exit, applying the configured total and/or idle timeout: on whichever deadline fires,
    // kill the tree (gracefully if `TimeoutGrace` is set, else hard) — one shared kill for both, so no
    // double kill — and report `Outcome.TimedOut`. The idle watchdog is armed inside `raceTimeout` as
    // the wait begins and reset by each stdout/stderr read through the activity-tracking wrappers. The
    // exit wait underneath is `boundedExitWait`, so the reap after ANY hard kill on this handle stays
    // bounded (the timeout race bounds its own post-kill reap too, see `Timeouts.raceTimeoutWithCts`).
    let waitWithTimeout () : Task<Outcome> =
        let onTimeout (configuredDuration: TimeSpan) : Task =
            task {
                configuredTimeoutDuration <- Some configuredDuration

                match config.TimeoutGrace with
                | Some grace -> do! host.GracefulKill grace
                | None -> host.StartKill()
            }
            :> Task

        Timeouts.raceTimeout config.Logger config.Program runId config.Timeout idleTimer onTimeout (boundedExitWait ())

    // Start (and memoize) a buffered verb's single exit wait, under `stateLock`. Every buffered verb
    // calls this instead of `waitWithTimeout()` directly; the first caller creates the wait, and both
    // the verb that owns the pipes and a concurrent `ExitTask` on the same handle (the "verb, then
    // WaitAny/WaitAll" order) share that one wait — one `host.Wait()`, one set of readers — with
    // correct cross-thread visibility of `bufferedOutcome` in either arrival order.
    //
    // `observeFault` is attached here, exactly once, the moment the wait is created — not on every
    // `ensureBufferedWait()` call. This covers `raceReadinessAgainstExit`, whose probe-vs-exit race
    // (below) never awaits `childExitTask`: without this, a fault from this same memoized wait (e.g.
    // `waitWithTimeout`'s timeout-race `onTimeout` hook, see its comment above) would surface as an
    // unobserved task exception at finalization on a probe-only handle (probe → dispose, no consuming
    // verb ever calls `awaitBufferedOutcome`/`ExitTask`). The attach is purely observational — it never
    // reads/replaces `t.Result`/`t.Exception` beyond marking it observed — so every real awaiter of this
    // exact `Task` (returned below, and by every subsequent `ensureBufferedWait()` call) still gets and
    // re-throws the original fault unchanged.
    let ensureBufferedWait () : Task<Outcome> =
        lock stateLock (fun () ->
            if obj.ReferenceEquals(bufferedOutcome, null) then
                bufferedOutcome <- waitWithTimeout ()
                observeFault bufferedOutcome

            bufferedOutcome)

    // The ceiling on the single post-exit readiness re-check in `raceReadinessAgainstExit` below.
    // Deliberately much shorter than a typical readiness `timeout`: the re-check exists to observe a
    // condition the child published a moment ago — a local file, an open port/socket, a health endpoint
    // — all of which answer in milliseconds even on a loaded CI runner, so this window is generous for
    // an honest answer while keeping the guarantee that mattered before it existed. Without a ceiling, a
    // caller-owned predicate that answers slowly (or, like `TaskCompletionSource<bool>().Task`, never)
    // would turn "an exited child resolves promptly" back into "waits out the whole timeout". Reaching
    // the ceiling costs nothing beyond the delay: the verdict is then the same `NotReady` the exit
    // branch reported before this re-check was added.
    let postExitRecheckGrace = TimeSpan.FromMilliseconds 500.0

    // Race a readiness probe against the child's own exit so a probe on a child that has already
    // exited — or that dies early on startup — resolves to `NotReady` promptly instead of burning the
    // whole `timeout` polling a condition that can never come true. Shared by all readiness probes
    // (`WaitForHttpAsync`/`WaitForPortAsync`/`WaitForSocketAsync`/`WaitForAsync`) so their early-exit
    // behaviour cannot drift apart: `startProbe` builds the underlying `ReadinessProbe.*` task from the
    // snapshotted (still-`Fresh`) drain streams and a readiness token linked to the caller's `cancellationToken`;
    // everything else — the exit race, cancellation, and `NotReady`/`Cancelled` selection — lives here,
    // once.
    //
    // Early-exit detection MUST share the one reap-once exit wait every other verb on this handle uses
    // (`ensureBufferedWait`, memoized under `stateLock`) rather than starting an independent
    // `host.Wait()`: on POSIX, `host.Wait()`/`waitPosix` REAPS the child and consumes its exit status,
    // and is idempotent only while a wait stays in flight — never after the pid has already been reaped
    // (KB K-016). A second, unrelated `host.Wait()` here would race the reap started by this one, so a
    // later `WaitAsync`/`ProfileAsync` call (the common "diagnose why the service died on startup" path)
    // would either see the pid already gone (ECHILD → fabricated `Outcome.Unobserved`) or, worse, risk
    // observing a recycled pid. Calling `ensureBufferedWait()` here (instead of claiming the pipes via
    // `claimBuffered`) starts/joins that ONE shared wait without claiming `consumption`, so `consumption`
    // stays `Fresh` and a subsequent buffered verb can still claim the pipes and reuse this exact same
    // memoized wait — one `host.Wait()`, one reap, shared by the probe and by whatever verb runs
    // afterward.
    //
    // Observing the exit is NOT by itself proof that the condition never came true. The polling probe's
    // in-flight attempt may have observed a stale `false` and then yielded long enough for the child to
    // publish readiness (a sentinel file, an open port/socket, a health endpoint served by a surviving
    // grandchild) and exit: cancelling that run and reporting `NotReady` at once would erase a state
    // that genuinely exists. So the exit branch below gives the condition exactly ONE more observation
    // before concluding, and only when there is budget left for it — see the numbered contract there.
    let raceReadinessAgainstExit
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        (startProbe:
            ReadinessAttempts
                -> Stream option
                -> Stream option
                -> TimeSpan
                -> CancellationToken
                -> Task<Result<unit, ProcessError>>)
        : Task<Result<unit, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled config.Program)
            else
                // The whole probe's budget, clamped exactly as the readiness core clamps it, measured
                // from here — the post-exit re-check below spends what is left of THIS budget, never a
                // fresh copy of it, and a `NotReady` still reports this same clamped total (not the
                // shorter slice the re-check was given) so the reported budget matches what was enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                let startedTimestamp = config.TimeProvider.GetTimestamp()
                let stdout, stderr = probeDrainStreams ()

                use readinessCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                let readinessTask =
                    startProbe ReadinessAttempts.PollUntilDeadline stdout stderr timeout readinessCts.Token

                let childExitTask = ensureBufferedWait ()
                let! winner = Task.WhenAny(readinessTask :> Task, childExitTask :> Task)

                let classifyCompletedResult result =
                    ReadinessRace.preferCancellationAndDeadline
                        config.Program
                        cancellationToken
                        (fun () -> config.TimeProvider.GetElapsedTime startedTimestamp >= armedTimeout)
                        (ProcessError.NotReady(config.Program, armedTimeout))
                        result

                if obj.ReferenceEquals(winner, readinessTask) || readinessTask.IsCompleted then
                    let! completed = readinessTask
                    return classifyCompletedResult completed
                else
                    readinessCts.Cancel()
                    let! raced = readinessTask

                    match raced with
                    | Ok() ->
                        // (1) The polling run DID see the condition hold — it just finished in the window
                        // between the race above picking the exit and the cancel here. Its `Ok` was
                        // computed with the caller's token and the shared deadline both still clear, so it
                        // is an honest success; discarding it in favour of `NotReady` would lose exactly
                        // the readiness this branch exists to preserve.
                        return classifyCompletedResult (Ok())
                    | Error _ ->
                        if cancellationToken.IsCancellationRequested then
                            // (2) The caller cancelled: cancellation outranks readiness, and no further
                            // observation may be started on a token that has already fired.
                            return Error(ProcessError.Cancelled config.Program)
                        else
                            let remaining = armedTimeout - config.TimeProvider.GetElapsedTime startedTimestamp

                            if remaining <= TimeSpan.Zero then
                                // (3) The overall deadline is spent, so there is no budget to observe
                                // anything else with — `NotReady`, exactly as before this re-check existed.
                                // Stated here rather than left to the readiness core (which does refuse a
                                // non-positive budget without invoking the probe): the rule that a spent
                                // deadline buys no further observation belongs with the decision to make
                                // one, and it keeps the spent-budget path from starting a probe run at all.
                                return Error(ProcessError.NotReady(config.Program, armedTimeout))
                            else
                                // (4) One final observation of a state that can no longer change, bounded
                                // by `min(remaining budget, postExitRecheckGrace)` and by the caller's own
                                // token. `Once` (never a second poll loop) is what keeps this cheap: a
                                // "still not ready" answer returns at the first attempt, so the ordinary
                                // "child died on startup" path costs one probe invocation, not the rest of
                                // the timeout. The grace caps the OTHER direction — a probe that answers
                                // slowly (or never) must not turn prompt early-exit detection back into
                                // waiting out the deadline. No drain streams are handed over: an exited
                                // child cannot block on a full pipe, so there is nothing left to unblock.
                                let! recheck =
                                    startProbe
                                        ReadinessAttempts.Once
                                        None
                                        None
                                        (min remaining postExitRecheckGrace)
                                        cancellationToken

                                match recheck with
                                | Ok() ->
                                    // The caller can cancel, or the original absolute deadline can elapse,
                                    // after the bounded re-check reports success but before this branch
                                    // returns. Both checks must use the original budget, not the re-check's
                                    // relative slice.
                                    return classifyCompletedResult recheck
                                | Error(ProcessError.Cancelled _) ->
                                    // The caller's token fired while the one re-check was in flight; it
                                    // outranks the re-check's own verdict, as in (2).
                                    return Error(ProcessError.Cancelled config.Program)
                                | Error _ -> return Error(ProcessError.NotReady(config.Program, armedTimeout))
        }

    let waitForHttp
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForHttp
                config.TimeProvider
                config.Program
                stdout
                stderr
                uri
                isSatisfactory
                attempts
                budget
                readinessToken)

    let waitForHttpWithClient
        (client: HttpClient)
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForHttpWithClient
                config.TimeProvider
                config.Program
                stdout
                stderr
                client
                uri
                isSatisfactory
                attempts
                budget
                readinessToken)

    let httpStatusPredicate (acceptableStatusCodes: seq<int>) =
        ArgumentNullException.ThrowIfNull acceptableStatusCodes
        let accepted = HashSet<int>(acceptableStatusCodes)

        if accepted.Count = 0 then
            raise (
                ArgumentException("At least one acceptable HTTP status code is required.", nameof acceptableStatusCodes)
            )

        Func<HttpResponseMessage, bool>(fun response -> accepted.Contains(int response.StatusCode))

    let waitForPort
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForPort
                config.TimeProvider
                config.Program
                stdout
                stderr
                endpoint
                attempts
                budget
                readinessToken)

    let waitForSocket
        (endpoint: EndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForSocket
                config.TimeProvider
                config.Program
                stdout
                stderr
                endpoint
                attempts
                budget
                readinessToken)

    let waitForCustom
        (probe: Func<Task<bool>>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitFor
                config.TimeProvider
                config.Program
                stdout
                stderr
                probe
                attempts
                budget
                readinessToken)

    // Kill the tree the moment an output pump faults, so a still-producing child can't wedge the exit
    // wait — and the pump's siblings — by blocking on a full pipe that nobody drains once the pump
    // reading it has died. Fire-and-forget and best-effort: the kill only unblocks the child so the
    // exit wait can conclude and the child is reaped in bounded time even with no configured timeout;
    // the ORIGINAL pump fault is still surfaced by whoever awaits the pump (`Task.WhenAll pumps`
    // below / `streamOutcome` / `eventOutcome`), so what propagates is that fault, not a secondary
    // closed-pipe/channel error. The continuation inspects only `IsFaulted` (never `Exception`), so
    // the pump's exception stays available for its real awaiter, and the continuation itself can't
    // fault (the `StartKill` call is guarded). Runs synchronously on the faulting pump's completion so
    // the kill is prompt.
    let killTreeOnPumpFault (pump: Task) : unit =
        pump.ContinueWith(
            Action<Task>(fun completed ->
                if completed.IsFaulted then
                    try
                        host.StartKill()
                        // A hard kill was delivered: start the bounded post-kill reap window, so a
                        // child that cannot be reaped (wedged in uninterruptible sleep) cannot hold the
                        // exit wait this kill exists to unblock.
                        armPostKillReap ()
                    with _ ->
                        // Best-effort: `reapGuard`'s teardown still reaps the tree, and the pump fault
                        // is surfaced by its awaiter, so a hiccup in this early kill loses nothing.
                        ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    // Await a buffered verb's exit wait (`waitTask`, from `ensureBufferedWait`) together with its
    // already-running `pumps`. Fault-aware in both directions:
    //  - A pump fault kills the tree at once (see `killTreeOnPumpFault`), so the child can't wedge
    //    `waitTask` by blocking on a pipe its dead pump no longer drains; `waitTask` then completes
    //    (the killed child is reaped) and the ORIGINAL pump fault surfaces from `Task.WhenAll pumps`.
    //  - `backend.Wait` (the innermost primitive) is designed never to fault, but `waitWithTimeout`
    //    layers a timeout race whose `onTimeout` hook calls native kill syscalls, so the composed wait
    //    CAN throw. `reapGuard`'s teardown disposes the streams the pumps read, so a pump still
    //    in-flight when such a fault escaped this scope would race that dispose; awaiting the pumps
    //    best-effort before re-raising closes that gap.
    // A pump's own fault on the success path (thrown from `Task.WhenAll pumps`) still propagates
    // exactly as before.
    let awaitBufferedOutcome (waitTask: Task<Outcome>) (pumps: Task[]) : Task<Outcome> =
        pumps |> Array.iter killTreeOnPumpFault

        task {
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = waitTask
                do! Task.WhenAll pumps
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault from `waitTask` before the pumps were awaited must not orphan them — observe
                // them best-effort. Their own fault, if any, is secondary to the error we surface.
                try
                    do! Task.WhenAll pumps
                with _ ->
                    // best-effort drain; the original fault above is what we report.
                    ()

            match error with
            | Some ex -> return! Task.FromException<Outcome> ex
            | None -> return outcome
        }

    // An async-disposable that reaps the tree on scope exit — normal OR exceptional. Every terminal
    // verb opens one with `use` so the container is always torn down, even when a pump faults (e.g.
    // a throwing line handler) before the verb would otherwise reach its teardown. `Teardown` is
    // idempotent (the group's release runs once), so the redundant call on `RunningProcess` disposal
    // is harmless.
    //
    // Load-bearing invariant: a verb must await ALL of its OWN pumps before this guard's scope exits,
    // because `Teardown` disposes the pipe streams the pumps read — a pump still in-flight at teardown
    // would race a stream `Dispose`. Every verb satisfies this (it awaits the pumps / `streamOutcome`
    // before returning); keep it that way when editing. A CONCURRENT verb's teardown is the exception the
    // invariant can't cover: a `StopAsync`/`Dispose` on the same handle reaps as soon as the shared exit
    // wait resolves, without waiting for an in-flight buffered verb's pumps — so it can dispose the pipes
    // mid-drain. `disposalCts.Cancel()` below (before `Teardown`) is what the buffered pumps read via
    // `isTearingDown` to tell that routine race apart from a genuine read fault (see `reportedPumpFault`).
    let reapGuard () =
        { new IAsyncDisposable with
            member _.DisposeAsync() =
                // Unblock bounded writers before/while tearing down, so they can't outlive this scope.
                // The general lifecycle token below remains the teardown marker for other pumps.
                cancelBackpressureWriters ()
                disposalCts.Cancel()
                // Clear `runs.active` for a verb that faults before reaching its own `conclude outcome`
                // (e.g. a throwing `OnStdoutLine`/`OnStderrLine` handler, or a faulted exit wait) — a
                // no-op (guarded by `RunTelemetryScope`'s once-guard) on the ordinary success path,
                // where `conclude outcome` already claimed it before this scope exits. Mirrors the outer
                // `RunningProcess.DisposeAsync`'s own `markAbandoned()` call below, for the same reason.
                markAbandoned ()
                host.Teardown() }

    // Log the spawn once, at construction. Both this `Log.spawn` and the `RunTelemetryScope.Start`
    // (`Diag.runStarted`) above swallow any fault the consumer's logger / metric / trace sink raises, so
    // constructing this handle can never throw *from observability*. That is what closes the ownership
    // window between the native spawn (already done inside `host`) and the hand-off to the caller: the
    // freshly-spawned tree's deterministic owner — this handle — is always successfully constructed and
    // returned, so a broken logger can never orphan the child here. The runner's construction site
    // (`JobRunner.start`) adds a defence-in-depth teardown as a backstop for any non-observability fault.
    do Log.spawn config.Logger config.Program host.Pid runId

    internal new(host: RunningHost) = RunningProcess(host, [])

    /// The pid, when known.
    member _.Pid = host.Pid

    /// When the process was started.
    member _.StartTime = host.StartTime

    /// Wall-clock time since the process started.
    member _.Elapsed = elapsed ()

    /// Cumulative CPU time (user + kernel) of the child right now, if the platform reports it and
    /// the process is still alive.
    member _.CpuTime: TimeSpan option =
        match host.Pid with
        | Some pid -> fst (processMetrics pid)
        | None -> None

    /// Peak resident memory of the child in bytes, if reported (some platforms, e.g. macOS, may
    /// not) and the process is still alive.
    member _.PeakMemoryBytes: int64 option =
        match host.Pid with
        | Some pid -> snd (processMetrics pid)
        | None -> None

    /// Whole-tree peak memory for internal resource monitors. A private group may expose accounting
    /// even when the leader has exited; shared/fallback groups fail honestly instead of attributing a
    /// sibling aggregate or silently substituting leader-only memory.
    member internal _.TreePeakMemoryBytes() : Result<int64, ProcessError> =
        try
            match host.TreeStats with
            | None -> Error(ProcessError.Unsupported "whole-tree memory accounting is unavailable for this run")
            | Some snapshot ->
                match snapshot () with
                | None -> Error(ProcessError.Unsupported "whole-tree memory accounting could not be read for this run")
                | Some stats ->
                    match stats.PeakMemoryBytes with
                    | Some bytes -> Ok bytes
                    | None ->
                        Error(ProcessError.Unsupported "whole-tree memory accounting is unavailable on this platform")
        with ex ->
            Error(ProcessError.Unsupported $"whole-tree memory accounting failed for this run: {ex.Message}")

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = readStdoutLineCount ()

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = readStderrLineCount ()

    /// Stream items dropped so far by a bounded streaming policy's `StreamFullMode.DropOldest`/
    /// `DropNewest` (always `0` unless `Command.StreamBuffer` is configured with one of those modes).
    /// For line/event streams this counts dropped lines/events; for `StdoutChunksAsync` it counts
    /// dropped chunks. It is the streaming analogue of a buffered verb's `ProcessResult.Truncated`.
    member _.DroppedStreamLineCount = Volatile.Read(&droppedStreamLineCount)

    /// Take the parent side of the POSIX full-duplex channel connected to `targetFd` in the child.
    /// Returns `Some` only for a descriptor configured with `Command.ExtraFd`, and only once.
    member _.TakeExtraFd(targetFd: int) : Stream option =
        if targetFd < 3 then
            invalidArg (nameof targetFd) "An extra child file descriptor must be at least 3."

        lock stateLock (fun () ->
            match extraFds.TryGetValue targetFd with
            | true, stream ->
                extraFds.Remove targetFd |> ignore
                Some stream
            | false, _ -> None)

    // The once-only interactive-stdin claim shared by `TakeStdin` and `TakeStdinAsync`. Taken under
    // `stateLock` so two concurrent callers can't both observe `not stdinTaken` and hand out the same
    // stream twice. `host.Stdin` is `Some` exactly when the pipe is kept open: `KeepStdinOpen` with no
    // source, or `KeepStdinOpen` WITH a source (a source WITHOUT `KeepStdinOpen` closes the pipe after
    // draining, so its `host.Stdin` is `None`). Deliberately claims WITHOUT waiting for the source
    // feeder: the wait must happen outside `stateLock`, and each caller below picks how to serve it.
    member private _.ClaimInteractiveStdin() : Stream option =
        lock stateLock (fun () ->
            match host.Stdin with
            | Some stream when config.KeepStdinOpen && not stdinTaken ->
                stdinTaken <- true
                Some stream
            | _ -> None)

    /// Take the interactive stdin handle — `Some` only when the command kept stdin open
    /// (`Command.KeepStdinOpen`), and only once. With **no** source it is available immediately; with a
    /// `Command.Stdin(source)` it is available once the background feeder has finished draining that source
    /// (this call blocks until then), so the caller never writes to the pipe while the feeder still is.
    /// That wait is deadlock-safe even on a single-threaded `SynchronizationContext` (a WPF/WinForms UI
    /// thread, classic ASP.NET): the source feeder runs detached on the thread pool (see
    /// `Pump.feedStdin`'s `backgroundTask`), so it always makes progress while this thread is blocked here
    /// and is never waiting to post a continuation back to it.
    member this.TakeStdin() : ProcessStdin option =
        match this.ClaimInteractiveStdin() with
        | Some stream ->
            // Wait — OUTSIDE `stateLock`, so it never blocks other verbs — for the source feeder to finish
            // before handing the stream over. A no-op when there is no source (interactive-only) or nothing
            // to feed; only a `Stdin(source)` + `KeepStdinOpen` run actually waits here. This is what makes
            // the interactive writer and the source feeder single-writer: the feeder drains the source
            // first, then the caller writes.
            host.StdinFeedComplete()
            Some(ProcessStdin(stream, host.Config.StdinEncoding, stdinTarget))
        | None -> None

    /// The non-blocking form of `TakeStdin`: the once-only claim above still happens SYNCHRONOUSLY, before
    /// this returns (so a racing `TakeStdin` still loses, and a caller that gets a task is genuinely the
    /// owner), but the wait for a `Command.Stdin(source)` feeder to finish draining moves into the returned
    /// task — served on the thread pool, where parking a thread is safe, instead of on the caller's.
    ///
    /// Internal, for `ContentLengthSession`: its constructor claims stdin right after starting the framed
    /// parse loop, and must return while the frames that loop is already producing are still unread. With a
    /// bounded frame backlog (`Command.StreamBuffer`) a blocking claim there deadlocks the run — the parse
    /// loop parks on a full channel whose only consumer is `FramesAsync()`, which the caller cannot reach
    /// until the constructor returns; the child then blocks writing stdout, stops reading stdin, and the
    /// very feeder this waits for never finishes.
    member internal this.TakeStdinAsync() : Task<ProcessStdin option> =
        match this.ClaimInteractiveStdin() with
        | Some stream ->
            task {
                // The same blocking `host.StdinFeedComplete()` `TakeStdin` performs (it has no async form),
                // moved onto the pool so awaiting it neither blocks the caller's thread nor needs the
                // caller's `SynchronizationContext` to pump — the feeder itself already runs detached
                // there (`Pump.feedStdin`'s `backgroundTask`), so it makes progress regardless.
                do! Task.Run(fun () -> host.StdinFeedComplete())
                return Some(ProcessStdin(stream, host.Config.StdinEncoding, stdinTarget))
            }
        | None -> Task.FromResult None

    /// Signal the process tree to die without waiting (fire-and-forget, like `Process.Kill()`); the
    /// tree is fully reaped when the handle is disposed. For a blocking kill, dispose the handle.
    ///
    /// Delivering the kill also starts this handle's bounded post-kill reap window: a tree that cannot
    /// be reaped afterwards (a child wedged in uninterruptible sleep defers even SIGKILL) resolves this
    /// handle's exit wait to an honest `Outcome.Unobserved` once the window elapses, instead of leaving
    /// a caller that killed and then awaited — notably a CANCELLED run, whose token registration calls
    /// exactly this — blocked forever. The native wait is not abandoned: the `PostKillReap` ledger owns
    /// it as the single eventual reaper.
    member _.Kill() =
        host.StartKill()
        armPostKillReap ()

    /// Forward parent termination requests into this run's graceful tree-stop path. POSIX registers
    /// `SIGINT` and `SIGTERM`; Windows handles Ctrl+C and Ctrl+Break through `Console.CancelKeyPress`.
    /// The first signal starts one `StopAsync(gracePeriod)` and suppresses the parent's default immediate
    /// termination while the tree stops; repeated signals never start duplicate teardown.
    ///
    /// The returned caller-owned scope removes the handlers when disposed. It is also removed
    /// automatically when the child exits. Registering the scope starts only the handle's shared exit
    /// observation and does not claim stdout/stderr, so capture and streaming verbs remain available.
    /// On Windows the forwarded request uses the ordinary `StopAsync` contract (best-effort `WM_CLOSE`,
    /// then Job termination after the grace window), not a promise that a console child receives the
    /// original Ctrl event.
    member this.ForwardParentSignals(gracePeriod: TimeSpan) : IDisposable =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero, nameof gracePeriod)

        let subscribe (forward: unit -> bool) =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                let handler =
                    ConsoleCancelEventHandler(fun _ eventArgs ->
                        if forward () then
                            eventArgs.Cancel <- true)

                Console.CancelKeyPress.AddHandler handler

                { new IDisposable with
                    member _.Dispose() =
                        Console.CancelKeyPress.RemoveHandler handler }
            else
                let callback (context: PosixSignalContext) =
                    if forward () then
                        context.Cancel <- true

                let interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, callback)

                try
                    let terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, callback)

                    { new IDisposable with
                        member _.Dispose() =
                            terminate.Dispose()
                            interrupt.Dispose() }
                with _ ->
                    interrupt.Dispose()
                    reraise ()

        this.ForwardParentSignalsUsing(gracePeriod, subscribe)

    /// `ForwardParentSignals` using the default 2-second graceful-stop window.
    member this.ForwardParentSignals() : IDisposable =
        this.ForwardParentSignals Limits.DefaultStopGrace

    /// Test seam for the forwarding lifecycle: production supplies platform signal registrations;
    /// tests inject a callback holder without sending a real signal to the test runner process.
    member internal this.ForwardParentSignalsUsing
        (gracePeriod: TimeSpan, subscribe: ((unit -> bool) -> IDisposable))
        : IDisposable =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero, nameof gracePeriod)
        ArgumentNullException.ThrowIfNull subscribe

        let registrationGate = obj ()
        let mutable registration: IDisposable option = None
        let mutable disposed = 0
        let mutable forwarded = 0

        let dispose () =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                lock registrationGate (fun () ->
                    registration |> Option.iter (fun value -> value.Dispose())
                    registration <- None)

        let forward () =
            if Volatile.Read(&forwarded) <> 0 then
                // A repeat signal that was already entering the callback when exit auto-unsubscribed
                // still belongs to this forwarding attempt and must suppress the parent's default action.
                true
            elif Volatile.Read(&disposed) <> 0 then
                false
            else
                if Interlocked.CompareExchange(&forwarded, 1, 0) = 0 then
                    let stopTask = this.StopAsync gracePeriod

                    stopTask.ContinueWith(
                        Action<Task<Outcome>>(fun completed ->
                            if completed.IsFaulted then
                                completed.Exception |> ignore),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
                    |> ignore

                true

        let created = subscribe forward

        lock registrationGate (fun () ->
            if Volatile.Read(&disposed) <> 0 then
                created.Dispose()
            else
                registration <- Some created)

        let exitTask = ensureBufferedWait ()

        exitTask.ContinueWith(
            Action<Task<Outcome>>(fun _ -> dispose ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

        { new IDisposable with
            member _.Dispose() = dispose () }

    /// Resize the child's controlling pseudo-terminal to `cols` columns x `rows` rows (a `Command.Pty`
    /// run only). Windows applies it with `ResizePseudoConsole`; POSIX applies `ioctl(TIOCSWINSZ)` on the
    /// pty master and then delivers `SIGWINCH` to the child so a running TUI re-queries its geometry (D6).
    ///
    /// Honest, never a silent no-op: on a **non-PTY** run this returns `Error(ProcessError.Unsupported)`,
    /// and a native resize failure returns `Error(ProcessError.Io ...)` — a garbled/partial resize is
    /// never reported as success. `cols` and `rows` must each be at least 1 and at most `Int16.MaxValue`
    /// (a terminal `COORD`/`winsize` is a `SHORT`), rejected with `ArgumentOutOfRangeException` at the
    /// boundary, matching the `Command.Pty` builder's geometry validation.
    ///
    /// A **pure**, non-consuming verb: it neither consumes the output pipes nor touches the exit-wait/reap
    /// path, so it never trips the "already consumed by another verb" gate and can run alongside a
    /// capturing/streaming/`WaitAsync` verb that has claimed the handle. It is honest about lifecycle,
    /// though: once the run has been **torn down** — a terminal verb has concluded and reaped it, or the
    /// handle has been disposed — the pty master fd / pseudoconsole handle behind the resize is closed, and
    /// its number is reusable by another run, so a resize then returns `Error(ProcessError.Unsupported ...)`
    /// rather than risk `ioctl`/`SIGWINCH`/`ResizePseudoConsole` landing on an unrelated run through a
    /// recycled fd/pid/handle. Resize a run while it is live.
    member _.ResizeAsync(cols: int, rows: int) : Task<Result<unit, ProcessError>> =
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1, nameof cols)
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, int Int16.MaxValue, nameof cols)
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1, nameof rows)
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, int Int16.MaxValue, nameof rows)

        // No `stateLock`, no `consumption` claim, no `host.Wait()`/`ensureBufferedWait()` — a resize is
        // independent of the exit-wait/reap-once ledger (KB K-016) and is not a consuming verb (KB K-031).
        match host.ResizePty with
        | Some resize -> Task.FromResult(resize (cols, rows))
        | None -> Task.FromResult(Error(ProcessError.Unsupported "Resize (not a PTY run)"))

    /// Gracefully stop the process tree, then reap it: send the command's configured `StopSignal`, wait
    /// up to `gracePeriod` for it to exit on its own, then hard-kill whatever is still alive — the
    /// same graceful-kill machinery `Command.TimeoutGrace` and `ProcessGroup.ShutdownAsync` drive.
    /// Returns the honest `Outcome` of how the child *actually* concluded (a clean `Exited` if it
    /// obeyed the signal, otherwise a `Signalled`/`Exited` from the escalated kill); a non-zero or
    /// killed exit is data, never a raised error. Unlike the fire-and-forget `Kill()`, this awaits the
    /// stop and tears the tree down before returning, so it is a terminal verb like `WaitAsync`.
    /// A negative `gracePeriod` is rejected with `ArgumentOutOfRangeException`; `TimeSpan.Zero`
    /// skips the grace window and escalates immediately.
    ///
    /// **Bounded, always.** The whole call — the grace window and the reap that follows the escalated
    /// hard kill — is bounded by `gracePeriod` plus a post-kill reap budget. A tree that cannot be
    /// reaped even after the hard kill lands (a child wedged in uninterruptible sleep defers SIGKILL
    /// until its I/O unblocks) therefore ends this call with an honest `Outcome.Unobserved` carrying
    /// that detail, never a fabricated exit and never an unbounded block. The wait is not dropped: the
    /// single remaining right to reap the tree passes to a background reaper, so nothing starts a
    /// second waiter and the eventual conclusion is still observed exactly once.
    ///
    /// This drains the child's stdout/stderr while it shuts down (a child blocked writing to a full
    /// pipe would otherwise ignore the soft signal until it could flush). If a streaming or capturing
    /// verb already owns the pipes, `StopAsync` reuses that session's wait rather than starting a
    /// second reader on them, so it is safe to call after `StdoutLinesAsync`/`OutputEventsAsync` or
    /// concurrently with an in-flight `FinishAsync`/`WaitAsync`. Idempotent and race-safe with `Kill`,
    /// `Dispose`, and a repeat `StopAsync`: the tree is reaped exactly once.
    ///
    /// **Platform / shared-group degradation (no new silent downgrade).** A soft signal needs a
    /// mechanism that has one. On **Windows** there is no per-tree graceful signal, but a windowed child
    /// (Electron/GUI) is sent a best-effort `WM_CLOSE` at the start of the grace window and can close
    /// itself within it; a child with no window (or one that vetoes the close) is hard-killed by the
    /// atomic Job terminate when the grace elapses — exactly as `Command.TimeoutGrace` and
    /// `ProcessGroup.ShutdownAsync` behave there (a console child can additionally get a best-effort
    /// CTRL+BREAK via `Command.WindowsCtrlSignals()` + `ProcessGroup.Signal`). On a **shared** group
    /// (a handle from `ProcessGroup.StartAsync`, where the group — not the handle — owns the tree)
    /// there is no per-child graceful signal either, so this immediately hard-kills just this child
    /// (like `Kill()`), matching the documented `TimeoutGrace` fallback for a shared group. A handle
    /// from the default runner (`Command.StartAsync()` / `IProcessRunner.SpawnAsync`) owns a private
    /// group and gets the full configured-soft-signal → grace → SIGKILL path on Unix.
    member this.StopAsync(gracePeriod: TimeSpan) : Task<Outcome> =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        task {
            use _reap = reapGuard ()
            // Release any bounded writer before asking the shared exit task to settle. This is the
            // terminal operation's explicit signal that an unread streaming backlog may be abandoned;
            // keep `disposalCts` untouched so the pump's normal I/O classification remains intact.
            cancelBackpressureWriters ()
            // Begin (or reuse) the exit wait BEFORE signalling, so the pipes are drained while the
            // child shuts down. `ExitTask` reuses whichever consumption already owns the pipes (a
            // streaming session, or an in-flight buffered verb) rather than racing a second reader,
            // and claims a fresh buffered drain only when no verb has run yet. It never reaps.
            let exitTask = this.ExitTask
            // Start racing the shared conclusion BEFORE the stop is asked for, bounded by this call's
            // own window: the grace it was given, plus the post-kill reap budget. Both halves of the
            // stop live inside that one window — the graceful wait (up to `gracePeriod`) and the reap
            // that follows the escalated hard kill — so a repeat `StopAsync` (which skips the kill and
            // only awaits the shared outcome) is bounded by exactly the same rule as the caller that
            // performs the escalation, without either of them cutting a still-legitimate grace window
            // short. Mirrors the ProcessKit-rs prototype's `grace.saturating_add(PUMP_TEARDOWN)`.
            let bounded =
                PostKillReap.awaitWithin (PostKillReap.plus gracePeriod (PostKillReap.budget ())) exitTask

            // Ask the tree to stop: soft signal, wait up to `gracePeriod`, then hard-kill the remainder
            // — reusing `host.GracefulKill`, the timeout machinery's own escalation. Degrades to the
            // documented immediate child/tree kill on Windows or a shared group (see the doc above).
            // Fired at most once (a repeat `StopAsync` only awaits the outcome), so it never re-enters
            // the native kill on a container a prior stop/`Dispose` already released.
            if Interlocked.Exchange(&stopStarted, 1) = 0 then
                do! host.GracefulKill gracePeriod
                // The escalation has now delivered the hard kill (`GracefulKill` returns only after its
                // grace-bounded poll force-killed whatever was still alive), so this is the honest
                // moment to start the handle's post-kill reap window — for this call and for any other
                // verb sharing the same exit wait.
                armPostKillReap ()

            match! bounded with
            | ValueSome outcome ->
                // Record the run as completed (once-guarded: a no-op if a concurrent terminal verb
                // sharing the same wait already concluded it). Return the honest outcome; a killed/
                // non-zero exit is data, so this never raises for the stop itself.
                conclude outcome
                return outcome
            | ValueNone ->
                // The tree was asked to stop and then hard-killed, but its conclusion did not land
                // inside the window. Report that honestly rather than blocking indefinitely or
                // fabricating an exit: the shared wait is now owned by the `PostKillReap` ledger (no
                // second waiter, its eventual fault observed), and a verb still awaiting the same
                // `ExitTask` will still see the real outcome if it ever arrives.
                let outcome = Outcome.Unobserved stopUnobservedReason
                conclude outcome
                return outcome
        }

    /// `StopAsync` using the default 2-second grace window (matching `ProcessGroupOptions.ShutdownTimeout`).
    member this.StopAsync() : Task<Outcome> = this.StopAsync Limits.DefaultStopGrace

    /// Deliver `signal` to this run's own contained process tree without consuming or reaping the
    /// handle. Delivery is lifecycle-gated: after teardown it returns a typed `Unsupported` error and
    /// never targets a recycled pid. On Windows only the documented Job/CTRL+BREAK/WM_CLOSE mappings are
    /// available; unsupported signals fail honestly.
    member _.Signal(signal: Signal) : Result<unit, ProcessError> = host.Signal signal

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data; the tree is
    /// reaped when the call returns.
    member _.OutputStringAsync() : Task<Result<ProcessResult<string>, ProcessError>> =
        if not (claimBuffered ()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()
                let stdoutTask = pumpStdoutBuffer ()
                let stderrTask = pumpStderrBuffer ()
                // Observe BOTH buffer pumps before reading either, so a throwing line handler in one
                // never orphans the other as an unobserved task (mirrors the streaming path's WhenAll);
                // `awaitBufferedOutcome` additionally guarantees this even if the exit wait itself faults.
                let! outcome =
                    awaitBufferedOutcome (ensureBufferedWait ()) [| (stdoutTask :> Task); (stderrTask :> Task) |]

                let! outBuf = stdoutTask
                let! errBuf = stderrTask
                conclude outcome

                if outBuf.TooLarge || errBuf.TooLarge then
                    return
                        Error(
                            tooLargeError
                                (int (min (int64 outBuf.TotalLines + int64 errBuf.TotalLines) (int64 Int32.MaxValue)))
                                (int (min (int64 outBuf.TotalBytes + int64 errBuf.TotalBytes) (int64 Int32.MaxValue)))
                        )
                else
                    match! stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<string>(
                                    config.Program,
                                    outBuf.Text,
                                    errBuf.Text,
                                    outcome,
                                    elapsed (),
                                    outBuf.Truncated || errBuf.Truncated,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = configuredTimeoutDuration,
                                    stdoutEncoding = config.StdoutEncoding
                                )
                            )
            }

    /// Run to completion, capturing stdout as raw bytes (no line splitting) and stderr as text.
    ///
    /// The configured `OutputBuffer` policy's **byte** controls apply to this raw stdout capture:
    /// `MaxBytes = Some cap` enforces the cap per `Overflow` — `Error` returns
    /// `ProcessError.OutputTooLarge` once the cumulative stdout exceeds the cap (the pipe is still
    /// drained), `DropOldest` keeps the last `cap` bytes, `DropNewest` keeps the first `cap` bytes, both
    /// setting `ProcessResult.Truncated` when anything was dropped. `MaxBytes = None` (the default)
    /// keeps the raw capture **unbounded** — there is no byte ceiling to enforce. `MaxLines` never
    /// applies to a raw byte stream (it has no line structure) and is ignored on stdout here; it still
    /// governs the line-pumped **stderr** capture. `Truncated` reflects truncation of stdout OR stderr,
    /// and `OutputTooLarge` fires if either stream trips its fail-loud ceiling.
    ///
    /// This is a deliberate, documented divergence from the Rust `ProcessKit-rs` reference, whose
    /// `output_bytes` bounds raw bytes only by `Timeout`, not by the buffer policy: a caller who set
    /// `MaxBytes`/`FailLoud` to bound memory would still get an unbounded stdout buffer otherwise.
    member _.OutputBytesAsync() : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        if not (claimBuffered ()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()

                // The raw stdout capture now honours the byte cap + overflow of `config.OutputBuffer`
                // (unbounded when `MaxBytes = None`, exactly as before); `MaxLines` does not apply to a
                // byte stream, so it is ignored here — it still governs the line-pumped stderr below. Goes
                // through `captureRawStdout` so a concurrent `StopAsync`/`Dispose` teardown race ends as an
                // honest incomplete capture rather than faulting the verb (see its comment) — T-087.
                let stdoutTask = captureRawStdout ()

                let stderrTask = pumpStderrBuffer ()
                // Observe both pumps before reading either, so a throwing stderr handler (or a raw-drain
                // I/O fault) can't orphan the other as an unobserved task; `awaitBufferedOutcome`
                // additionally guarantees this even if the exit wait itself faults.
                let! outcome =
                    awaitBufferedOutcome (ensureBufferedWait ()) [| (stdoutTask :> Task); (stderrTask :> Task) |]

                let! stdoutCapture = stdoutTask
                let! errBuf = stderrTask
                conclude outcome

                if stdoutCapture.TooLarge || errBuf.TooLarge then
                    // The raw stdout byte cap contributes no lines (a byte stream has none); stderr is
                    // line-pumped, so its totals carry the lines and both streams' bytes are summed.
                    return
                        Error(
                            tooLargeError
                                errBuf.TotalLines
                                (int (
                                    min
                                        (int64 stdoutCapture.TotalBytes + int64 errBuf.TotalBytes)
                                        (int64 Int32.MaxValue)
                                ))
                        )
                else
                    match! stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<byte[]>(
                                    config.Program,
                                    stdoutCapture.Bytes,
                                    errBuf.Text,
                                    outcome,
                                    elapsed (),
                                    stdoutCapture.Truncated || errBuf.Truncated,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = configuredTimeoutDuration,
                                    stdoutEncoding = config.StdoutEncoding
                                )
                            )
            }

    /// Wait for the process to exit, discarding its output. Reaps the tree.
    member _.WaitAsync() : Task<Outcome> =
        if not (claimBuffered ()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = reapGuard ()
            // Drain both pipes (so the child never blocks on a full buffer) without retaining.
            let stdoutTask = drainDiscardReporting stdoutStream
            let stderrTask = drainDiscardReporting stderrStream
            // Observe both drains together so an I/O fault on one can't orphan the other;
            // `awaitBufferedOutcome` additionally guarantees this even if the exit wait itself faults.
            let! outcome = awaitBufferedOutcome (ensureBufferedWait ()) [| stdoutTask; stderrTask |]
            conclude outcome
            return outcome
        }

    /// Run to completion while periodically sampling the child's CPU/memory and, where available, its
    /// private containment tree's I/O every `interval`, then return a `RunProfile`. Drains and discards
    /// output (like `WaitAsync`) and reaps the tree. A run in a shared group reports no I/O counters,
    /// because the group's aggregate would include sibling runs.
    /// A non-positive `interval` (`<= TimeSpan.Zero`) is rejected with `ArgumentOutOfRangeException`
    /// — a sampling cadence must be a positive duration. Validated up front, before the pipes are
    /// claimed, so an invalid call neither consumes this one-shot handle nor starts a tight loop.
    member _.ProfileAsync(interval: TimeSpan) : Task<RunProfile> =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero)

        if not (claimBuffered ()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = reapGuard ()

            let period = interval

            let mutable samples = 0
            let mutable lastCpu = None
            let mutable peakMemory = None
            let mutable lastIo = None
            use sampleCts = new CancellationTokenSource()

            let sampleTreeIo () =
                match host.TreeStats with
                | Some stats ->
                    match stats () with
                    | Some snapshot -> snapshot.IoCounters |> Option.iter (fun counters -> lastIo <- Some counters)
                    | None -> ()
                | None -> ()

            let sampler =
                task {
                    try
                        while not sampleCts.IsCancellationRequested do
                            match host.Pid with
                            | Some pid ->
                                let cpu, memory = processMetrics pid
                                cpu |> Option.iter (fun c -> lastCpu <- Some c)

                                match memory with
                                | Some m ->
                                    peakMemory <-
                                        Some(
                                            match peakMemory with
                                            | Some existing -> max existing m
                                            | None -> m
                                        )
                                | None -> ()
                            | None -> ()

                            sampleTreeIo ()

                            samples <- samples + 1
                            // Clamp so an over-long sampling period can't throw out of `Task.Delay`.
                            do! Task.Delay(Timeouts.clampArmable period, sampleCts.Token)
                    with :? OperationCanceledException ->
                        // The run finished and we cancelled sampling; stop quietly.
                        ()
                }

            let stdoutTask = drainDiscardReporting stdoutStream
            let stderrTask = drainDiscardReporting stderrStream

            // Capture a fault rather than letting it escape immediately, so the sampler is ALWAYS
            // cancelled and awaited before its CTS is disposed at scope exit — never left running as
            // an unobserved task. (A task CE cannot `do!` inside a `finally`, so this is the
            // try/with-then-single-cleanup form of try/finally; the cleanup must also precede reading
            // the sampler's metrics on the success path, which a `finally`/`use` could not guarantee.)
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = ensureBufferedWait ()
                do! Task.WhenAll([| stdoutTask; stderrTask |])
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault before the drains were awaited (e.g. waitWithTimeout threw) must not orphan
                // them — observe them best-effort. Their own fault is secondary to the error we surface.
                try
                    do! Task.WhenAll([| stdoutTask; stderrTask |])
                with _ ->
                    // best-effort teardown drain; the original fault above is what we report.
                    ()

            sampleCts.Cancel()
            do! sampler

            match error with
            | Some ex -> return! Task.FromException<RunProfile> ex
            | None ->
                // Job/cgroup accounting remains queryable after the child exits and is cumulative, so
                // take one final snapshot before this private group is released. It closes the common
                // short-run race where the child finishes between periodic ticks.
                sampleTreeIo ()
                conclude outcome
                return RunProfile(outcome, elapsed (), lastCpu, peakMemory, lastIo, samples)
        }

    /// `ProfileAsync` sampling every 100 ms.
    member this.ProfileAsync() =
        this.ProfileAsync(TimeSpan.FromMilliseconds 100.0)

    // Returns false when a different consumption (a buffered verb, or event streaming) already owns the
    // pipes; true once the stdout streaming session is (or already was) ours. The whole check + claim +
    // session setup runs under `stateLock`, so a concurrent second `StdoutLinesAsync`/`WaitForLineAsync`/
    // `FinishAsync` either observes a fully-constructed session (channel + pumps + `streamOutcome` all
    // assigned) or, if it is an incompatible consumer, is atomically refused — never a half-built
    // session, and never two racing setups building two readers on the one channel.
    member private _.StartStdoutStreaming() : bool =
        lock stateLock (fun () ->
            if consumption = Consumption.StdoutStreaming then
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.StdoutStreaming
                let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
                stderrStreamBuffer <- stderrBuffer

                let stdoutPump =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stdoutStream
                                    config.StdoutEncoding
                                    config.StdoutLineTerminator
                                    config.StdoutTee
                                    (fun line ->
                                        invokeLine config.OnStdoutLine line
                                        bumpStdoutLine ()
                                        bumpStdoutStreamedBytes (int64 (Encoding.UTF8.GetByteCount line) + 1L)

                                        writeStreamItem
                                            stdoutChannel
                                            (fun policy ->
                                                // One channel item is one framed stdout line, 1:1, so the
                                                // channel's item capacity IS a genuine line limit and
                                                // `readStdoutLineCount()` is the true count of lines
                                                // produced before the cap tripped — both stayed honest
                                                // already. `TotalBytes` was hardcoded `0` before T-297; it
                                                // now reports the UTF-8 size of those lines using the same
                                                // "own bytes + 1 separator byte" accounting
                                                // `Pump.LineBuffer`'s doc comment explains (a small,
                                                // deliberate over-count, never an under-count) — this
                                                // streaming channel retains nothing to re-scan, so the cost
                                                // is tracked incrementally instead.
                                                ProcessError.OutputTooLarge(
                                                    config.Program,
                                                    Some policy.Capacity,
                                                    None,
                                                    readStdoutLineCount (),
                                                    readStdoutStreamedByteCount ()
                                                ))
                                            bumpDroppedStreamLine
                                            line)
                                    None
                                    (fun () -> disposalCts.Token.IsCancellationRequested)

                            stdoutChannel.TryComplete() |> ignore
                        with ex ->
                            // A pump fault — a throwing `OnStdoutLine` handler, `StreamFullMode.Error`
                            // tripping its cap, or a genuine OS read fault (reclassified into
                            // `ProcessError.Io` by `reportedPumpFault` — T-087) — must still complete the
                            // channel, carrying the error, so a `StdoutLinesAsync` consumer observes it
                            // instead of hanging on a reader that never ends. Re-raise (preserving the
                            // original stack; `reraise` is unavailable inside a task CE) so `streamOutcome`
                            // / `FinishAsync` surface the same fault.
                            let reported = reportedPumpFault ex
                            stdoutChannel.TryComplete reported |> ignore
                            ExceptionDispatchInfo.Throw reported
                    }

                let stderrPump =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stderrStream
                                    config.StderrEncoding
                                    config.StderrLineTerminator
                                    config.StderrTee
                                    (fun line ->
                                        invokeLine config.OnStderrLine line
                                        bumpStderrLine ()
                                        stderrBuffer.Add line
                                        ValueTask.CompletedTask)
                                    config.OutputBuffer.MaxBytes
                                    (fun () -> disposalCts.Token.IsCancellationRequested)
                        with ex ->
                            // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087) before
                            // it faults `streamOutcome` / `FinishAsync` below.
                            ExceptionDispatchInfo.Throw(reportedPumpFault ex)
                    }

                // A fault in either pump kills the tree at once, so a still-producing child can't wedge
                // `waitWithTimeout()` (below) by blocking on a pipe its dead pump no longer drains — the
                // exit wait then completes and `streamOutcome` surfaces the original pump fault.
                killTreeOnPumpFault (stdoutPump :> Task)
                killTreeOnPumpFault (stderrPump :> Task)

                streamOutcome <-
                    task {
                        let! outcome = waitWithTimeout ()
                        // Await both pumps together so neither task is left unobserved if the other faults.
                        do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                        return outcome
                    }

                // A `StdoutLinesAsync()` consumer can abandon `FinishAsync()` (e.g. its enumeration throws
                // because a faulting `OnStdoutLine` handler completed the channel with the error), so observe
                // the outcome fault here.
                observeFault streamOutcome

                true)

    // The byte-chunk counterpart to `StartStdoutStreaming`. It owns the same stdout pipe and stderr
    // capture, but its pump deliberately does no decoding or line framing: one channel item is one
    // non-empty OS read. The setup is reentrant for `FinishAsync`/`ExitTask`, while the public
    // `StdoutChunksAsync` method below has its own one-enumerator guard.
    member private _.StartStdoutChunkStreaming() : bool =
        lock stateLock (fun () ->
            if consumption = Consumption.StdoutChunkStreaming then
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.StdoutChunkStreaming
                let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
                stderrStreamBuffer <- stderrBuffer

                let stdoutPump =
                    task {
                        try
                            match stdoutStream with
                            | Some stream ->
                                do!
                                    Pump.readBytesUntilDone
                                        stream
                                        config.StdoutTee
                                        (fun chunk ->
                                            bumpStdoutChunk ()
                                            bumpStdoutChunkStreamedBytes chunk.Length

                                            writeChunkItem chunk)
                                        (fun () -> disposalCts.Token.IsCancellationRequested)
                                        CancellationToken.None
                            | None -> ()

                            stdoutChunkChannel.TryComplete() |> ignore
                        with
                        | :? OperationCanceledException when chunkBackpressureCts.Token.IsCancellationRequested ->
                            // The dedicated chunk backpressure token cancels an abandoned writer; this is
                            // routine completion, not a read/tee fault for the chunk stream.
                            stdoutChunkChannel.TryComplete() |> ignore
                        | ex ->
                            // A genuine read fault, a throwing tee, or a bounded-channel failure must
                            // wake the chunk consumer and remain visible through `chunkOutcome`.
                            let reported = reportedPumpFault ex
                            stdoutChunkChannel.TryComplete reported |> ignore
                            ExceptionDispatchInfo.Throw reported
                    }

                let stderrPump =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stderrStream
                                    config.StderrEncoding
                                    config.StderrLineTerminator
                                    config.StderrTee
                                    (fun line ->
                                        invokeLine config.OnStderrLine line
                                        bumpStderrLine ()
                                        stderrBuffer.Add line
                                        ValueTask.CompletedTask)
                                    config.OutputBuffer.MaxBytes
                                    (fun () -> disposalCts.Token.IsCancellationRequested)
                        with ex ->
                            // Complete stdout on a sibling failure so a consumer cannot wait forever for
                            // a channel whose stderr pump has already made the combined session fail.
                            let reported = reportedPumpFault ex
                            stdoutChunkChannel.TryComplete reported |> ignore
                            ExceptionDispatchInfo.Throw reported
                    }

                killTreeOnPumpFault (stdoutPump :> Task)
                killTreeOnPumpFault (stderrPump :> Task)

                chunkOutcome <-
                    task {
                        let! outcome = waitWithTimeout ()
                        do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                        return outcome
                    }

                // The enumerator may be abandoned without a subsequent FinishAsync; keep the combined
                // task observed while preserving its original exception for a real awaiter.
                observeFault chunkOutcome

                true)

    /// Stream stdout as raw byte chunks. Each item is a non-empty `ReadOnlyMemory<byte>` containing
    /// exactly one underlying read, including NUL bytes, invalid UTF-8, and arbitrary read boundaries.
    /// The returned memory owns its backing array and remains valid after the next item is produced.
    /// Call `FinishAsync()` afterwards for stderr and the process outcome.
    member this.StdoutChunksAsync() : IAsyncEnumerable<ReadOnlyMemory<byte>> =
        if not (this.StartStdoutChunkStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        lock stateLock (fun () ->
            if stdoutChunksClaimed then
                raise (InvalidOperationException alreadyConsumedMessage)
            else
                stdoutChunksClaimed <- true)

        stdoutChunkChannel.Reader.ReadAllAsync()

    /// Stream stdout line by line as it arrives. Call `FinishAsync` afterwards for stderr + outcome.
    /// Hands out its ONE enumerator exactly once per handle — a second call (directly, or via
    /// `StdoutJsonLinesAsync`, which itself calls this) throws `InvalidOperationException`, same as any
    /// other already-consumed verb; `FinishAsync`/`WaitForLineAsync` remain free to rejoin the same
    /// session afterwards (they do not produce a second enumerator).
    member this.StdoutLinesAsync() : IAsyncEnumerable<string> =
        if not (this.StartStdoutStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        lock stateLock (fun () ->
            if stdoutLinesClaimed then
                // `StartStdoutStreaming()` above is deliberately reentrant (it must let `FinishAsync`/
                // `WaitForLineAsync` rejoin an already-claimed session), so it alone can't refuse this
                // second enumerator-producing call — that is what this flag is for.
                raise (InvalidOperationException alreadyConsumedMessage)
            else
                stdoutLinesClaimed <- true)

        stdoutChannel.Reader.ReadAllAsync()

    /// Stream stdout as NDJSON / JSON Lines: each non-empty line is deserialized into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults) as it arrives. A thin wrapper over
    /// `StdoutLinesAsync()` — it shares the very same exclusive-consumption gate
    /// (`StartStdoutStreaming`) and the same already-consumed enumerator guard, `LineTerminator`, and
    /// `StreamBuffer` policy, so calling this instead of `StdoutLinesAsync()` (or vice versa, or twice)
    /// on one handle follows the same already-consumed contract every other streaming verb already has;
    /// nothing extra needs configuring here. An empty line (after that line-terminator policy is applied) is skipped
    /// silently, never deserialized — a common NDJSON producer quirk (a trailing blank line, a
    /// keep-alive newline). A non-empty line that fails to deserialize ends the enumeration with
    /// `ProcessException(ProcessError.Parse(...))`, exactly like every other JSON verb's
    /// `ProcessError.Parse` (`OutputJsonAsync`/`ParseAsync`) — never a raw, undocumented exception
    /// escaping the `IAsyncEnumerable`. Call `FinishAsync()` afterwards for stderr + outcome, same as
    /// after `StdoutLinesAsync()`.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe
    /// — pass a `JsonTypeInfo&lt;'T&gt;` via the other overload, or avoid this verb, in a
    /// trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a NativeAOT app.">]
    member this.StdoutJsonLinesAsync<'T>([<Optional>] options: JsonSerializerOptions | null) : IAsyncEnumerable<'T> =
        // Non-generic, `Type`-based overload rather than the generic `JsonSerializer.Deserialize<'T>` —
        // same reasoning as `CaptureVerbs.outputJson`: the BCL's generic overload returns a
        // `TValue?`-annotated value the F# nullness checker can't reconcile against our ambient,
        // unconstrained `'T`. A genuine JSON `null` raises here, turned into `ProcessError.Parse` below
        // exactly like a malformed document would.
        let optionsArg = Option.ofObj options |> Option.toObj

        let deserialize (line: string) : 'T =
            match JsonSerializer.Deserialize(line, typeof<'T>, optionsArg) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        JsonLinesEnumerable<'T>(config.Program, this.StdoutLinesAsync(), deserialize) :> IAsyncEnumerable<'T>

    /// Like the overload above, but deserializes each line via a source-generated
    /// `JsonTypeInfo&lt;'T&gt;` instead of reflection — no `RequiresUnreferencedCode`/
    /// `RequiresDynamicCode`, so this overload is trim-/NativeAOT-safe. Pass
    /// `MyJsonContext.Default.MyType` from a `[&lt;JsonSerializable&gt;]`-annotated
    /// `JsonSerializerContext`. Same empty-line-skip / `ProcessError.Parse` contract as the reflection
    /// overload above.
    member this.StdoutJsonLinesAsync<'T>(typeInfo: JsonTypeInfo<'T>) : IAsyncEnumerable<'T> =
        ArgumentNullException.ThrowIfNull typeInfo

        // Through the non-generic `JsonTypeInfo` base overload for the same reason the reflection
        // overload above goes through `typeof<'T>` rather than the generic `Deserialize<'T>` — sidesteps
        // the BCL's `TValue?`-annotated generic return the F# nullness checker can't reconcile against
        // an unconstrained `'T`.
        let deserialize (line: string) : 'T =
            match JsonSerializer.Deserialize(line, typeInfo :> JsonTypeInfo) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        JsonLinesEnumerable<'T>(config.Program, this.StdoutLinesAsync(), deserialize) :> IAsyncEnumerable<'T>

    /// After streaming stdout, wait for exit and return the captured stderr. Reaps the tree.
    member this.FinishAsync() : Task<Result<Finished, ProcessError>> =
        let outcomeTask =
            if this.StartStdoutStreaming() then Some streamOutcome
            elif this.StartStdoutChunkStreaming() then Some chunkOutcome
            else None

        match outcomeTask with
        | None -> Task.FromResult(Error(alreadyConsumedError ()))
        | Some outcome ->
            // `FinishAsync` is the explicit terminal hand-off after a streaming consumer has stopped.
            // Wake any bounded writer before awaiting the shared session outcome; otherwise an unread
            // full channel would keep this very task waiting for the pump that is waiting for a reader.
            cancelBackpressureWriters ()

            task {
                use _reap = reapGuard ()
                let! settled = outcome
                conclude settled

                if stderrStreamBuffer.TooLarge then
                    return Error(tooLargeError stderrStreamBuffer.TotalLines stderrStreamBuffer.TotalBytes)
                else
                    match! stdinErrorOnSuccess settled with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                Finished(
                                    settled,
                                    stderrStreamBuffer.Text,
                                    Volatile.Read(&droppedStreamLineCount) > 0 || stderrStreamBuffer.Truncated
                                )
                            )
            }

    // Returns false when a different consumption (a buffered verb, or stdout streaming) already owns the
    // pipes, OR when the event-streaming session itself was already claimed by an earlier
    // `OutputEventsAsync()` call; true only for the ONE call that first claims the session. As with
    // `StartStdoutStreaming`, the whole check + claim + setup runs under `stateLock`, so a concurrent
    // second `OutputEventsAsync` observes a fully-constructed session or is atomically refused.
    member private _.StartEventStreaming() : bool =
        lock stateLock (fun () ->
            if consumption = Consumption.EventStreaming then
                // `eventChannel` is created with `SingleReader = true` (`StreamChannel.create`), so a
                // second concurrent reader relies on undefined behaviour of a single-consumer-optimized
                // channel — refuse it instead of reentrantly handing out a second enumerator. No internal
                // caller re-enters this method to rejoin an already-claimed session (`ExitTask`/
                // `StopAsync` reuse `eventOutcome` directly), so this branch only ever serves a repeat
                // `OutputEventsAsync()` call.
                if eventStreamClaimed then
                    false
                else
                    eventStreamClaimed <- true
                    true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.EventStreaming
                eventStreamClaimed <- true
                // Each pump completes the shared event channel on its own fault (carrying the error), so an
                // `OutputEventsAsync` consumer observes a throwing handler promptly rather than hanging until the
                // process exits — `eventOutcome` below only completes the channel after the exit wait, which
                // for a long-running child can be far away. `TryComplete` because the two pumps and the
                // combined task below all race to complete the one channel; re-raise so `eventOutcome` faults.
                // One helper for both streams so the fault-completion invariant lives in a single place.
                let eventPump
                    (stream: Stream option)
                    encoding
                    terminator
                    tee
                    (onLine: Action<string> option)
                    (bump: unit -> unit)
                    (bumpBytes: int64 -> unit)
                    (wrap: OutputLine -> OutputEvent)
                    =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stream
                                    encoding
                                    terminator
                                    tee
                                    (fun line ->
                                        // Capture metadata at the framing boundary, before a user handler can
                                        // block or mutate a deterministic TimeProvider. The two pumps share the
                                        // atomic counter, so the number records which framed line reached this
                                        // boundary first rather than which handler happened to return first.
                                        let outputLine =
                                            OutputLine(
                                                line,
                                                config.TimeProvider.GetUtcNow(),
                                                Interlocked.Increment(&outputEventSequence)
                                            )

                                        invokeLine onLine line
                                        bump ()
                                        bumpBytes (int64 (Encoding.UTF8.GetByteCount line) + 1L)

                                        writeStreamItem
                                            eventChannel
                                            (fun _policy ->
                                                // The event channel merges stdout's and stderr's framed
                                                // lines into ONE shared backlog, so its item capacity
                                                // bounds their COMBINED count, never either stream's own
                                                // line count alone — reporting it as a `LineLimit` (T-297's
                                                // bug) claimed a per-stream cap that never existed here.
                                                // `LineLimit = None`. `TotalLines` still reports something
                                                // honest and available at this site: the combined count of
                                                // framed lines both pumps have produced so far (each event
                                                // wraps exactly one line, so this total is real — just not
                                                // tied to a channel-capacity-shaped limit). `ByteLimit`
                                                // stays `None`; `TotalBytes` uses the same UTF-8-plus-
                                                // separator accounting as stdout line streaming, summed
                                                // across both event producers including this event.
                                                ProcessError.OutputTooLarge(
                                                    config.Program,
                                                    None,
                                                    None,
                                                    readCombinedLineCount (),
                                                    readCombinedStreamedByteCount ()
                                                ))
                                            bumpDroppedStreamLine
                                            (wrap outputLine))
                                    None
                                    (fun () -> disposalCts.Token.IsCancellationRequested)
                        with ex ->
                            // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087)
                            // before it completes the channel / faults `eventOutcome` below.
                            let reported = reportedPumpFault ex
                            eventChannel.TryComplete reported |> ignore
                            ExceptionDispatchInfo.Throw reported
                    }

                let stdoutPump =
                    eventPump
                        stdoutStream
                        config.StdoutEncoding
                        config.StdoutLineTerminator
                        config.StdoutTee
                        config.OnStdoutLine
                        bumpStdoutLine
                        bumpStdoutStreamedBytes
                        OutputEvent.Stdout

                let stderrPump =
                    eventPump
                        stderrStream
                        config.StderrEncoding
                        config.StderrLineTerminator
                        config.StderrTee
                        config.OnStderrLine
                        bumpStderrLine
                        bumpStderrStreamedBytes
                        OutputEvent.Stderr

                // A fault in either pump kills the tree at once, so a still-producing child can't wedge
                // `waitWithTimeout()` (below) by blocking on a pipe its dead pump no longer drains — the
                // exit wait then completes and `eventOutcome` surfaces the original pump fault.
                killTreeOnPumpFault (stdoutPump :> Task)
                killTreeOnPumpFault (stderrPump :> Task)

                eventOutcome <-
                    task {
                        let mutable error: exn option = None
                        let mutable outcome = Unchecked.defaultof<Outcome>

                        try
                            let! settled = waitWithTimeout ()
                            outcome <- settled
                            // Await both pumps together so neither is left unobserved if the other faults.
                            do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                            eventChannel.TryComplete() |> ignore
                        with ex ->
                            error <- Some ex
                            // A fault (a throwing handler, or the exit wait itself) completes the channel WITH
                            // the error so an `OutputEventsAsync` consumer observes it instead of hanging — idempotent
                            // with the per-pump completion above. The fault is otherwise consumed here (and by
                            // the ContinueWith below) rather than surfacing as an unobserved task exception.
                            eventChannel.TryComplete ex |> ignore

                        // Surface the outcome, or re-raise the fault for a concurrent ExitTask (WaitAny/WaitAll
                        // on this handle). The ContinueWith below observes that fault, so the OutputEvents-only
                        // case never leaves an unobserved task exception.
                        match error with
                        | Some ex -> return! Task.FromException<Outcome> ex
                        | None ->
                            conclude outcome
                            return outcome
                    }

                // Observe any fault on this otherwise fire-and-forget task (the OutputEvents-only case, where
                // nothing awaits `ExitTask`).
                observeFault eventOutcome

                true)

    /// Stream merged stdout+stderr line events as they arrive, each tagged with its origin
    /// (`OutputEvent.Stdout`/`OutputEvent.Stderr`). Under `Command.MergeStderr` the child has no separate
    /// stderr stream (it is folded into stdout at the OS level), so every event is an `OutputEvent.Stdout`
    /// — the stderr lines are already interleaved, in order, within the stdout byte stream.
    member this.OutputEventsAsync() : IAsyncEnumerable<OutputEvent> =
        if not (this.StartEventStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        eventChannel.Reader.ReadAllAsync()

    // Claim the pipes for an interactive expect-style session (`PtySession`) and start its raw readers,
    // returning the shared `ExpectWindow` they fill. Unlike `StartStdoutStreaming`/`StartEventStreaming`
    // this is deliberately NOT reentrant: a second session over one handle would give two matchers one
    // window, each silently consuming the other's output, so it is refused with the same
    // already-consumed error every other verb reports. The whole check + claim + setup runs under
    // `stateLock`, so a concurrent second call observes a fully-constructed session or is refused.
    //
    // The readers are raw (`Pump.readTextUntilDone`), not line pumps: an interactive prompt carries no
    // line terminator, so framing the stream is precisely what must not happen here. That also means
    // `LineTerminator`, `Command.OnStdoutLine`/`OnStderrLine` and the streaming line counters have
    // nothing to observe on this path — the byte-exact tees (`StdoutTee`/`StderrTee`) still do, and are
    // fed exactly as the line pumps feed them.
    member internal _.StartInteractiveSession
        (windowChars: int, transcriptChars: int option, filterAnsi: bool)
        : Result<ExpectWindow, ProcessError> =
        lock stateLock (fun () ->
            if consumption <> Consumption.Fresh then
                Error(alreadyConsumedError ())
            else
                consumption <- Consumption.Interactive
                let window = ExpectWindow(windowChars, transcriptChars)

                let rawPump (stream: Stream option) encoding tee : Task =
                    match stream with
                    | None -> Task.CompletedTask
                    | Some s ->
                        let append =
                            if filterAnsi then
                                let filter = AnsiEscapeFilter()
                                fun text -> window.AppendFiltered(filter, text)
                            else
                                fun text -> window.Append text

                        task {
                            try
                                do! Pump.readTextUntilDone s encoding tee append isTearingDown CancellationToken.None
                            with ex ->
                                // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087)
                                // before it reaches `Complete`/`interactiveOutcome` below.
                                ExceptionDispatchInfo.Throw(reportedPumpFault ex)
                        }
                        :> Task

                // A PTY run has ONE terminal device, so `stderrStream` is `None` there and only the
                // merged reader runs; a plain (piped) run keeps both, and both feed the one window in
                // arrival order — an interactive session is about what the terminal shows, so the two
                // are deliberately not tagged apart here (`OutputEventsAsync` is the verb that tags).
                let stdoutPump = rawPump stdoutStream config.StdoutEncoding config.StdoutTee
                let stderrPump = rawPump stderrStream config.StderrEncoding config.StderrTee
                let pumps = [| stdoutPump; stderrPump |]

                // A fault in either reader kills the tree at once, so a still-producing child can't wedge
                // `waitWithTimeout()` (below) by blocking on a pipe its dead reader no longer drains.
                killTreeOnPumpFault stdoutPump
                killTreeOnPumpFault stderrPump

                // Close the window as soon as BOTH readers finish, independently of the exit wait, so a
                // pattern wait ends promptly on the child's end-of-output instead of burning its whole
                // timeout. Never faults (it stashes the reader fault into the window instead), so
                // awaiting it below can't mask the pump fault `interactiveOutcome` re-raises.
                let drained =
                    task {
                        let mutable fault: exn option = None

                        try
                            do! Task.WhenAll pumps
                        with ex ->
                            fault <- Some ex

                        window.Complete fault
                    }
                    :> Task

                interactiveOutcome <-
                    task {
                        // `ensureBufferedWait()`, not a raw `waitWithTimeout()`: a readiness probe can
                        // already own the one shared exit wait while deliberately leaving `consumption`
                        // at `Fresh` (see `raceReadinessAgainstExit`), which is exactly the state this
                        // session claims from — so a probe-then-session sequence must join that wait
                        // rather than start a second `host.Wait()` racing its reap (KB K-016). It is
                        // reentrant on `stateLock`, which this setup already holds.
                        let! outcome = ensureBufferedWait ()
                        do! drained
                        // Re-await the readers themselves so a genuine read fault still surfaces to
                        // whoever awaits this outcome (`ExitTask`/`StopAsync`), exactly as it does for
                        // the streaming sessions.
                        do! Task.WhenAll pumps
                        conclude outcome
                        return outcome
                    }

                // Observe any fault on this otherwise fire-and-forget task (the expect-only case, where
                // the caller never awaits the exit).
                observeFault interactiveOutcome

                Ok window)

    /// Claim stdout for a Content-Length parser supplied by `ContentLengthSession`, while draining
    /// stderr independently so a chatty protocol server cannot block. The parser receives the raw
    /// stdout stream and byte-exact tee; its task becomes this handle's shared interactive outcome.
    member internal _.StartContentLengthSession
        (startStdoutPump: Stream -> Stream option -> (unit -> bool) -> Task)
        : Result<unit, ProcessError> =
        ArgumentNullException.ThrowIfNull startStdoutPump

        lock stateLock (fun () ->
            if consumption <> Consumption.Fresh then
                Error(alreadyConsumedError ())
            else
                match stdoutStream with
                | None -> Error(ProcessError.Unsupported "Content-Length sessions require piped stdout")
                | Some stdout ->
                    consumption <- Consumption.Interactive

                    let stdoutPump =
                        task {
                            try
                                do! startStdoutPump stdout config.StdoutTee isTearingDown
                            with
                            | :? ObjectDisposedException when isTearingDown () ->
                                // This handle's teardown closed stdout while the framed reader was active.
                                ()
                            | :? IOException when isTearingDown () ->
                                // This handle's teardown broke the pipe; the run outcome remains authoritative.
                                ()
                            | ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
                        }
                        :> Task

                    let stderrPump =
                        match stderrStream with
                        | None -> Task.CompletedTask
                        | Some stderr ->
                            Pump.readTextUntilDone
                                stderr
                                config.StderrEncoding
                                config.StderrTee
                                ignore
                                isTearingDown
                                CancellationToken.None

                    let pumps = [| stdoutPump; stderrPump |]
                    killTreeOnPumpFault stdoutPump
                    killTreeOnPumpFault stderrPump

                    interactiveOutcome <-
                        task {
                            let! outcome = ensureBufferedWait ()
                            do! Task.WhenAll pumps
                            conclude outcome
                            return outcome
                        }

                    observeFault interactiveOutcome
                    Ok())

    /// The `CommandConfig` this handle was started from. Internal: `PtySession` reads the program name
    /// and the terminal encoding from it.
    member internal _.Config: CommandConfig = config

    /// Cancelled once this handle's own teardown begins. Internal: buffered pumps use this marker to
    /// distinguish a routine broken-pipe/close race caused by teardown from a genuine I/O failure.
    member internal _.DisposalToken: CancellationToken = disposalCts.Token

    /// Cancelled when a terminal/shared-exit path takes ownership of ending a streaming session. It is
    /// separate from `DisposalToken`, because a bounded Backpressure writer must wake before the shared
    /// outcome is awaited while the pump's I/O fault classification still needs the actual teardown bit.
    member internal _.BackpressureToken: CancellationToken = backpressureCts.Token

    /// Whether this run actually has a live pseudo-terminal behind it — what `PtySession` asks before
    /// choosing the carriage return a terminal expects for Enter over a plain pipe's line feed. Read
    /// from the spawned host (`ResizePty` is `Some` exactly for a pty-backed run) as well as the
    /// config, so a test double that models a PTY (`FakeProcess.WithPty`) answers the same as the real
    /// spawn it stands in for, rather than diverging on a config field it never set.
    member internal _.HasPseudoTerminal: bool = hasPseudoTerminal

    /// Wait until a stdout line satisfies `predicate`, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Consumed lines are not re-delivered; a
    /// later `StdoutLinesAsync`/`FinishAsync` sees the rest.
    member this.WaitForLineAsync
        (predicate: Func<string, bool>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<string, ProcessError>> =
        ArgumentNullException.ThrowIfNull predicate

        if not (this.StartStdoutStreaming()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                // Clamp so an out-of-range timeout can't throw out of the CTS constructor. The clamped
                // value is also what gets reported in NotReady below — uniform with
                // `ReadinessProbe.waitForPortUsing`/`waitForCoreUsing`: an over-long requested timeout is
                // silently capped at ~24.8 days, so reporting the raw, un-clamped value would claim a
                // budget longer than what was actually enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                use timeoutCts = new CancellationTokenSource(armedTimeout, config.TimeProvider)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                try
                    let mutable matched = false
                    let mutable found = Unchecked.defaultof<string>

                    while not matched do
                        let! line = stdoutChannel.Reader.ReadAsync linked.Token

                        if predicate.Invoke line then
                            found <- line
                            matched <- true

                    return Ok found
                with
                | :? OperationCanceledException ->
                    // The caller's token wins over the deadline: a cancelled wait is an error, a
                    // timed-out one is "not ready yet".
                    if cancellationToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled config.Program)
                    else
                        return Error(ProcessError.NotReady(config.Program, armedTimeout))
                | :? ChannelClosedException as ex ->
                    // The stdout pump completed the channel. A clean EOF (stdout ended before a matching
                    // line) means the readiness condition was never met → NotReady. But a pump FAULT (a
                    // throwing `OnStdoutLine`/`StdoutTee` handler, or a decode/IO error) completed it WITH
                    // that exception as the InnerException; re-raise it (preserving its stack) so a real
                    // bug surfaces exactly as it does through `FinishAsync`/`StdoutLinesAsync`, rather than
                    // being masked as a spurious readiness timeout that also returns before the deadline.
                    match ex.InnerException with
                    | null -> return Error(ProcessError.NotReady(config.Program, armedTimeout))
                    | inner ->
                        ExceptionDispatchInfo.Throw inner
                        return Unchecked.defaultof<_>
            }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` once the shared
    /// `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). Every connect
    /// attempt and polling backoff shares that one deadline, so a slow or non-cooperative connect can
    /// never overrun a short `timeout` — see `ReadinessProbe.waitForCoreUsing` for the full contract,
    /// including the ratified scheduler-bounded window at the deadline. If the child exits before the
    /// port opens, the endpoint is dialled exactly once more — bounded by what is left of `timeout` and
    /// by a brief internal grace, so a port opened immediately before the child terminated is reported
    /// as `Ok` instead of being lost — and this then returns `NotReady` rather than polling out the full
    /// `timeout`; a cancelled token or an already-spent deadline still wins over that last dial. That is
    /// the same early-exit contract `WaitForHttpAsync`/`WaitForSocketAsync`/`WaitForAsync` honour, and it
    /// runs via the one reap-once exit wait the rest of the handle shares (so a later
    /// `WaitAsync`/`ProfileAsync` still reports the real exit). Background-drains (and discards) the child's piped stdout/stderr for the
    /// duration of the poll — like `WaitForLineAsync`, so a child that writes more than one OS pipe buffer
    /// of startup output (~64 KiB on Linux) before becoming ready can't block in `write()` and spuriously
    /// time out this probe — but unlike `WaitForLineAsync`, the drained bytes are discarded rather than
    /// handed back, and draining stops once the probe concludes rather than continuing as an established
    /// streaming session. A capture verb (`OutputStringAsync`/`OutputBytesAsync`/`StdoutLinesAsync`/
    /// `OutputEventsAsync`) called AFTER this probe therefore only sees what the child wrote after the
    /// probe concluded, not the full run — the same "doesn't compose with a subsequent fresh capture"
    /// limitation `WaitForLineAsync` already documents, now uniform across all five readiness probes. If
    /// a buffered/streaming verb already claimed the pipes before this call, that verb's own pump is
    /// already draining them and this probe leaves them alone (no second reader).
    member _.WaitForPortAsync
        (endpoint: IPEndPoint, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull endpoint
        waitForPort endpoint timeout cancellationToken

    /// Wait until a connection to the Unix domain socket at `path` succeeds, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). Behaves
    /// exactly like `WaitForPortAsync` (same deadline mechanics, same early-exit-on-child-death contract,
    /// same background stdout/stderr draining), but dials `AddressFamily.Unix` instead of TCP — see
    /// `ReadinessProbe.waitForSocket`/`waitForCoreUsing` for the full contract. Requires the host to
    /// support `AF_UNIX` sockets (Windows 10 1809+, any current Linux/macOS via .NET's own requirement);
    /// on a host without that support this returns `Error(ProcessError.Unsupported ...)` immediately,
    /// before ever attempting to dial — never a silent downgrade or an inevitable hang. A path that
    /// cannot fit the platform's Unix-socket address fails immediately with `ArgumentOutOfRangeException`
    /// rather than being retried as if no listener were present.
    member _.WaitForSocketAsync
        (path: string, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull path

        match ReadinessProbe.unixDomainSocketsSupported (fun () -> Socket.OSSupportsUnixDomainSockets) with
        | Error err -> Task.FromResult(Error err)
        | Ok() ->
            let endpoint = UnixDomainSocketEndPoint path
            waitForSocket endpoint timeout cancellationToken

    /// Poll `uri` with HTTP GET until a response passes the default 2xx check, or fail with `NotReady`
    /// once `timeout` expires (or `Cancelled` if `cancellationToken` fires first). Connection failures,
    /// DNS failures, and request cancellations caused by the shared deadline are retried every 50ms.
    /// If the child exits before a satisfactory response arrives, exactly one more request is sent —
    /// bounded by what is left of `timeout` and by a brief internal grace — and this returns `NotReady`
    /// unless that last response is satisfactory, exactly as `WaitForPortAsync` describes.
    /// While polling, the child's piped stdout/stderr are background-drained and discarded exactly like
    /// `WaitForPortAsync`, so startup output cannot block a chatty child before it becomes ready. `uri`
    /// must be absolute; a relative URI throws `ArgumentException` before polling begins.
    member this.WaitForHttpAsync
        (uri: Uri, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri

        this.WaitForHttpAsync(uri, ReadinessProbe.defaultHttpSuccess, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but sends requests through the
    /// caller-owned `client`. ProcessKit neither mutates nor disposes the client.
    member this.WaitForHttpAsync
        (uri: Uri, client: HttpClient, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        this.WaitForHttpAsync(uri, client, ReadinessProbe.defaultHttpSuccess, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but treats only status codes from
    /// `acceptableStatusCodes` as ready. The sequence is materialized once before polling, so every retry
    /// applies the same criteria. The sequence must contain at least one status code.
    member this.WaitForHttpAsync
        (uri: Uri, acceptableStatusCodes: seq<int>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken) : Task<
                                                                                                                                Result<
                                                                                                                                    unit,
                                                                                                                                    ProcessError
                                                                                                                                 >
                                                                                                                             >
        =
        ReadinessProbe.validateAbsoluteUri uri
        let isSatisfactory = httpStatusPredicate acceptableStatusCodes

        this.WaitForHttpAsync(uri, isSatisfactory, timeout, cancellationToken)

    /// Like the status-code overload, but sends requests through the caller-owned `client`. ProcessKit
    /// neither mutates nor disposes the client.
    member this.WaitForHttpAsync
        (
            uri: Uri,
            client: HttpClient,
            acceptableStatusCodes: seq<int>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull client
        let isSatisfactory = httpStatusPredicate acceptableStatusCodes
        this.WaitForHttpAsync(uri, client, isSatisfactory, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but uses `isSatisfactory` to inspect
    /// each response. A false result is retried; an exception from caller-supplied validation propagates.
    /// `uri` must be absolute.
    member _.WaitForHttpAsync
        (
            uri: Uri,
            isSatisfactory: Func<HttpResponseMessage, bool>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull isSatisfactory
        waitForHttp uri isSatisfactory timeout cancellationToken

    /// Like the predicate overload, but sends requests through the caller-owned `client`. ProcessKit
    /// neither mutates nor disposes the client.
    member _.WaitForHttpAsync
        (
            uri: Uri,
            client: HttpClient,
            isSatisfactory: Func<HttpResponseMessage, bool>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull client
        ArgumentNullException.ThrowIfNull isSatisfactory
        waitForHttpWithClient client uri isSatisfactory timeout cancellationToken

    /// Poll `probe` until it returns true, or fail with `NotReady` once the shared `timeout` deadline
    /// elapses (or `Cancelled` if `cancellationToken` fires first). The deadline is honored even if
    /// `probe` never completes — or blocks synchronously without ever returning a task: the invocation
    /// is isolated on the thread pool and raced against the shared deadline, and the caller's token
    /// takes priority over a concurrent success. The API cannot force a caller-owned `probe` to stop, so
    /// an abandoned invocation keeps running in the background, but its late outcome is safely observed
    /// (a late fault never becomes an unobserved task exception). See `ReadinessProbe.waitForCoreUsing` for
    /// the full contract, including the ratified scheduler-bounded window at the deadline. If the child
    /// exits before `probe` returns true, `probe` is invoked exactly once more — bounded by what is left
    /// of `timeout` and by a brief internal grace, so readiness published immediately before the child
    /// terminated is reported as `Ok` instead of being lost — and this then returns `NotReady` rather
    /// than polling out the full `timeout`. Callers therefore must expect one extra `probe` invocation
    /// after the child exits; a cancelled token or an already-spent deadline suppresses it. That is the
    /// same early-exit contract `WaitForHttpAsync`/`WaitForPortAsync` honour, and it runs via the one
    /// reap-once exit wait the rest of the handle shares (so a later `WaitAsync`/`ProfileAsync` still
    /// reports the real exit). Background-drains (and discards) the child's piped stdout/stderr for
    /// the duration of the poll, exactly like `WaitForPortAsync` — see its doc for what that does and
    /// doesn't compose with afterward.
    member _.WaitForAsync
        (probe: Func<Task<bool>>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull probe
        waitForCustom probe timeout cancellationToken

    /// A memoized task that waits for the process to exit (draining its pipes) without reaping it —
    /// the racing primitive behind `WaitAnyAsync`/`WaitAllAsync`. Built exactly once under `stateLock`
    /// (so concurrent `WaitAnyAsync`/`WaitAllAsync` on the same handle can't create two racing waits),
    /// reusing whichever consumption already owns the pipes instead of ever starting a second reader:
    /// - `StdoutStreaming`/`EventStreaming`/`Interactive`: the session's own combined outcome.
    /// - `Buffered` (a capture verb already started — the "verb, then WaitAny/WaitAll" order): the
    ///   verb's own single wait, shared via `ensureBufferedWait` (memoized under the same lock, so it
    ///   is observed here regardless of which of the two reached it first).
    /// - `Fresh` (WaitAny/WaitAll arrives first, and no readiness probe already started the shared
    ///   wait either): claims the buffered slot itself and runs its own drains, so a terminal verb
    ///   called afterwards on the same handle is refused (`alreadyConsumedError`) rather than racing
    ///   a second reader. Even here the wait itself goes through `ensureBufferedWait()`, not a raw
    ///   `waitWithTimeout()`: a readiness probe's own early-exit detection (`waitForHttp` et al.) can
    ///   already have started the one shared `host.Wait()` while deliberately leaving `consumption`
    ///   at `Fresh` (so a later buffered verb can still claim the pipes) — `ensureBufferedWait()`
    ///   reuses that wait when it exists, or starts the sole wait itself when this genuinely is the
    ///   first consumer, either way guaranteeing exactly one `host.Wait()`/reap per handle.
    member internal _.ExitTask: Task<Outcome> =
        // WaitAny/WaitAll are shared terminal waits. Release a bounded streaming/frame writer before
        // returning the memoized outcome task, while leaving `disposalCts` as the actual teardown marker.
        cancelBackpressureWriters ()

        lock stateLock (fun () ->
            if not exitStarted then
                exitStarted <- true

                exitTaskValue <-
                    if consumption = Consumption.StdoutStreaming then
                        streamOutcome
                    elif consumption = Consumption.StdoutChunkStreaming then
                        chunkOutcome
                    elif consumption = Consumption.EventStreaming then
                        // The event pumps already drain both pipes; reuse their shared outcome rather than
                        // starting our own drains here, which would race a second reader on the same streams.
                        eventOutcome
                    elif consumption = Consumption.Interactive then
                        // An interactive raw session already drains the pipes; reuse its shared outcome for
                        // the same reason as the event session above.
                        interactiveOutcome
                    elif consumption = Consumption.Buffered then
                        // A buffered verb already claimed the pipes; share its single wait (memoized under
                        // this same lock) rather than starting a second pair of readers and a second
                        // `host.Wait()`. Its own pumps drain the pipes, so the reused wait needs none.
                        ensureBufferedWait ()
                    else
                        // Fresh: no verb has claimed the pipes yet. Claim the buffered slot (inline — we
                        // already hold `stateLock`) so a terminal verb called after WaitAny/WaitAll on the
                        // same handle is refused rather than racing a second reader on these pipes.
                        consumption <- Consumption.Buffered

                        task {
                            // These drains are fire-and-forget for a race loser the caller may dispose
                            // mid-drain, so they must complete quietly on teardown rather than fault unobserved.
                            let stdoutDrain =
                                Pump.drainDiscardOrEmptyUntilDone stdoutStream CancellationToken.None

                            let stderrDrain =
                                Pump.drainDiscardOrEmptyUntilDone stderrStream CancellationToken.None

                            // `ensureBufferedWait()`, not `waitWithTimeout()`: a readiness probe may already
                            // own the one shared exit wait (see the doc comment above), and `consumption`
                            // staying `Fresh` until this very claim is exactly what lets that be true — so a
                            // raw `waitWithTimeout()` here would start a second, independent `host.Wait()`
                            // racing the probe's, reproducing the reap-once bug (KB K-016) `ensureBufferedWait`
                            // exists to prevent. `ensureBufferedWait()` reuses the probe's wait if one is
                            // already in flight, or starts it fresh otherwise — reentrant on `stateLock`,
                            // which we already hold here (see the `Buffered` branch above, which does the same).
                            let! outcome = ensureBufferedWait ()
                            do! Task.WhenAll([| stdoutDrain; stderrDrain |])
                            // Racing this handle to exit *is* its completion (conclude does not reap, so the
                            // no-reap contract holds), so a `WaitAny`/`WaitAll`-only run still records its
                            // exit/metrics/span and clears the in-flight mark. Once-guarded, so a terminal verb
                            // afterwards (already refused by the buffered claim above) can't double-count.
                            conclude outcome
                            return outcome
                        }

            exitTaskValue)

    /// Wait for the first of `processes` to exit; returns its index and outcome. Does not reap any
    /// of them — dispose them yourself. Safe to call on a handle a buffered verb (`OutputStringAsync`/
    /// `OutputBytesAsync`/`WaitAsync`/`ProfileAsync`) already started: it reuses that verb's own wait
    /// (see `ExitTask`) rather than racing a second reader on the same pipes.
    ///
    /// `processes` must be non-null, non-empty, and free of null elements — each is a programmer
    /// error, not a process outcome, so it throws (`ArgumentNullException` for a null array,
    /// `ArgumentException` for an empty array or a null element) rather than reporting through a
    /// `Result`. Symmetric with `WaitAllAsync` on all three axes: error channel, empty input, and
    /// null handling. If a pump backing one of the raced `ExitTask`s faults, that exception propagates
    /// unchanged from the awaited task — also not wrapped in a `Result`.
    static member WaitAnyAsync(processes: RunningProcess[]) : Task<WaitAnyResult> =
        ArgumentNullException.ThrowIfNull processes

        if processes.Length = 0 then
            raise (ArgumentException("expected at least one process", nameof processes))

        if processes |> Array.exists (fun p -> obj.ReferenceEquals(p, null)) then
            raise (ArgumentException("processes must not contain a null element", nameof processes))

        task {
            let tasks = processes |> Array.map (fun p -> p.ExitTask)
            let! completed = Task.WhenAny tasks
            let index = tasks |> Array.findIndex (fun t -> obj.ReferenceEquals(t, completed))
            let! outcome = completed
            return WaitAnyResult(index, outcome)
        }

    /// Wait for all of `processes` to exit; returns their outcomes in order. Does not reap them.
    ///
    /// `processes` must be non-null, non-empty, and free of null elements — each is a programmer
    /// error, not a process outcome, so it throws (`ArgumentNullException` for a null array,
    /// `ArgumentException` for an empty array or a null element) rather than reporting through a
    /// `Result`. Symmetric with `WaitAnyAsync` on all three axes: error channel, empty input, and null
    /// handling. If a pump backing one of the `ExitTask`s faults, that exception propagates unchanged
    /// from `Task.WhenAll` — also not wrapped in a `Result`.
    static member WaitAllAsync(processes: RunningProcess[]) : Task<Outcome[]> =
        ArgumentNullException.ThrowIfNull processes

        if processes.Length = 0 then
            raise (ArgumentException("expected at least one process", nameof processes))

        if processes |> Array.exists (fun p -> obj.ReferenceEquals(p, null)) then
            raise (ArgumentException("processes must not contain a null element", nameof processes))

        processes |> Array.map (fun p -> p.ExitTask) |> Task.WhenAll

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            cancelBackpressureWriters ()
            disposalCts.Cancel()
            // Stop and release the idle-timeout watchdog (if any); a pump still resetting it races this
            // harmlessly (`Reset` after disposal is a no-op).
            idleTimer |> Option.iter (fun t -> (t :> IDisposable).Dispose())
            // Clear `runs.active` for a handle disposed without ever reaching a terminal verb — a no-op
            // (guarded by `RunTelemetryScope`'s once-guard, `telemetry.Abandon()` racing `Conclude`)
            // when a terminal verb already ran, so a normal verb-then-dispose sequence, or a repeated
            // dispose, cannot double-decrement.
            markAbandoned ()
            host.Teardown()

/// Guarded construction of the handle handed back to a caller once a tree has been spawned. Shared
/// by the two sites that turn an already-spawned `RunningHost` into the returned `RunningProcess`:
/// `JobRunner.start` (a private, per-run group) and `ProcessGroup.StartAsync` (a shared group).
module internal RunningProcess =

    let private build (host: RunningHost) (extraFds: (int * Stream) list) : Task<RunningProcess> =
        task {
            let constructed =
                try
                    Ok(RunningProcess(host, extraFds))
                with ex ->
                    Error ex

            match constructed with
            | Ok running -> return running
            | Error ex ->
                do! host.Teardown()
                ExceptionDispatchInfo.Throw ex
                return Unchecked.defaultof<_>
        }

    /// Build `RunningProcess host` in try/with. Constructing the handle is non-throwing in practice
    /// — its observability (`Log.spawn` / `RunTelemetryScope.Start`) swallows any sink fault, see the
    /// comment on those calls in the type above — but guard it anyway: should the constructor ever
    /// fault after the native spawn, reap the tree and release the container via `host.Teardown()`
    /// here so the child is deterministically killed/reaped instead of being orphaned to GC-time
    /// kill-on-close, then re-raise the original fault (never a silent swallow of a genuine
    /// construction bug — the caller still sees it, just without a leaked process tree).
    let buildGuarded (host: RunningHost) : Task<RunningProcess> = build host []

    let buildGuardedWithExtraFds (host: RunningHost) (extraFds: (int * Stream) list) : Task<RunningProcess> =
        build host extraFds
