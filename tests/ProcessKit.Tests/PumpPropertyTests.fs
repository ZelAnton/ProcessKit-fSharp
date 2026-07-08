namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open FsCheck
open FsCheck.FSharp
open ProcessKit

/// A read-only stream over a fixed byte payload that hands back at most the next entry of
/// `chunkSizes` per `Read`/`ReadAsync` call (looping back to the payload's remaining length once
/// the list is exhausted), regardless of how large the caller's own buffer is. `Pump.readLines`
/// always reads through a fixed 8192-byte buffer, so this is the seam the property tests below use
/// to drive arbitrary, test-controlled chunk boundaries — including landing squarely inside a
/// multi-character `\r\n` terminator — independent of that internal buffer size.
type private ChunkedStream(payload: byte[], chunkSizes: int list) =
    inherit Stream()

    let mutable pos = 0
    let sizes = Queue<int>(chunkSizes |> List.filter (fun n -> n > 0))

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_offset: int64, _origin: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_value: int64) = raise (NotSupportedException())
    override _.Write(_buffer: byte[], _offset: int, _count: int) = raise (NotSupportedException())

    override _.Read(buffer: byte[], offset: int, count: int) : int =
        let remaining = payload.Length - pos

        if remaining <= 0 || count <= 0 then
            0
        else
            let chunkCap = if sizes.Count > 0 then sizes.Dequeue() else remaining
            let n = min count (min remaining chunkCap) |> max 1
            Array.blit payload pos buffer offset n
            pos <- pos + n
            n

