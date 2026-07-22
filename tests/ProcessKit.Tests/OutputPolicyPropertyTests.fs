namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
open NUnit.Framework
open FsCheck
open FsCheck.FSharp
open ProcessKit

/// Property-based tests (FsCheck) for `OutputBufferPolicy` (src/ProcessKit/OutputPolicy.fs), exercised
/// through its two enforcement points: `Pump.LineBuffer` (the line-capturing verbs' retention) and
/// `Pump.RawBuffer` / `Pump.captureRawOrEmpty` (the raw-byte capture path). Generalizes the
/// example-driven coverage in `PumpTests.fs` across randomly generated content, cap sizes, and
/// `OverflowMode`s, in the same style as `PumpPropertyTests.fs`. Each property calls
/// `Check.QuickThrowOnFailure`, which throws (with the failing case in the message) on the first
/// counter-example, surfacing as an ordinary NUnit test failure.
[<TestFixture>]
type OutputPolicyPropertyTests() =

    static let allOverflowModes =
        [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ]

    // Content pool for generated line/byte payloads: letters, digits, a couple of punctuation
    // characters, and a few multibyte (non-surrogate-pair) characters, so the byte-accounting
    // properties below exercise multibyte boundaries — a cap that lands mid-character-cost, not just
    // mid-ASCII-count. Deliberately excludes '\r'/'\n' so a generated line can never be confused with
    // multiple lines once split back apart.
    static let contentPool =
        [ 'a' .. 'z' ]
        @ [ '0' .. '9' ]
        @ [ ' '; '.'; '-' ]
        @ [ 'é'; 'ü'; 'ñ'; '中'; '€' ]

    // A non-empty line. Non-empty is load-bearing here (as in `PumpPropertyTests.segmentGen`): the
    // properties below read the retained line COUNT back off `LineBuffer.Text` by splitting on '\n',
    // which is only unambiguous when every retained line is itself non-empty (otherwise a retained
    // empty-string line is indistinguishable from zero retained lines).
    static let lineGen: Gen<string> =
        Gen.nonEmptyListOf (Gen.elements contentPool)
        |> Gen.map (fun cs -> String(List.toArray cs))

    // Small, varied chunk sizes (including 1) for feeding `RawBuffer.Append` piecemeal, so an
    // eviction/accounting bug that depends on how many bytes arrive per call (rather than on total
    // content) gets exercised.
    static let chunkSizesGen: Gen<int list> = Gen.nonEmptyListOf (Gen.choose (1, 5))

    // Build an `OutputBufferPolicy` from optional caps and an overflow mode, mirroring how a caller
    // composes one via `Unbounded.WithMaxLines`/`.WithMaxBytes`/`.WithOverflow`.
    static let buildPolicy (maxLines: int option) (maxBytes: int option) (overflow: OverflowMode) =
        let withLines =
            match maxLines with
            | Some cap -> OutputBufferPolicy.Unbounded.WithMaxLines cap
            | None -> OutputBufferPolicy.Unbounded

        let withBytes =
            match maxBytes with
            | Some cap -> withLines.WithMaxBytes cap
            | None -> withLines

        withBytes.WithOverflow overflow

    // The number of lines actually retained, read back off `Text` (sound only when every retained line
    // is non-empty — see `lineGen`'s doc comment).
    static let retainedLineCount (buf: Pump.LineBuffer) =
        if buf.Text = "" then 0 else buf.Text.Split('\n').Length

    // The UTF-8 byte size of what `Text` actually reassembles (not the internally accounted total,
    // which is a deliberate small over-estimate — see `LineBuffer`'s doc comment).
    static let retainedByteCount (buf: Pump.LineBuffer) =
        if buf.Text = "" then
            0
        else
            Encoding.UTF8.GetByteCount buf.Text

    // Feed `bytes` to `buf` split across `chunkSizes`, cycling back to the start of the size list once
    // it is exhausted (rather than falling back to a single "rest of the bytes" chunk) — the `RawBuffer`
    // analogue of `PumpPropertyTests`' `ChunkedStream`. Cycling keeps the chunking pattern consistent for
    // the whole input regardless of how many sizes were generated, which matters for Property 4
    // (chunk-independence): without it, a short generated size list exhausts early and degrades to a
    // single large `Append`, masking eviction/accounting bugs that only manifest across many small calls.
    static let feedRawChunked (buf: Pump.RawBuffer) (bytes: byte[]) (chunkSizes: int list) =
        let mutable pos = 0
        let mutable sizes = chunkSizes

        while pos < bytes.Length do
            let chunk =
                match sizes with
                | s :: rest ->
                    sizes <- rest
                    s
                | [] ->
                    // Exhausted - cycle back to the start of the original list instead of dumping the
                    // remaining bytes in one call. `chunkSizes` is non-empty by construction (the
                    // generator only ever produces at least one size), but guard explicitly rather than
                    // relying on that invariant holding at every call site.
                    match chunkSizes with
                    | s :: rest ->
                        sizes <- rest
                        s
                    | [] -> bytes.Length - pos

            let n = min chunk (bytes.Length - pos) |> max 1
            buf.Append(bytes, pos, n)
            pos <- pos + n

    // --- Property 1: bounded retention — the retained volume, read back off `Text`, never exceeds a
    // configured `MaxLines`/`MaxBytes` cap for any combination of caps and any `OverflowMode`; and
    // `TotalLines` always counts every `Add` call regardless of what was actually retained. Catches an
    // eviction-loop off-by-one at the retention boundary, and a counter that stops incrementing once a
    // cap trips (which would silently hide truncation from a caller that only reads `TotalLines`). ---

    [<Test>]
    member _.``LineBuffer never retains more than its MaxLines/MaxBytes cap, and TotalLines counts every Add regardless of truncation``
        ()
        =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! hasMaxLines = Gen.elements [ true; false ]
                let! maxLines = Gen.choose (0, 5)
                let! hasMaxBytes = Gen.elements [ true; false ]
                let! maxBytes = Gen.choose (0, 30)
                let! lines = Gen.listOf lineGen

                return
                    {| Overflow = overflow
                       MaxLines = (if hasMaxLines then Some maxLines else None)
                       MaxBytes = (if hasMaxBytes then Some maxBytes else None)
                       Lines = lines |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let policy = buildPolicy case.MaxLines case.MaxBytes case.Overflow
                let buf = Pump.LineBuffer(policy)
                case.Lines |> List.iter buf.Add

                let lineOk =
                    match policy.MaxLines with
                    | Some cap -> retainedLineCount buf <= cap
                    | None -> true

                let byteOk =
                    match policy.MaxBytes with
                    | Some cap -> retainedByteCount buf <= cap
                    | None -> true

                lineOk && byteOk && buf.TotalLines = List.length case.Lines)

        Check.QuickThrowOnFailure property

    // --- Property 2: the overflow signal — `Truncated` (DropOldest/DropNewest) or `TooLarge` (Error) —
    // is set if and only if fewer lines are retained than were added. Catches a flag that is
    // spuriously always-on/always-off, or off-by-one at the exact-cap boundary (tripped a line too
    // early, or missed on the line that actually overflows). ---

    [<Test>]
    member _.``LineBuffer's overflow flag is set if and only if a line was actually dropped``() =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! hasMaxLines = Gen.elements [ true; false ]
                let! maxLines = Gen.choose (0, 4)
                let! hasMaxBytes = Gen.elements [ true; false ]
                let! maxBytes = Gen.choose (0, 20)
                let! lines = Gen.nonEmptyListOf lineGen

                return
                    {| Overflow = overflow
                       MaxLines = (if hasMaxLines then Some maxLines else None)
                       MaxBytes = (if hasMaxBytes then Some maxBytes else None)
                       Lines = lines |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let policy = buildPolicy case.MaxLines case.MaxBytes case.Overflow
                let buf = Pump.LineBuffer(policy)
                case.Lines |> List.iter buf.Add

                let overflowFlag =
                    match case.Overflow with
                    | OverflowMode.Error -> buf.TooLarge
                    | _ -> buf.Truncated

                let somethingDropped = retainedLineCount buf < buf.TotalLines
                overflowFlag = somethingDropped)

        Check.QuickThrowOnFailure property

    // --- Property 3: the raw-byte-capture analogue of Properties 1-2 — `RawBuffer` (the accumulator
    // behind `OutputBytesAsync`/pipeline stdout capture) never retains more bytes than its cap, and its
    // overflow signal is set if and only if fewer bytes were retained than were actually fed in (the
    // input's own length, not the internally accounted `TotalBytes` — comparing against `TotalBytes`
    // alone would let an under-counting bug in `TotalBytes` itself mask a real overflow-flag mismatch, so
    // `TotalBytes` accuracy is asserted separately as a baseline). `OutputBufferPolicy.MaxBytes` governs
    // both this raw path and `LineBuffer` above, so the same contract must hold on both — a divergence
    // here would mean the byte verb and the text verbs silently disagree about what "MaxBytes" means. ---

    [<Test>]
    member _.``RawBuffer never retains more than its byte cap, and its overflow flag is set iff a byte was actually dropped``
        ()
        =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! cap = Gen.choose (0, 20)
                let! byteList = Gen.nonEmptyListOf (Gen.choose (0, 255) |> Gen.map byte)
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
                feedRawChunked buf case.Bytes case.ChunkSizes

                let retainedLen = buf.ToArray().Length
                let capOk = retainedLen <= case.Cap
                let totalBytesOk = buf.TotalBytes = case.Bytes.Length

                let overflowFlag =
                    match case.Overflow with
                    | OverflowMode.Error -> buf.TooLarge
                    | _ -> buf.Truncated

                let somethingDropped = retainedLen < case.Bytes.Length
                capOk && totalBytesOk && overflowFlag = somethingDropped)

        Check.QuickThrowOnFailure property

    // --- Property 4: chunk-independence — for fixed content, cap, and overflow mode, `RawBuffer`'s
    // final retained bytes and flags never depend on how the input arrives chunked across `Append`
    // calls. Catches an eviction/accounting bug that depends on per-call chunk size rather than total
    // content (e.g. an eviction loop that only fires once per `Append` instead of until it truly
    // fits). ---

    [<Test>]
    member _.``RawBuffer's retained bytes and flags are independent of how the input is chunked across Append calls``
        ()
        =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! cap = Gen.choose (0, 20)
                let! byteList = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)
                let! chunkSizesA = chunkSizesGen
                let! chunkSizesB = chunkSizesGen

                return
                    {| Overflow = overflow
                       Cap = cap
                       Bytes = List.toArray byteList
                       ChunkSizesA = chunkSizesA
                       ChunkSizesB = chunkSizesB |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let bufA = Pump.RawBuffer(case.Cap, case.Overflow)
                feedRawChunked bufA case.Bytes case.ChunkSizesA
                let bufB = Pump.RawBuffer(case.Cap, case.Overflow)
                feedRawChunked bufB case.Bytes case.ChunkSizesB

                bufA.ToArray() = bufB.ToArray()
                && bufA.Truncated = bufB.Truncated
                && bufA.TooLarge = bufB.TooLarge
                && bufA.TotalBytes = bufB.TotalBytes)

        Check.QuickThrowOnFailure property

    // --- Property 5: the documented "no ceiling to cross" contract — with no `MaxLines`/`MaxBytes` cap
    // set at all, `OutputBufferPolicy.Unbounded` retains every added line, in order, and never flags an
    // overflow, regardless of `Overflow` mode (see `OverflowMode.Error`'s doc comment: "with no cap set
    // ... there is no ceiling to cross"). Catches a regression where `Error` starts flagging (or
    // `Drop*` starts evicting) even though no cap was configured. ---

    [<Test>]
    member _.``Unbounded LineBuffer policy retains every line and never flags an overflow, in every OverflowMode``() =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! lines = Gen.listOf lineGen
                return {| Overflow = overflow; Lines = lines |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                let policy = OutputBufferPolicy.Unbounded.WithOverflow case.Overflow
                let buf = Pump.LineBuffer(policy)
                case.Lines |> List.iter buf.Add

                buf.Text = String.Join('\n', case.Lines)
                && not buf.Truncated
                && not buf.TooLarge
                && buf.TotalLines = List.length case.Lines)

        Check.QuickThrowOnFailure property

    // --- Property 6: the same "no ceiling" contract at the public raw-capture entry point —
    // `Pump.captureRawOrEmpty` with `MaxBytes = None` (the byte-verb/pipeline default) hands back the
    // exact bytes the stream produced, untruncated, regardless of `Overflow` (which is irrelevant with
    // no cap set). Exercises the actual production seam (`OutputBytesAsync`/pipeline capture route
    // through this), not just the internal `RawBuffer` type. ---

    [<Test>]
    member _.``captureRawOrEmpty with no MaxBytes cap returns the exact input bytes untruncated``() =
        let case =
            gen {
                let! overflow = Gen.elements allOverflowModes
                let! byteList = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)

                return
                    {| Overflow = overflow
                       Bytes = List.toArray byteList |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun case ->
                use stream = new MemoryStream(case.Bytes)
                let policy = OutputBufferPolicy.Unbounded.WithOverflow case.Overflow

                let capture =
                    (Pump.captureRawOrEmpty (Some(stream :> Stream)) None policy CancellationToken.None).Result

                capture.Bytes = case.Bytes && not capture.Truncated && not capture.TooLarge)

        Check.QuickThrowOnFailure property

    // --- Boundary conditions, checked explicitly (not merely relied upon to appear via random
    // generation) across all three `OverflowMode`s: exactly at the byte cap vs. one byte over it, empty
    // input, a single line that overflows the cap all by itself, and a cap that lands mid-multibyte-
    // character cost. ---

    [<Test>]
    member _.``LineBuffer at exactly the byte cap retains everything; one byte over it, the flag trips``() =
        for overflow in allOverflowModes do
            // "ab" (2 bytes) and "c" (1 byte), each +1 separator byte, sum to exactly 5 - the cap below.
            let atCap =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(5).WithOverflow overflow)

            [ "ab"; "c" ] |> List.iter atCap.Add
            Assert.That(atCap.Text, Is.EqualTo "ab\nc", $"{overflow} at-cap text")
            // The internally accounted cost is exactly the cap (5); the actual reassembled `Text` is one
            // byte less (4) because the accounted formula charges every retained line a separator byte,
            // including the last one, which needs none once joined - see `LineBuffer`'s doc comment.
            Assert.That(retainedByteCount atCap, Is.EqualTo 4, $"{overflow} at-cap bytes")
            Assert.That(atCap.Truncated, Is.False, $"{overflow} at-cap truncated")
            Assert.That(atCap.TooLarge, Is.False, $"{overflow} at-cap tooLarge")

            // "ab" and "cc" sum to 3 + 3 = 6 - one byte over the same cap of 5: the second line must
            // trip the overflow signal, and the retained bytes must still respect the cap.
            let overCap =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(5).WithOverflow overflow)

            [ "ab"; "cc" ] |> List.iter overCap.Add
            Assert.That(retainedByteCount overCap, Is.LessThanOrEqualTo 5, $"{overflow} over-cap bytes")

            match overflow with
            | OverflowMode.Error -> Assert.That(overCap.TooLarge, Is.True, $"{overflow} over-cap tooLarge")
            | _ -> Assert.That(overCap.Truncated, Is.True, $"{overflow} over-cap truncated")

    [<Test>]
    member _.``LineBuffer and RawBuffer on empty input retain nothing and never flag an overflow``() =
        for overflow in allOverflowModes do
            let lineBuf =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxLines(2).WithMaxBytes(5).WithOverflow overflow)

            Assert.That(lineBuf.Text, Is.EqualTo "", $"{overflow} LineBuffer text")
            Assert.That(lineBuf.TotalLines, Is.EqualTo 0, $"{overflow} LineBuffer TotalLines")
            Assert.That(lineBuf.Truncated, Is.False, $"{overflow} LineBuffer truncated")
            Assert.That(lineBuf.TooLarge, Is.False, $"{overflow} LineBuffer tooLarge")

            let rawBuf = Pump.RawBuffer(4, overflow)
            Assert.That(rawBuf.ToArray(), Is.Empty, $"{overflow} RawBuffer content")
            Assert.That(rawBuf.TotalBytes, Is.EqualTo 0, $"{overflow} RawBuffer TotalBytes")
            Assert.That(rawBuf.Truncated, Is.False, $"{overflow} RawBuffer truncated")
            Assert.That(rawBuf.TooLarge, Is.False, $"{overflow} RawBuffer tooLarge")

    [<Test>]
    member _.``LineBuffer drops a single line that alone exceeds the byte cap entirely, never partially``() =
        for overflow in allOverflowModes do
            let buf =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(3).WithOverflow overflow)

            let longLine = String('x', 10)
            buf.Add longLine

            Assert.That(buf.Text, Is.EqualTo "", $"{overflow} text")
            Assert.That(buf.TotalLines, Is.EqualTo 1, $"{overflow} TotalLines")
            Assert.That(buf.TotalBytes, Is.EqualTo 11, $"{overflow} TotalBytes") // 10 content bytes + 1 separator

            match overflow with
            | OverflowMode.Error -> Assert.That(buf.TooLarge, Is.True, $"{overflow} tooLarge")
            | _ -> Assert.That(buf.Truncated, Is.True, $"{overflow} truncated")

    [<Test>]
    member _.``LineBuffer's byte cap is enforced on actual UTF-8 byte cost, not .Length, at a multibyte boundary``() =
        // '中' costs 3 UTF-8 bytes (+1 separator = 4) despite a `.Length` of 1 - a cap sized off the
        // naive character length instead of `Encoding.UTF8.GetByteCount` would never trip here, letting
        // the reassembled `Text` exceed the configured byte cap.
        let oneChar = "中"
        Assert.That(Encoding.UTF8.GetByteCount oneChar, Is.EqualTo 3)

        for overflow in allOverflowModes do
            // Cap sized to hold exactly one retained '中' (3 bytes + 1 separator = 4) but not two.
            let buf =
                Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(4).WithOverflow overflow)

            [ oneChar; oneChar ] |> List.iter buf.Add

            Assert.That(retainedByteCount buf, Is.LessThanOrEqualTo 4, $"{overflow} retained bytes")
            Assert.That(buf.TotalLines, Is.EqualTo 2, $"{overflow} TotalLines")

            match overflow with
            | OverflowMode.Error -> Assert.That(buf.TooLarge, Is.True, $"{overflow} tooLarge")
            | _ -> Assert.That(buf.Truncated, Is.True, $"{overflow} truncated")
