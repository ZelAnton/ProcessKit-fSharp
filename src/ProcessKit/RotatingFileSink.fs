namespace ProcessKit

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// A caller-owned write-only stream that rotates a file by size for use with `Command.StdoutTee` or
/// `Command.StderrTee`. The active file is `path`; archives are `path.1` (newest) through
/// `path.<maxFiles>` (oldest). Writes are split at `maxBytes`, so no file created by the sink exceeds
/// the configured size. Rotation and write failures propagate through the ordinary tee error path.
///
/// Unlike `StdoutToFile`/`StderrToFile`, this sink is fed by ProcessKit's parent-side pump: it supports
/// rotation, but stops receiving bytes when the parent exits. The caller owns and must dispose it.
[<Sealed>]
type RotatingFileSink(path: string, maxBytes: int64, maxFiles: int) =
    inherit Stream()

    do ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof path)
    do ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L, nameof maxBytes)
    do ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1, nameof maxFiles)

    let fullPath = Path.GetFullPath path
    let gate = new SemaphoreSlim(1, 1)
    let mutable disposed = false

    let openActive () =
        let stream =
            new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous
            )

        stream.Seek(0L, SeekOrigin.End) |> ignore
        stream

    let mutable active: FileStream option = Some(openActive ())

    let throwIfDisposed () =
        ObjectDisposedException.ThrowIf(disposed, nameof RotatingFileSink)

    let current () =
        match active with
        | Some stream -> stream
        | None ->
            let stream = openActive ()
            active <- Some stream
            stream

    let rotate () =
        match active with
        | Some stream ->
            stream.Dispose()
            active <- None
        | None -> ()

        for index = maxFiles downto 1 do
            let source = if index = 1 then fullPath else $"{fullPath}.{index - 1}"
            let destination = $"{fullPath}.{index}"

            if File.Exists destination then
                File.Delete destination

            if File.Exists source then
                File.Move(source, destination)

        active <- Some(openActive ())

    let prepareChunk (remaining: int) =
        let stream = current ()

        if stream.Length >= maxBytes then
            rotate ()

        let stream = current ()
        let capacity = maxBytes - stream.Length
        stream, (min (int64 remaining) capacity |> int)

    member _.Path = fullPath
    member _.MaxBytes = maxBytes
    member _.MaxFiles = maxFiles

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = not disposed

    override _.Length =
        gate.Wait()

        try
            throwIfDisposed ()
            (current ()).Length
        finally
            gate.Release() |> ignore

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() =
        gate.Wait()

        try
            throwIfDisposed ()
            (current ()).Flush()
        finally
            gate.Release() |> ignore

    override _.FlushAsync(cancellationToken: CancellationToken) : Task =
        task {
            do! gate.WaitAsync cancellationToken

            try
                throwIfDisposed ()
                do! (current ()).FlushAsync cancellationToken
            finally
                gate.Release() |> ignore
        }
        :> Task

    override _.Write(buffer: byte[], offset: int, count: int) =
        ArgumentNullException.ThrowIfNull buffer
        ArgumentOutOfRangeException.ThrowIfNegative offset
        ArgumentOutOfRangeException.ThrowIfNegative count

        if offset > buffer.Length - count then
            raise (ArgumentException "offset and count exceed the buffer length")

        gate.Wait()

        try
            throwIfDisposed ()
            let mutable cursor = offset
            let mutable remaining = count

            while remaining > 0 do
                let stream, chunk = prepareChunk remaining
                stream.Write(buffer, cursor, chunk)
                cursor <- cursor + chunk
                remaining <- remaining - chunk
        finally
            gate.Release() |> ignore

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) : ValueTask =
        ValueTask(
            task {
                do! gate.WaitAsync cancellationToken

                try
                    throwIfDisposed ()
                    let mutable cursor = 0

                    while cursor < buffer.Length do
                        let stream, chunk = prepareChunk (buffer.Length - cursor)
                        do! stream.WriteAsync(buffer.Slice(cursor, chunk), cancellationToken)
                        cursor <- cursor + chunk
                finally
                    gate.Release() |> ignore
            }
        )

    override this.WriteAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) : Task =
        this.WriteAsync(ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask()

    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())

    override _.Dispose(disposing: bool) =
        if disposing then
            gate.Wait()

            try
                if not disposed then
                    disposed <- true
                    active |> Option.iter (fun stream -> stream.Dispose())
                    active <- None
            finally
                gate.Release() |> ignore

        base.Dispose disposing
