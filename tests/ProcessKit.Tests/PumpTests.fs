namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// Direct tests of the internal output pump: decoding (BOM, multibyte boundaries, line endings)
/// and the `OutputBufferPolicy` retention logic. These drive `Pump.readLines` / `Pump.LineBuffer`
/// over in-memory streams, so they are deterministic and need no subprocess.
[<TestFixture>]
type PumpTests() =

    // U+FEFF, constructed (not a source literal) so the test file itself carries no BOM.
    static let bom = string (char 0xFEFF)

    let collect (bytes: byte[]) (encoding: Encoding) : string list =
        use stream = new MemoryStream(bytes)
        let lines = ResizeArray<string>()

        (Pump.readLines
            stream
            encoding
            None
            (fun l ->
                lines.Add l
                ValueTask.CompletedTask)
            None
            CancellationToken.None)
            .Wait()

        List.ofSeq lines

    // Feed a payload to a RawBuffer in a fixed chunk size so the ring/head logic is exercised across
    // chunk seams (a single 8192-byte read never straddles the accumulator's chunk boundaries otherwise).
    let feedRaw (buf: Pump.RawBuffer) (bytes: byte[]) (chunk: int) =
        let mutable i = 0

        while i < bytes.Length do
            let n = min chunk (bytes.Length - i)
            buf.Append(bytes, i, n)
            i <- i + n

    let bytesOf (s: string) = Encoding.UTF8.GetBytes s

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
    member _.``LineBuffer byte cap evicts oldest to fit``() =
        let buf = Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes 3)
        [ "aa"; "bb" ] |> List.iter buf.Add
        Assert.That(buf.Text, Is.EqualTo "bb")
        Assert.That(buf.Truncated, Is.True)

    [<Test>]
    member _.``readLines force-flushes an over-long unterminated line at the cap``() =
        // A 7-char newline-free blob with a cap of 3 is flushed as <=3-char segments, so the in-flight
        // buffer never grows past the cap — a newline-free flood can't outgrow it.
        use stream = new MemoryStream(Encoding.UTF8.GetBytes "aaabbbc")
        let segments = ResizeArray<string>()

        (Pump.readLines
            stream
            Encoding.UTF8
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
