namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// A stdin `Stream` whose every write throws a broken-pipe `IOException` (the child closed its stdin
/// early). The T-069 feeder must treat this as benign — never a stdin error — and it must not mask a
/// simultaneous source fault. Private to this file; the source/enumerator doubles come from
/// `PipelineTests.fs`.
type private BrokenPipeStdinStream() =
    inherit Stream()

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Write(_buffer, _offset, _count) = raise (IOException "broken-pipe")

    override _.WriteAsync(_buffer: ReadOnlyMemory<byte>, _cancellationToken: CancellationToken) : ValueTask =
        raise (IOException "broken-pipe")

    override _.FlushAsync(_cancellationToken: CancellationToken) : Task = raise (IOException "broken-pipe")

/// A `FromStream` source whose reads throw — a source that faults mid-read. The T-069 feeder must
/// surface it as a genuine source fault, not a benign broken pipe.
type private FaultyReadStdinStream() =
    inherit Stream()

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()

    override _.Read(_buffer, _offset, _count) : int =
        raise (InvalidOperationException "read-source-boom")

    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())

    override _.ReadAsync(_buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        raise (InvalidOperationException "read-source-boom")

/// A stdout/stderr double whose read yields `chunks` (if any), in order, then throws `fault` on the
/// next read — the deterministic way to drive `Pump.readLinesUntilDone`'s genuine-read-fault-vs-
/// teardown-race classification (T-087) without a live child pipe.
type private ErroringReadStream(chunks: byte[] list, fault: exn) =
    inherit Stream()
    let mutable remaining = chunks

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        match remaining with
        | chunk :: rest ->
            remaining <- rest
            chunk.AsSpan().CopyTo(buffer.Span)
            ValueTask<int>(chunk.Length)
        | [] -> raise fault

/// Direct tests of the internal output pump: decoding (BOM, multibyte boundaries, line endings)
/// and the `OutputBufferPolicy` retention logic. These drive `Pump.readLines` / `Pump.LineBuffer`
/// over in-memory streams, so they are deterministic and need no subprocess.
[<TestFixture>]
type PumpTests() =

    // U+FEFF, constructed (not a source literal) so the test file itself carries no BOM.
    static let bom = string (char 0xFEFF)

    let collectWith (terminator: LineTerminator) (bytes: byte[]) (encoding: Encoding) : string list =
        use stream = new MemoryStream(bytes)
        let lines = ResizeArray<string>()

        (Pump.readLines
            stream
            encoding
            terminator
            None
            (fun l ->
                lines.Add l
                ValueTask.CompletedTask)
            None
            CancellationToken.None)
            .Wait()

        List.ofSeq lines

    // The default (`Lf`) framing — split on `\n`, stripping a preceding `\r` — used by the existing
    // decoding/BOM tests below (which predate the configurable terminator).
    let collect (bytes: byte[]) (encoding: Encoding) : string list =
        collectWith LineTerminator.Lf bytes encoding

    // Feed a payload to a RawBuffer in a fixed chunk size so the ring/head logic is exercised across
    // chunk seams (a single 8192-byte read never straddles the accumulator's chunk boundaries otherwise).
    let feedRaw (buf: Pump.RawBuffer) (bytes: byte[]) (chunk: int) =
        let mutable i = 0

        while i < bytes.Length do
            let n = min chunk (bytes.Length - i)
            buf.Append(bytes, i, n)
            i <- i + n

    let bytesOf (s: string) = Encoding.UTF8.GetBytes s

    // Run `source` to completion against `stdin` via the real `feedStdinSource` entry point (its own
    // lifecycle CTS, close-when-done) and return the genuine fault it stashed, or `None`. (T-069)
    let feederFault (source: Stdin) (stdin: Stream) : exn option =
        let feeder = Pump.feedStdinSource (Some stdin) (Some source) false
        feeder.Task.Result

    [<Test>]
    member _.``readLines strips a leading UTF-8 BOM``() =
        let bytes =
            Array.append (Encoding.UTF8.GetPreamble()) (Encoding.UTF8.GetBytes "hello\nworld")

        CollectionAssert.AreEqual([ "hello"; "world" ], collect bytes Encoding.UTF8)

    [<Test>]
    member _.``readLines keeps a non-leading U+FEFF``() =
        let payload = "a" + bom + "b"
        CollectionAssert.AreEqual([ payload ], collect (Encoding.UTF8.GetBytes payload) Encoding.UTF8)

    [<Test>]
    member _.``readLines decodes a multibyte char split across the read boundary``() =
        // 8191 ASCII bytes + a 2-byte 'é' straddles the 8192-byte read boundary, exercising the
        // stateful decoder across two reads.
        let prefix = String('a', 8191)
        let bytes = Encoding.UTF8.GetBytes(prefix + "é\n")
        CollectionAssert.AreEqual([ prefix + "é" ], collect bytes Encoding.UTF8)

    [<Test>]
    member _.``readLines replaces a truncated UTF-8 sequence at EOF in every terminator mode``() =
        let bytes = [| byte 'a'; 0xC3uy |]

        for terminator in
            [ LineTerminator.Lf
              LineTerminator.Cr
              LineTerminator.CrLf
              LineTerminator.Any ] do
            CollectionAssert.AreEqual([ "a\uFFFD" ], collectWith terminator bytes Encoding.UTF8, string terminator)

    [<Test>]
    member _.``readLines replaces a truncated UTF-16 surrogate at EOF``() =
        let bytes = [| byte 'a'; 0uy; 0uy; 0xD8uy |]
        CollectionAssert.AreEqual([ "a\uFFFD" ], collect bytes Encoding.Unicode)

    [<Test>]
    member _.``readLines applies the line cap to replacement characters flushed at EOF``() =
        use stream = new MemoryStream([| byte 'a'; byte 'b'; 0xC3uy |])
        let lines = ResizeArray<string>()

        (Pump.readLines
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun line ->
                lines.Add line
                ValueTask.CompletedTask)
            (Some 2)
            CancellationToken.None)
            .Wait()

        CollectionAssert.AreEqual([ "ab"; "\uFFFD" ], lines)

    [<Test>]
    member _.``readLines resolves a pending CR before flushing a truncated sequence``() =
        let bytes = [| byte 'a'; byte '\r'; 0xC3uy |]

        for terminator, expected in
            [ LineTerminator.Cr, [ "a"; "\uFFFD" ]
              LineTerminator.CrLf, [ "a\r\uFFFD" ]
              LineTerminator.Any, [ "a"; "\uFFFD" ] ] do
            CollectionAssert.AreEqual(expected, collectWith terminator bytes Encoding.UTF8, string terminator)

    [<Test>]
    member _.``readLinesUntilDone propagates decoder exception fallback raised at EOF``() =
        use stream = new MemoryStream([| 0xC3uy |])
        let encoding = UTF8Encoding(false, true)

        let action =
            Func<Task>(fun () ->
                Pump.readLinesUntilDone
                    stream
                    encoding
                    LineTerminator.Lf
                    None
                    (fun _ -> ValueTask.CompletedTask)
                    None
                    (fun () -> false)
                    CancellationToken.None)

        Assert.ThrowsAsync<DecoderFallbackException>(action) |> ignore

    // --- T-087: readLinesUntilDone's genuine-read-fault vs teardown-race classification ---

    [<Test>]
    member _.``readLinesUntilDone surfaces a genuine mid-stream IOException when teardown has not begun``() =
        use stream =
            new ErroringReadStream([ Encoding.UTF8.GetBytes "partial" ], IOException "disk read error")

        let lines = ResizeArray<string>()

        let action =
            Func<Task>(fun () ->
                Pump.readLinesUntilDone
                    stream
                    Encoding.UTF8
                    LineTerminator.Lf
                    None
                    (fun l ->
                        lines.Add l
                        ValueTask.CompletedTask)
                    None
                    (fun () -> false)
                    CancellationToken.None)

        Assert.ThrowsAsync<IOException>(action) |> ignore

    [<Test>]
    member _.``readLinesUntilDone surfaces a genuine ObjectDisposedException when teardown has not begun``() =
        use stream = new ErroringReadStream([], ObjectDisposedException "stream")

        let action =
            Func<Task>(fun () ->
                Pump.readLinesUntilDone
                    stream
                    Encoding.UTF8
                    LineTerminator.Lf
                    None
                    (fun _ -> ValueTask.CompletedTask)
                    None
                    (fun () -> false)
                    CancellationToken.None)

        Assert.ThrowsAsync<ObjectDisposedException>(action) |> ignore

    [<Test>]
    member _.``readLinesUntilDone swallows an IOException once teardown has begun (no false positive)``() =
        // Non-regression: the routine dispose/broken-pipe race this library's own teardown triggers by
        // design must still be swallowed quietly, not misclassified as a genuine fault. The task
        // completes without throwing (the still-unterminated in-flight line is never flushed, since the
        // fault interrupts the read loop before the clean-EOF path that would flush it — same as any
        // other mid-line teardown race).
        use stream =
            new ErroringReadStream([ Encoding.UTF8.GetBytes "partial" ], IOException "pipe closed")

        let lines = ResizeArray<string>()

        (Pump.readLinesUntilDone
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun l ->
                lines.Add l
                ValueTask.CompletedTask)
            None
            (fun () -> true)
            CancellationToken.None)
            .Wait()

        CollectionAssert.AreEqual([], lines)

    [<Test>]
    member _.``readLinesUntilDone swallows an ObjectDisposedException once teardown has begun (no false positive)``() =
        use stream = new ErroringReadStream([], ObjectDisposedException "stream")

        (Pump.readLinesUntilDone
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun _ -> ValueTask.CompletedTask)
            None
            (fun () -> true)
            CancellationToken.None)
            .Wait()

    [<Test>]
    member _.``readLines strips CRLF and LF``() =
        CollectionAssert.AreEqual([ "a"; "b"; "c" ], collect (Encoding.UTF8.GetBytes "a\r\nb\nc") Encoding.UTF8)

    [<Test>]
    member _.``readLines strips a trailing CR that lands exactly at the 8192-byte read boundary``() =
        // The '\r' is the very last byte of the first 8192-byte read; the '\n' that completes the CRLF
        // arrives as the first byte of the next read. The carry (an unterminated line ending in '\r')
        // must survive across reads so the pair still collapses to a single line break.
        let prefix = String('a', 8191)
        let bytes = Encoding.UTF8.GetBytes(prefix + "\r\nworld")
        CollectionAssert.AreEqual([ prefix; "world" ], collect bytes Encoding.UTF8)

    [<Test>]
    member _.``readLines strips a lone trailing CR at EOF``() =
        // No following '\n' ever arrives — the final unterminated line still drops its trailing '\r',
        // same as the mid-stream CRLF case.
        CollectionAssert.AreEqual([ "hello" ], collect (Encoding.UTF8.GetBytes "hello\r") Encoding.UTF8)

    // --- LineTerminator: '\r'-aware framing (Cr / CrLf / Any) ---

    [<Test>]
    member _.``Lf mode keeps a bare CR as content (carriage-return progress accumulates)``() =
        // The default: a '\r' not before '\n' is content, so a redraw-in-place progress line stays one
        // ever-growing line until the final '\n'.
        let bytes = Encoding.UTF8.GetBytes "50%\r100%\n"
        CollectionAssert.AreEqual([ "50%\r100%" ], collectWith LineTerminator.Lf bytes Encoding.UTF8)

    [<Test>]
    member _.``Cr mode splits carriage-return progress into per-frame lines``() =
        // Each '\r' frame becomes its own line; the last, unterminated frame is the final line.
        let bytes = Encoding.UTF8.GetBytes "10%\r55%\r100%"
        CollectionAssert.AreEqual([ "10%"; "55%"; "100%" ], collectWith LineTerminator.Cr bytes Encoding.UTF8)

    [<Test>]
    member _.``Cr mode treats a lone LF as content and CRLF as one terminator``() =
        // '\n' alone is content under Cr; a '\r\n' pair is a single terminator (no spurious empty line).
        let bytes = Encoding.UTF8.GetBytes "a\nb\r\nc"
        CollectionAssert.AreEqual([ "a\nb"; "c" ], collectWith LineTerminator.Cr bytes Encoding.UTF8)

    [<Test>]
    member _.``Cr mode emits the frame before a trailing bare CR``() =
        let bytes = Encoding.UTF8.GetBytes "done\r"
        CollectionAssert.AreEqual([ "done" ], collectWith LineTerminator.Cr bytes Encoding.UTF8)

    [<Test>]
    member _.``Any mode splits on LF, CR, and CRLF alike``() =
        // A lone '\n', a lone '\r', and a '\r\n' pair are each a single terminator.
        let bytes = Encoding.UTF8.GetBytes "a\rb\nc\r\nd"
        CollectionAssert.AreEqual([ "a"; "b"; "c"; "d" ], collectWith LineTerminator.Any bytes Encoding.UTF8)

    [<Test>]
    member _.``CrLf mode splits only on CRLF, keeping lone CR and lone LF as content``() =
        let bytes = Encoding.UTF8.GetBytes "a\rb\nc\r\nd"
        CollectionAssert.AreEqual([ "a\rb\nc"; "d" ], collectWith LineTerminator.CrLf bytes Encoding.UTF8)

    [<Test>]
    member _.``Any mode collapses a CRLF split across the 8192-byte read boundary``() =
        // The '\r' is the last byte of the first read; the '\n' is the first byte of the next. The
        // deferred-CR carry must survive across reads so the pair stays a single terminator (no empty
        // line between the two frames), exactly as the default Lf path handles it.
        let prefix = String('a', 8191)
        let bytes = Encoding.UTF8.GetBytes(prefix + "\r\nworld")
        CollectionAssert.AreEqual([ prefix; "world" ], collectWith LineTerminator.Any bytes Encoding.UTF8)

    [<Test>]
    member _.``Cr mode force-flushes an over-long frame at the byte cap``() =
        // The '\r'-aware path honours `maxLineLength` too: a newline-free (here, CR-free) frame is
        // flushed in <=cap segments, so a runaway frame can't outgrow the in-flight buffer.
        use stream = new MemoryStream(Encoding.UTF8.GetBytes "aaabbbc\rtail")
        let segments = ResizeArray<string>()

        (Pump.readLines
            stream
            Encoding.UTF8
            LineTerminator.Cr
            None
            (fun l ->
                segments.Add l
                ValueTask.CompletedTask)
            (Some 3)
            CancellationToken.None)
            .Wait()

        CollectionAssert.AreEqual([ "aaa"; "bbb"; "c"; "tai"; "l" ], segments)

    [<Test>]
    member _.``LineTerminatorRules map each mode to its lone-LF / lone-CR split rules``() =
        // Lf splits lone '\n'; Cr splits lone '\r'; Any splits both; CrLf splits neither ('\r\n' only).
        Assert.That(LineTerminatorRules.splitsOnLf LineTerminator.Lf, Is.True)
        Assert.That(LineTerminatorRules.splitsOnCr LineTerminator.Lf, Is.False)
        Assert.That(LineTerminatorRules.splitsOnLf LineTerminator.Cr, Is.False)
        Assert.That(LineTerminatorRules.splitsOnCr LineTerminator.Cr, Is.True)
        Assert.That(LineTerminatorRules.splitsOnLf LineTerminator.CrLf, Is.False)
        Assert.That(LineTerminatorRules.splitsOnCr LineTerminator.CrLf, Is.False)
        Assert.That(LineTerminatorRules.splitsOnLf LineTerminator.Any, Is.True)
        Assert.That(LineTerminatorRules.splitsOnCr LineTerminator.Any, Is.True)

    [<Test>]
    member _.``LineBuffer DropOldest keeps the most recent lines``() =
        let buf = Pump.LineBuffer(OutputBufferPolicy.Bounded 2)
        [ "a"; "b"; "c" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "b\nc")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 3)

    [<Test>]
    member _.``LineBuffer DropNewest keeps the earliest lines``() =
        let buf =
            Pump.LineBuffer((OutputBufferPolicy.Bounded 2).WithOverflow OverflowMode.DropNewest)

        [ "a"; "b"; "c" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "a\nb")
        Assert.That(buf.Truncated, Is.True)

    [<Test>]
    member _.``LineBuffer DropNewest keeps a contiguous prefix after a byte-cap rejection``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(10).WithOverflow OverflowMode.DropNewest)

        [ "aaaa"; String('b', 11); "cc" ] |> List.iter buf.Add

        Assert.That(buf.Text, Is.EqualTo "aaaa")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 3)
        Assert.That(buf.TotalBytes, Is.EqualTo 20)

    [<Test>]
    member _.``LineBuffer Error with no limits retains all lines``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithOverflow OverflowMode.Error)

        [ "a"; "b"; "c"; "d" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "a\nb\nc\nd")
        Assert.That(buf.TooLarge, Is.False)
        Assert.That(buf.TotalLines, Is.EqualTo 4)

    [<Test>]
    member _.``LineBuffer Error with MaxLines 3 errors after line 3``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxLines(3).WithOverflow OverflowMode.Error)

        [ "a"; "b"; "c"; "d" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "a\nb\nc")
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 4)

    [<Test>]
    member _.``LineBuffer Error with MaxBytes 5 errors after byte 5``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(5).WithOverflow OverflowMode.Error)

        [ "ab"; "c"; "d" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "ab\nc")
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.TotalBytes, Is.EqualTo 7)

    [<Test>]
    member _.``LineBuffer Error with MaxLines 0 errors from the first line``() =
        // R-1: the contract's "strictly after exceeding" must hold at the zero-limit boundary too — a
        // `MaxLines = Some 0` ceiling is exceeded by the very first retained line.
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxLines(0).WithOverflow OverflowMode.Error)

        [ "a"; "b" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "")
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 2)

    [<Test>]
    member _.``LineBuffer Error with MaxBytes 0 errors from the first line``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(0).WithOverflow OverflowMode.Error)

        [ "a"; "b" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "")
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.TotalBytes, Is.EqualTo 4)

    [<Test>]
    member _.``LineBuffer Error with MaxLines 2 and MaxBytes 10 errors at the first limit``() =
        let buf =
            Pump.LineBuffer(
                OutputBufferPolicy.Unbounded.WithMaxLines(2).WithMaxBytes(10).WithOverflow OverflowMode.Error
            )

        [ "abc"; "def"; "g" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "abc\ndef")
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 3)
        Assert.That(buf.TotalBytes, Is.EqualTo 10)

    [<Test>]
    member _.``LineBuffer byte cap evicts oldest to fit``() =
        let buf = Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes 3)
        [ "aa"; "bb" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "bb")
        Assert.That(buf.Truncated, Is.True)

    [<Test>]
    member _.``LineBuffer byte accounting charges the separator Text reintroduces, so a cap bounds the reassembled size``
        ()
        =
        // Two 1-byte lines "a"/"b" sum to 2 content bytes, exactly the cap below — the pre-fix
        // accounting (each line's own UTF-8 byte count only) would have retained both, reassembling
        // via Text to "a\nb" (3 bytes), which *exceeds* the configured cap of 2. Charging a separator
        // byte per retained line closes that gap: the second line now reads as over-cap, gets evicted,
        // and the reassembled size never exceeds the cap.
        let buf = Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes 2)
        [ "a"; "b" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "b")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(Encoding.UTF8.GetByteCount buf.Text, Is.LessThanOrEqualTo 2)

    [<Test>]
    member _.``LineBuffer under a byte cap alone bounds an empty-line flood with DropOldest``() =
        // A bare-newline flood produces an unbounded number of empty-string lines. Under the pre-fix
        // accounting an empty line cost 0 bytes, so retention was unbounded even with MaxBytes set and
        // no MaxLines. The corrected accounting charges a separator byte per line, so retention stays
        // bounded to (roughly) the cap regardless of how many empty lines arrive.
        let buf = Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes 10)

        for _ in 1..100_000 do
            buf.Add ""

        Assert.That(buf.TotalLines, Is.EqualTo 100_000)
        Assert.That(buf.Truncated, Is.True)
        Assert.That(Encoding.UTF8.GetByteCount buf.Text, Is.LessThanOrEqualTo 10)

    [<Test>]
    member _.``LineBuffer under a byte cap alone bounds an empty-line flood with DropNewest``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(10).WithOverflow OverflowMode.DropNewest)

        for _ in 1..100_000 do
            buf.Add ""

        Assert.That(buf.TotalLines, Is.EqualTo 100_000)
        Assert.That(buf.Truncated, Is.True)
        Assert.That(Encoding.UTF8.GetByteCount buf.Text, Is.LessThanOrEqualTo 10)

    [<Test>]
    member _.``LineBuffer DropNewest byte cap keeps a contiguous prefix after rejecting a long line``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(10).WithOverflow OverflowMode.DropNewest)

        [ "aaaa"; String('b', 11); "cc" ] |> List.iter buf.Add

        Assert.That(buf.Text, Is.EqualTo "aaaa")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(buf.TotalLines, Is.EqualTo 3)
        Assert.That(buf.TotalBytes, Is.EqualTo 20)

    [<Test>]
    member _.``LineBuffer under a byte cap alone bounds an empty-line flood with Error``() =
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(10).WithOverflow OverflowMode.Error)

        for _ in 1..100_000 do
            buf.Add ""

        Assert.That(buf.TotalLines, Is.EqualTo 100_000)
        Assert.That(buf.TooLarge, Is.True)
        // Error retains nothing once tripped — the fail-loud ceiling counts but never retains.
        Assert.That(Encoding.UTF8.GetByteCount buf.Text, Is.LessThanOrEqualTo 10)

    [<Test>]
    member _.``LineBuffer with MaxBytes = 0 retains nothing but still flags every overflow mode sanely``() =
        for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ] do
            let buf =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(0).WithOverflow overflow)

            for _ in 1..1000 do
                buf.Add ""

            Assert.That(buf.Text, Is.EqualTo "", $"{overflow} text")
            Assert.That(buf.TotalLines, Is.EqualTo 1000, $"{overflow} total lines")

            if overflow = OverflowMode.Error then
                Assert.That(buf.TooLarge, Is.True, $"{overflow} tooLarge")
            else
                Assert.That(buf.Truncated, Is.True, $"{overflow} truncated")

    [<Test>]
    member _.``readLines force-flushes an over-long unterminated line at the cap``() =
        // A 7-char newline-free blob with a cap of 3 is flushed as <=3-char segments, so the in-flight
        // buffer never grows past the cap — a newline-free flood can't outgrow it.
        use stream = new MemoryStream(Encoding.UTF8.GetBytes "aaabbbc")
        let segments = ResizeArray<string>()

        (Pump.readLines
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun l ->
                segments.Add l
                ValueTask.CompletedTask)
            (Some 3)
            CancellationToken.None)
            .Wait()

        CollectionAssert.AreEqual([ "aaa"; "bbb"; "c" ], segments)

    [<Test>]
    member _.``readLines force-flush segments stay aligned when the cap boundary coincides with the 8192-char read boundary``
        ()
        =
        // 8200 newline-free 'x' chars with a cap of 4: 8192 / 4 is exact, so the force-flush right at
        // the read boundary lands exactly on a cap boundary too — the in-flight segment must carry over
        // to the next read (unflushed) rather than emitting a short/misaligned segment at the seam.
        let payload = String('x', 8200)
        use stream = new MemoryStream(Encoding.UTF8.GetBytes payload)
        let segments = ResizeArray<string>()

        (Pump.readLines
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun l ->
                segments.Add l
                ValueTask.CompletedTask)
            (Some 4)
            CancellationToken.None)
            .Wait()

        Assert.That(segments.Count, Is.EqualTo 2050)
        Assert.That(String.concat "" segments, Is.EqualTo payload)

        for segment in segments do
            Assert.That(segment, Is.EqualTo "xxxx")

    [<Test>]
    member _.``a newline-free flood still trips the byte-cap fail-loud ceiling via the in-flight cap``() =
        // The force-flushed segments flow through the buffer policy, so a fail-loud byte ceiling still
        // errors on a newline-free flood (and the in-flight buffer stays bounded to the cap).
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(3).WithOverflow OverflowMode.Error)

        use stream = new MemoryStream(Encoding.UTF8.GetBytes "aaaaaaaaaa")

        (Pump.readLines
            stream
            Encoding.UTF8
            LineTerminator.Lf
            None
            (fun l ->
                buf.Add l
                ValueTask.CompletedTask)
            (Some 3)
            CancellationToken.None)
            .Wait()

        Assert.That(buf.TooLarge, Is.True)

    // --- RawBuffer: the byte-cap accumulator for the raw stdout capture (OutputBytesAsync / pipeline) ---

    [<Test>]
    member _.``RawBuffer DropOldest keeps the last cap bytes as a tail``() =
        let buf = Pump.RawBuffer(3, OverflowMode.DropOldest)
        feedRaw buf (bytesOf "abcdefg") 2
        Assert.That(Encoding.UTF8.GetString(buf.ToArray()), Is.EqualTo "efg")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(buf.TooLarge, Is.False)
        Assert.That(buf.TotalBytes, Is.EqualTo 7)

    [<Test>]
    member _.``RawBuffer DropNewest keeps the first cap bytes as a head``() =
        let buf = Pump.RawBuffer(3, OverflowMode.DropNewest)
        feedRaw buf (bytesOf "abcdefg") 2
        Assert.That(Encoding.UTF8.GetString(buf.ToArray()), Is.EqualTo "abc")
        Assert.That(buf.Truncated, Is.True)
        Assert.That(buf.TooLarge, Is.False)

    [<Test>]
    member _.``RawBuffer Error trips the fail-loud ceiling once the cap is exceeded``() =
        let buf = Pump.RawBuffer(3, OverflowMode.Error)
        feedRaw buf (bytesOf "abcd") 1
        Assert.That(buf.TooLarge, Is.True)
        Assert.That(buf.Truncated, Is.False)
        Assert.That(buf.TotalBytes, Is.EqualTo 4)

    [<Test>]
    member _.``RawBuffer exactly at the cap neither truncates nor errors``() =
        // Total == cap is the boundary: nothing is dropped and the fail-loud ceiling is not crossed.
        for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ] do
            let buf = Pump.RawBuffer(4, overflow)
            feedRaw buf (bytesOf "abcd") 3
            Assert.That(Encoding.UTF8.GetString(buf.ToArray()), Is.EqualTo "abcd", $"{overflow} content")
            Assert.That(buf.Truncated, Is.False, $"{overflow} truncated")
            Assert.That(buf.TooLarge, Is.False, $"{overflow} tooLarge")

    [<Test>]
    member _.``RawBuffer with a zero cap retains nothing but flags any output``() =
        let dropOldest = Pump.RawBuffer(0, OverflowMode.DropOldest)
        feedRaw dropOldest (bytesOf "abc") 1
        Assert.That(dropOldest.ToArray(), Is.Empty)
        Assert.That(dropOldest.Truncated, Is.True)

        let error = Pump.RawBuffer(0, OverflowMode.Error)
        feedRaw error (bytesOf "abc") 1
        Assert.That(error.ToArray(), Is.Empty)
        Assert.That(error.TooLarge, Is.True)

    [<Test>]
    member _.``RawBuffer on empty input retains nothing and flags nothing``() =
        for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ] do
            let buf = Pump.RawBuffer(4, overflow)
            Assert.That(buf.ToArray(), Is.Empty, $"{overflow} content")
            Assert.That(buf.Truncated, Is.False, $"{overflow} truncated")
            Assert.That(buf.TooLarge, Is.False, $"{overflow} tooLarge")
            Assert.That(buf.TotalBytes, Is.EqualTo 0, $"{overflow} total")

    [<Test>]
    member _.``RawBuffer DropOldest tail survives many small evicting chunks``() =
        // Feed one byte at a time so eviction runs on nearly every Append — the ring must still hold
        // exactly the last cap bytes regardless of chunking.
        let buf = Pump.RawBuffer(5, OverflowMode.DropOldest)
        feedRaw buf (bytesOf "0123456789") 1
        Assert.That(Encoding.UTF8.GetString(buf.ToArray()), Is.EqualTo "56789")
        Assert.That(buf.TotalBytes, Is.EqualTo 10)

    [<Test>]
    member _.``captureRawOrEmpty with no byte cap is unbounded and never truncates``() =
        // MaxBytes = None keeps the raw capture unbounded (unchanged behaviour): all bytes, no flags.
        use stream = new MemoryStream(bytesOf "unbounded payload")

        let capture =
            (Pump.captureRawOrEmpty (Some(stream :> Stream)) None OutputBufferPolicy.Unbounded CancellationToken.None)
                .Result

        Assert.That(Encoding.UTF8.GetString capture.Bytes, Is.EqualTo "unbounded payload")
        Assert.That(capture.Truncated, Is.False)
        Assert.That(capture.TooLarge, Is.False)

    [<Test>]
    member _.``captureRawOrEmpty applies the byte cap and tees the full stream``() =
        // The tee mirrors the full child output; retention obeys the DropOldest cap independently.
        use stream = new MemoryStream(bytesOf "abcdefgh")
        use tee = new MemoryStream()
        let policy = OutputBufferPolicy.Unbounded.WithMaxBytes 3

        let capture =
            (Pump.captureRawOrEmpty (Some(stream :> Stream)) (Some(tee :> Stream)) policy CancellationToken.None).Result

        Assert.That(Encoding.UTF8.GetString capture.Bytes, Is.EqualTo "fgh") // last 3 bytes (DropOldest)
        Assert.That(capture.Truncated, Is.True)
        Assert.That(Encoding.UTF8.GetString(tee.ToArray()), Is.EqualTo "abcdefgh") // tee is byte-exact

    [<Test>]
    member _.``captureRawOrEmpty over a missing stream yields an empty capture``() =
        let capture =
            (Pump.captureRawOrEmpty None None (OutputBufferPolicy.Unbounded.WithMaxBytes 4) CancellationToken.None)
                .Result

        Assert.That(capture.Bytes, Is.Empty)
        Assert.That(capture.Truncated, Is.False)
        Assert.That(capture.TooLarge, Is.False)

    [<Test>]
    member _.``OutputBufferPolicy rejects a negative cap but accepts zero``() =
        // `Some 0` retains nothing but is still valid (documented on `MaxLines`) — only negative caps
        // are rejected as meaningless.
        OutputBufferPolicy.Bounded 0 |> ignore
        OutputBufferPolicy.FailLoud 0 |> ignore
        OutputBufferPolicy.Unbounded.WithMaxLines 0 |> ignore
        OutputBufferPolicy.Unbounded.WithMaxBytes 0 |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> OutputBufferPolicy.Bounded -1 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> OutputBufferPolicy.FailLoud -1 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> OutputBufferPolicy.Unbounded.WithMaxLines -1 |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> OutputBufferPolicy.Unbounded.WithMaxBytes -1 |> ignore)
        )
        |> ignore

    // --- T-069: the stdin feeder (`Pump.feedStdin` / `feedStdinSource` / `StdinFeeder`). Deterministic,
    // over in-memory streams and the `FaultyStdin*` / `HangingStdinAsyncLines` doubles from PipelineTests. ---

    [<Test>]
    member _.``feedStdin surfaces a File source whose open fails with a non-allow-listed error``() =
        // An embedded NUL makes `File.OpenRead` throw `ArgumentException` — NOT one of the three types
        // on the old benign allow-list. The pre-fix code lost it; it must now surface as a source fault.
        use stdin = new MemoryStream()

        match feederFault (Stdin.FromFile("bad" + string (char 0) + "path")) stdin with
        | Some _ -> ()
        | None -> Assert.Fail "a failed File.OpenRead must surface as a genuine stdin-source fault"

    [<Test>]
    member _.``feedStdin surfaces a FromStream source whose read faults``() =
        use stdin = new MemoryStream()

        match feederFault (Stdin.FromStream(new FaultyReadStdinStream())) stdin with
        | Some ex -> Assert.That(ex.Message, Does.Contain "read-source-boom")
        | None -> Assert.Fail "a source-stream read fault must surface as a genuine stdin-source fault"

    [<Test>]
    member _.``feedStdin keeps a broken-pipe write benign``() =
        // The child closed stdin early: every write throws a broken-pipe IOException, which is routine
        // and must NOT surface as a stdin error.
        match feederFault (Stdin.FromString "payload") (new BrokenPipeStdinStream()) with
        | None -> ()
        | Some ex -> Assert.Fail $"a broken-pipe write must stay benign, got {ex.Message}"

    [<Test>]
    member _.``feedStdin does not let a broken-pipe write mask a source fault``() =
        // The source faults acquiring its enumerator AND the stdin stream is a broken pipe. The source
        // fault must still win — it is raised before any write is attempted.
        match feederFault (Stdin.FromLines(FaultyStdinLines AtGetEnumerator)) (new BrokenPipeStdinStream()) with
        | Some _ -> ()
        | None -> Assert.Fail "a source fault must not be masked by a co-occurring broken-pipe write"

    [<Test>]
    member _.``feedStdin surfaces a fault at every sync enumeration stage``() =
        for stage in [ AtGetEnumerator; AtMoveNext; AtCurrent ] do
            use stdin = new MemoryStream()

            match feederFault (Stdin.FromLines(FaultyStdinLines stage)) stdin with
            | Some _ -> ()
            | None -> Assert.Fail $"a sync fault at {stage} must surface as a genuine stdin-source fault"

    [<Test>]
    member _.``feedStdin surfaces a fault at every async enumeration stage``() =
        for stage in [ AtGetEnumerator; AtMoveNext; AtCurrent ] do
            use stdin = new MemoryStream()

            match feederFault (Stdin.FromAsyncLines(FaultyStdinAsyncLines stage)) stdin with
            | Some _ -> ()
            | None -> Assert.Fail $"an async fault at {stage} must surface as a genuine stdin-source fault"

    [<Test>]
    member _.``StdinFeeder Stop cancels a hung async feed and disposes the enumerator``() : Task =
        task {
            let source = HangingStdinAsyncLines()
            use stdin = new MemoryStream()

            let feeder =
                Pump.feedStdinSource (Some(stdin :> Stream)) (Some(Stdin.FromAsyncLines source)) false

            // Wait until the feed is genuinely parked in `MoveNextAsync`.
            let! started = Task.WhenAny(source.Started, Task.Delay 5000)
            Assert.That(started, Is.SameAs source.Started, "the feed never started")

            feeder.Stop()

            let! fault = feeder.Task

            match fault with
            | None -> ()
            | Some ex -> Assert.Fail $"a cancelled feed must report no fault, got {ex.Message}"

            Assert.That(Option.isNone feeder.Fault, Is.True)

            let! disposed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(disposed, Is.SameAs source.Disposed, "the hung async enumerator was never disposed")
        }
        :> Task

    [<Test>]
    member _.``StdinFeeder Stop is idempotent and the nothing-to-feed feeder is inert``() : Task =
        task {
            // Nothing to feed (no child stdin pipe): no token, `Stop` is a no-op, `Fault` is always None.
            let noop = Pump.feedStdinSource None (Some(Stdin.FromString "ignored")) false
            noop.Stop()
            noop.Stop()
            let! noopFault = noop.Task
            Assert.That(Option.isNone noopFault, Is.True)
            Assert.That(Option.isNone noop.Fault, Is.True)

            // A real feed: repeated `Stop`, including after the feed has finished, stays a safe no-op.
            let source = HangingStdinAsyncLines()
            use stdin = new MemoryStream()

            let feeder =
                Pump.feedStdinSource (Some(stdin :> Stream)) (Some(Stdin.FromAsyncLines source)) false

            let! _ = source.Started
            feeder.Stop()
            let! _ = feeder.Task
            feeder.Stop()
            feeder.Stop()
            Assert.That(Option.isNone feeder.Fault, Is.True)
        }
        :> Task

    // --- T-123: `Stdin(source)` + `KeepStdinOpen` — the source feeds first, then the pipe is left open
    // for interactive writing via `RunningProcess.TakeStdin`, with the source and the writer never on the
    // pipe at once. Deterministic, over in-memory streams and the `GatedStdinAsyncLines` double. ---

    [<Test>]
    member _.``feedStdinSource with KeepStdinOpen leaves the stdin pipe open after the source is drained``() : Task =
        task {
            // `KeepStdinOpen = true`: the feed drains the source and flushes, but must NOT close (EOF) the
            // pipe — it stays open (writable) so `TakeStdin` can keep writing to it.
            let kept = new MemoryStream()

            let keptFeeder =
                Pump.feedStdinSource (Some(kept :> Stream)) (Some(Stdin.FromString "hello")) true

            let! keptFault = keptFeeder.Task
            Assert.That(Option.isNone keptFault, Is.True)
            Assert.That(kept.CanWrite, Is.True, "KeepStdinOpen must leave the stdin pipe open after the source")
            Assert.That(Encoding.UTF8.GetString(kept.ToArray()), Is.EqualTo "hello")

            // The default (`KeepStdinOpen = false`) still closes the pipe once the source is drained, so the
            // child sees EOF — the pre-existing behaviour must not regress.
            let closed = new MemoryStream()

            let closedFeeder =
                Pump.feedStdinSource (Some(closed :> Stream)) (Some(Stdin.FromString "hello")) false

            let! _ = closedFeeder.Task
            Assert.That(closed.CanWrite, Is.False, "without KeepStdinOpen the source must close (EOF) the pipe")
        }
        :> Task

    [<Test>]
    member _.``feedStdinSource for a KeepStdinOpen source completes only once the source is drained``() : Task =
        task {
            // The single-writer invariant, deterministically (no timing guesswork): while the source is
            // still feeding, the feed task is NOT complete and nothing is on the pipe — so `TakeStdin`,
            // which blocks on this task (`StdinFeedComplete`), cannot hand the pipe to a second writer
            // mid-feed. Only once the source is drained does the feed complete, leaving the pipe OPEN.
            let gated = GatedStdinAsyncLines "SRC"
            let pipe = new MemoryStream()

            let feeder =
                Pump.feedStdinSource (Some(pipe :> Stream)) (Some(Stdin.FromAsyncLines gated)) true

            // Wait (via the source's own `Parked` signal, not a delay) until the feed is genuinely parked in
            // the source, before it has written anything.
            let! parked = Task.WhenAny(gated.Parked, Task.Delay 5000)
            Assert.That(parked, Is.SameAs gated.Parked, "the feed never parked in the source")

            Assert.That(
                feeder.Task.IsCompleted,
                Is.False,
                "the feed must not complete while the source is still feeding"
            )

            Assert.That(pipe.Length, Is.EqualTo 0L, "nothing must reach the pipe while the source is parked")

            // Drain the source: the feed writes the line, flushes, and completes WITHOUT closing the pipe.
            gated.Release()
            let! fault = feeder.Task
            Assert.That(Option.isNone fault, Is.True)
            Assert.That(pipe.CanWrite, Is.True, "the kept-open pipe must stay open after the source is drained")
            Assert.That(Encoding.UTF8.GetString(pipe.ToArray()), Is.EqualTo "SRC\n")
        }
        :> Task
