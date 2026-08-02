namespace ProcessKit

open System
open System.Buffers
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Runtime.ExceptionServices
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type private ContentLengthReader(stream: Stream, tee: Stream option, invalid: string -> exn) =
    let buffer = ArrayPool<byte>.Shared.Rent 8192
    let mutable offset = 0
    let mutable count = 0
    let mutable returned = false

    let refill () =
        task {
            let! read = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None)

            if read > 0 then
                match tee with
                | Some sink -> do! sink.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None)
                | None -> ()

            offset <- 0
            count <- read
            return read
        }

    member _.ReadByteAsync() : Task<int option> =
        task {
            if count = 0 then
                let! read = refill ()

                if read > 0 then
                    let value = int buffer[offset]
                    offset <- offset + 1
                    count <- count - 1
                    return Some value
                else
                    return None
            else
                let value = int buffer[offset]
                offset <- offset + 1
                count <- count - 1
                return Some value
        }

    member this.ReadLineAsync(maxBytes: int) : Task<byte[] option> =
        task {
            let line = ResizeArray<byte>()
            let mutable complete = false
            let mutable cleanEof = false

            while not complete && not cleanEof do
                match! this.ReadByteAsync() with
                | None when line.Count = 0 -> cleanEof <- true
                | None -> raise (invalid "unexpected EOF inside a Content-Length header")
                | Some 13 ->
                    match! this.ReadByteAsync() with
                    | Some 10 -> complete <- true
                    | _ -> raise (invalid "Content-Length headers must use CRLF line endings")
                | Some 10 -> raise (invalid "Content-Length headers must use CRLF line endings")
                | Some value ->
                    if value > 127 then
                        raise (invalid "Content-Length headers must contain ASCII bytes only")

                    line.Add(byte value)

                    if line.Count > maxBytes then
                        raise (invalid $"Content-Length headers exceed the {maxBytes}-byte limit")

            if cleanEof then
                return None
            else
                return Some(line.ToArray())
        }

    member this.ReadExactlyAsync(length: int) : Task<byte[]> =
        task {
            let payload = Array.zeroCreate<byte> length
            let mutable written = 0

            while written < length do
                if count = 0 then
                    let! read = refill ()

                    if read = 0 then
                        raise (invalid $"unexpected EOF inside a {length}-byte Content-Length payload")

                let copied = min count (length - written)
                Buffer.BlockCopy(buffer, offset, payload, written, copied)
                offset <- offset + copied
                count <- count - copied
                written <- written + copied

            return payload
        }

    member _.FlushTeeAsync() : Task =
        match tee with
        | Some sink -> sink.FlushAsync CancellationToken.None
        | None -> Task.CompletedTask

    interface IDisposable with
        member _.Dispose() =
            if not returned then
                returned <- true
                ArrayPool<byte>.Shared.Return buffer

