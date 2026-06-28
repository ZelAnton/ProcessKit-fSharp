namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
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
        (Pump.readLines stream encoding None (fun l -> lines.Add l) None CancellationToken.None).Wait()
        List.ofSeq lines

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

        (Pump.readLines stream Encoding.UTF8 None (fun l -> segments.Add l) (Some 3) CancellationToken.None).Wait()

        CollectionAssert.AreEqual([ "aaa"; "bbb"; "c" ], segments)

    [<Test>]
    member _.``a newline-free flood still trips the byte-cap fail-loud ceiling via the in-flight cap``() =
        // The force-flushed segments flow through the buffer policy, so a fail-loud byte ceiling still
        // errors on a newline-free flood (and the in-flight buffer stays bounded to the cap).
        let buf =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(3).WithOverflow OverflowMode.Error)

        use stream = new MemoryStream(Encoding.UTF8.GetBytes "aaaaaaaaaa")
        (Pump.readLines stream Encoding.UTF8 None buf.Add (Some 3) CancellationToken.None).Wait()
        Assert.That(buf.TooLarge, Is.True)
