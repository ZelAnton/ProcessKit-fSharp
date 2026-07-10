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
        let mutable totalLines = 0
        let mutable totalBytes = 0
        let mutable truncated = false
        let mutable tooLarge = false

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

        member _.TotalLines = totalLines
        member _.TotalBytes = totalBytes
        member _.Truncated = truncated
        member _.TooLarge = tooLarge

        member _.Text = String.Join('\n', retained |> Seq.map (fun struct (s, _) -> s))

        /// Record a complete line, applying the policy. See the type doc comment for how `line`'s
        /// accounted byte cost is derived (its own UTF-8 bytes plus one separator byte).
        member _.Add(line: string) =
            let bytes = if needBytes then Encoding.UTF8.GetByteCount line + 1 else 0
            totalLines <- totalLines + 1
            totalBytes <- totalBytes + bytes
            let full = overLineCap () || wouldOverByteCap bytes

            match policy.Overflow with
            | OverflowMode.Error when full ->
                // Fail-loud ceiling: count but never retain.
                tooLarge <- true
            | OverflowMode.DropNewest when full -> truncated <- true
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

    /// Append the run `buffer[start .. stop-1]` to `line`, force-flushing it to `onLine` whenever it
    /// reaches `cap` characters. Batched: append up to the remaining budget (`cap - line.Length`) in
    /// one `StringBuilder.Append(char[], int, int)` call; if the run isn't fully consumed yet, flush
    /// the now-full line, append exactly the one character that tripped the cap (an unconditional
    /// post-check append, so even a cap of 0 still makes progress), and repeat for the remainder of
    /// the run. The one definition of the force-flush-at-cap algorithm, shared by `readLines`'s `Lf`
    /// hot path and its CR-aware path's `appendRun` (a multi-character run) and `appendChar` (a
    /// single character passed as a one-element range, so a deferred '\r'/'\n' that no longer lives
    /// at its original `charBuffer` position still goes through the same logic).
    let private appendCapped
        (line: StringBuilder)
        (buffer: char[])
        (start: int)
        (stop: int)
        (cap: int)
        (onLine: string -> ValueTask)
        : Task =
        task {
            let mutable p = start

            while p < stop do
                if line.Length >= cap then
                    do! onLine (line.ToString())
                    line.Clear() |> ignore
                    line.Append buffer[p] |> ignore
                    p <- p + 1
                else
                    let budget = cap - line.Length
                    let take = min budget (stop - p)
                    line.Append(buffer, p, take) |> ignore
                    p <- p + take
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
    let private flushTeeQuietly (tee: Stream option) (cancellationToken: CancellationToken) : Task =
        task {
            match tee with
            | Some sink ->
                try
                    do! sink.FlushAsync cancellationToken
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
    /// is a single terminator in every mode. When `maxLineLength` is set, an unterminated line that
    /// reaches that many characters is force-flushed to `onLine` as a segment, so a newline-free flood
    /// can't grow the in-flight buffer without bound (the segment then goes through the caller's buffer
    /// policy).
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

                    if read = 0 then
                        reading <- false
                    else
                        match tee with
                        | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                        | None -> ()

                        let chars = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0)
                        let mutable pos = consumeBom chars

                        // Scan the decoded chunk for '\n' via `IndexOf` instead of a per-character loop —
                        // each run of non-newline characters between two `\n` (or to the end of the chunk)
                        // is appended to `line` in one batched `StringBuilder.Append(char[], int, int)` call.
                        while pos < chars do
                            let newlineIndex = Array.IndexOf(charBuffer, '\n', pos, chars - pos)
                            let runEnd = if newlineIndex >= 0 then newlineIndex else chars

                            match maxLineLength with
                            | None ->
                                if runEnd > pos then
                                    line.Append(charBuffer, pos, runEnd - pos) |> ignore

                                pos <- runEnd
                            | Some cap ->
                                // `appendCapped` batches the per-character force-flush at the cap (see its
                                // doc comment); it always advances through the whole run, so `pos` lands on
                                // `runEnd` exactly as the inline version's `p` used to.
                                do! appendCapped line charBuffer pos runEnd cap onLine
                                pos <- runEnd

                            if newlineIndex >= 0 then
                                if line.Length > 0 && line[line.Length - 1] = '\r' then
                                    line.Length <- line.Length - 1

                                do! onLine (line.ToString())
                                line.Clear() |> ignore
                                pos <- newlineIndex + 1

                let flushedChars = decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true)
                let mutable flushedPos = consumeBom flushedChars

                while flushedPos < flushedChars do
                    let newlineIndex =
                        Array.IndexOf(charBuffer, '\n', flushedPos, flushedChars - flushedPos)

                    let runEnd = if newlineIndex >= 0 then newlineIndex else flushedChars

                    match maxLineLength with
                    | None ->
                        if runEnd > flushedPos then
                            line.Append(charBuffer, flushedPos, runEnd - flushedPos) |> ignore

                        flushedPos <- runEnd
                    | Some cap ->
                        do! appendCapped line charBuffer flushedPos runEnd cap onLine
                        flushedPos <- runEnd

                    if newlineIndex >= 0 then
                        if line.Length > 0 && line[line.Length - 1] = '\r' then
                            line.Length <- line.Length - 1

                        do! onLine (line.ToString())
                        line.Clear() |> ignore
                        flushedPos <- newlineIndex + 1

                if line.Length > 0 then
                    if line[line.Length - 1] = '\r' then
                        line.Length <- line.Length - 1

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
                    }

                // A single deferred content character (a lone '\r' or '\n') never lives at a stable
                // `charBuffer` position of its own — `pendingCr` can carry it across a read boundary,
                // past whatever the next `decoder.GetChars` overwrites `charBuffer` with — so
                // `appendChar` stages it here as a one-element range and routes it through the same
                // `appendCapped` helper the runs below use.
                let oneChar = Array.zeroCreate<char> 1

                // Append one content character, honouring the `maxLineLength` force-flush.
                let appendChar (c: char) =
                    task {
                        match maxLineLength with
                        | None -> line.Append c |> ignore
                        | Some cap ->
                            oneChar[0] <- c
                            do! appendCapped line oneChar 0 1 cap onLine
                    }

                // Append the content run `charBuffer[start .. stop-1]`, honouring the force-flush (the
                // same batched logic as the `Lf` path above).
                let appendRun (start: int) (stop: int) =
                    task {
                        match maxLineLength with
                        | None ->
                            if stop > start then
                                line.Append(charBuffer, start, stop - start) |> ignore
                        | Some cap -> do! appendCapped line charBuffer start stop cap onLine
                    }

                while reading do
                    let! read = stream.ReadAsync(byteBuffer.AsMemory(0, byteBuffer.Length), cancellationToken)

                    if read = 0 then
                        reading <- false
                    else
                        match tee with
                        | Some sink -> do! sink.WriteAsync(byteBuffer.AsMemory(0, read), cancellationToken)
                        | None -> ()

                        let chars = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0)
                        let mutable pos = consumeBom chars

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

                // EOF: resolve any deferred trailing '\r', then flush a final unterminated line.
                if pendingCr then
                    if crSplits then
                        // Trailing bare '\r' ended the last frame — emit its content (even if empty,
                        // mirroring how a trailing '\n' emits under `Lf`).
                        do! emitLine ()
                    else
                        // Content under `CrLf` — keep the trailing '\r' on the final line.
                        do! appendChar '\r'

                    pendingCr <- false

                let flushedChars = decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, true)
                let mutable flushedPos = consumeBom flushedChars

                while flushedPos < flushedChars do
                    if pendingCr then
                        if charBuffer[flushedPos] = '\n' then
                            do! emitLine ()
                            flushedPos <- flushedPos + 1
                        elif crSplits then
                            do! emitLine ()
                        else
                            do! appendChar '\r'

                        pendingCr <- false
                    else
                        let crIndex = Array.IndexOf(charBuffer, '\r', flushedPos, flushedChars - flushedPos)
                        let lfIndex = Array.IndexOf(charBuffer, '\n', flushedPos, flushedChars - flushedPos)

                        let sigIndex =
                            match crIndex, lfIndex with
                            | -1, -1 -> -1
                            | -1, n -> n
                            | r, -1 -> r
                            | r, n -> min r n

                        let runEnd = if sigIndex >= 0 then sigIndex else flushedChars
                        do! appendRun flushedPos runEnd

                        if sigIndex < 0 then
                            flushedPos <- flushedChars
                        elif charBuffer[sigIndex] = '\n' then
                            if lfSplits then do! emitLine () else do! appendChar '\n'
                            flushedPos <- sigIndex + 1
                        else
                            pendingCr <- true
                            flushedPos <- sigIndex + 1

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
        (maxLineLength: int option)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            // The F# `task` builder doesn't support an async (`do!`) `finally` clause (FS0750), so the
            // "flush on every exit path" guarantee is hand-rolled here: catch, flush unconditionally,
            // then rethrow the original exception (via `ExceptionDispatchInfo`, preserving its stack)
            // so a read-loop failure still propagates to the caller after the tee is flushed.
            let mutable fault: exn option = None

            try
                do! readLinesBody stream encoding terminator tee onLine maxLineLength cancellationToken
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee cancellationToken

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
        (maxLineLength: int option)
        (isTearingDown: unit -> bool)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            try
                do! readLines stream encoding terminator tee onLine maxLineLength cancellationToken
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

    /// Read `stream` to EOF as raw bytes (no line splitting), teeing if a sink is set. `tee` is flushed
    /// once the read loop ends — clean EOF or a read failure alike, via `finally` — so a buffered tee
    /// sink sees its last bytes without waiting for the caller to dispose it (see `flushTeeQuietly`).
    let drainRaw (stream: Stream) (tee: Stream option) (cancellationToken: CancellationToken) : Task<byte[]> =
        task {
            use buffer = new MemoryStream()
            // See the comment on `readLines`'s wrapper for why this is hand-rolled instead of an async
            // `finally` (FS0750): flush unconditionally, then rethrow the original fault (if any).
            let mutable fault: exn option = None

            try
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
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee cancellationToken

            match fault with
            | Some ex -> ExceptionDispatchInfo.Throw ex
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
    /// child never blocks: the pipe is always drained to EOF even after the cap is reached. `tee` is
    /// flushed once the read loop ends — clean EOF or a read failure alike, via `finally` — so a
    /// buffered tee sink sees its last bytes without waiting for the caller to dispose it (see
    /// `flushTeeQuietly`).
    let drainRawBounded
        (stream: Stream)
        (tee: Stream option)
        (cap: int)
        (overflow: OverflowMode)
        (cancellationToken: CancellationToken)
        : Task<RawCapture> =
        task {
            let buffer = RawBuffer(cap, overflow)
            // See the comment on `readLines`'s wrapper for why this is hand-rolled instead of an async
            // `finally` (FS0750): flush unconditionally, then rethrow the original fault (if any).
            let mutable fault: exn option = None

            try
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
            with ex ->
                fault <- Some ex

            do! flushTeeQuietly tee cancellationToken

            match fault with
            | Some ex -> ExceptionDispatchInfo.Throw ex
            | None -> ()

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
    let closeSpawned (spawned: Native.Common.Spawned) =
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

    /// Write a stdin source to the child's stdin stream; close it (EOF) afterwards unless the
    /// caller is keeping it open for interactive writing. Never faults: returns a genuine
    /// source failure — a source-acquisition failure (per `genuineStdinFault`) on the write side, or
    /// (per `StdinSourceFault`, unconditionally) any fault reading/iterating the source itself — for
    /// the caller to surface, or `None`.
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
                    // `File.OpenRead` through `sourceStep`, so ANY open failure (not just the three
                    // types on `genuineStdinFault`'s allow-list) surfaces as a genuine source fault
                    // instead of being mistaken for a benign broken pipe.
                    use file = sourceStep (fun () -> File.OpenRead path)
                    do! pumpStream file stdinStream cancellationToken
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
                            let bytes = Encoding.UTF8.GetBytes(current + "\n")
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
                            let bytes = Encoding.UTF8.GetBytes(current + "\n")
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
                disposeQuietly stdinStream

            return fault
        }

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
    /// feed yields `None` and never blocks — preserving the "surface `ProcessError.Stdin` only on an
    /// otherwise-successful run" contract (the caller deliberately never awaits the feed, so a
    /// source fault that loses the race with the child's own exit is not surfaced).
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

    /// Feed a stdin `source` (if any) into the child's `stdin` (if piped) in the background, then EOF.
    /// A source is the child's complete input, so stdin is closed after. Returns a `StdinFeeder` — the
    /// feed task plus its lifecycle token — so the run can observe a genuine source failure once the
    /// feed has finished AND stop the feed at teardown. When there is nothing to feed the feeder is an
    /// inert no-op (no token, `Stop` does nothing, `Fault` is always `None`). (The no-source
    /// interactive case keeps the stream for `TakeStdin`.)
    let feedStdinSource (stdin: Stream option) (source: Stdin option) : StdinFeeder =
        match stdin, source with
        | Some stdinStream, Some src ->
            // The lifecycle token: cancelling it stops the feed (unblocking a parked source read),
            // disposed by the feeder once the feed has finished.
            let cts = new CancellationTokenSource()
            let feed = feedStdin src.Source stdinStream true cts.Token
            StdinFeeder(feed, Some cts)
        | _ -> StdinFeeder(Task.FromResult(None: exn option), None)
