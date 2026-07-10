namespace ProcessKit

open System.IO
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The low-level channel plumbing shared by `RunningProcess`'s two streaming sessions: constructing
/// the stdout/event channel per the opt-in `StreamBufferPolicy`, the policy-aware per-item write
/// (backpressure / drop / fail-loud), and the stream→line pump those writes are fed from. Factored
/// out of `RunningProcess` so the channel/backpressure machinery lives in one place next to `Pump`;
/// the sessions that own the channels stay in `RunningProcess`.
module internal StreamChannel =

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
        match streamBuffer with
        | Some policy -> Channel.CreateBounded<'T>(boundedOptions policy.Capacity singleWriter)
        | None -> Channel.CreateUnbounded<'T>(UnboundedChannelOptions(SingleReader = true, SingleWriter = singleWriter))

    // Write one item to a (possibly bounded) channel per `streamBuffer` (`None` = the default
    // unbounded `TryWrite`, unchanged). `Backpressure` awaits room via `WriteAsync`, bounded to
    // `disposalToken` so an abandoned bounded stream's writer can't outlive its handle.
    // `DropNewest`/`DropOldest` keep the channel's item count bounded losslessly but the CONTENT is
    // lossy, bumping `onDrop`. `Error` faults the pump with `ProcessError.OutputTooLarge` once full —
    // reusing the exact fault path a throwing per-line handler already goes through (the caller's
    // `try`/`with` completes the channel and re-raises). `program`/`countSoFar` only feed that fault.
    let writeItem
        (streamBuffer: StreamBufferPolicy option)
        (program: string)
        (disposalToken: CancellationToken)
        (writer: ChannelWriter<'T>)
        (reader: ChannelReader<'T>)
        (countSoFar: unit -> int)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        match streamBuffer with
        | None ->
            writer.TryWrite item |> ignore
            ValueTask.CompletedTask
        | Some policy ->
            match policy.FullMode with
            | StreamFullMode.Backpressure -> writer.WriteAsync(item, disposalToken)
            | StreamFullMode.DropNewest ->
                if not (writer.TryWrite item) then
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
                // fault path — a throwing handler, a decode/IO error — calls `Writer.TryComplete ex`),
                // both `TryRead` and `TryWrite` permanently return `false`; without this check the loop
                // would spin forever (a livelock pinning a CPU core, and `eventOutcome`/`FinishAsync`
                // would never complete). Capacity is always >= 1 (`StreamBufferPolicy.Bounded` rejects
                // less), so a non-completed channel reporting `TryWrite` full always has something to
                // evict — `TryRead` failing here is therefore only possible once the channel is done.
                let mutable written = writer.TryWrite item
                let mutable canRetry = true

                while not written && canRetry do
                    let evicted, _ = reader.TryRead()

                    if evicted then
                        onDrop ()
                        written <- writer.TryWrite item
                    else
                        // Nothing left to evict and nowhere to write: the channel is done. This item
                        // can't be delivered either way — count it dropped and stop instead of spinning.
                        onDrop ()
                        canRetry <- false

                ValueTask.CompletedTask
            | StreamFullMode.Error ->
                if writer.TryWrite item then
                    ValueTask.CompletedTask
                else
                    raise (
                        ProcessException(
                            ProcessError.OutputTooLarge(program, Some policy.Capacity, None, countSoFar (), 0)
                        )
                    )

    // Pump one stream's lines through `onLine` until the stream ends — the streaming-verb analogue
    // of `Pump`'s buffered capture (which captures to a `LineBuffer` instead). No-op when the stream
    // isn't piped. The caller owns the sink (a channel writer, a buffer) and any completion signal.
    // No in-flight line cap for streaming: it is consumer-paced and applies no buffer policy, so a
    // consumer receives whole lines (the in-flight cap is a buffered-verb concern). `isTearingDown`
    // is threaded straight through to `Pump.readLinesUntilDone`'s genuine-vs-teardown-race
    // classification (T-087) — the caller reports whether ITS handle's own teardown has begun.
    let pumpLines
        (stream: Stream option)
        encoding
        terminator
        tee
        (onLine: string -> ValueTask)
        (isTearingDown: unit -> bool)
        =
        task {
            match stream with
            | Some s ->
                do! Pump.readLinesUntilDone s encoding terminator tee onLine None isTearingDown CancellationToken.None
            | None -> ()
        }
