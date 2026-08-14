namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Runtime.ExceptionServices
open System.Text
open System.Threading
open System.Threading.Tasks

/// Internal: reading captured output into lines, raw bytes, and feeding stdin.
module internal Pump =

    /// The default line terminator for input interpreted as a terminal Enter key or as a plain line.
    let defaultInputLineTerminator (terminalEnter: bool) = if terminalEnter then "\r" else "\n"

    /// Encode one nullable text line and append `terminator` without first concatenating strings.
    /// `Stdin.FromLines` historically treats a null element as an empty line, so retain that behaviour.
    let lineWithTerminator (encoding: Encoding) (terminator: string) (text: string) : byte[] =
        let text = if isNull (box text) then String.Empty else text

        let bytes =
            Array.zeroCreate<byte> (encoding.GetByteCount text + encoding.GetByteCount terminator)

        let written = encoding.GetBytes(text.AsSpan(), bytes.AsSpan())
        encoding.GetBytes(terminator.AsSpan(), bytes.AsSpan(written)) |> ignore
        bytes

    /// Encode one nullable text line and append the protocol's LF.
    let lineWithLf (encoding: Encoding) (text: string) : byte[] =
        lineWithTerminator encoding (defaultInputLineTerminator false) text

    /// Accumulates retained output lines under an `OutputBufferPolicy`, tracking cumulative
    /// totals and whether the fail-loud ceiling tripped. Not thread-safe; one per stream.
    ///
    /// Byte accounting: a retained line's accounted cost is `Encoding.UTF8.GetByteCount line + 1` —
    /// the line's own UTF-8 bytes, plus one byte for the `\n` separator that `Text` reintroduces when
    /// it joins retained lines back together (`String.Join('\n', ...)`). Every retained line is
    /// charged the separator byte, including the very last one (which needs no *trailing* separator
    /// once reassembled), so the accounted total is a deliberate, small over-estimate — by at most one
    /// byte per retained line — of what `Text` actually produces; it never under-counts, so a
    /// configured `MaxBytes` genuinely bounds the reassembled string's size. Charging the separator
    /// this way is also what makes an empty line cost a non-zero amount: without it,
    /// `Encoding.UTF8.GetByteCount ""` is `0`, so an unbounded flood of bare newlines under `MaxBytes`
    /// alone (no `MaxLines`) would retain an unbounded number of empty-string records — defeating the
    /// byte cap as a memory bound. This is a line-buffer-only convention: the raw-byte capture path
    /// (`RawBuffer`/`RawCapture`, used by `OutputBytesAsync` and pipeline stdout/stderr capture) has no
    /// line structure and charges the literal byte count, unaffected by any of this.
    type LineBuffer(policy: OutputBufferPolicy) =
        // Each retained line carries its own accounted byte cost alongside it (computed once, on
        // Add — see the type doc comment above), so a `DropOldest` eviction can subtract the stored
        // cost instead of re-scanning the evicted string through `Encoding.UTF8.GetByteCount` a second
        // time.
        let retained = LinkedList<struct (string * int)>()
        let mutable retainedBytes = 0
        let mutable totalLines = 0L
        let mutable totalBytes = 0L
        let mutable truncated = false
        let mutable tooLarge = false
        // DropNewest must retain a contiguous prefix: once its byte cap rejects a line (or is
        // reached exactly), later shorter lines cannot fill a hole in the captured output.
        let mutable dropNewestByteCapClosed = false

        // The retained/total byte counts only matter when a byte cap is set or the fail-loud ceiling
        // is in play; under the default (line-only / unbounded) policy, skip the per-line UTF-8 scan
        // and its separator surcharge. `TotalBytes` is therefore meaningful only in those modes.
        let needBytes = policy.MaxBytes.IsSome || policy.Overflow = OverflowMode.Error

        let overLineCap () =
            match policy.MaxLines with
            | Some cap -> retained.Count >= cap
            | None -> false

        let wouldOverByteCap (addition: int) =
            match policy.MaxBytes with
            | Some cap -> retainedBytes + addition > cap
            | None -> false

        member _.TotalLines = int (min totalLines (int64 Int32.MaxValue))
        member _.TotalBytes = int (min totalBytes (int64 Int32.MaxValue))
        member _.Truncated = truncated
        member _.TooLarge = tooLarge

        member _.Text = String.Join('\n', retained |> Seq.map (fun struct (s, _) -> s))

        /// Record a complete line, applying the policy. See the type doc comment for how `line`'s
        /// accounted byte cost is derived (its own UTF-8 bytes plus one separator byte).
        member _.Add(line: string) =
            let bytes = if needBytes then Encoding.UTF8.GetByteCount line + 1 else 0

            if totalLines < Int64.MaxValue then
                totalLines <- totalLines + 1L

            let byteCount = int64 bytes

            totalBytes <-
                if totalBytes > Int64.MaxValue - byteCount then
                    Int64.MaxValue
                else
                    totalBytes + byteCount

            let full =
                overLineCap ()
                || wouldOverByteCap bytes
                || (policy.Overflow = OverflowMode.DropNewest && dropNewestByteCapClosed)

            match policy.Overflow with
            | OverflowMode.Error when full ->
                // Fail-loud ceiling: count but never retain.
                tooLarge <- true
            | OverflowMode.DropNewest when full ->
                truncated <- true
                dropNewestByteCapClosed <- dropNewestByteCapClosed || wouldOverByteCap bytes
            | OverflowMode.DropOldest when full ->
                truncated <- true
                retained.AddLast(struct (line, bytes)) |> ignore
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
                        let struct (_, evictedBytes) = node.Value
                        retained.RemoveFirst()
                        retainedBytes <- retainedBytes - evictedBytes
            | _ ->
                retained.AddLast(struct (line, bytes)) |> ignore
                retainedBytes <- retainedBytes + bytes

                match policy.MaxBytes with
                | Some cap when policy.Overflow = OverflowMode.DropNewest && retainedBytes >= cap ->
                    dropNewestByteCapClosed <- true
                | _ -> ()

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
    /// ignored by construction. The unbounded (`MaxBytes = None`) case never constructs this — `RawSink`
    /// keeps a plain accumulator there — so `cap` is always the configured non-negative `MaxBytes`.
    /// `DropOldest` keeps the LAST `cap` bytes (a byte ring built from retained chunks, evicting the
    /// front); `DropNewest` and `Error` keep the FIRST `cap` bytes, `Error` additionally tripping its
    /// fail-loud ceiling once the cap is exceeded. Memory is bounded to `cap` (plus one in-flight read
    /// chunk while evicting), so a small cap never buffers a large flood. Not thread-safe on its own;
    /// `RawSink`, the only production owner, serializes every access to it.
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

    /// Append the run `buffer[start .. stop-1]` to `line`, force-flushing it to `onLine` whenever it
    /// reaches `cap` UTF-8 bytes. A Unicode scalar value is kept intact even when it is larger than
    /// the cap, because splitting a surrogate pair would corrupt the decoded text; the caller's
    /// `LineBuffer` policy then decides whether that one over-cap segment is retained. The
    /// `lineBytes` cell carries the exact UTF-8 size across decoded chunks and is reset whenever the
    /// line is emitted. The same algorithm is shared by `readLines`'s `Lf` hot path and its CR-aware
    /// path's `appendRun` (a multi-character run) and `appendChar` (a single character passed as a
    /// one-element range, so a deferred '\r'/'\n' that no longer lives at its original `charBuffer`
    /// position still goes through the same logic).
    let private appendCapped
        (line: StringBuilder)
        (buffer: char[])
        (start: int)
        (stop: int)
        (cap: int)
        (onLine: string -> ValueTask)
        (lineBytes: int64 ref)
        : Task =
        task {
            let capBytes = int64 cap
            let utf8 = Encoding.UTF8

            let scalarLength p =
                if
                    Char.IsHighSurrogate buffer[p]
                    && p + 1 < stop
                    && Char.IsLowSurrogate buffer[p + 1]
                then
                    2
                else
                    1

            let flush () =
                task {
                    do! onLine (line.ToString())
                    line.Clear() |> ignore
                    lineBytes.Value <- 0L
                }

            let mutable p = start

            while p < stop do
                // A decoder may place the two UTF-16 code units of one scalar in adjacent output
                // chunks. The high surrogate was counted as its replacement fallback in the previous
                // call, so join it here before deciding whether the cap forces a flush. Otherwise a
                // cap boundary could emit the pair as two invalid strings.
                if
                    Char.IsLowSurrogate buffer[p]
                    && line.Length > 0
                    && Char.IsHighSurrogate line[line.Length - 1]
                then
                    let prefixBytes = lineBytes.Value - 3L

                    if line.Length > 1 && prefixBytes + 4L > capBytes then
                        let high = line[line.Length - 1]
                        line.Length <- line.Length - 1
                        lineBytes.Value <- prefixBytes
                        do! flush ()
                        line.Append high |> ignore
                        line.Append buffer[p] |> ignore
                        lineBytes.Value <- 4L
                    else
                        line.Append buffer[p] |> ignore
                        lineBytes.Value <- prefixBytes + 4L

                    p <- p + 1
                else
                    let scalarWidth = scalarLength p
                    let scalarBytes = int64 (utf8.GetByteCount(buffer, p, scalarWidth))

                    if line.Length > 0 && lineBytes.Value >= capBytes then
                        do! flush ()
                    elif line.Length > 0 && lineBytes.Value + scalarBytes > capBytes then
                        do! flush ()

                    line.Append(buffer, p, scalarWidth) |> ignore
                    lineBytes.Value <- lineBytes.Value + scalarBytes
                    p <- p + scalarWidth
        }
        :> Task

    /// Flush a tee sink after a pump's read loop ends, so a buffered tee (a `BufferedStream`, a
    /// compression/encryption wrapper, a `StreamWriter`-backed stream) hands its last bytes to a
    /// concurrent reader without waiting for the caller to dispose it. Runs on every pump exit path —
    /// clean EOF as well as a read-loop failure — because the caller (`try`/`finally` around the pump
    /// body) invokes it unconditionally. ProcessKit does not own the caller-supplied tee and never
    /// disposes it — only flushes it — and the flush itself is teardown-race-safe: it swallows the
    /// same two exceptions `disposeQuietly` does (a tee closed out from under us, or its underlying
    /// pipe/file breaking mid-flush), so a torn-down tee can never mask the pump's own outcome.
    let private flushTeeQuietly (tee: Stream option) : Task =
        task {
            match tee with
            | Some sink ->
                try
                    // Final flushing must outlive pump cancellation so it cannot replace a saved read fault.
                    do! sink.FlushAsync CancellationToken.None
                with
                | :? ObjectDisposedException ->
                    // The tee was disposed out from under us during a teardown race; nothing to flush.
                    ()
                | :? IOException ->
                    // The tee's underlying pipe/file broke while flushing during teardown; best-effort.
                    ()
            | None -> ()
        }
        :> Task

    /// Read `stream` to EOF: tee the raw bytes (if a sink is set), decode with `encoding`, split into
    /// lines under `terminator`, and pass each complete line — including a final unterminated one — to
    /// `onLine`. `terminator` decides where a line ends: `Lf` (the default) splits on `\n` only,
    /// stripping a preceding `\r`; `Cr`/`CrLf`/`Any` also (or instead) split on a bare `\r`, so
    /// carriage-return progress output streams as per-frame lines (see `LineTerminator`). A `\r\n` pair
    /// is a single terminator in every mode. When `maxLineBytes` is set, an unterminated line that
    /// reaches that many UTF-8 bytes is force-flushed to `onLine` as a segment, so a newline-free flood
    /// can't grow the in-flight buffer without bound (the segment then goes through the caller's buffer
    /// policy). A single Unicode scalar larger than the cap is emitted intact rather than split.
    ///
    /// `onLine` returns a `ValueTask` (not `unit`) so a streaming consumer's sink can genuinely await —
    /// e.g. a bounded channel's backpressured `WriteAsync`, which must stop this very read loop from
    /// draining more of the pipe until the consumer catches up. A buffered sink (a `LineBuffer`) is
    /// synchronous work wrapped in `ValueTask.CompletedTask`, so it costs nothing extra on that path.
    ///
    /// The line-splitting body itself lives in `readLinesBody`; this function only adds the
    /// `finally`-flush of `tee` (see `flushTeeQuietly`), which must run on every exit path of the read
    /// loop — clean EOF as well as a read failure — not just the happy path.
    let private readLinesBody
        (stream: Stream)
        (encoding: Encoding)
        (terminator: LineTerminator)
        (tee: Stream option)
        (onLine: string -> ValueTask)
        (maxLineBytes: int option)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            let decoder = encoding.GetDecoder()
            let byteBuffer = Array.zeroCreate<byte> 8192
            let charBuffer = Array.zeroCreate<char> (encoding.GetMaxCharCount byteBuffer.Length)
            let line = StringBuilder()
            let lineBytes = ref 0L
            let mutable reading = true
            // A leading byte-order mark of the chosen encoding is stripped from the decoded text
            // (GetDecoder, unlike StreamReader, leaves it in). The raw `tee` and OutputBytes stay
            // byte-exact — only decoded text drops the BOM.
            let mutable atStreamStart = true

            // Consume the leading BOM (at most once, across the whole stream) at index 0 of the first
            // non-empty decode, returning the scan start position for the freshly decoded `chars`.
            let consumeBom (chars: int) : int =
                if atStreamStart && chars > 0 then
                    atStreamStart <- false
                    if charBuffer[0] = char 0xFEFF then 1 else 0
                else
                    0

            match terminator with
            | LineTerminator.Lf ->
                // The default, hot path: split on '\n' only, stripping a preceding '\r'. Left exactly
                // as ProcessKit has always pumped lines — a bare '\r' is content (accumulated whole).
                while reading do
                    let! read = stream.ReadAsync(byteBuffer.AsMemory(0, byteBuffer.Length), cancellationToken)
                    let mutable chars = 0

                    if read = 0 then
                        reading <- false
                        chars <- decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true)
                    else
                        match tee with
                        | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                        | None -> ()

                        chars <- decoder.GetChars(byteBuffer, 0, read, charBuffer, 0)

                    let mutable pos = consumeBom chars

                    // Scan both ordinary decoded chunks and the decoder's EOF flush through this one
                    // block. Keeping it inline in the read state machine avoids allocating a Task/closure
                    // per chunk on this hot path.
                    while pos < chars do
                        let newlineIndex = Array.IndexOf(charBuffer, '\n', pos, chars - pos)
                        let runEnd = if newlineIndex >= 0 then newlineIndex else chars

                        match maxLineBytes with
                        | None ->
                            if runEnd > pos then
                                line.Append(charBuffer, pos, runEnd - pos) |> ignore

                            pos <- runEnd
                        | Some cap ->
                            // `appendCapped` advances by Unicode scalar values and enforces the UTF-8
                            // byte cap (see its doc comment), including across decoder chunks.
                            do! appendCapped line charBuffer pos runEnd cap onLine lineBytes
                            pos <- runEnd

                        if newlineIndex >= 0 then
                            if line.Length > 0 && line[line.Length - 1] = '\r' then
                                line.Length <- line.Length - 1

                                match maxLineBytes with
                                | Some _ -> lineBytes.Value <- lineBytes.Value - 1L
                                | None -> ()

                            do! onLine (line.ToString())
                            line.Clear() |> ignore
                            lineBytes.Value <- 0L
                            pos <- newlineIndex + 1

                if line.Length > 0 then
                    if line[line.Length - 1] = '\r' then
                        line.Length <- line.Length - 1

                        match maxLineBytes with
                        | Some _ -> lineBytes.Value <- lineBytes.Value - 1L
                        | None -> ()

                    do! onLine (line.ToString())
            | _ ->
                // '\r'-aware framing (`Cr`/`CrLf`/`Any`). A '\r' is deferred (held in `pendingCr`) until
                // the next character resolves it — a following '\n' makes it a single `\r\n` terminator;
                // otherwise it is a lone '\r', which ends a line under `Cr`/`Any` or is content under
                // `CrLf`. `pendingCr` carries across read boundaries, so a `\r\n` split between two reads
                // still collapses to one terminator.
                let lfSplits = LineTerminatorRules.splitsOnLf terminator
                let crSplits = LineTerminatorRules.splitsOnCr terminator
                let mutable pendingCr = false

                // Emit `line` (a terminator was reached) and reset it for the next line.
                let emitLine () =
                    task {
                        do! onLine (line.ToString())
                        line.Clear() |> ignore
                        lineBytes.Value <- 0L
                    }

                // A single deferred content character (a lone '\r' or '\n') never lives at a stable
                // `charBuffer` position of its own — `pendingCr` can carry it across a read boundary,
                // past whatever the next `decoder.GetChars` overwrites `charBuffer` with — so
                // `appendChar` stages it here as a one-element range and routes it through the same
                // `appendCapped` helper the runs below use.
                let oneChar = Array.zeroCreate<char> 1

                // Append one content character, honouring the `maxLineBytes` UTF-8 byte cap.
                let appendChar (c: char) =
                    task {
                        match maxLineBytes with
                        | None -> line.Append c |> ignore
                        | Some cap ->
                            oneChar[0] <- c
                            do! appendCapped line oneChar 0 1 cap onLine lineBytes
                    }

                // Append the content run `charBuffer[start .. stop-1]`, honouring the UTF-8 byte cap
                // (the same logic as the `Lf` path above).
                let appendRun (start: int) (stop: int) =
                    task {
                        match maxLineBytes with
                        | None ->
                            if stop > start then
                                line.Append(charBuffer, start, stop - start) |> ignore
                        | Some cap -> do! appendCapped line charBuffer start stop cap onLine lineBytes
                    }

                while reading do
                    let! read = stream.ReadAsync(byteBuffer.AsMemory(0, byteBuffer.Length), cancellationToken)
                    let mutable chars = 0

                    if read = 0 then
                        reading <- false

                        // Preserve the existing EOF order: a deferred CR from real input is resolved
                        // before decoder fallback characters are flushed (see the truncated-sequence
                        // regression). The flushed characters then enter the shared scan below.
                        if pendingCr then
                            if crSplits then do! emitLine () else do! appendChar '\r'

                            pendingCr <- false

                        chars <- decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true)
                    else
                        match tee with
                        | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                        | None -> ()

                        chars <- decoder.GetChars(byteBuffer, 0, read, charBuffer, 0)

                    let mutable pos = consumeBom chars

                    // Ordinary decoded chunks and the decoder's EOF flush share the same scanner,
                    // without an extra async helper allocation per read.
                    while pos < chars do
                        if pendingCr then
                            // Resolve the deferred '\r' against the next character.
                            if charBuffer[pos] = '\n' then
                                // '\r\n' — a single terminator; emit the content before the '\r' and
                                // consume the '\n'.
                                do! emitLine ()
                                pos <- pos + 1
                            elif crSplits then
                                // Lone '\r' that ends a line — emit; the current char starts the next
                                // line, so it is left for the scan below (pos not advanced).
                                do! emitLine ()
                            else
                                // Lone '\r' that is content under `CrLf` — keep it, then re-scan the
                                // current char (pos not advanced).
                                do! appendChar '\r'

                            pendingCr <- false
                        else
                            // Batch the run of content up to the next '\r' or '\n'.
                            let crIndex = Array.IndexOf(charBuffer, '\r', pos, chars - pos)
                            let lfIndex = Array.IndexOf(charBuffer, '\n', pos, chars - pos)

                            let sigIndex =
                                match crIndex, lfIndex with
                                | -1, -1 -> -1
                                | -1, n -> n
                                | r, -1 -> r
                                | r, n -> min r n

                            let runEnd = if sigIndex >= 0 then sigIndex else chars
                            do! appendRun pos runEnd

                            if sigIndex < 0 then
                                pos <- chars
                            elif charBuffer[sigIndex] = '\n' then
                                // A lone '\n' (no pending '\r'): a terminator under `Lf`/`Any`,
                                // content under `Cr`/`CrLf`.
                                if lfSplits then do! emitLine () else do! appendChar '\n'
                                pos <- sigIndex + 1
                            else
                                // A '\r' — defer the decision until the next character (or EOF).
                                pendingCr <- true
                                pos <- sigIndex + 1

                // A trailing CR produced by the decoder flush is resolved after its shared scan.
                if pendingCr then
                    if crSplits then do! emitLine () else do! appendChar '\r'

                if line.Length > 0 then
                    do! onLine (line.ToString())
        }
        :> Task

    /// Read `stream` to EOF via `readLinesBody`, then flush `tee` (if set) so a buffered tee sink sees
    /// its last bytes without waiting for the caller to dispose it — on both the clean-EOF path and a
    /// read-loop failure (the `finally` runs either way). See `flushTeeQuietly`.
    let readLines
        (stream: Stream)
        (encoding: Encoding)
        (terminator: LineTerminator)
        (tee: Stream option)
        (onLine: string -> ValueTask)
        (maxLineBytes: int option)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            // The F# `task` builder doesn't support an async (`do!`) `finally` clause (FS0750), so the
            // "flush on every exit path" guarantee is hand-rolled here: catch, flush unconditionally,
            // then rethrow the original exception (via `ExceptionDispatchInfo`, preserving its stack)
            // so a read-loop failure still propagates to the caller after the tee is flushed.
            let mutable fault: exn option = None

            try
                do! readLinesBody stream encoding terminator tee onLine maxLineBytes cancellationToken
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee

            match fault with
            | Some ex -> ExceptionDispatchInfo.Throw ex
            | None -> ()
        }
        :> Task

    /// Classify an `ObjectDisposedException`/`IOException` caught by a background pump
    /// (`readLinesUntilDone`) reading a live child's stdout/stderr pipe. `isTearingDown` is polled
    /// at the moment the exception is CAUGHT (not when the read started), reporting whether this
    /// handle's own teardown has already begun disposing the pipe streams (`RunningProcess`'s
    /// `disposalCts` is cancelled immediately before `host.Teardown()` runs — see
    /// `RunningProcess.reapGuard`/`DisposeAsync`). When it has, the identical exception types are
    /// the routine dispose/broken-pipe race this library's own teardown triggers by design (closing
    /// the streams while a still-running background pump may be mid-read) and are swallowed
    /// (`None`) — the run's outcome still reflects the child. Otherwise the exception is a genuine
    /// OS-level read failure (or an unexpected external dispose) and is returned (`Some`) so the
    /// caller can surface it as `ProcessError.Io` instead of silently ending the capture as if it
    /// were a clean EOF. By analogy with `genuineStdinFault`/`StdinSourceFault` below, which draw
    /// the equivalent genuine-vs-benign distinction for the stdin-feed side.
    let private genuineReadFault (isTearingDown: unit -> bool) (ex: exn) : exn option =
        match ex with
        | :? ObjectDisposedException
        | :? IOException -> if isTearingDown () then None else Some ex
        | _ -> Some ex

    /// `readLines` for a background pump: swallows the disposal / broken-pipe exceptions of a
    /// teardown race — the stream closed underneath an in-flight read AFTER `isTearingDown` started
    /// reporting `true` (see `genuineReadFault`) — so the task never faults unobserved on a routine
    /// teardown. A GENUINE read fault — the identical exception types, but caught while
    /// `isTearingDown` still reports `false` — is a real OS-level read failure and is re-raised
    /// (preserving its original stack via `ExceptionDispatchInfo`) so the caller can surface it as
    /// `ProcessError.Io` instead of silently reporting a truncated capture as a success.
    let readLinesUntilDone
        (stream: Stream)
        (encoding: Encoding)
        (terminator: LineTerminator)
        (tee: Stream option)
        (onLine: string -> ValueTask)
        (maxLineBytes: int option)
        (isTearingDown: unit -> bool)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                do! readLines stream encoding terminator tee onLine maxLineBytes cancellationToken
            with
            | :? ObjectDisposedException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
                    // The stream was torn down (early dispose) while reading. Stop quietly.
                    ()
            | :? IOException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
                    // The pipe broke during teardown. Stop; the run's outcome reflects the child.
                    ()
        }
        :> Task

    /// Read `stream` to EOF as raw decoded TEXT — no line splitting at all — handing each decoded chunk
    /// to `onText` the instant it arrives, and teeing the raw bytes first if a sink is set. The
    /// unframed counterpart of `readLinesBody`, for the interactive expect-style session
    /// (`PtySession`): a terminal prompt such as `Password: ` or `> ` carries **no** line terminator,
    /// so a line pump holds it in its in-flight buffer until a newline (or EOF) finally arrives and a
    /// pattern waiter would never see it. Chunk boundaries are whatever the OS read returned and carry
    /// no meaning — the caller reassembles them into its own sliding window.
    ///
    /// Decoding is incremental through one `Decoder`, so a multi-byte character split across two OS
    /// reads is decoded correctly rather than turning into replacement characters; the decoder's
    /// remaining state is flushed at EOF. A leading byte-order mark is stripped from the decoded text
    /// exactly as `readLinesBody` strips it, so the two paths agree on what the child's text is; the
    /// raw `tee` stays byte-exact.
    let private readTextBody
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onText: string -> unit)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            let decoder = encoding.GetDecoder()
            let byteBuffer = Array.zeroCreate<byte> 8192
            let charBuffer = Array.zeroCreate<char> (encoding.GetMaxCharCount byteBuffer.Length)
            let mutable reading = true
            // A leading BOM of the chosen encoding is content-free framing, not child output — dropped
            // once, at index 0 of the first non-empty decode (same rule as `readLinesBody`).
            let mutable atStreamStart = true

            let emit (chars: int) =
                let start =
                    if atStreamStart && chars > 0 then
                        atStreamStart <- false
                        if charBuffer[0] = char 0xFEFF then 1 else 0
                    else
                        0

                if chars > start then
                    onText (String(charBuffer, start, chars - start))

            while reading do
                let! read = stream.ReadAsync(byteBuffer.AsMemory(0, byteBuffer.Length), cancellationToken)

                if read = 0 then
                    // EOF: flush whatever the decoder still holds (an incomplete trailing sequence
                    // surfaces as the encoding's replacement character rather than vanishing).
                    emit (decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true))
                    reading <- false
                else
                    match tee with
                    | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                    | None -> ()

                    emit (decoder.GetChars(byteBuffer, 0, read, charBuffer, 0))
        }
        :> Task

    /// `readTextBody` plus the same unconditional tee flush `readLines` performs — on the clean-EOF
    /// path and on a read-loop failure alike (see `flushTeeQuietly`).
    let readText
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onText: string -> unit)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            // Hand-rolled "flush on every exit path", for the same reason `readLines` hand-rolls it:
            // the F# `task` builder has no async `finally` (FS0750).
            let mutable fault: exn option = None

            try
                do! readTextBody stream encoding tee onText cancellationToken
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee

            match fault with
            | Some ex -> ExceptionDispatchInfo.Throw ex
            | None -> ()
        }
        :> Task

    /// `readText` for a background pump — the raw-text twin of `readLinesUntilDone`: a teardown race
    /// (the stream disposed underneath an in-flight read once `isTearingDown` reports `true`) ends the
    /// read quietly, while the identical exception types caught before teardown began are a genuine
    /// OS-level read failure and are re-raised with their original stack (see `genuineReadFault`).
    let readTextUntilDone
        (stream: Stream)
        (encoding: Encoding)
        (tee: Stream option)
        (onText: string -> unit)
        (isTearingDown: unit -> bool)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                do! readText stream encoding tee onText cancellationToken
            with
            | :? ObjectDisposedException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
                    // The stream was torn down (early dispose) while reading. Stop quietly.
                    ()
            | :? IOException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
                    // The pipe broke during teardown. Stop; the run's outcome reflects the child.
                    ()
        }
        :> Task

    /// Read `stream` to EOF as raw byte chunks, preserving the exact boundaries returned by each
    /// `ReadAsync`. Every non-empty read owns a fresh byte array, so a consumer may retain the
    /// `ReadOnlyMemory<byte>` after the pump advances to the next read. The raw tee receives the same
    /// bytes before the chunk is handed to the consumer.
    let private readBytesBody
        (stream: Stream)
        (tee: Stream option)
        (onChunk: ReadOnlyMemory<byte> -> ValueTask)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            let mutable reading = true

            while reading do
                let buffer = Array.zeroCreate<byte> 8192
                let! read = stream.ReadAsync(buffer.AsMemory(), cancellationToken)

                if read = 0 then
                    reading <- false
                else
                    let chunk = buffer.AsMemory(0, read)

                    match tee with
                    | Some sink -> do! sink.WriteAsync(chunk, cancellationToken)
                    | None -> ()

                    do! onChunk (ReadOnlyMemory<byte>(buffer, 0, read))
        }
        :> Task

    /// Read raw chunks for a background stdout pump, flushing the tee on every exit path. A
    /// disposal/broken-pipe race is quiet once the caller's teardown flag is set, while the same
    /// exception caught before teardown remains a genuine read failure for the caller to classify as
    /// `ProcessError.Io`. The read/write fault is saved while the tee flush runs so flushing cannot
    /// hide the original error.
    let readBytesUntilDone
        (stream: Stream)
        (tee: Stream option)
        (onChunk: ReadOnlyMemory<byte> -> ValueTask)
        (isTearingDown: unit -> bool)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            let mutable fault: exn option = None

            try
                do! readBytesBody stream tee onChunk cancellationToken
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee

            try
                match fault with
                | Some ex -> ExceptionDispatchInfo.Throw ex
                | None -> ()
            with
            | :? ObjectDisposedException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
                    // The stream was torn down while reading. Stop quietly.
                    ()
            | :? IOException as ex ->
                match genuineReadFault isTearingDown ex with
                | Some fault -> ExceptionDispatchInfo.Throw fault
                | None ->
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

    /// A raw-byte capture accumulator the CALLER owns, rather than one the read loop keeps to itself
    /// and hands back only on completion. That distinction is what lets a consumer report what WAS
    /// captured without first awaiting the pump: the bounded post-exit output drain (`PostExitDrain`)
    /// can end a verb's wait on a pump whose pipe a surviving descendant holds open, and the raw
    /// capture must then still carry the bytes that did arrive — where the old read loop, which
    /// discarded its private buffer on ANY fault, could only offer an empty one.
    ///
    /// Reading a sink while its pump may still be appending IS a concurrent access, so every member
    /// runs under one uncontended `Monitor` — negligible next to the 8 KiB OS read that produced the
    /// chunk. `MaxBytes = None` retains everything (the unbounded default, so `Truncated`/`TooLarge`
    /// are always false); `Some cap` applies the byte cap + `Overflow` mode through `RawBuffer`
    /// (`MaxLines` never applies to a byte stream — it has no line structure).
    type RawSink(policy: OutputBufferPolicy) =
        let gate = obj ()

        // Exactly one of the two is live, chosen once by the policy: the capped ring/prefix buffer, or
        // the unbounded accumulator. A `match` (never `.IsSome`/`.Value`) at every use, per this
        // repository's IONIDE-006 rule.
        let bounded =
            policy.MaxBytes |> Option.map (fun cap -> RawBuffer(cap, policy.Overflow))

        let unbounded = new MemoryStream()

        /// Record a chunk of raw bytes. `source[offset .. offset+count-1]` is copied out (the caller
        /// reuses `source` across reads), so the sink owns its retained bytes.
        member _.Append(source: byte[], offset: int, count: int) =
            lock gate (fun () ->
                match bounded with
                | Some buffer -> buffer.Append(source, offset, count)
                | None -> unbounded.Write(source, offset, count))

        /// The capture as it stands right now — safe to take while the pump is still running.
        member _.Snapshot() : RawCapture =
            lock gate (fun () ->
                match bounded with
                | Some buffer ->
                    { Bytes = buffer.ToArray()
                      Truncated = buffer.Truncated
                      TooLarge = buffer.TooLarge
                      TotalBytes = buffer.TotalBytes }
                | None ->
                    let bytes = unbounded.ToArray()

                    { Bytes = bytes
                      Truncated = false
                      TooLarge = false
                      TotalBytes = bytes.Length })

    /// Read an optional raw stdout stream to EOF into a caller-owned `sink`, teeing the FULL byte
    /// stream if a tee is set (the tee mirrors exactly what the child produced, independently of the
    /// sink's retention policy — just as `readLines` tees before its line buffer applies). The child
    /// never blocks: the pipe is drained to EOF even after a cap is reached. `tee` is flushed once the
    /// read loop ends — clean EOF or a read failure alike — so a buffered tee sink sees its last bytes
    /// without waiting for the caller to dispose it (see `flushTeeQuietly`). The single raw read loop
    /// behind both `captureRawOrEmpty` and `RunningProcess`'s byte verb.
    let captureRawInto
        (sink: RawSink)
        (stream: Stream option)
        (tee: Stream option)
        (cancellationToken: CancellationToken)
        : Task =
        match stream with
        | None -> Task.CompletedTask
        | Some s ->
            task {
                // See the comment on `readLines`'s wrapper for why this is hand-rolled instead of an
                // async `finally` (FS0750): flush unconditionally, then rethrow the original fault.
                let mutable fault: exn option = None

                try
                    let chunk = Array.zeroCreate<byte> 8192
                    let mutable reading = true

                    while reading do
                        let! read = s.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)

                        if read = 0 then
                            reading <- false
                        else
                            match tee with
                            | Some target -> do! target.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                            | None -> ()

                            sink.Append(chunk, 0, read)
                with ex ->
                    fault <- Some ex

                do! flushTeeQuietly tee

                match fault with
                | Some ex -> ExceptionDispatchInfo.Throw ex
                | None -> ()
            }
            :> Task

    /// Capture an optional raw stdout stream to EOF under `policy`. `policy.MaxBytes = None` keeps the
    /// capture UNBOUNDED — there is no byte ceiling to enforce, so its `Truncated`/`TooLarge` are
    /// always false; `Some cap` applies the byte cap + `Overflow` mode (`MaxLines` never applies to a
    /// raw byte stream — it has no line structure). The single entry point the pipeline's stage
    /// captures use, sharing its read loop and its `RawSink` with `RunningProcess.OutputBytesAsync`
    /// (which owns the sink itself, so the bounded post-exit drain can read a partial capture off it)
    /// — so their raw-capture semantics can't drift.
    let captureRawOrEmpty
        (stream: Stream option)
        (tee: Stream option)
        (policy: OutputBufferPolicy)
        (cancellationToken: CancellationToken)
        : Task<RawCapture> =
        task {
            let sink = RawSink policy
            do! captureRawInto sink stream tee cancellationToken
            return sink.Snapshot()
        }

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

    /// Asynchronously dispose a stream, swallowing the same teardown-race exceptions as
    /// `disposeQuietly` — a double-close, or a broken pipe surfaced while flushing on dispose
    /// because the peer is already gone. The async counterpart, for callers that would otherwise
    /// block a thread on a synchronous `Dispose` where a `DisposeAsync` is available.
    let disposeQuietlyAsync (stream: Stream) : Task =
        task {
            try
                do! stream.DisposeAsync().AsTask()
            with
            | :? ObjectDisposedException ->
                // Already disposed (double close during teardown); nothing to do.
                ()
            | :? IOException ->
                // The pipe broke while flushing on dispose (peer end already gone); best-effort teardown.
                ()
        }

    /// Quietly dispose all of a spawned child's parent-side pipe streams (teardown-race-safe).
    let closeSpawned (spawned: Native.Common.Spawned) =
        spawned.Stdout |> Option.iter disposeQuietly
        spawned.Stderr |> Option.iter disposeQuietly
        spawned.Stdin |> Option.iter disposeQuietly
        spawned.ExtraFds |> List.iter (snd >> disposeQuietly)

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

    /// Marks an exception raised while reading/iterating the user-supplied stdin *source* — a
    /// `FromFile`/`FromStream` stream, or a `FromLines`/`FromAsyncLines` generator — as distinct from
    /// one raised while *writing* to the child's stdin pipe (where a broken pipe is routine: the
    /// child may close stdin early, and that is not the caller's error). `feedStdin` tells the two
    /// apart by *where* they were thrown, not by exception type: `readSource` below wraps every
    /// non-cancellation exception from the read/iterate step in this, so the outer handler can
    /// surface it unconditionally as `ProcessError.Stdin`, while a write-side exception still goes
    /// through `genuineStdinFault`'s conservative allow-list.
    exception private StdinSourceFault of inner: exn

    /// Await one read/iteration step against the user-supplied stdin source (`Stream.ReadAsync`,
    /// `IEnumerator.MoveNext`, `IAsyncEnumerator.MoveNextAsync`), wrapping any exception it raises as
    /// a `StdinSourceFault` — except a cancellation, which is rethrown as itself (preserving its
    /// original stack via `ExceptionDispatchInfo`; `reraise` is unavailable inside a task CE) so it
    /// still falls through to `genuineStdinFault`'s ordinary "a cancelled write is not the caller's
    /// error" handling instead of being misclassified as a genuine source fault.
    let inline private readSource (read: unit -> Task<'T>) : Task<'T> =
        task {
            try
                let! result = read ()
                return result
            with
            | :? OperationCanceledException as ex ->
                ExceptionDispatchInfo.Throw ex
                return Unchecked.defaultof<'T>
            | ex -> return raise (StdinSourceFault ex)
        }

    /// Run one *synchronous* acquisition/read step against the user-supplied stdin source
    /// (`seq.GetEnumerator`, `IAsyncEnumerable.GetAsyncEnumerator`, an enumerator's `Current`, or
    /// `File.OpenRead`), wrapping any exception it raises as a `StdinSourceFault` — except a
    /// cancellation, which is rethrown as itself (via `ExceptionDispatchInfo`, preserving its stack) so
    /// it still classifies through `genuineStdinFault`'s ordinary "a cancelled feed is not the caller's
    /// error" handling. The synchronous companion to `readSource`: together they make a fault at ANY
    /// enumeration stage — not just `MoveNext`/`ReadAsync` — surface as `ProcessError.Stdin` instead of
    /// slipping past into the outer handler and being mistaken for a benign broken pipe.
    let private sourceStep (step: unit -> 'T) : 'T =
        try
            step ()
        with
        | :? OperationCanceledException as ex ->
            ExceptionDispatchInfo.Throw ex
            Unchecked.defaultof<'T>
        | ex -> raise (StdinSourceFault ex)

    /// Read one inter-stage relay chunk while retaining the fact that the exception came from the
    /// upstream stream. `Stream.CopyToAsync` combines the read and write sides into one task, so its
    /// caller cannot tell a genuine upstream read fault from the downstream process closing its input.
    let private readCopyChunk
        (source: Stream)
        (buffer: byte[])
        (cancellationToken: CancellationToken)
        : Task<Result<int, exn>> =
        task {
            try
                let! read = source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).AsTask()
                return Ok read
            with ex ->
                // Preserve the source-side provenance; the relay classifies this separately from a
                // downstream write failure after the read step returns.
                return Error ex
        }

    /// Classify one inter-stage copy. A write-side broken pipe (the downstream stage stopped reading)
    /// and a write-side teardown race are routine and return `None`. A read-side IOException or
    /// ObjectDisposedException is quiet only when this pipeline's teardown has already begun; the same
    /// exception before teardown is a genuine upstream read failure and becomes a typed `ProcessError.Io`.
    /// Other read/write exceptions are also surfaced as I/O failures rather than being allowed to turn a
    /// truncated downstream input into a successful pipeline. The task itself never faults, so callers'
    /// `Task.WhenAll` observations cannot lose the first copy-pump failure.
    let copyToAsync
        (upstreamProgram: string)
        (downstreamProgram: string)
        (source: Stream)
        (destination: Stream)
        (isTearingDown: unit -> bool)
        (cancellationToken: CancellationToken)
        : Task<ProcessError option> =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let mutable reading = true
            let mutable fault: ProcessError option = None

            while reading && fault.IsNone do
                let! readResult = readCopyChunk source buffer cancellationToken

                match readResult with
                | Error ex ->
                    match ex with
                    | (:? ObjectDisposedException | :? IOException) when isTearingDown () ->
                        // The pipeline is already tearing down and closed the relay under an in-flight
                        // upstream read; the terminal stage outcome remains authoritative.
                        reading <- false
                    | _ ->
                        fault <-
                            Some(ProcessError.Io $"pipeline stage '{upstreamProgram}' stdout read failed: {ex.Message}")

                        reading <- false
                | Ok read when read = 0 -> reading <- false
                | Ok read ->
                    try
                        do! destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    with
                    | :? IOException ->
                        // The downstream stage closed its input early; the resulting broken pipe is the
                        // normal `producer | head` relay outcome, not a copy-pump error.
                        reading <- false
                    | :? ObjectDisposedException ->
                        // Teardown or an early downstream exit disposed the write end; close the relay
                        // quietly and let the observed process outcomes decide pipefail.
                        reading <- false
                    | :? OperationCanceledException when isTearingDown () ->
                        // A teardown cancellation raced the write even though the relay normally uses an
                        // uncancelled token; no data fault should be reported for this routine race.
                        reading <- false
                    | :? OperationCanceledException as ex ->
                        fault <-
                            Some(
                                ProcessError.Io $"pipeline stage '{downstreamProgram}' stdin write failed: {ex.Message}"
                            )

                        reading <- false
                    | ex ->
                        fault <-
                            Some(
                                ProcessError.Io $"pipeline stage '{downstreamProgram}' stdin write failed: {ex.Message}"
                            )

                        reading <- false

            return fault
        }

    /// Copy `source` to `destination` chunk-by-chunk, reading via `readSource` so a read-side fault
    /// against a `FromFile`/`FromStream` source is distinguishable, by where it was thrown, from a
    /// write-side broken pipe. Used instead of `Stream.CopyToAsync`, which performs both sides in one
    /// call and would erase that distinction.
    let private pumpStream (source: Stream) (destination: Stream) (cancellationToken: CancellationToken) : Task =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! read =
                    readSource (fun () ->
                        source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).AsTask())

                if read = 0 then
                    reading <- false
                else
                    do! destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
        }
        :> Task

    /// End the child's stdin where a bulk stdin feed is done with it: the source was the child's complete
    /// input, so the child must now see END OF INPUT. For a pipe end that is the ordinary close. A stdin
    /// stream that owns no handle to close has to deliver its terminal's own end-of-input gesture instead
    /// (`Native.Common.IStdinFinisher`) — both implementations today are non-owning PTY views, the POSIX one
    /// over the shared pty master and the Windows one over the ConPTY session's host-input pipe — because
    /// closing either releases nothing and takes no terminal with it, so a child reading to EOF would wait
    /// forever for an end of input that can never arrive.
    ///
    /// BOTH points where a bulk feed reaches the end of its delivery route through here, so neither can end a
    /// PTY run's stdin with a close that ends nothing: after the source is drained (`feedStdinWithEncoding`),
    /// and where delivery ends before it starts because the source could not be opened at all (the eager
    /// `StdinSource.File` failure in `feedStdinSourceWithEncoding`).
    ///
    /// Never throws. The close is best-effort exactly as `disposeQuietly` is, but a failed end-of-input
    /// delivery is NOT a teardown race to swallow — the child is left waiting on input this feed promised to
    /// end — so it is RETURNED for the caller to stash behind any earlier fault it already recorded.
    /// (`FinishAsync` itself already completes successfully for the two cases where end of input is moot
    /// rather than lost: a hung-up terminal, an already-torn-down run.)
    ///
    /// The gesture runs as a `backgroundTask` for the same reason the feed does — one caller is on the thread
    /// that started the run — while a pipe end's close needs no task at all and completes synchronously.
    let private endStdinAfterSource (stdinStream: Stream) : Task<exn option> =
        match box stdinStream with
        | :? Native.Common.IStdinFinisher as finisher ->
            backgroundTask {
                try
                    do! finisher.FinishAsync()
                    return None
                with ex ->
                    // Returned rather than thrown or swallowed: this task must never fault (one caller
                    // stashes it behind an earlier fault, the other has nothing left to report it through).
                    return Some ex
            }
        | _ ->
            disposeQuietly stdinStream
            Task.FromResult<exn option> None

    /// Write a stdin source to the child's stdin stream; close it (EOF) afterwards unless the
    /// caller is keeping it open for interactive writing. Never faults: returns a genuine
    /// source failure — a source-acquisition failure (per `genuineStdinFault`) on the write side, or
    /// (per `StdinSourceFault`, unconditionally) any fault reading/iterating the source itself — for
    /// the caller to surface, or `None`.
    ///
    /// Runs as a `backgroundTask` — detached onto the thread pool — so it never captures the
    /// `SynchronizationContext` of the thread that started the run. The feed is kicked off
    /// synchronously from the caller of `command.StartAsync` (via `feedStdinSource` inside
    /// `ProcessGroup.BuildHost`), so a plain `task { }` would post its post-`await` continuations back
    /// to that caller's context. On a single-threaded context (a WPF/WinForms UI thread, classic
    /// ASP.NET) that would deadlock `RunningProcess.TakeStdin`, which blocks that same thread on this
    /// feed's completion: the feed's continuation could never run because the one thread is parked in
    /// `TakeStdin`. `backgroundTask` keeps the whole feed on the pool, so that blocking wait is safe.
    /// (Off any such context — a pool or background thread, as in the tests and CI — `backgroundTask`
    /// is identical to `task`, so nothing else changes.)
    let private feedStdinWithEncoding
        (encoding: Encoding)
        (source: StdinSource)
        (stdinStream: Stream)
        (closeWhenDone: bool)
        (cancellationToken: CancellationToken)
        : Task<exn option> =
        backgroundTask {
            let mutable fault = None

            try
                match source with
                | StdinSource.Empty -> ()
                | StdinSource.Inherit ->
                    // Unreachable in practice: `Command.InheritStdin` creates no stdin pipe, so the
                    // native spawn returns `Spawned.Stdin = None` and `feedStdinSource` never starts a
                    // feed for it (there is no `stdinStream` to write into). Handled explicitly rather
                    // than via a wildcard so this stays exhaustive if a new source is added; writing
                    // nothing and reaching EOF is the only safe behaviour should it ever be reached.
                    ()
                | StdinSource.Text text ->
                    let bytes = encoding.GetBytes text
                    do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                | StdinSource.Bytes bytes -> do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                | StdinSource.File _ ->
                    // Unreachable in practice: `feedStdinSource` below always intercepts
                    // `StdinSource.File` first, opening the file eagerly (so a fast child can't lose a
                    // missing file behind an otherwise-successful outcome) and rewriting it to
                    // `StdinSource.Reader` before ever calling `feedStdin`. Handled explicitly rather
                    // than via a wildcard so this stays exhaustive if a new source is added; there is no
                    // safe independent implementation to fall back to here, so this case never runs.
                    ()
                | StdinSource.Reader reader -> do! pumpStream reader stdinStream cancellationToken
                | StdinSource.Lines lines ->
                    // `GetEnumerator`/`Current` through `sourceStep` (as `MoveNext` already goes through
                    // `readSource`), so a fault at any of the three enumeration stages surfaces.
                    use enumerator = sourceStep (fun () -> lines.GetEnumerator())
                    let mutable more = true

                    while more do
                        // Stop promptly between lines when the lifecycle token is cancelled — a sync
                        // `MoveNext` can't be interrupted mid-call, but an infinite/slow generator must
                        // not keep feeding a torn-down child. A cancelled feed is not the caller's error
                        // (it falls through to `genuineStdinFault` as `None`).
                        cancellationToken.ThrowIfCancellationRequested()
                        let! hasNext = readSource (fun () -> Task.FromResult(enumerator.MoveNext()))

                        if hasNext then
                            let current = sourceStep (fun () -> enumerator.Current)
                            let bytes = lineWithLf encoding current
                            do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                        else
                            more <- false
                | StdinSource.AsyncLines lines ->
                    // `use` on an `IAsyncEnumerator<'T>` (an `IAsyncDisposable`) inside this `task { }`
                    // makes the F# task-CE binder call `DisposeAsync` genuinely asynchronously and
                    // exactly once — on normal completion, on an exception from `MoveNextAsync`/
                    // `WriteAsync`, and on cancellation — instead of blocking a thread-pool thread on
                    // `.DisposeAsync().AsTask().GetAwaiter().GetResult()`. Any exception raised here
                    // still propagates to the outer `with ex ->` below unchanged.
                    use enumerator = sourceStep (fun () -> lines.GetAsyncEnumerator cancellationToken)
                    let mutable more = true

                    while more do
                        let! has = readSource (fun () -> enumerator.MoveNextAsync().AsTask())

                        if has then
                            let current = sourceStep (fun () -> enumerator.Current)
                            let bytes = lineWithLf encoding current
                            do! stdinStream.WriteAsync(bytes.AsMemory(), cancellationToken)
                        else
                            more <- false

                do! stdinStream.FlushAsync cancellationToken
            with ex ->
                // The stdin writer runs detached, so swallow to keep it from faulting unobserved; but
                // stash a genuine fault so an otherwise-successful run can surface it as
                // `ProcessError.Stdin` instead of silently feeding the child truncated/empty input. A
                // read-side fault against the source (`StdinSourceFault`) always surfaces; a write-side
                // exception — broken pipe / torn-down stream / cancelled write — classifies via the
                // conservative `genuineStdinFault` allow-list, so a routine broken pipe stays `None`.
                fault <-
                    match ex with
                    | StdinSourceFault inner -> Some inner
                    | _ -> genuineStdinFault ex

            if closeWhenDone then
                // The source was the child's complete input, so the child must now see END OF INPUT — a
                // close for a pipe end, the terminal's own end-of-input gesture for a stdin stream that owns
                // no handle to close (see `endStdinAfterSource`).
                let! endFault = endStdinAfterSource stdinStream

                // A failed end-of-input delivery is stashed so an otherwise-successful run surfaces it as
                // `ProcessError.Stdin`, but never displaces a source fault already recorded above — that one
                // is the earlier, primary cause. (A best-effort close never reports one at all.)
                match fault with
                | None -> fault <- endFault
                | Some _ -> ()

            return fault
        }

    /// Feed a source with the historical UTF-8 default. Production callers use
    /// `feedStdinSourceWithEncoding` so a command's text-stdin encoding is honoured.
    let feedStdin
        (source: StdinSource)
        (stdinStream: Stream)
        (closeWhenDone: bool)
        (cancellationToken: CancellationToken)
        : Task<exn option> =
        feedStdinWithEncoding Encoding.UTF8 source stdinStream closeWhenDone cancellationToken

    /// The ceiling on the bounded FINAL observation of a still-running stdin feed
    /// (`StdinFeeder.ObserveFaultAsync`): how long a result-producing verb may wait, after its child has
    /// already exited cleanly and its output drains have finished, for a slow source to conclude and
    /// stash its genuine failure. Mirrors the `PUMP_TEARDOWN` window ProcessKit-rs bounds the same
    /// observation by (`87d6ca498696`/`bc2e43efab45`). Long enough that a source which merely lost the
    /// race with a fast-exiting child still gets to report honestly; short enough — and always followed
    /// by `Stop` — that a genuinely hung source can never hold the verb open indefinitely. It is only
    /// ever *reached* by a feed still running after the child exited, which is the rare case; a feed that
    /// already finished resolves with no wait at all.
    let stdinFinalObservationBudget = TimeSpan.FromSeconds 5.0

    /// Internal test seam: overrides `stdinFinalObservationBudget` for the bounded final observation, so
    /// a regression test can prove the "a hung source never holds the verb" bound in milliseconds instead
    /// of seconds. `None` (the default) in every production run; nothing but a test ever sets it.
    let mutable stdinFinalObservationBudgetForTests: TimeSpan option = None

    /// Internal test seam: invoked exactly when a bounded final observation is about to WAIT on a feed
    /// that is still running — never on the fast path where the feed has already finished, and never when
    /// the caller decided not to observe at all (a non-accepted outcome). It lets a deterministic
    /// regression test release its gated source at precisely that instant, so "the source failed AFTER
    /// the child exited and the pumps drained" needs no timing guesswork. `None` in every production run.
    let mutable stdinFinalObservationTestHook: (unit -> unit) option = None

    /// A started background stdin feed together with the lifecycle token that stops it. Created by
    /// `feedStdinSource`, one per started child.
    ///
    /// Closing the child's stdin pipe only ever unblocks a feed parked on a *write*; a feed parked in a
    /// user source's own read step (a `FromAsyncLines` hung in `MoveNextAsync`, an endless
    /// `FromLines`) is unblocked only by cancelling this token. `Stop` does exactly that, so teardown,
    /// cancellation, a timeout, or an early child exit can prompt the feed to unwind and dispose the
    /// user's enumerator/stream instead of leaking it past teardown. The feed never faults (every
    /// exception is swallowed into a stashed fault), so a stopped feed simply completes with no fault.
    ///
    /// `Fault` reports a genuine source failure, but only once the feed has finished — a still-running
    /// feed yields `None` and never blocks. It is the non-blocking *peek*, for a caller that has already
    /// decided the fault could not be surfaced anyway (a louder failure won). A caller about to decide an
    /// otherwise-successful run's result uses `ObserveFaultAsync` instead: the peek alone loses a source
    /// that is still reading when the child exits, which is exactly the failure `ProcessError.Stdin`
    /// exists to report.
    type StdinFeeder internal (feed: Task<exn option>, cts: CancellationTokenSource option) =
        // Serializes the lifecycle CTS's `Cancel` (from `Stop`) against its single `Dispose` (from the
        // completion cleanup below) so cancelling can never race the dispose. Not a hot path — one
        // feeder per started child, touched only at teardown/completion.
        let gate = obj ()
        let mutable disposed = false

        // Release the lifecycle CTS once the feed has finished — on its own or because `Stop` cancelled
        // it. Scheduled (NOT `ExecuteSynchronously`) so this can never run inline on the thread inside
        // `Stop`'s `Cancel`; combined with `gate` it either runs before `Stop` observes `disposed`
        // (then `Stop` is a no-op) or waits for `Stop`'s `Cancel` to return first.
        do
            match cts with
            | Some source ->
                feed.ContinueWith(
                    (fun (_: Task<exn option>) ->
                        lock gate (fun () ->
                            if not disposed then
                                disposed <- true
                                source.Dispose())),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                )
                |> ignore
            | None -> ()

        /// The background feed task. It never faults; a genuine source failure is stashed in its result.
        member _.Task = feed

        /// A genuine stdin-source failure, observable only once the feed has finished, else `None`.
        member _.Fault: exn option = if feed.IsCompletedSuccessfully then feed.Result else None

        /// The BOUNDED FINAL observation of this feed, for the one moment a result-producing verb decides
        /// an otherwise-successful run's result: the child has exited with an accepted code and the output
        /// drains have finished, so nothing but this feed can still turn that success into a
        /// `ProcessError.Stdin`.
        ///
        /// A feed that has already finished resolves immediately with its stashed fault (the common case —
        /// no wait, no timer). A feed still running gets `stdinFinalObservationBudget` to conclude on its
        /// own, so a slow `FromStream`/`FromLines`/`FromAsyncLines` source that only fails AFTER a fast
        /// child exited is observed and reported as the real cause instead of being torn down unread. A
        /// source still parked when the budget runs out is `Stop`ped — its lifecycle token is cancelled so
        /// it unwinds and disposes the user's enumerator/stream — and reported as no fault: this awaits the
        /// budget, never the source, so a genuinely hung source can delay the verb's result by at most the
        /// budget and can never hang it. (`Stop` is idempotent, so the teardown that follows is unaffected.)
        ///
        /// A routine broken pipe stays a non-failure exactly as before: the classification of what counts
        /// as a genuine fault lives in the feed itself (`genuineStdinFault`/`StdinSourceFault`) and is not
        /// touched here — this only decides how long to WAIT for that verdict. The feed never faults, so
        /// this never throws; it returns `None` for a feed that somehow did not complete successfully.
        member this.ObserveFaultAsync() : Task<exn option> =
            if feed.IsCompleted then
                Task.FromResult(if feed.IsCompletedSuccessfully then feed.Result else None)
            else
                task {
                    stdinFinalObservationTestHook |> Option.iter (fun hook -> hook ())

                    let budget =
                        stdinFinalObservationBudgetForTests
                        |> Option.defaultValue stdinFinalObservationBudget

                    // The delay's own token exists solely to release the timer as soon as the feed wins the
                    // race, so a short verb never leaves a budget-long timer armed behind it.
                    use timer = new CancellationTokenSource()
                    let feedTask = feed :> Task
                    let! finished = Task.WhenAny(feedTask, Task.Delay(budget, timer.Token))

                    if obj.ReferenceEquals(finished, feedTask) then
                        timer.Cancel()
                        return (if feed.IsCompletedSuccessfully then feed.Result else None)
                    else
                        // Still reading its source after the whole budget: stop it (cancelling the lifecycle
                        // token unwinds a parked read and disposes the user's enumerator/stream) and report
                        // no fault rather than waiting on a source that may never conclude.
                        this.Stop()
                        return None
                }

        /// Stop the feed: cancel its lifecycle token so a feed parked in the user's source unwinds and
        /// disposes the user's enumerator/stream. Idempotent and teardown-race-safe (a no-op once the
        /// feed has finished, and for the nothing-to-feed feeder).
        member _.Stop() =
            match cts with
            | Some source ->
                lock gate (fun () ->
                    if not disposed then
                        try
                            source.Cancel()
                        with :? ObjectDisposedException ->
                            // The feed finished and the completion cleanup disposed the CTS between our
                            // `disposed` check and here; there is nothing left to cancel.
                            ())
            | None -> ()

    /// Feed a stdin `source` (if any) into the child's `stdin` (if piped) in the background. Whether the
    /// pipe is closed (EOF) once the source is drained is decided by `keepStdinOpen`: normally a source is
    /// the child's complete input, so stdin is closed after (`keepStdinOpen = false`); but when the command
    /// set `KeepStdinOpen` the pipe is left open so the caller can keep writing to it interactively via
    /// `RunningProcess.TakeStdin` after the source is exhausted (`keepStdinOpen = true`). Returns a
    /// `StdinFeeder` — the feed task plus its lifecycle token — so the run can observe a genuine source
    /// failure once the feed has finished, stop the feed at teardown, AND (via `StdinFeeder.Task`) know when
    /// the source is fully drained so a kept-open pipe is handed to the caller with no second writer racing
    /// it. When there is nothing to feed the feeder is an inert no-op (no token, `Stop` does nothing,
    /// `Fault` is always `None`). (The no-source interactive case keeps the stream for `TakeStdin`.)
    let feedStdinSourceWithEncoding
        (encoding: Encoding)
        (stdin: Stream option)
        (source: Stdin option)
        (keepStdinOpen: bool)
        : StdinFeeder =
        match stdin, source with
        | Some stdinStream, Some src ->
            // Open a file source before returning the spawned handle. A fast child can otherwise exit
            // before the background feed gets scheduled, making a missing file nondeterministically
            // disappear behind an otherwise-successful outcome.
            match src.Source with
            | StdinSource.File path ->
                try
                    let file = File.OpenRead path
                    let cts = new CancellationTokenSource()

                    let feed =
                        feedStdinWithEncoding
                            encoding
                            (StdinSource.Reader file)
                            stdinStream
                            (not keepStdinOpen)
                            cts.Token

                    feed.ContinueWith(
                        (fun (_: Task<exn option>) -> file.Dispose()),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default
                    )
                    |> ignore

                    StdinFeeder(feed, Some cts)
                with ex ->
                    // No source bytes can ever be written, so this IS where bulk delivery ends: end the
                    // child's stdin exactly as a drained feed does, or a PTY child reading to EOF is left
                    // waiting on input this run promised to deliver — a close of a non-owning view releases
                    // nothing and delivers no end of input (see `endStdinAfterSource`). Gated by
                    // `keepStdinOpen` the same way `closeWhenDone` gates the drained feed: a caller that
                    // keeps writing interactively has not reached its end of input, and this gesture cannot
                    // be taken back — that caller ends its own input through `ProcessStdin.FinishAsync`,
                    // through the very stream this branch would otherwise have taken away from it.
                    let feed =
                        backgroundTask {
                            if not keepStdinOpen then
                                // A failed delivery does not displace `ex`: the source error is the earlier,
                                // primary cause and the one the caller must fix, and a feeder reports one
                                // fault. (Both would surface through the same `ProcessError.Stdin`.)
                                let! _ = endStdinAfterSource stdinStream
                                ()

                            return Some ex
                        }

                    // No lifecycle token: there is no source to stop, so the feeder is finished — carrying the
                    // source error to the terminal verb — as soon as the child's stdin has been ended.
                    StdinFeeder(feed, None)
            | _ ->
                // The lifecycle token: cancelling it stops the feed (unblocking a parked source read),
                // disposed by the feeder once the feed has finished.
                let cts = new CancellationTokenSource()
                // Close the pipe (EOF) after the source UNLESS `KeepStdinOpen` kept it open for interactive
                // writing: then the feed just drains + flushes the source and leaves the stream open, and its
                // completion (`StdinFeeder.Task`) is the point after which `TakeStdin` may hand the stream to
                // the caller — never while the feed is still writing (two writers on one pipe is forbidden).
                let feed =
                    feedStdinWithEncoding encoding src.Source stdinStream (not keepStdinOpen) cts.Token

                StdinFeeder(feed, Some cts)
        | _ -> StdinFeeder(Task.FromResult(None: exn option), None)

    /// Feed a stdin source with the historical UTF-8 default; kept for internal callers and focused
    /// pump tests that do not have a `Command` configuration to supply.
    let feedStdinSource (stdin: Stream option) (source: Stdin option) (keepStdinOpen: bool) : StdinFeeder =
        feedStdinSourceWithEncoding Encoding.UTF8 stdin source keepStdinOpen
