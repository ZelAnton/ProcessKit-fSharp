namespace ProcessKit

open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The low-level channel plumbing shared by line/event streaming and `ContentLengthSession`:
/// constructing a channel per the opt-in `StreamBufferPolicy`, the policy-aware per-item write
/// (backpressure / drop / fail-loud), and the stream→line pump those writes are fed from. Factored
/// out of `RunningProcess` so the channel/backpressure machinery lives in one place next to `Pump`;
/// the sessions that own the channels stay in `RunningProcess`/`ContentLengthSession`.
module internal StreamChannel =

    /// A channel together with the first completion cause supplied by its owner. `ChannelWriter.TryWrite`
    /// returns `false` for both a full bounded channel and a completed channel, while
    /// `ChannelReader.Completion` does not settle until an already-buffered completed channel is drained.
    /// Keeping the owner's completion state beside the channel is therefore the only non-blocking way for
    /// `writeItem` to distinguish those cases without changing bounded-write scheduling.
    type Channel<'T> internal (inner: System.Threading.Channels.Channel<'T>) =
        let completionGate = obj ()
        let mutable completion: exn option option = None

        member _.Reader = inner.Reader
        member _.Writer = inner.Writer

        member internal _.Completion = lock completionGate (fun () -> completion)

        member private _.TryCompleteCore(error: exn option) =
            lock completionGate (fun () ->
                match completion with
                | Some _ -> false
                | None ->
                    // Publish the cause before closing the writer. Any concurrent failed `TryWrite`
                    // can then recover this exact cause instead of fabricating an overflow.
                    completion <- Some error

                    match error with
                    | Some cause -> inner.Writer.TryComplete cause
                    | None -> inner.Writer.TryComplete())

        member this.TryComplete() = this.TryCompleteCore None
        member this.TryComplete(error: exn) = this.TryCompleteCore(Some error)

    let private rethrow<'T> (error: exn) : 'T =
        ExceptionDispatchInfo.Capture(error).Throw()
        Unchecked.defaultof<'T>

    let private ensureNotCompleted (channel: Channel<'T>) =
        match channel.Completion with
        | None -> ()
        | Some None -> raise (ChannelClosedException())
        | Some(Some error) -> rethrow error

    // A bounded channel for an opt-in `StreamBufferPolicy`. `SingleReader = false` regardless of
    // `FullMode` (not just for `DropOldest`, which needs the writer to evict via `Reader.TryRead`) —
    // one uniform construction path is simpler than a mode-dependent one, and the cost only applies to
    // an opt-in bounded stream, never to the default. Every full mode is otherwise implemented over
    // `BoundedChannelFullMode.Wait`'s precise, non-blocking "is it full?" signal (`TryWrite`'s bool) —
    // the channel's own built-in Drop full-modes always report `TryWrite` success, which would hide
    // whether a drop actually happened.
    let private boundedOptions (capacity: int) (singleWriter: bool) =
        BoundedChannelOptions(
            capacity,
            SingleReader = false,
            SingleWriter = singleWriter,
            FullMode = BoundedChannelFullMode.Wait
        )

    // Single-reader/single-writer *unbounded* channels remain the unconditional default: each is
    // consumed by exactly one reader, and the stdout channel is written by exactly one pump (the event
    // channel by two), selecting the faster single-consumer channel implementation. Opting in to
    // `Command.StreamBuffer` switches both to the bounded construction above instead.
    /// Create a streaming channel per `streamBuffer`: bounded (per the construction above) when a
    /// policy is set, else an unbounded single-reader channel with `singleWriter` as given (the stdout
    /// channel has one pump, the event channel two).
    let create<'T> (streamBuffer: StreamBufferPolicy option) (singleWriter: bool) : Channel<'T> =
        let inner =
            match streamBuffer with
            | Some policy ->
                System.Threading.Channels.Channel.CreateBounded<'T>(boundedOptions policy.Capacity singleWriter)
            | None ->
                System.Threading.Channels.Channel.CreateUnbounded<'T>(
                    UnboundedChannelOptions(SingleReader = true, SingleWriter = singleWriter)
                )

        Channel inner

    // Write one item to a (possibly bounded) channel per `streamBuffer` (`None` = the default
    // unbounded `TryWrite`, unchanged). `Backpressure` awaits room via `WriteAsync`, bounded to
    // `disposalToken` so an abandoned bounded stream's writer can't outlive its handle.
    // `DropNewest`/`DropOldest` keep the channel's item count bounded losslessly but the CONTENT is
    // lossy, bumping `onDrop`. `Error` faults the pump with the caller-built `ProcessError` once full —
    // reusing the exact fault path a throwing per-line handler already goes through (the caller's
    // `try`/`with` completes the channel and re-raises).
    //
    // `buildOverflowError` is a closure rather than a fixed `ProcessError.OutputTooLarge` construction
    // here on purpose: this one function backs several distinct channels (stdout line streaming, the
    // merged stdout+stderr event channel, raw stdout byte chunks, and `ContentLengthSession`'s protocol
    // frames), and only the caller knows what unit its own items are actually counted in. Building a
    // fixed case here once meant every caller reported the channel's item *capacity* as a `LineLimit`
    // and a hardcoded `TotalBytes = 0`, regardless of whether its items were lines at all (T-297) — see
    // each call site's own comment for how it now maps its channel onto the honest fields. The closure
    // is evaluated only on the `StreamFullMode.Error` overflow path, never on every write, and receives
    // the tripped `policy` so a caller need not re-derive `streamBuffer.Value` itself.
    let writeItem
        (streamBuffer: StreamBufferPolicy option)
        (disposalToken: CancellationToken)
        (channel: Channel<'T>)
        (buildOverflowError: StreamBufferPolicy -> ProcessError)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        let writer = channel.Writer
        let reader = channel.Reader

        match streamBuffer with
        | None ->
            if not (writer.TryWrite item) then
                ensureNotCompleted channel

            ValueTask.CompletedTask
        | Some policy ->
            match policy.FullMode with
            | StreamFullMode.Backpressure -> writer.WriteAsync(item, disposalToken)
            | StreamFullMode.DropNewest ->
                if not (writer.TryWrite item) then
                    ensureNotCompleted channel
                    onDrop ()

                ValueTask.CompletedTask
            | StreamFullMode.DropOldest ->
                // Full: evict the oldest queued item ourselves — safe because bounded channels are
                // always created with SingleReader = false — then retry, looping rather than retrying
                // once: the event channel has two concurrent writers (stdout + stderr), so a sibling
                // pump can refill the freed slot before our retry lands. Looping keeps `onDrop` exactly
                // in step with actual evictions instead of under-counting on that race (a single-writer
                // stdout-only stream always succeeds on the first iteration).
                //
                // Bounded to genuine progress: if a sibling pump has completed the channel (its own
                // fault path — a throwing handler, a decode/IO error — calls `channel.TryComplete ex`),
                // both `TryRead` and `TryWrite` permanently return `false`; without this check the loop
                // would spin forever (a livelock pinning a CPU core, and `eventOutcome`/`FinishAsync`
                // would never complete). Capacity is always >= 1 (`StreamBufferPolicy.Bounded` rejects
                // less), so a non-completed channel reporting `TryWrite` full always has something to
                // evict unless the consumer raced us and drained it first. In that case the immediate
                // retry below uses the newly freed slot; if a sibling writer wins it, the loop continues.
                let mutable written = writer.TryWrite item

                while not written do
                    let evicted, _ = reader.TryRead()

                    if evicted then
                        onDrop ()
                        written <- writer.TryWrite item
                    else
                        // Either the consumer drained the full slot first, or the channel is done.
                        // Preserve a completion cause; otherwise retry the write against the freed slot.
                        ensureNotCompleted channel
                        written <- writer.TryWrite item

                ValueTask.CompletedTask
            | StreamFullMode.Error ->
                if writer.TryWrite item then
                    ValueTask.CompletedTask
                else
                    // `TryWrite = false` also means completed. In particular, one event pump may have
                    // faulted and closed the shared channel while its sibling was framing a line. The
                    // original fault must win over a synthetic `OutputTooLarge` from that sibling.
                    ensureNotCompleted channel
                    raise (ProcessException(buildOverflowError policy))

    // Pump one stream's lines through `onLine` until the stream ends — the streaming-verb analogue
    // of `Pump`'s buffered capture (which captures to a `LineBuffer` instead). No-op when the stream
    // isn't piped. The caller owns the sink (a channel writer, a buffer) and any completion signal.
    // `maxLineBytes` is the in-flight byte cap for a NEWLINE-FREE flood, threaded straight through to
    // `Pump.readLinesUntilDone` — pass `None` for a genuinely consumer-paced channel (a consumer
    // receives whole lines, e.g. the stdout streaming channel and the event channels), or
    // `config.OutputBuffer.MaxBytes` for a call site that captures into a `Pump.LineBuffer` under that
    // same policy (the stderr side of the stdout streaming session), so the assembly buffer can't grow
    // unbounded on that flood — same reasoning as `pumpToBuffer`'s buffered capture. `isTearingDown` is
    // threaded straight through to `Pump.readLinesUntilDone`'s genuine-vs-teardown-race classification
    // (T-087) — the caller reports whether ITS handle's own teardown has begun.
    let pumpLines
        (stream: Stream option)
        encoding
        terminator
        tee
        (onLine: string -> ValueTask)
        (maxLineBytes: int option)
        (isTearingDown: unit -> bool)
        =
        task {
            match stream with
            | Some s ->
                do!
                    Pump.readLinesUntilDone
                        s
                        encoding
                        terminator
                        tee
                        onLine
                        maxLineBytes
                        isTearingDown
                        CancellationToken.None
            | None -> ()
        }
