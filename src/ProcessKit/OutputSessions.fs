namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Runtime.ExceptionServices
open System.Text
open System.Threading
open System.Threading.Tasks

/// A `Pump.LineBuffer` whose captured state stays safely readable from the consumer's thread while
/// its pump may still be writing into it — the line-capture counterpart of `Pump.RawSink`, and for
/// the same reason. The bounded post-exit output drain (`PostExitDrain`) can end a verb's wait on a
/// pump whose pipe a surviving descendant holds open, and the verb must then still report what WAS
/// captured; reading the buffer without first awaiting that pump IS a concurrent access, and
/// `LineBuffer` retains its lines in a `LinkedList` that a concurrent `Add` would be walking.
///
/// One uncontended `Monitor` per framed line, on a path that has just decoded and allocated that
/// line — immeasurable next to it, and it buys every reader of these counters (`FinishAsync`'s
/// stderr capture, the buffered verbs' captures) a consistent snapshot instead of a race.
type internal GuardedLineBuffer(policy: OutputBufferPolicy) =
    let gate = obj ()
    let buffer = Pump.LineBuffer policy

    member _.Add(line: string) = lock gate (fun () -> buffer.Add line)
    member _.Text = lock gate (fun () -> buffer.Text)
    member _.Truncated = lock gate (fun () -> buffer.Truncated)
    member _.TooLarge = lock gate (fun () -> buffer.TooLarge)
    member _.TotalLines = lock gate (fun () -> buffer.TotalLines)
    member _.TotalBytes = lock gate (fun () -> buffer.TotalBytes)

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

