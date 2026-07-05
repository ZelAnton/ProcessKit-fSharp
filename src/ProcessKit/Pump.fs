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

    /// The retained bytes of a bounded raw-byte capture, plus whether the byte cap truncated them
    /// (`DropOldest`/`DropNewest`) or tripped the fail-loud ceiling (`Error`), and the cumulative byte
    /// total seen (`TotalBytes`, saturating at `Int32.MaxValue` — carried into the `OutputTooLarge`
    /// diagnostics). A raw byte stream has no line structure, so `TotalLines` is meaningless and is not
    /// tracked here.
    type RawCapture =
        { Bytes: byte[]
          Truncated: bool
          TooLarge: bool
          TotalBytes: int }

    /// Accumulates retained raw stdout bytes under an `OutputBufferPolicy`'s byte cap + `OverflowMode` —
    /// the byte-stream analogue of `LineBuffer` (which retains decoded lines). Only `MaxBytes` and
    /// `Overflow` govern a raw byte stream; `MaxLines` has no meaning without line structure, so it is
    /// ignored by construction. The unbounded (`MaxBytes = None`) case never constructs this — it uses
    /// `drainRaw` — so `cap` is always the configured non-negative `MaxBytes`. `DropOldest` keeps the
    /// LAST `cap` bytes (a byte ring built from retained chunks, evicting the front); `DropNewest` and
    /// `Error` keep the FIRST `cap` bytes, `Error` additionally tripping its fail-loud ceiling once the
    /// cap is exceeded. Memory is bounded to `cap` (plus one in-flight read chunk while evicting), so a
    /// small cap never buffers a large flood. Not thread-safe; one per stream.
    type RawBuffer(cap: int, overflow: OverflowMode) =
        // DropOldest retains from the tail (evicting the front); Error/DropNewest retain from the head.
        let isTail = overflow = OverflowMode.DropOldest
        // Retained bytes as their arrived chunks. `frontOffset` skips already-evicted bytes at the front
        // of the first chunk, so a tail eviction can trim at sub-chunk granularity without recopying the
        // survivors. `retained` is the live retained-byte count (Σ chunk lengths − frontOffset).
        let chunks = LinkedList<byte[]>()
        let mutable frontOffset = 0
        let mutable retained = 0
        let mutable total = 0L

        /// Record a chunk of raw bytes, applying the byte cap. `source[offset .. offset+count-1]` is
        /// copied out (the caller reuses `source` across reads), so the buffer owns its retained bytes.
        member _.Append(source: byte[], offset: int, count: int) =
            if count > 0 then
                total <- total + int64 count

                if isTail then
                    // Retain the new bytes, then evict from the front until we fit `cap` (only the LAST
                    // `cap` bytes of the whole stream can survive).
                    chunks.AddLast(Array.sub source offset count) |> ignore
                    retained <- retained + count

                    while retained > cap && chunks.Count > 0 do
                        match chunks.First with
                        | null ->
                            // Unreachable while Count > 0 (a non-empty LinkedList has a First node); the
                            // arm exists only to satisfy the nullable match and never loops.
                            ()
                        | node ->
                            let available = node.Value.Length - frontOffset
                            let over = retained - cap

                            if available <= over then
                                // The whole front chunk is now stale — drop it.
                                chunks.RemoveFirst()
                                frontOffset <- 0
                                retained <- retained - available
                            else
                                // Part of the front chunk survives — skip its stale prefix in place.
                                frontOffset <- frontOffset + over
                                retained <- retained - over
                elif retained < cap then
                    // Head: retain only up to `cap`; the excess is dropped (DropNewest) or trips the
                    // fail-loud ceiling (Error) — `Truncated`/`TooLarge` below read that off `total`.
                    let take = min count (cap - retained)
                    chunks.AddLast(Array.sub source offset take) |> ignore
                    retained <- retained + take

        /// True once anything was dropped (a `DropOldest`/`DropNewest` truncation); always false for
        /// `Error`, whose over-cap signal is `TooLarge`.
        member _.Truncated = overflow <> OverflowMode.Error && total > int64 cap

        /// True once an `Error` (fail-loud) cap is exceeded; always false for the dropping modes.
        member _.TooLarge = overflow = OverflowMode.Error && total > int64 cap

        /// Cumulative stdout bytes seen, saturating at `Int32.MaxValue` (a raw flood can exceed it).
        member _.TotalBytes = int (min total (int64 Int32.MaxValue))

        /// The retained bytes, in stream order.
        member _.ToArray() : byte[] =
            let result = Array.zeroCreate<byte> retained
            let mutable pos = 0
            // Skip the evicted prefix on the first chunk only (`frontOffset`); later chunks are whole.
            let mutable skip = frontOffset

            for chunk in chunks do
                let len = chunk.Length - skip

                if len > 0 then
                    Array.blit chunk skip result pos len
                    pos <- pos + len

                skip <- 0

            result

    /// Read `stream` to EOF: tee the raw bytes (if a sink is set), decode with `encoding`, split
    /// into lines (stripping `\n` / `\r\n`), and pass each complete line — including a final
    /// unterminated one — to `onLine`. When `maxLineLength` is set, an unterminated line that reaches
    /// that many characters is force-flushed to `onLine` as a segment, so a newline-free flood can't
    /// grow the in-flight buffer without bound (the segment then goes through the caller's buffer policy).
    ///
    /// `onLine` returns a `ValueTask` (not `unit`) so a streaming consumer's sink can genuinely await —
    /// e.g. a bounded channel's backpressured `WriteAsync`, which must stop this very read loop from
    /// draining more of the pipe until the consumer catches up. A buffered sink (a `LineBuffer`) is
    /// synchronous work wrapped in `ValueTask.CompletedTask`, so it costs nothing extra on that path.
    let readLines
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onLine: string -> ValueTask)
        (maxLineLength: int option)
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

                                do! onLine (line.ToString())
                                line.Clear() |> ignore
                            else
                                match maxLineLength with
                                | Some cap when line.Length >= cap ->
                                    // Force-flush an over-long unterminated line so a newline-free flood
                                    // can't grow the in-flight buffer past the cap; the flushed segment
                                    // then goes through the caller's buffer policy (dropped / errored).
                                    do! onLine (line.ToString())
                                    line.Clear() |> ignore
                                | _ -> ()

                                line.Append c |> ignore

                        i <- i + 1

            if line.Length > 0 then
                if line[line.Length - 1] = '\r' then
                    line.Length <- line.Length - 1

                do! onLine (line.ToString())
        }
        :> Task

    /// `readLines` for a background pump: swallows the disposal / broken-pipe exceptions of a
    /// teardown race (the stream closed underneath an in-flight read), so the task never faults
    /// unobserved.
    let readLinesUntilDone
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onLine: string -> ValueTask)
        (maxLineLength: int option)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                do! readLines stream encoding tee onLine maxLineLength cancellationToken
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
    /// broken pipe). For a fire-and-forget drain that nobody awaits — e.g. a `WaitAnyAsync`/`WaitAllAsync` loser
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

    /// Read `stream` to EOF as raw bytes under an `OutputBufferPolicy`'s byte `cap` + `overflow` mode,
    /// teeing the FULL byte stream if a sink is set — the tee mirrors exactly what the child produced,
    /// so it is independent of the in-memory retention policy (just as `readLines` tees before its line
    /// buffer applies). Returns the retained bytes plus the truncation / fail-loud / total signals. The
    /// child never blocks: the pipe is always drained to EOF even after the cap is reached.
    let drainRawBounded
        (stream: Stream)
        (tee: Stream option)
        (cap: int)
        (overflow: OverflowMode)
        (cancellationToken: CancellationToken)
        : Task<RawCapture> =
        task {
            let buffer = RawBuffer(cap, overflow)
            let chunk = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! read = stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)

                if read = 0 then
                    reading <- false
                else
                    match tee with
                    | Some sink -> do! sink.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    | None -> ()

                    buffer.Append(chunk, 0, read)

            return
                { Bytes = buffer.ToArray()
                  Truncated = buffer.Truncated
                  TooLarge = buffer.TooLarge
                  TotalBytes = buffer.TotalBytes }
        }

    /// Capture an optional raw stdout stream to EOF under `policy`. `policy.MaxBytes = None` keeps the
    /// capture UNBOUNDED (via `drainRaw`, unchanged) — there is no byte ceiling to enforce, so its
    /// `Truncated`/`TooLarge` are always false; `Some cap` applies the byte cap + `Overflow` mode
    /// (`MaxLines` never applies to a raw byte stream — it has no line structure). The single entry
    /// point the byte verb (`RunningProcess.OutputBytesAsync`) and the pipeline's last-stage capture
    /// share, so their raw-capture semantics can't drift.
    let captureRawOrEmpty
        (stream: Stream option)
        (tee: Stream option)
        (policy: OutputBufferPolicy)
        (cancellationToken: CancellationToken)
        : Task<RawCapture> =
        match policy.MaxBytes with
        | None ->
            task {
                let! bytes = drainRawOrEmpty stream tee cancellationToken

                return
                    { Bytes = bytes
                      Truncated = false
                      TooLarge = false
                      TotalBytes = bytes.Length }
            }
        | Some cap ->
            match stream with
            | Some s -> drainRawBounded s tee cap policy.Overflow cancellationToken
            | None ->
                Task.FromResult
                    { Bytes = Array.empty<byte>
                      Truncated = false
                      TooLarge = false
                      TotalBytes = 0 }

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

    /// Classify a stdin-feed exception. A genuine *source-acquisition* failure — the input could not
    /// be opened or accessed (a missing `FromFile` path, a directory in its place, no read
    /// permission) — is actionable and returned so it can surface as `ProcessError.Stdin`. Everything
    /// else is `None` (swallowed): a broken pipe (the child closed stdin early — routine, and the
    /// child decides when to stop reading), a stream disposed at teardown, or a cancelled write are
    /// not the caller's error. The set is deliberately conservative and matched by exception *type*,
    /// which is identical across Windows/Linux/macOS — never by a platform-specific error code — so a
    /// routine broken-pipe write can never be misclassified as a failure (which would spuriously fail
    /// the common `producer | head` early-exit pattern).
    let private genuineStdinFault (ex: exn) : exn option =
        match ex with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException
        | :? UnauthorizedAccessException -> Some ex
        | _ -> None

    /// Write a stdin source to the child's stdin stream; close it (EOF) afterwards unless the
    /// caller is keeping it open for interactive writing. Never faults: returns a genuine
    /// source-acquisition failure (per `genuineStdinFault`) for the caller to surface, or `None`.
    let feedStdin
        (source: StdinSource)
        (stdinStream: Stream)
        (closeWhenDone: bool)
        (cancellationToken: CancellationToken)
        : Task<exn option> =
        task {
            let mutable fault = None

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
                    // `use` on an `IAsyncEnumerator<'T>` (an `IAsyncDisposable`) inside this `task { }`
                    // makes the F# task-CE binder call `DisposeAsync` genuinely asynchronously and
                    // exactly once — on normal completion, on an exception from `MoveNextAsync`/
                    // `WriteAsync`, and on cancellation — instead of blocking a thread-pool thread on
                    // `.DisposeAsync().AsTask().GetAwaiter().GetResult()`. Any exception raised here
                    // still propagates to the outer `with ex ->` below unchanged.
                    use enumerator = lines.GetAsyncEnumerator cancellationToken
                    let mutable more = true

                    while more do
                        let! has = enumerator.MoveNextAsync()

                        if has then
                            let bytes = Encoding.UTF8.GetBytes(enumerator.Current + "\n")
                            do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                        else
                            more <- false

                do! stdinStream.FlushAsync cancellationToken
            with ex ->
                // The stdin writer runs detached, so swallow to keep it from faulting unobserved; but
                // stash a genuine source failure so an otherwise-successful run can surface it as
                // `ProcessError.Stdin` instead of silently feeding the child empty input. A broken
                // pipe / torn-down stream / cancelled write classifies as `None` — see `genuineStdinFault`.
                fault <- genuineStdinFault ex

            if closeWhenDone then
                disposeQuietly stdinStream

            return fault
        }

    /// Feed a stdin `source` (if any) into the child's `stdin` (if piped) in the background, then EOF.
    /// A source is the child's complete input, so stdin is closed after. Returns the feed task so the
    /// run can observe a genuine source failure once it has finished (`Task.FromResult None` when
    /// there is nothing to feed). (The no-source interactive case keeps the stream for `TakeStdin`.)
    let feedStdinSource (stdin: Stream option) (source: Stdin option) : Task<exn option> =
        match stdin, source with
        | Some stdinStream, Some src -> feedStdin src.Source stdinStream true CancellationToken.None
        | _ -> Task.FromResult(None: exn option)
