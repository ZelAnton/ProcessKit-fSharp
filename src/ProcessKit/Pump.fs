namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// Internal: reading captured output into lines, raw bytes, and feeding stdin.
module internal Pump =

    /// Accumulates retained output lines under an `OutputBufferPolicy`, tracking cumulative
    /// totals and whether the fail-loud ceiling tripped. Not thread-safe; one per stream.
    type LineBuffer(policy: OutputBufferPolicy) =
        let retained = LinkedList<string>()
        let mutable retainedBytes = 0
        let mutable totalLines = 0
        let mutable totalBytes = 0
        let mutable truncated = false
        let mutable tooLarge = false

        let byteLen (line: string) = Encoding.UTF8.GetByteCount line

        // The retained/total byte counts only matter when a byte cap is set or the fail-loud ceiling
        // is in play; under the default (line-only / unbounded) policy, skip the per-line UTF-8 scan.
        // `TotalBytes` is therefore meaningful only in those modes.
        let needBytes = policy.MaxBytes.IsSome || policy.Overflow = OverflowMode.Error

        let overLineCap () =
            match policy.MaxLines with
            | Some cap -> retained.Count >= cap
            | None -> false

        let wouldOverByteCap (addition: int) =
            match policy.MaxBytes with
            | Some cap -> retainedBytes + addition > cap
            | None -> false

        member _.TotalLines = totalLines
        member _.TotalBytes = totalBytes
        member _.Truncated = truncated
        member _.TooLarge = tooLarge
        member _.Text = String.Join('\n', retained)

        /// Record a complete line, applying the policy.
        member _.Add(line: string) =
            let bytes = if needBytes then byteLen line else 0
            totalLines <- totalLines + 1
            totalBytes <- totalBytes + bytes
            let unbounded = policy.MaxLines.IsNone && policy.MaxBytes.IsNone
            let full = overLineCap () || wouldOverByteCap bytes

            match policy.Overflow with
            | OverflowMode.Error when full || unbounded ->
                // Fail-loud ceiling: count but never retain.
                tooLarge <- true
            | OverflowMode.DropNewest when full -> truncated <- true
            | OverflowMode.DropOldest when full ->
                truncated <- true
                retained.AddLast line |> ignore
                retainedBytes <- retainedBytes + bytes

                let fits () =
                    (match policy.MaxLines with
                     | Some cap -> retained.Count <= cap
                     | None -> true)
                    && (match policy.MaxBytes with
                        | Some cap -> retainedBytes <= cap
                        | None -> true)

                while not (fits ()) && retained.Count > 0 do
                    match retained.First with
                    | null -> ()
                    | node ->
                        retained.RemoveFirst()
                        retainedBytes <- retainedBytes - (if needBytes then byteLen node.Value else 0)
            | _ ->
                retained.AddLast line |> ignore
                retainedBytes <- retainedBytes + bytes

    /// Read `stream` to EOF: tee the raw bytes (if a sink is set), decode with `encoding`, split
    /// into lines (stripping `\n` / `\r\n`), and pass each complete line — including a final
    /// unterminated one — to `onLine`.
    let readLines
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onLine: string -> unit)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            let decoder = encoding.GetDecoder()
            let byteBuffer = Array.zeroCreate<byte> 8192
            let charBuffer = Array.zeroCreate<char> (encoding.GetMaxCharCount byteBuffer.Length)
            let line = StringBuilder()
            let mutable reading = true
            // A leading byte-order mark of the chosen encoding is stripped from the decoded text
            // (GetDecoder, unlike StreamReader, leaves it in). The raw `tee` and OutputBytes stay
            // byte-exact — only decoded text drops the BOM.
            let mutable atStreamStart = true

            while reading do
                let! read = stream.ReadAsync(byteBuffer.AsMemory(0, byteBuffer.Length), cancellationToken)

                if read = 0 then
                    reading <- false
                else
                    match tee with
                    | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                    | None -> ()

                    let chars = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0)
                    let mutable i = 0

                    while i < chars do
                        let c = charBuffer[i]

                        if atStreamStart && c = char 0xFEFF then
                            atStreamStart <- false
                        else
                            atStreamStart <- false

                            if c = '\n' then
                                if line.Length > 0 && line[line.Length - 1] = '\r' then
                                    line.Length <- line.Length - 1

                                onLine (line.ToString())
                                line.Clear() |> ignore
                            else
                                line.Append c |> ignore

                        i <- i + 1

            if line.Length > 0 then
                if line[line.Length - 1] = '\r' then
                    line.Length <- line.Length - 1

                onLine (line.ToString())
        }
        :> Task

    /// `readLines` for a background pump: swallows the disposal / broken-pipe exceptions of a
    /// teardown race (the stream closed underneath an in-flight read), so the task never faults
    /// unobserved.
    let readLinesUntilDone
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onLine: string -> unit)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                do! readLines stream encoding tee onLine cancellationToken
            with
            | :? ObjectDisposedException ->
                // The stream was torn down (early dispose) while reading. Stop quietly.
                ()
            | :? IOException ->
                // The pipe broke during teardown. Stop; the run's outcome reflects the child.
                ()
        }
        :> Task

    /// Read `stream` to EOF, discarding everything (so the child never blocks on a full pipe).
    let drainDiscard (stream: Stream) (cancellationToken: CancellationToken) : Task =
        task {
            let chunk = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! read = stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)

                if read = 0 then
                    reading <- false
        }
        :> Task

    /// Read `stream` to EOF as raw bytes (no line splitting), teeing if a sink is set.
    let drainRaw (stream: Stream) (tee: Stream option) (cancellationToken: CancellationToken) : Task<byte[]> =
        task {
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! read = stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)

                if read = 0 then
                    reading <- false
                else
                    do! buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)

                    match tee with
                    | Some sink -> do! sink.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    | None -> ()

            return buffer.ToArray()
        }

    /// `drainDiscard` over an optional stream — a completed no-op when the stream isn't piped.
    let drainDiscardOrEmpty (stream: Stream option) (cancellationToken: CancellationToken) : Task =
        match stream with
        | Some s -> drainDiscard s cancellationToken
        | None -> Task.CompletedTask

    /// `drainDiscardOrEmpty` that stops quietly if the stream is torn down mid-drain (early dispose /
    /// broken pipe). For a fire-and-forget drain that nobody awaits — e.g. a `WaitAny`/`WaitAll` loser
    /// the caller disposes while its drain is still reading — so the abandoned drain completes instead
    /// of faulting as an unobserved task.
    let drainDiscardOrEmptyUntilDone (stream: Stream option) (cancellationToken: CancellationToken) : Task =
        task {
            try
                match stream with
                | Some s -> do! drainDiscard s cancellationToken
                | None -> ()
            with
            | :? ObjectDisposedException ->
                // The stream was disposed by teardown while draining (an abandoned race loser).
                ()
            | :? IOException ->
                // The pipe broke during teardown. Stop quietly.
                ()
            | :? OperationCanceledException ->
                // The drain was cancelled during teardown. Stop quietly.
                ()
        }
        :> Task

    /// `drainRaw` over an optional stream — an empty byte array when the stream isn't piped.
    let drainRawOrEmpty
        (stream: Stream option)
        (tee: Stream option)
        (cancellationToken: CancellationToken)
        : Task<byte[]> =
        match stream with
        | Some s -> drainRaw s tee cancellationToken
        | None -> Task.FromResult Array.empty<byte>

    /// Dispose a stream, swallowing the exceptions a teardown race raises — a double-close, or a
    /// broken pipe surfaced while flushing on dispose because the peer is already gone. The one
    /// definition of "teardown-race-safe close" used wherever a pipe stream is torn down.
    let disposeQuietly (stream: Stream) =
        try
            stream.Dispose()
        with
        | :? ObjectDisposedException ->
            // Already disposed (double close during teardown); nothing to do.
            ()
        | :? IOException ->
            // The pipe broke while flushing on dispose (peer end already gone); best-effort teardown.
            ()

    /// Quietly dispose all three of a spawned child's parent-side pipe streams (teardown-race-safe).
    let closeSpawned (spawned: Native.Spawned) =
        spawned.Stdout |> Option.iter disposeQuietly
        spawned.Stderr |> Option.iter disposeQuietly
        spawned.Stdin |> Option.iter disposeQuietly

    /// Write a stdin source to the child's stdin stream; close it (EOF) afterwards unless the
    /// caller is keeping it open for interactive writing.
    let feedStdin
        (source: StdinSource)
        (stdinStream: Stream)
        (closeWhenDone: bool)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                match source with
                | StdinSource.Empty -> ()
                | StdinSource.Bytes bytes -> do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                | StdinSource.File path ->
                    use file = File.OpenRead path
                    do! file.CopyToAsync(stdinStream, cancellationToken)
                | StdinSource.Reader reader -> do! reader.CopyToAsync(stdinStream, cancellationToken)
                | StdinSource.Lines lines ->
                    for entry in lines do
                        let bytes = Encoding.UTF8.GetBytes(entry + "\n")
                        do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                | StdinSource.AsyncLines lines ->
                    let enumerator = lines.GetAsyncEnumerator cancellationToken

                    try
                        let mutable more = true

                        while more do
                            let! has = enumerator.MoveNextAsync()

                            if has then
                                let bytes = Encoding.UTF8.GetBytes(enumerator.Current + "\n")
                                do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                            else
                                more <- false
                    finally
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

                do! stdinStream.FlushAsync cancellationToken
            with _ ->
                // The stdin writer is best-effort and runs detached: a broken pipe (child closed
                // stdin early), a missing FromFile path, or a torn-down stream all mean the child
                // gets partial/no input, which the run's outcome already reflects. Swallowing keeps
                // the detached task from faulting unobserved; the failure is never actionable here.
                ()

            if closeWhenDone then
                disposeQuietly stdinStream
        }
        :> Task

    /// Feed a stdin `source` (if any) into the child's `stdin` (if piped) in the background, then EOF.
    /// Detached fire-and-forget: a source is the child's complete input, so stdin is closed after.
    /// (The no-source interactive case keeps the stream for `TakeStdin` instead of feeding here.)
    let feedStdinSource (stdin: Stream option) (source: Stdin option) =
        match stdin, source with
        | Some stdinStream, Some src -> feedStdin src.Source stdinStream true CancellationToken.None |> ignore
        | _ -> ()