/// Everything that reads ONE handle's stdout/stderr: the pumps themselves, the streaming channels
/// they fill, the line/byte counters they publish, and the five session shapes a claimed handle can
/// take (stdout lines, stdout byte chunks, stderr byte chunks, merged output events, and a raw
/// interactive session).
///
/// The claim decisions are NOT here — `ConsumptionGate` owns which of these sessions a handle is
/// allowed to start, and calls the `Start*` members below from inside its own lock so a session is
/// always fully constructed before any other verb can observe the claim. Nor are the bounds here:
/// every session's combined outcome joins its pumps through `RunTerminal`, so the post-exit drain
/// bound and the timeout/post-kill anchoring are the shared ones, not per-session copies.
///
/// Platform-agnostic by construction: it is handed two already-wrapped `Stream option`s and never
/// asks what is behind them — a pipe, a pty master, a ConPTY view or a test double all pump the same
/// way, and the platform divergence stays where the spawn made it.
type internal OutputSessions
    (
        config: CommandConfig,
        terminal: RunTerminal,
        stdoutStream: Stream option,
        stderrStream: Stream option,
        // `sharedExitWait`: the handle's ONE memoized exit wait (`ConsumptionGate.EnsureBufferedWait`).
        // The interactive sessions join it rather than starting a second `host.Wait()`, because a
        // readiness probe can already own it while deliberately leaving the handle `Fresh` (KB K-016).
        sharedExitWait: unit -> Task<Outcome>,
        // `conclude`: record the run as concluded — once-guarded telemetry, so a session that concludes
        // and a concurrent terminal verb sharing the same wait cannot double-count.
        conclude: Outcome -> unit,
        // `discardingStdoutStream`: whether the terminal `FinishAsync` has latched the retain-nothing
        // stdout sink (`ConsumptionGate.DiscardingStdoutStream`). Read on every framed stdout line.
        discardingStdoutStream: unit -> bool
    ) =

    let isTearingDown () = terminal.IsTearingDown()

    let mutable stdoutLineCount = 0L
    let mutable stdoutChunkCount = 0
    let mutable stderrLineCount = 0L
    let mutable droppedStreamLineCount = 0

    // Cumulative bytes actually pumped into the stdout LINE streaming channel / the raw stdout CHUNK
    // channel, tracked only to feed an honest `ProcessError.OutputTooLarge.TotalBytes` on
    // `StreamFullMode.Error` overflow (T-297) — neither channel's own consumption path needs them.
    // `int64` because a long-running stream can plausibly exceed `Int32.MaxValue` bytes before the cap
    // (if any) ever trips; the saturating reads below take them back down to `int` the same way
    // `Pump.LineBuffer.TotalBytes` does.
    let mutable stdoutStreamedByteCount = 0L
    let mutable stderrStreamedByteCount = 0L
    let mutable stdoutChunkStreamedByteCount = 0L
    let mutable stderrChunkStreamedByteCount = 0L

    // The stderr capture a streaming/chunk session fills for `FinishAsync` (stdout goes to the
    // caller's channel; stderr is what `Finished` carries). Assigned by the session setup, which
    // `ConsumptionGate` runs under its lock before any consumer can observe the claim.
    let mutable stderrStreamBuffer = Unchecked.defaultof<GuardedLineBuffer>

    let mutable streamOutcome = Unchecked.defaultof<Task<Outcome>>
    let mutable chunkOutcome = Unchecked.defaultof<Task<Outcome>>

    // The stderr byte-chunk session's combined outcome (exit wait + the stderr chunk pump + the
    // retain-nothing stdout drain). `FinishAsync`/`ExitTask` share it exactly as they share the stdout
    // chunk session's own outcome.
    let mutable stderrChunkOutcome = Unchecked.defaultof<Task<Outcome>>

    // The event-streaming session's single combined outcome (waiting for exit + draining both pipes via
    // the two pumps). `ExitTask` reuses it for an `EventStreaming` handle so it does not start a second,
    // racing set of drains on the same streams.
    let mutable eventOutcome = Unchecked.defaultof<Task<Outcome>>

    // An interactive raw session's single combined outcome — the exit wait plus both readers draining.
    // `PtySession` owns unframed text; `ContentLengthSession` owns framed stdout plus a stderr drain.
    // `ExitTask` reuses it for either `Interactive` handle so it never starts racing readers.
    let mutable interactiveOutcome = Unchecked.defaultof<Task<Outcome>>

    // One sequence domain for both event pumps. The atomic increment records the order in which the
    // two independently-drained streams reach ProcessKit's line-framing boundary.
    let mutable outputEventSequence = 0L

    let bumpDroppedStreamLine () =
        Interlocked.Increment(&droppedStreamLineCount) |> ignore

    // `stdoutLineCount`/`stderrLineCount` are written by a background pump task and read from the
    // consumer's thread via `StdoutLineCount`/`StderrLineCount` and the `OutputTooLarge`-building
    // closures below — `Interlocked.Increment` to publish each write, `Volatile.Read` to read a fresh
    // value, the same atomic approach `droppedStreamLineCount` already uses.
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

    let bumpStderrChunkStreamedBytes (delta: int) =
        Interlocked.Add(&stderrChunkStreamedByteCount, int64 delta) |> ignore

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

    let readStderrChunkStreamedByteCount () =
        Volatile.Read(&stderrChunkStreamedByteCount) |> saturateInt64ToInt

    // The streaming channels and their policy-aware writer live in `StreamChannel`: the stdout channel
    // is written by exactly one pump, the event channel by two (stdout + stderr), and either is bounded
    // when `config.StreamBuffer` opts in (else unbounded, as before).
    let stdoutChannel: StreamChannel.Channel<string> =
        StreamChannel.create config.StreamBuffer true

    // The byte-chunk session has its own channel type but shares the same configured capacity and
    // full-mode policy. It remains dormant unless `StdoutChunksAsync` claims this handle.
    let stdoutChunkChannel: StreamChannel.Channel<ReadOnlyMemory<byte>> =
        StreamChannel.create config.StreamBuffer true

    // The stderr byte-chunk session's own channel, dormant unless `StderrChunksAsync` claims this
    // handle. A separate channel rather than a shared one: the two chunk sessions are alternatives
    // (the claim gate lets exactly one of them own the pipes), and one item on each channel means one
    // read of THAT stream — merging them would lose which pipe a chunk came from, the very distinction
    // this verb exists to keep.
    let stderrChunkChannel: StreamChannel.Channel<ReadOnlyMemory<byte>> =
        StreamChannel.create config.StreamBuffer true

    let eventChannel: StreamChannel.Channel<OutputEvent> =
        StreamChannel.create config.StreamBuffer false

    // Write one item to a line/event streaming channel per `config.StreamBuffer` (see
    // `StreamChannel.writeItem`): unbounded `TryWrite` when unset, else backpressure / drop / fail-loud.
    // Bound `Backpressure` to the handle's backpressure token so terminal/shared-exit paths can release
    // an abandoned writer before they await its outcome; the separate disposal token remains the
    // teardown marker used by the pump fault classifier.
    let writeStreamItem
        (channel: StreamChannel.Channel<'T>)
        (buildOverflowError: StreamBufferPolicy -> ProcessError)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        let pending =
            StreamChannel.writeItem
                config.StreamBuffer
                terminal.BackpressureToken
                channel
                buildOverflowError
                onDrop
                item

        if pending.IsCompletedSuccessfully then
            pending
        else
            ValueTask(
                task {
                    try
                        do! pending
                    with :? OperationCanceledException when terminal.BackpressureToken.IsCancellationRequested ->
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
            terminal.ChunkBackpressureToken
            stdoutChunkChannel
            (fun _policy ->
                ProcessError.OutputTooLarge(config.Program, None, None, 0, readStdoutChunkStreamedByteCount ()))
            bumpDroppedStreamLine
            item

    // The stderr twin of `writeChunkItem`, on the stderr chunk channel and with the same honest
    // totals: a raw stderr chunk has no line structure either, so `LineLimit`/`ByteLimit`/`TotalLines`
    // stay `None`/`0` and the cumulative chunk BYTES are the one real total this site can report. It
    // shares `ChunkBackpressureToken` with the stdout chunk channel because the two sessions are
    // alternatives — at most one of them ever has a bounded writer parked on it — so the terminal
    // paths keep releasing an abandoned chunk consumer through the one token they already cancel.
    let writeStderrChunkItem (item: ReadOnlyMemory<byte>) : ValueTask =
        StreamChannel.writeItem
            config.StreamBuffer
            terminal.ChunkBackpressureToken
            stderrChunkChannel
            (fun _policy ->
                ProcessError.OutputTooLarge(config.Program, None, None, 0, readStderrChunkStreamedByteCount ()))
            bumpDroppedStreamLine
            item

    // Invoke a per-line callback without allocating a closure per line (which `Option.iter (fun cb ->
    // cb.Invoke line)` would, capturing `line`). On the hot per-line path.
    let invokeLine (callback: Action<string> option) (line: string) =
        match callback with
        | Some cb -> cb.Invoke line
        | None -> ()

    // Reclassify a fault escaping a stdout/stderr pump into a typed `ProcessError.Io` when it is one of
    // the two exception types a genuine OS read fault surfaces as. Only ever reached once the routine
    // teardown-race case has already been excluded: the streaming pumps route through
    // `Pump.readLinesUntilDone`'s `genuineReadFault` (`isTearingDown` by the disposal token) first, and
    // the buffered pumps (`pumpToBuffer` / the discard drains / the raw stdout capture) gate on
    // `isTearingDown ()` themselves before calling this. That gate is load-bearing: a buffered verb
    // awaits its OWN pumps before its reap guard tears down, but a CONCURRENT `StopAsync`/`Dispose` on
    // the same handle can dispose the pipes while those pumps are still draining a large tail —
    // reclassifying that routine race as a genuine `ProcessError.Io` used to falsely fault the verb
    // (and, through the supervision layer, `SupervisionSession.Completion`). Any other pump fault (a
    // throwing line handler, a decoder failure, an already-typed `ProcessException` from
    // `StreamChannel`'s fail-loud bounded-channel mode) passes through unchanged — T-087.
    let reportedPumpFault (ex: exn) : exn =
        match ex with
        | :? IOException
        | :? ObjectDisposedException -> ProcessException(ProcessError.Io ex.Message) :> exn
        | _ -> ex

    // Captures into a `buffer` the CALLER owns (a `GuardedLineBuffer`), not one this task hands back on
    // completion: the bounded post-exit output drain can end the verb's wait on this pump while a
    // descendant still holds the pipe open, and the verb must then be able to report what was captured
    // without awaiting a task that may never complete.
    let pumpToBuffer
        (stream: Stream)
        encoding
        terminator
        tee
        (callback: Action<string> option)
        counter
        (buffer: GuardedLineBuffer)
        : Task =
        task {
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
        }
        :> Task

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

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = readStdoutLineCount ()

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = readStderrLineCount ()

    /// Stream items dropped so far by a bounded streaming policy's `StreamFullMode.DropOldest`/
    /// `DropNewest`. For line/event streams this counts dropped lines/events; for the chunk session it
    /// counts dropped chunks.
    member _.DroppedStreamLineCount = Volatile.Read(&droppedStreamLineCount)

    /// Whether a bounded streaming policy has dropped anything at all — the streaming analogue of a
    /// buffered verb's `ProcessResult.Truncated`, as `Finished` reports it.
    member _.AnyStreamLinesDropped = Volatile.Read(&droppedStreamLineCount) > 0

    /// The stderr a streaming/chunk session captured for `FinishAsync`. Valid only once such a session
    /// has been claimed — which is exactly when `FinishAsync` reads it. Empty by construction for the
    /// STDERR chunk session, whose stderr went to the caller as bytes (see `StartStderrChunkSession`).
    member _.SessionStderrText = stderrStreamBuffer.Text

    member _.SessionStderrTruncated = stderrStreamBuffer.Truncated
    member _.SessionStderrTooLarge = stderrStreamBuffer.TooLarge
    member _.SessionStderrTotalLines = stderrStreamBuffer.TotalLines
    member _.SessionStderrTotalBytes = stderrStreamBuffer.TotalBytes

    /// A fresh line capture under this run's `OutputBuffer` policy, owned by the buffered verb that
    /// asks for it (see `GuardedLineBuffer`: the verb, not the pump, must outlive the bound).
    member _.NewCaptureBuffer() = GuardedLineBuffer config.OutputBuffer

    /// Line-pump stdout into a caller-owned capture (the buffered text verb).
    member _.PumpStdoutBuffer(buffer: GuardedLineBuffer) : Task =
        match stdoutStream with
        | Some s ->
            pumpToBuffer
                s
                config.StdoutEncoding
                config.StdoutLineTerminator
                config.StdoutTee
                config.OnStdoutLine
                bumpStdoutLine
                buffer
        | None -> Task.CompletedTask

    /// Line-pump stderr into a caller-owned capture (both buffered capture verbs).
    member _.PumpStderrBuffer(buffer: GuardedLineBuffer) : Task =
        match stderrStream with
        | Some s ->
            pumpToBuffer
                s
                config.StderrEncoding
                config.StderrLineTerminator
                config.StderrTee
                config.OnStderrLine
                bumpStderrLine
                buffer
        | None -> Task.CompletedTask

    /// The raw stdout capture backing `OutputBytesAsync`, into a `Pump.RawSink` the VERB owns — same
    /// reason `PumpStdoutBuffer` takes its `GuardedLineBuffer`: the bounded post-exit output drain can
    /// end the verb's wait on this pump, and the bytes that did arrive must survive that.
    ///
    /// It shares the buffered-pump teardown race above: a concurrent `StopAsync`/`Dispose` can dispose
    /// the pipe mid-read. That is quiet here, and honest as well — the sink keeps everything read
    /// before the race instead of an empty capture. A genuine mid-run read fault (teardown not begun)
    /// still propagates unchanged — T-087.
    member _.CaptureRawStdout(sink: Pump.RawSink) : Task =
        task {
            try
                do! Pump.captureRawInto sink stdoutStream config.StdoutTee CancellationToken.None
            with (:? IOException | :? ObjectDisposedException) when isTearingDown () ->
                ()
        }
        :> Task

    /// Drain stdout to EOF, discarding it (`WaitAsync`/`ProfileAsync`), reporting a genuine read fault.
    member _.DrainStdoutDiscarding() = drainDiscardReporting stdoutStream

    /// Drain stderr to EOF, discarding it (`WaitAsync`/`ProfileAsync`), reporting a genuine read fault.
    member _.DrainStderrDiscarding() = drainDiscardReporting stderrStream

    /// The fire-and-forget discard drains a `WaitAny`/`WaitAll` claim starts on a still-fresh handle:
    /// a race loser's handle may be disposed mid-drain, so these complete quietly on teardown rather
    /// than faulting unobserved.
    member _.DrainStdoutQuietly() =
        Pump.drainDiscardOrEmptyUntilDone stdoutStream CancellationToken.None

    member _.DrainStderrQuietly() =
        Pump.drainDiscardOrEmptyUntilDone stderrStream CancellationToken.None

    /// The stdout LINE session's one enumerator source. Handing it out is `ConsumptionGate`'s decision;
    /// this is only where the lines are.
    member _.StdoutLines: IAsyncEnumerable<string> = stdoutChannel.Reader.ReadAllAsync()

    /// Read the next framed stdout line, for `WaitForLineAsync`'s predicate loop.
    member _.ReadStdoutLineAsync(cancellationToken: CancellationToken) =
        stdoutChannel.Reader.ReadAsync cancellationToken

    /// The stdout CHUNK session's one enumerator source.
    member _.StdoutChunks: IAsyncEnumerable<ReadOnlyMemory<byte>> =
        stdoutChunkChannel.Reader.ReadAllAsync()

    /// The stderr CHUNK session's one enumerator source.
    member _.StderrChunks: IAsyncEnumerable<ReadOnlyMemory<byte>> =
        stderrChunkChannel.Reader.ReadAllAsync()

    /// The merged output-event session's one enumerator source.
    member _.OutputEvents: IAsyncEnumerable<OutputEvent> =
        eventChannel.Reader.ReadAllAsync()

    /// The stdout line session's combined outcome (exit wait + both pumps drained).
    member _.LineOutcome = streamOutcome

    /// The stdout chunk session's combined outcome.
    member _.ChunkOutcome = chunkOutcome

    /// The stderr chunk session's combined outcome.
    member _.StderrChunkOutcome = stderrChunkOutcome

    /// The event session's combined outcome.
    member _.EventOutcome = eventOutcome

    /// The interactive session's combined outcome (`PtySession`/`ContentLengthSession`).
    member _.InteractiveOutcome = interactiveOutcome

    /// Build the stdout LINE-streaming session: the stdout line pump (which either queues each framed
    /// line for the caller's enumerator or, once the terminal discard latch is set, drops it), the
    /// stderr capture pump, and the combined outcome both `FinishAsync` and `ExitTask` share.
    ///
    /// Called by `ConsumptionGate.TryClaimStdoutStreaming` from inside its lock, on the ONE call that
    /// claims the session from fresh — so the whole session is constructed (channel + pumps + outcome)
    /// before any concurrent verb can observe the claim.
    member _.StartLineSession() =
        let stderrBuffer = GuardedLineBuffer config.OutputBuffer
        stderrStreamBuffer <- stderrBuffer

        // Where a framed stdout line goes. Normally into the line channel for the streaming consumer;
        // once a terminal-only `FinishAsync` has latched the discard (see
        // `ConsumptionGate.DiscardingStdoutStream`) the line is dropped instead — it was still framed,
        // teed and handed to `OnStdoutLine` exactly as on the streamed path, it is only never queued,
        // because no reader for the channel exists and the same latch makes the claim gate refuse to
        // create one (that refusal is what keeps this drop from being a silent one). Nothing being
        // queued also means the channel's `StreamBuffer` capacity has nothing left to overflow: no
        // fail-loud `OutputTooLarge` and no drop bookkeeping can fire from a stream that retains
        // nothing, so the byte counter feeding that diagnostic is left alone too.
        let sinkStdoutLine (line: string) : ValueTask =
            if discardingStdoutStream () then
                ValueTask.CompletedTask
            else
                bumpStdoutStreamedBytes (int64 (Encoding.UTF8.GetByteCount line) + 1L)

                writeStreamItem
                    stdoutChannel
                    (fun policy ->
                        // One channel item is one framed stdout line, 1:1, so the channel's item
                        // capacity IS a genuine line limit and `readStdoutLineCount()` is the true
                        // count of lines produced before the cap tripped — both stayed honest
                        // already. `TotalBytes` was hardcoded `0` before T-297; it now reports the
                        // UTF-8 size of those lines using the same "own bytes + 1 separator byte"
                        // accounting `Pump.LineBuffer`'s doc comment explains (a small, deliberate
                        // over-count, never an under-count) — this streaming channel retains
                        // nothing to re-scan, so the cost is tracked incrementally instead.
                        ProcessError.OutputTooLarge(
                            config.Program,
                            Some policy.Capacity,
                            None,
                            readStdoutLineCount (),
                            readStdoutStreamedByteCount ()
                        ))
                    bumpDroppedStreamLine
                    line

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
                                sinkStdoutLine line)
                            None
                            (fun () -> terminal.DisposalToken.IsCancellationRequested)

                    stdoutChannel.TryComplete() |> ignore
                with ex ->
                    // A pump fault — a throwing `OnStdoutLine` handler, `StreamFullMode.Error`
                    // tripping its cap, or a genuine OS read fault (reclassified into
                    // `ProcessError.Io` by `reportedPumpFault` — T-087) — must still complete the
                    // channel, carrying the error, so a `StdoutLinesAsync` consumer observes it
                    // instead of hanging on a reader that never ends. Re-raise (preserving the
                    // original stack; `reraise` is unavailable inside a task CE) so the session
                    // outcome / `FinishAsync` surface the same fault.
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
                            (fun () -> terminal.DisposalToken.IsCancellationRequested)
                with ex ->
                    // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087) before
                    // it faults the session outcome / `FinishAsync` below.
                    ExceptionDispatchInfo.Throw(reportedPumpFault ex)
            }

        // A fault in either pump kills the tree at once, so a still-producing child can't wedge the
        // exit wait below by blocking on a pipe its dead pump no longer drains — the exit wait then
        // completes and the session outcome surfaces the original pump fault.
        terminal.KillTreeOnPumpFault(stdoutPump :> Task)
        terminal.KillTreeOnPumpFault(stderrPump :> Task)

        streamOutcome <-
            task {
                let! outcome = terminal.WaitWithTimeout()
                // Await both pumps together so neither task is left unobserved if the other
                // faults, bounded so a descendant that inherited this child's stdout/stderr
                // cannot hold `FinishAsync`/`ExitTask` open past the leader's own conclusion.
                do! terminal.DrainPumpsBounded [| stdoutPump :> Task; stderrPump :> Task |]
                // Normally the stdout pump completed the channel on its own EOF (including the
                // EOF the sever hands it). A pump the bound had to abandon never will, so end
                // the channel here too — a `StdoutLinesAsync` consumer must reach the end of its
                // stream when this session concludes, not wait on a pump nobody owns any more.
                // Idempotent with the pump's own completion.
                stdoutChannel.TryComplete() |> ignore
                return outcome
            }

        // A `StdoutLinesAsync()` consumer can abandon `FinishAsync()` (e.g. its enumeration throws
        // because a faulting `OnStdoutLine` handler completed the channel with the error), so observe
        // the outcome fault here.
        RunTerminal.ObserveFault streamOutcome

    /// Build the byte-chunk session. It owns the same stdout pipe and stderr capture as the line
    /// session, but its pump deliberately does no decoding or line framing: one channel item is one
    /// non-empty OS read. Called by `ConsumptionGate.TryClaimStdoutChunkStreaming` under its lock.
    member _.StartChunkSession() =
        let stderrBuffer = GuardedLineBuffer config.OutputBuffer
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
                                (fun () -> terminal.DisposalToken.IsCancellationRequested)
                                CancellationToken.None
                    | None -> ()

                    stdoutChunkChannel.TryComplete() |> ignore
                with
                | :? OperationCanceledException when terminal.ChunkBackpressureToken.IsCancellationRequested ->
                    // The dedicated chunk backpressure token cancels an abandoned writer; this is
                    // routine completion, not a read/tee fault for the chunk stream.
                    stdoutChunkChannel.TryComplete() |> ignore
                | ex ->
                    // A genuine read fault, a throwing tee, or a bounded-channel failure must
                    // wake the chunk consumer and remain visible through the session outcome.
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
                            (fun () -> terminal.DisposalToken.IsCancellationRequested)
                with ex ->
                    // Complete stdout on a sibling failure so a consumer cannot wait forever for
                    // a channel whose stderr pump has already made the combined session fail.
                    let reported = reportedPumpFault ex
                    stdoutChunkChannel.TryComplete reported |> ignore
                    ExceptionDispatchInfo.Throw reported
            }

        terminal.KillTreeOnPumpFault(stdoutPump :> Task)
        terminal.KillTreeOnPumpFault(stderrPump :> Task)

        chunkOutcome <-
            task {
                let! outcome = terminal.WaitWithTimeout()
                do! terminal.DrainPumpsBounded [| stdoutPump :> Task; stderrPump :> Task |]
                // As on the line session above: end the chunk channel here as well, so a
                // consumer is never left enumerating a channel whose abandoned pump can no
                // longer complete it. Idempotent with the pump's own completion.
                stdoutChunkChannel.TryComplete() |> ignore
                return outcome
            }

        // The enumerator may be abandoned without a subsequent FinishAsync; keep the combined
        // task observed while preserving its original exception for a real awaiter.
        RunTerminal.ObserveFault chunkOutcome

    /// Build the STDERR byte-chunk session — `StartChunkSession` with the two pipes' roles swapped.
    /// stderr is pumped raw for the caller's enumerator (one channel item is one non-empty OS read, no
    /// decoding and no line framing), and stdout is pumped exactly as every other session pumps it but
    /// retained NOWHERE. Called by `ConsumptionGate.TryClaimStderrChunkStreaming` under its lock.
    ///
    /// **Why stdout is dropped rather than captured.** `Finished` — all a terminal `FinishAsync` can
    /// hand back — carries the outcome and the captured stderr, never stdout, and this session's stderr
    /// is already the caller's own byte stream. Holding a whole run's stdout for something with nowhere
    /// to return it is precisely the unbounded retention T-357 removed from an untaken stdout line
    /// stream. The drop is the same shape as that one, and equally loud: stdout is still read, framed,
    /// teed (`StdoutTee`), handed to `OnStdoutLine` and counted into `StdoutLineCount` — so a child
    /// writing stdout never blocks on a full pipe and every observation knob behaves as it does
    /// elsewhere — it is only never retained, and every later stdout consumer is refused by the claim
    /// gate with the ordinary already-consumed error rather than handed an empty answer (KB K-163).
    member _.StartStderrChunkSession() =
        // `FinishAsync` reads this session's stderr capture like it reads any other's. Here it is
        // deliberately an EMPTY buffer nothing writes into, because the stderr bytes went to the
        // caller's channel: `Finished.Stderr` is "" (documented on the verb) and `Truncated`/`TooLarge`
        // then report on a capture that genuinely holds nothing, instead of dereferencing an unassigned
        // buffer.
        let stderrBuffer = GuardedLineBuffer config.OutputBuffer
        stderrStreamBuffer <- stderrBuffer

        let stderrPump =
            task {
                try
                    match stderrStream with
                    | Some stream ->
                        do!
                            Pump.readBytesUntilDone
                                stream
                                config.StderrTee
                                (fun chunk ->
                                    bumpStderrChunkStreamedBytes chunk.Length

                                    writeStderrChunkItem chunk)
                                (fun () -> terminal.DisposalToken.IsCancellationRequested)
                                CancellationToken.None
                    | None -> ()

                    stderrChunkChannel.TryComplete() |> ignore
                with
                | :? OperationCanceledException when terminal.ChunkBackpressureToken.IsCancellationRequested ->
                    // The shared chunk backpressure token released an abandoned writer; routine
                    // completion, not a read/tee fault for the chunk stream (as on the stdout twin).
                    stderrChunkChannel.TryComplete() |> ignore
                | ex ->
                    // A genuine read fault, a throwing tee, or a bounded-channel failure must
                    // wake the chunk consumer and remain visible through the session outcome.
                    let reported = reportedPumpFault ex
                    stderrChunkChannel.TryComplete reported |> ignore
                    ExceptionDispatchInfo.Throw reported
            }

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
                                // Retained nowhere: no channel to queue into (no consumer can be
                                // handed one) and no capture to grow. Everything observable about the
                                // line has already happened above.
                                ValueTask.CompletedTask)
                            // The buffer policy's byte cap doubles as the in-flight line ceiling, as it
                            // does for every pump whose lines no consumer paces: a newline-free flood
                            // must not grow an assembly buffer for output this session retains nothing
                            // of. `None` (the default) keeps the previous unbounded assembly.
                            config.OutputBuffer.MaxBytes
                            (fun () -> terminal.DisposalToken.IsCancellationRequested)
                with ex ->
                    // Complete the stderr chunk channel on a sibling failure so a consumer cannot wait
                    // forever for a channel whose stdout pump has already failed the combined session.
                    let reported = reportedPumpFault ex
                    stderrChunkChannel.TryComplete reported |> ignore
                    ExceptionDispatchInfo.Throw reported
            }

        terminal.KillTreeOnPumpFault(stderrPump :> Task)
        terminal.KillTreeOnPumpFault(stdoutPump :> Task)

        stderrChunkOutcome <-
            task {
                let! outcome = terminal.WaitWithTimeout()
                do! terminal.DrainPumpsBounded [| stderrPump :> Task; stdoutPump :> Task |]
                // As on both other streaming sessions: end the channel here as well, so a consumer is
                // never left enumerating a channel whose abandoned pump can no longer complete it.
                // Idempotent with the pump's own completion.
                stderrChunkChannel.TryComplete() |> ignore
                return outcome
            }

        // The enumerator may be abandoned without a subsequent FinishAsync; keep the combined task
        // observed while preserving its original exception for a real awaiter (KB K-084).
        RunTerminal.ObserveFault stderrChunkOutcome

    /// Build the merged output-event session: one line pump per stream, both writing into the shared
    /// event channel, plus their combined outcome. Called by `ConsumptionGate.TryClaimEventStreaming`
    /// under its lock.
    member _.StartEventSession() =
        // Each pump completes the shared event channel on its own fault (carrying the error), so an
        // `OutputEventsAsync` consumer observes a throwing handler promptly rather than hanging until the
        // process exits — the combined outcome below only completes the channel after the exit wait, which
        // for a long-running child can be far away. `TryComplete` because the two pumps and the
        // combined task below all race to complete the one channel; re-raise so the outcome faults.
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
                            (fun () -> terminal.DisposalToken.IsCancellationRequested)
                with ex ->
                    // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087)
                    // before it completes the channel / faults the outcome below.
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

        // A fault in either pump kills the tree at once, so a still-producing child can't wedge the
        // exit wait below by blocking on a pipe its dead pump no longer drains — the exit wait then
        // completes and the event outcome surfaces the original pump fault.
        terminal.KillTreeOnPumpFault(stdoutPump :> Task)
        terminal.KillTreeOnPumpFault(stderrPump :> Task)

        eventOutcome <-
            task {
                let mutable error: exn option = None
                let mutable outcome = Unchecked.defaultof<Outcome>

                try
                    let! settled = terminal.WaitWithTimeout()
                    outcome <- settled
                    // Await both pumps together so neither is left unobserved if the other
                    // faults, bounded like every other post-exit pump join on this handle. The
                    // channel completion below then also releases an `OutputEventsAsync`
                    // consumer whose pump the bound had to abandon.
                    do! terminal.DrainPumpsBounded [| stdoutPump :> Task; stderrPump :> Task |]
                    eventChannel.TryComplete() |> ignore
                with ex ->
                    error <- Some ex
                    // A fault (a throwing handler, or the exit wait itself) completes the channel WITH
                    // the error so an `OutputEventsAsync` consumer observes it instead of hanging — idempotent
                    // with the per-pump completion above. The fault is otherwise consumed here (and by
                    // the observation below) rather than surfacing as an unobserved task exception.
                    eventChannel.TryComplete ex |> ignore

                // Surface the outcome, or re-raise the fault for a concurrent ExitTask (WaitAny/WaitAll
                // on this handle). The observation below covers that fault, so the OutputEvents-only
                // case never leaves an unobserved task exception.
                match error with
                | Some ex -> return! Task.FromException<Outcome> ex
                | None ->
                    conclude outcome
                    return outcome
            }

        // Observe any fault on this otherwise fire-and-forget task (the OutputEvents-only case, where
        // nothing awaits `ExitTask`).
        RunTerminal.ObserveFault eventOutcome

    /// Build an interactive expect-style session's raw readers and return the shared `ExpectWindow`
    /// they fill. Called by `ConsumptionGate.TryClaimInteractive` under its lock.
    ///
    /// The readers are raw (`Pump.readTextUntilDone`), not line pumps: an interactive prompt carries no
    /// line terminator, so framing the stream is precisely what must not happen here. That also means
    /// `LineTerminator`, `Command.OnStdoutLine`/`OnStderrLine` and the streaming line counters have
    /// nothing to observe on this path — the byte-exact tees (`StdoutTee`/`StderrTee`) still do, and are
    /// fed exactly as the line pumps feed them.
    member _.StartInteractiveRawSession(windowChars: int, transcriptChars: int option, filterAnsi: bool) =
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
                        // before it reaches `Complete`/the session outcome below.
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

        // A fault in either reader kills the tree at once, so a still-producing child can't wedge the
        // exit wait below by blocking on a pipe its dead reader no longer drains.
        terminal.KillTreeOnPumpFault stdoutPump
        terminal.KillTreeOnPumpFault stderrPump

        // Close the window as soon as BOTH readers finish, independently of the exit wait, so a
        // pattern wait ends promptly on the child's end-of-output instead of burning its whole
        // timeout. Never faults (it stashes the reader fault into the window instead), so
        // awaiting it below can't mask the pump fault the outcome re-raises. It is
        // deliberately left UNBOUNDED: it is not a wait anything blocks on, and it must stay
        // able to close the window mid-run. Should the post-exit drain bound have to abandon a
        // reader, the outcome closes the window itself and this task simply never
        // completes — `ExpectWindow.Complete` is idempotent, so a late arrival changes nothing.
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
                // The handle's shared exit wait, not a fresh one: a readiness probe can
                // already own it while deliberately leaving the handle `Fresh` (see the readiness
                // race), which is exactly the state this session claims from — so a
                // probe-then-session sequence must join that wait rather than start a second
                // `host.Wait()` racing its reap (KB K-016). It is reentrant on the claim lock,
                // which this setup already holds.
                let! outcome = sharedExitWait ()
                // Bounded, and taken on the READERS rather than on `drained`: `drained` only
                // ever completes once they do, so joining it first would reintroduce exactly the
                // unbounded wait this bound exists to remove.
                let mutable readerFault: exn option = None
                let mutable readersSettled = true

                try
                    let! settled = terminal.AwaitPumpsSettled pumps
                    readersSettled <- settled
                with ex ->
                    // A genuine read fault. The readers HAVE ended — that is the only way it can
                    // surface here — so hold it until the window has been closed with it below,
                    // preserving the "no pattern waiter outlives this outcome" ordering the
                    // unbounded join had. It is re-raised unchanged for whoever awaits this
                    // outcome (`ExitTask`/`StopAsync`), as on the streaming sessions.
                    readerFault <- Some ex

                if readersSettled then
                    // The readers ended, so the window they fill is closed by `drained` with
                    // their fault (if any) — its own completion is now immediate.
                    do! drained
                else
                    // A reader the bound had to abandon can no longer close the window, and a
                    // pattern waiter must not outlive this session's conclusion. Close it with
                    // no fault: nothing failed — the output simply stops here.
                    window.Complete None

                match readerFault with
                | Some ex -> return! Task.FromException<Outcome> ex
                | None ->
                    conclude outcome
                    return outcome
            }

        // Observe any fault on this otherwise fire-and-forget task (the expect-only case, where
        // the caller never awaits the exit).
        RunTerminal.ObserveFault interactiveOutcome

        window

    /// Hand stdout to a `Content-Length` parser while draining stderr independently so a chatty
    /// protocol server cannot block. Called by `ConsumptionGate.TryClaimInteractive` under its lock —
    /// and it is the one session that can refuse itself: a run with no piped stdout has nothing to
    /// frame, and reporting that leaves the handle unclaimed rather than poisoned.
    member _.StartContentLengthSession(startStdoutPump: Stream -> Stream option -> (unit -> bool) -> Task) =
        match stdoutStream with
        | None -> Error(ProcessError.Unsupported "Content-Length sessions require piped stdout")
        | Some stdout ->
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
            terminal.KillTreeOnPumpFault stdoutPump
            terminal.KillTreeOnPumpFault stderrPump

            interactiveOutcome <-
                task {
                    let! outcome = sharedExitWait ()
                    do! terminal.DrainPumpsBounded pumps
                    conclude outcome
                    return outcome
                }

            RunTerminal.ObserveFault interactiveOutcome
            Ok()
