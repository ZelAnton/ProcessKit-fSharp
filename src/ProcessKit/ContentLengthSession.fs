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
[<Sealed>]
type ContentLengthSession(running: RunningProcess, maxFrameBytes: int) =
    do ArgumentNullException.ThrowIfNull(running, nameof running)
    do ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1, nameof maxFrameBytes)

    let program = running.Config.Program

    let frames =
        Channel.CreateUnbounded<byte[]>(UnboundedChannelOptions(SingleReader = true, SingleWriter = true))

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
                            do! frames.Writer.WriteAsync(payload).AsTask()
            with
            | (:? ObjectDisposedException | :? IOException) when isTearingDown () ->
                // The owning handle closed stdout during teardown; end the frame stream normally.
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

    let stdin = running.TakeStdin()

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
    member _.SendAsync(payload: byte[], cancellationToken: CancellationToken) : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(payload, nameof payload)

        if payload.Length > maxFrameBytes then
            raise (
                ArgumentOutOfRangeException(nameof payload, payload.Length, $"payload exceeds {maxFrameBytes} bytes")
            )

        match stdin with
        | None ->
            Task.FromResult(
                Error(
                    ProcessError.Unsupported
                        "Content-Length sending requires a command built with Command.KeepStdinOpen"
                )
            )
        | Some pipe ->
            task {
                try
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
    member _.FinishInputAsync() : Task<Result<unit, ProcessError>> =
        match stdin with
        | None ->
            Task.FromResult(
                Error(
                    ProcessError.Unsupported "Content-Length input requires a command built with Command.KeepStdinOpen"
                )
            )
        | Some pipe ->
            task {
                try
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