/// Property-based tests (FsCheck) for `Pump.readLines` / `Pump.LineBuffer` / `Pump.RawBuffer`
/// (`src/ProcessKit/Pump.fs`), generalizing the example-driven coverage in `PumpTests.fs` across
/// randomly generated content, chunk boundaries, and buffer policies. Each property calls
/// `Check.QuickThrowOnFailure`, which throws (with the failing case in the message) on the first
/// counter-example, surfacing as an ordinary NUnit test failure.
[<TestFixture>]
type PumpPropertyTests() =

    // Content-character pool for the "clean" line segments used by the round-trip and force-flush
    // properties below: letters, digits, punctuation, and a few multibyte (but non-surrogate-pair)
    // characters so multibyte encode/decode boundaries get exercised too. Deliberately excludes
    // '\r', '\n' (those are the terminator characters under test, injected only via the mode-aware
    // separator generator) and U+FEFF (the BOM character, injected only via the dedicated
    // `IncludeLeadingBom` flag) so a segment can never be confused with a terminator or a BOM.
    static let contentPool =
        [ 'a' .. 'z' ]
        @ [ 'A' .. 'Z' ]
        @ [ '0' .. '9' ]
        @ [ ' '; '.'; '!'; '-'; '_' ]
        @ [ 'é'; 'ü'; 'ñ'; '中'; '€' ]

    // A non-empty run of `contentPool` characters. Non-empty is load-bearing: an empty segment
    // sandwiched between two separators could make an intentionally-separate lone '\r' and lone
    // '\n' land adjacently in the raw byte stream, which is indistinguishable from a genuine '\r\n'
    // pair (a pair is, by design, always a single terminator — see `LineTerminator`) and would
    // silently swallow an expected empty line. Requiring non-empty segments rules that out.
    static let segmentGen: Gen<string> =
        Gen.nonEmptyListOf (Gen.elements contentPool)
        |> Gen.map (fun cs -> String(List.toArray cs))

    static let allTerminators =
        [ LineTerminator.Lf
          LineTerminator.Cr
          LineTerminator.CrLf
          LineTerminator.Any ]

    // The terminator byte sequences that split a line under `terminator` — used to join generated
    // segments so the "true" line boundaries are known up front. `CrLf` splits only on the two-byte
    // pair; the '\r'-aware modes (`Cr`/`Any`) also accept a lone '\r'; `Lf` (the '\n'-only default)
    // also accepts a '\r\n' pair (whose '\r' is stripped as part of the terminator).
    static let separatorsFor (terminator: LineTerminator) : Gen<string> =
        match terminator with
        | LineTerminator.Lf -> Gen.elements [ "\n"; "\r\n" ]
        | LineTerminator.Cr -> Gen.elements [ "\r"; "\r\n" ]
        | LineTerminator.CrLf -> Gen.constant "\r\n"
        | LineTerminator.Any -> Gen.elements [ "\n"; "\r"; "\r\n" ]

    // Small, deliberately varied read-chunk sizes (including 1, to force a read every single byte),
    // so a multi-byte terminator or a multi-byte UTF-8 character is exercised straddling a chunk
    // boundary with high probability.
    static let chunkSizesGen: Gen<int list> = Gen.nonEmptyListOf (Gen.choose (1, 7))

    static let runReadLines
        (bytes: byte[])
        (encoding: Encoding)
        (terminator: LineTerminator)
        (maxLineLength: int option)
        (chunkSizes: int list)
        : ResizeArray<string> =
        use stream = new ChunkedStream(bytes, chunkSizes)
        let lines = ResizeArray<string>()

        (Pump.readLines
            stream
            encoding
            terminator
            None
            (fun l ->
                lines.Add l
                ValueTask.CompletedTask)
            maxLineLength
            CancellationToken.None)
            .Wait()

        lines

    // --- Property 1: round-trip — concatenating the emitted lines (with their terminators put
    // back) reconstructs the original byte stream, independent of chunk boundaries, for every
    // `LineTerminator` mode and an optional leading BOM. ---

    [<Test>]
    member _.``readLines round-trips mode-separated segments regardless of chunk boundaries and an optional leading BOM``
        ()
        =
        let case =
            gen {
                let! terminator = Gen.elements allTerminators
                let! segments = Gen.nonEmptyListOf segmentGen
                let sep = separatorsFor terminator
                let! separators = Gen.listOfLength (max 0 (List.length segments - 1)) sep
                let! includeTrailing = Gen.elements [ true; false ]
                let! trailingSeparator = sep
                let! includeBom = Gen.elements [ true; false ]
                let! chunkSizes = chunkSizesGen

                return
                    {| Terminator = terminator
                       Segments = segments
                       Separators = separators
                       Trailing = (if includeTrailing then Some trailingSeparator else None)
                       IncludeBom = includeBom
                       ChunkSizes = chunkSizes |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let sb = StringBuilder()

                case.Segments
                |> List.iteri (fun i seg ->
                    sb.Append(seg: string) |> ignore

                    if i < case.Separators.Length then
                        sb.Append(case.Separators[i]: string) |> ignore)

                match case.Trailing with
                | Some sep -> sb.Append(sep: string) |> ignore
                | None -> ()

                let content = Encoding.UTF8.GetBytes(sb.ToString())

                let bytes =
                    if case.IncludeBom then
                        Array.append (Encoding.UTF8.GetPreamble()) content
                    else
                        content

                let actual = runReadLines bytes Encoding.UTF8 case.Terminator None case.ChunkSizes
                List.ofSeq actual = case.Segments)

        Check.QuickThrowOnFailure property

    // --- Property 2: chunk-independence — for fixed content, the resulting lines never depend on
    // how the input stream is cut into read-sized chunks. ---

    [<Test>]
    member _.``readLines splits the same content identically no matter how it is chunked``() =
        let case =
            gen {
                let! terminator = Gen.elements allTerminators
                // Free-form content, including raw '\r'/'\n' at any position (unlike the round-trip
                // property's "clean" segments) — this property only compares two chunkings of the
                // SAME bytes against each other, so no separately-known "true" line boundary is
                // needed.
                let! chars = Gen.nonEmptyListOf (Gen.elements ('\r' :: '\n' :: contentPool))
                let! chunkSizesA = chunkSizesGen
                let! chunkSizesB = chunkSizesGen

                return
                    {| Terminator = terminator
                       Text = String(List.toArray chars)
                       ChunkSizesA = chunkSizesA
                       ChunkSizesB = chunkSizesB |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let bytes = Encoding.UTF8.GetBytes case.Text
                let a = runReadLines bytes Encoding.UTF8 case.Terminator None case.ChunkSizesA
                let b = runReadLines bytes Encoding.UTF8 case.Terminator None case.ChunkSizesB
                List.ofSeq a = List.ofSeq b)

        Check.QuickThrowOnFailure property

    // --- Property 3: bounded retention — `LineBuffer` never retains more than its `MaxLines` /
    // `MaxBytes` cap, for every `OverflowMode`. ---

    [<Test>]
    member _.``LineBuffer never retains more than its MaxLines/MaxBytes cap under any OverflowMode``() =
        let case =
            gen {
                let! overflow = Gen.elements [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ]
                let! hasMaxLines = Gen.elements [ true; false ]
                let! maxLines = Gen.choose (0, 5)
                let! hasMaxBytes = Gen.elements [ true; false ]
                let! maxBytes = Gen.choose (0, 40)
                // Non-empty line content (see `segmentGen`'s doc comment): keeps `LineBuffer.Text`
                // unambiguous, so the retained line count can be read back off it via `Split('\n')`
                // (an empty `Text` then unambiguously means "nothing retained").
                let! lines = Gen.listOf segmentGen

                return
                    {| Overflow = overflow
                       MaxLines = (if hasMaxLines then Some maxLines else None)
                       MaxBytes = (if hasMaxBytes then Some maxBytes else None)
                       Lines = lines |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let withLines =
                    match case.MaxLines with
                    | Some cap -> OutputBufferPolicy.Unbounded.WithMaxLines cap
                    | None -> OutputBufferPolicy.Unbounded

                let withBytes =
                    match case.MaxBytes with
                    | Some cap -> withLines.WithMaxBytes cap
                    | None -> withLines

                let policy = withBytes.WithOverflow case.Overflow
                let buf = Pump.LineBuffer(policy)
                case.Lines |> List.iter buf.Add

                let retainedCount, retainedBytes =
                    if buf.Text = "" then
                        0, 0
                    else
                        let split = buf.Text.Split '\n'
                        split.Length, (split |> Array.sumBy Encoding.UTF8.GetByteCount)

                let lineOk =
                    match policy.MaxLines with
                    | Some cap -> retainedCount <= cap
                    | None -> true

                let byteOk =
                    match policy.MaxBytes with
                    | Some cap -> retainedBytes <= cap
                    | None -> true

                lineOk && byteOk)

        Check.QuickThrowOnFailure property

    // --- Property 3b: the byte-stream analogue — `RawBuffer` never retains more than its byte cap,
    // for every `OverflowMode`. ---

    [<Test>]
    member _.``RawBuffer never retains more than its byte cap under any OverflowMode``() =
        let case =
            gen {
                let! overflow = Gen.elements [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ]
                let! cap = Gen.choose (0, 20)
                let! byteList = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)
                let! chunkSizes = chunkSizesGen

                return
                    {| Overflow = overflow
                       Cap = cap
                       Bytes = List.toArray byteList
                       ChunkSizes = chunkSizes |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let buf = Pump.RawBuffer(case.Cap, case.Overflow)
                let mutable pos = 0
                let mutable sizes = case.ChunkSizes

                while pos < case.Bytes.Length do
                    let chunk =
                        match sizes with
                        | s :: rest ->
                            sizes <- rest
                            s
                        | [] -> case.Bytes.Length - pos

                    let n = min chunk (case.Bytes.Length - pos) |> max 1
                    buf.Append(case.Bytes, pos, n)
                    pos <- pos + n

                buf.ToArray().Length <= case.Cap)

        Check.QuickThrowOnFailure property

    // --- Property 4: force-flush at the `maxLineLength` cap — every emitted segment is bounded by
    // the cap, and concatenating all emitted segments (across the whole stream) reconstructs the
    // concatenation of the "true" (uncapped) line contents, regardless of where a chunk boundary —
    // including one straddling a `\r\n` pair — falls. ---

    [<Test>]
    member _.``readLines force-flushes at the cap without losing or duplicating content, across chunk and CRLF boundaries``
        ()
        =
        let case =
            gen {
                let! terminator = Gen.elements allTerminators
                let! segments = Gen.nonEmptyListOf segmentGen
                let sep = separatorsFor terminator
                let! separators = Gen.listOfLength (max 0 (List.length segments - 1)) sep
                let! includeTrailing = Gen.elements [ true; false ]
                let! trailingSeparator = sep
                // A small cap so the force-flush triggers routinely; 0 is excluded (documented as a
                // deliberate one-char-at-a-time exception on `appendCapped`, not this invariant).
                let! cap = Gen.choose (1, 4)
                let! chunkSizes = chunkSizesGen

                return
                    {| Terminator = terminator
                       Segments = segments
                       Separators = separators
                       Trailing = (if includeTrailing then Some trailingSeparator else None)
                       Cap = cap
                       ChunkSizes = chunkSizes |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let sb = StringBuilder()

                case.Segments
                |> List.iteri (fun i seg ->
                    sb.Append(seg: string) |> ignore

                    if i < case.Separators.Length then
                        sb.Append(case.Separators[i]: string) |> ignore)

                match case.Trailing with
                | Some sep -> sb.Append(sep: string) |> ignore
                | None -> ()

                let bytes = Encoding.UTF8.GetBytes(sb.ToString())

                let actual =
                    runReadLines bytes Encoding.UTF8 case.Terminator (Some case.Cap) case.ChunkSizes

                let lengthsOk = actual |> Seq.forall (fun s -> s.Length <= case.Cap)
                let reconstructed = String.concat "" actual
                let expected = String.concat "" case.Segments
                lengthsOk && reconstructed = expected)

        Check.QuickThrowOnFailure property