/// A full-duplex Content-Length framed transport over a live process's stdin/stdout, suitable for
/// LSP, DAP, BSP, and similar protocols. Construct it over a command started with
/// `Command.KeepStdinOpen()`, enumerate `FramesAsync()`, and send raw payload bytes with `SendAsync`.
/// The session owns the run's stdout consumption; other output verbs on the same handle are refused.
///
/// `Command.StreamBuffer` bounds the unread frame backlog; leaving it unset preserves the default
/// unbounded backlog. Only its two LOSSLESS full modes are honoured here: `Backpressure` paces the
/// parser (and, through the pipe, the child) against your consumer, and `Error` faults the frame stream
/// at the cap. The two DROP modes are refused at construction with a typed
/// `ProcessError.Unsupported` — a framed transport carries protocol messages a peer correlates with its
/// requests, so quietly deleting a queued frame is a corruption no consumer could detect, and this
/// library refuses inapplicable configuration rather than downgrading it silently.
///
/// With a bounded backlog, drain `FramesAsync()` concurrently with your sends rather than awaiting a send
/// first: backpressure deliberately stops the parser (and the child) once the backlog is full, so a
/// consumer that only starts reading after some other await can stall the child it is waiting on. The
/// constructor itself never waits on the child — a `Command.Stdin(source)` feeder is awaited by the first
/// `SendAsync`/`FinishInputAsync` instead, so the caller always gets the session back and can start
/// draining frames.
[<Sealed>]
type ContentLengthSession(running: RunningProcess, maxFrameBytes: int) =
    do ArgumentNullException.ThrowIfNull(running, nameof running)
    do ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1, nameof maxFrameBytes)

    let program = running.Config.Program

    let refuseLossyBacklog (mode: string) =
        ProcessException(
            ProcessError.Unsupported
                $"StreamBuffer(StreamFullMode.{mode}) on a Content-Length session: a framed transport delivers protocol messages, so dropping a queued frame would silently delete a message the peer is correlating with a request and no consumer could tell. Use StreamFullMode.Backpressure to pace the child losslessly, StreamFullMode.Error to fail loudly at the cap, or drop StreamBuffer for an unbounded backlog."
        )
        :> exn

    // Refuse the lossy full modes BEFORE claiming stdout below, so a refused configuration leaves the
    // handle exactly as it found it (its capture/streaming verbs still available) — the same
    // refuse-rather-than-downgrade stance `Command.LaunchDetached` takes on the knobs it cannot honour
    // (`DetachedLaunch.incompatibleKnob`).
    do
        match running.Config.StreamBuffer with
        | Some policy ->
            match policy.FullMode with
            | StreamFullMode.DropOldest -> raise (refuseLossyBacklog "DropOldest")
            | StreamFullMode.DropNewest -> raise (refuseLossyBacklog "DropNewest")
            | StreamFullMode.Backpressure
            | StreamFullMode.Error -> ()
        | None -> ()

    // Honestly apply `Command.StreamBuffer` to the incoming-frame backlog, the same channel
    // construction `RunningProcess`'s own streaming verbs use (`StreamChannel.create`): bounded per
    // the configured policy, or the unbounded single-reader/single-writer channel this session has
    // always used when the config leaves it unset. Single writer: only this session's own parse loop
    // ever writes a frame.
    let frames: Channel<byte[]> = StreamChannel.create running.Config.StreamBuffer true

    // The running count of frames written so far, fed to `StreamChannel.writeItem`'s `countSoFar` —
    // only consulted by `StreamFullMode.Error`, to report how many frames had already arrived when the
    // cap tripped.
    let mutable writtenFrameCount = 0

    let sendGate = new SemaphoreSlim(1, 1)
    let mutable framesClaimed = 0

    let invalid detail =
        ProcessException(ProcessError.Parse(program, detail)) :> exn

    let reportedFault (error: exn) =
        match error with
        | :? IOException
        | :? ObjectDisposedException -> ProcessException(ProcessError.Io error.Message) :> exn
        | _ -> error

    let parse (stream: Stream) (tee: Stream option) (isTearingDown: unit -> bool) : Task =
        task {
            use reader = new ContentLengthReader(stream, tee, invalid)
            let mutable fault: exn option = None

            try
                let mutable reading = true

                while reading do
                    match! reader.ReadLineAsync 16384 with
                    | None -> reading <- false
                    | Some firstLine ->
                        let mutable line = firstLine
                        let mutable headerBytes = firstLine.Length + 2
                        let mutable contentLength: int option = None

                        while line.Length > 0 do
                            let text = Encoding.ASCII.GetString line
                            let separator = text.IndexOf ':'

                            if separator <= 0 then
                                raise (invalid $"malformed Content-Length header line '{text}'")

                            let name = text.Substring(0, separator).Trim()
                            let value = text.Substring(separator + 1).Trim()

                            if name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) then
                                if contentLength.IsSome then
                                    raise (invalid "duplicate Content-Length header")

                                match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
                                | true, length when length <= maxFrameBytes -> contentLength <- Some length
                                | true, length ->
                                    raise (invalid $"Content-Length {length} exceeds the {maxFrameBytes}-byte limit")
                                | _ -> raise (invalid $"invalid Content-Length value '{value}'")

                            match! reader.ReadLineAsync 16384 with
                            | None -> raise (invalid "unexpected EOF before the Content-Length header terminator")
                            | Some next ->
                                headerBytes <- headerBytes + next.Length + 2

                                if headerBytes > 16384 then
                                    raise (invalid "Content-Length headers exceed the 16384-byte limit")

                                line <- next

                        match contentLength with
                        | None -> raise (invalid "frame is missing a Content-Length header")
                        | Some length ->
                            let! payload = reader.ReadExactlyAsync length
                            writtenFrameCount <- writtenFrameCount + 1

                            do!
                                StreamChannel.writeItem
                                    running.Config.StreamBuffer
                                    program
                                    running.DisposalToken
                                    frames.Writer
                                    frames.Reader
                                    (fun () -> writtenFrameCount)
                                    (fun () ->
                                        // Unreachable by construction: `writeItem` calls `onDrop` only from
                                        // its two DROP full modes, and both are refused for this session
                                        // above. Kept fail-loud rather than `ignore` so a frame can never
                                        // vanish silently if that guard is ever weakened — the parse loop
                                        // below turns this into the frame stream's fault.
                                        invalidOp
                                            "ContentLengthSession must never drop a protocol frame; the lossy StreamBuffer full modes are refused at construction")
                                    payload
            with
            | (:? ObjectDisposedException | :? IOException) when isTearingDown () ->
                // The owning handle closed stdout during teardown; end the frame stream normally.
                ()
            | :? OperationCanceledException when isTearingDown () ->
                // The other way that same teardown reaches this loop: a bounded backlog's `Backpressure`
                // write was parked waiting for the consumer to make room when the handle cancelled
                // `DisposalToken`, the token that wait is deliberately bounded to (`StreamChannel.writeItem`,
                // so an abandoned bounded stream's writer cannot outlive its handle). Disposal is not a
                // parser fault, so end the frame stream normally here too — a consumer that disposes while
                // behind must see a clean end, never a spurious cancellation out of its enumerator.
                ()
            | ex -> fault <- Some(reportedFault ex)

            try
                do! reader.FlushTeeAsync()
            with
            | (:? ObjectDisposedException | :? IOException) when isTearingDown () ->
                // Teardown can close a configured tee with stdout; there is no remaining data to flush.
                ()
            | flushEx ->
                if fault.IsNone then
                    fault <- Some(reportedFault flushEx)

            match fault with
            | None -> frames.Writer.TryComplete() |> ignore
            | Some ex ->
                frames.Writer.TryComplete ex |> ignore
                ExceptionDispatchInfo.Throw ex
        }
        :> Task

    do
        match running.StartContentLengthSession parse with
        | Ok() -> ()
        | Error error -> raise (InvalidOperationException error.Message)

    // Claim the interactive stdin the moment the parse loop above owns stdout, but do NOT wait here for a
    // `Command.Stdin(source)` feeder to finish draining: `TakeStdinAsync` performs that once-only claim
    // synchronously (a racing `TakeStdin` still loses, exactly as when this blocked) and hands the wait
    // back as a task the send verbs await. Blocking the constructor on that feeder deadlocks a bounded
    // frame backlog: the parse loop parks on the full channel, whose only consumer — `FramesAsync()` — the
    // caller cannot reach until this constructor returns, so the child blocks writing stdout, stops
    // reading stdin, and the feeder never completes.
    let stdin: Task<ProcessStdin option> = running.TakeStdinAsync()

    /// A session using the default 16 MiB maximum payload size.
    new(running: RunningProcess) = ContentLengthSession(running, 16 * 1024 * 1024)

    /// The maximum payload size in bytes in either direction. Oversized incoming frames fail before
    /// allocating the payload; oversized sends are rejected before writing a header.
    member _.MaxFrameBytes = maxFrameBytes

    /// Enumerate incoming payloads. Headers are validated and omitted; each yielded array is exactly the
    /// advertised payload bytes. This single-consumer method may be called only once.
    member _.FramesAsync() : IAsyncEnumerable<byte[]> =
        if Interlocked.Exchange(&framesClaimed, 1) <> 0 then
            raise (InvalidOperationException "this ContentLengthSession's frames have already been consumed")

        frames.Reader.ReadAllAsync()

    /// Send one byte-exact payload. Concurrent calls are serialized so their headers and payloads cannot
    /// interleave. Cancellation may leave a partial frame in the child, so abandon the session after it.
    ///
    /// On a `Command.Stdin(source)` + `KeepStdinOpen` run this is where the source feeder is awaited (the
    /// constructor no longer blocks on it), so the first send completes only once the source has been
    /// drained and the interactive writer is the pipe's single writer — `cancellationToken` bounds that
    /// wait too, reported as `ProcessError.Cancelled` like any other cancelled send.
    member _.SendAsync(payload: byte[], cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(payload, nameof payload)

        if payload.Length > maxFrameBytes then
            raise (
                ArgumentOutOfRangeException(nameof payload, payload.Length, $"payload exceeds {maxFrameBytes} bytes")
            )

        task {
            try
                let! claimed = stdin.WaitAsync cancellationToken

                match claimed with
                | None ->
                    return
                        Error(
                            ProcessError.Unsupported
                                "Content-Length sending requires a command built with Command.KeepStdinOpen"
                        )
                | Some pipe ->
                    do! sendGate.WaitAsync cancellationToken

                    try
                        let header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n")
                        do! pipe.WriteAsync(header, cancellationToken)
                        do! pipe.WriteAsync(payload, cancellationToken)
                        do! pipe.FlushAsync cancellationToken
                        return Ok()
                    finally
                        sendGate.Release() |> ignore
            with
            | :? OperationCanceledException -> return Error(ProcessError.Cancelled program)
            | :? IOException as ex -> return Error(ProcessError.Io ex.Message)
            | :? ObjectDisposedException as ex -> return Error(ProcessError.Io ex.Message)
        }

    /// Send one payload without cancellation.
    member this.SendAsync(payload: byte[]) : Task<Result<unit, ProcessError>> =
        this.SendAsync(payload, CancellationToken.None)

    /// Close the child's framed input so it observes EOF. Sending is unsupported when the command did
    /// not keep stdin open; repeated close calls follow `ProcessStdin.FinishAsync`'s idempotent contract.
    /// Like `SendAsync`, this awaits a `Command.Stdin(source)` feeder first — closing the pipe under a
    /// still-writing feeder would truncate the source.
    member _.FinishInputAsync() : Task<Result<unit, ProcessError>> =
        task {
            try
                let! claimed = stdin

                match claimed with
                | None ->
                    return
                        Error(
                            ProcessError.Unsupported
                                "Content-Length input requires a command built with Command.KeepStdinOpen"
                        )
                | Some pipe ->
                    do! sendGate.WaitAsync()

                    try
                        do! pipe.FinishAsync()
                        return Ok()
                    finally
                        sendGate.Release() |> ignore
            with
            | :? IOException as ex -> return Error(ProcessError.Io ex.Message)
            | :? ObjectDisposedException as ex -> return Error(ProcessError.Io ex.Message)
        }
