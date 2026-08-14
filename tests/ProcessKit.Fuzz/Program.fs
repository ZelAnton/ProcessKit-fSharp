namespace ProcessKit.Fuzz

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open SharpFuzz
open ProcessKit
open ProcessKit.Testing

module private TestHostErrorMode =

    [<Literal>]
    let private SEM_FAILCRITICALERRORS = 0x00000001u

    [<Literal>]
    let private SEM_NOGPFAULTERRORBOX = 0x00000002u

    [<Literal>]
    let private SEM_NOOPENFILEERRORBOX = 0x00008000u

    [<Literal>]
    let private SuppressedDialogModes =
        SEM_FAILCRITICALERRORS ||| SEM_NOGPFAULTERRORBOX ||| SEM_NOOPENFILEERRORBOX

    [<DllImport("kernel32.dll")>]
    extern uint32 SetErrorMode(uint32 uMode)

    let suppressModalDialogs () =
        if OperatingSystem.IsWindows() then
            SetErrorMode SuppressedDialogModes |> ignore

module private Targets =

    let private maxInputBytes = 65_536

    let private selector (input: ReadOnlySpan<byte>) index =
        if index < input.Length then int input[index] else 0

    let private selectEncoding value : Encoding =
        match value &&& 3 with
        | 0 -> UTF8Encoding(false, false)
        | 1 -> UnicodeEncoding(false, false, false)
        | 2 -> UnicodeEncoding(true, false, false)
        | _ -> UTF32Encoding(false, false, false)

    let private selectTerminator value =
        match value &&& 3 with
        | 0 -> LineTerminator.Lf
        | 1 -> LineTerminator.Cr
        | 2 -> LineTerminator.CrLf
        | _ -> LineTerminator.Any

    let private selectOverflow value =
        match value % 3 with
        | 0 -> OverflowMode.DropOldest
        | 1 -> OverflowMode.DropNewest
        | _ -> OverflowMode.Error

    let pump (input: ReadOnlySpan<byte>) : unit =
        if input.Length <= maxInputBytes then
            let encoding = selectEncoding (selector input 0)
            let terminator = selectTerminator (selector input 1)
            let maxLines = selector input 2 % 33
            let maxBytes = ((selector input 3 <<< 4) + selector input 4) % 4097
            let overflow = selectOverflow (selector input 5)
            let payload = input.Slice(min 6 input.Length).ToArray()
            use stream = new MemoryStream(payload, false)

            let policy =
                OutputBufferPolicy.Bounded(maxLines).WithMaxBytes(maxBytes).WithOverflow(overflow)

            let buffer = Pump.LineBuffer policy

            let onLine line =
                buffer.Add line
                ValueTask.CompletedTask

            Pump.readLines stream encoding terminator None onLine (Some maxBytes) CancellationToken.None
            |> fun readTask -> readTask.GetAwaiter().GetResult()

            let retainedBytes = Encoding.UTF8.GetByteCount buffer.Text

            if retainedBytes > maxBytes then
                invalidOp $"retained text exceeded its byte cap ({retainedBytes} > {maxBytes})"

            if buffer.TotalLines < 0 || buffer.TotalBytes < retainedBytes then
                invalidOp "line-buffer cumulative counters became inconsistent"

            if overflow = OverflowMode.Error && buffer.Truncated then
                invalidOp "fail-loud buffering reported lossy truncation"

            if overflow <> OverflowMode.Error && buffer.TooLarge then
                invalidOp "lossy buffering reported a fail-loud overflow"

    /// Feeds arbitrary bytes to `ContentLengthSession`'s framing parser as if they were a child
    /// process's raw stdout, through the same `RunningHost` seam `ContentLengthSessionTests` uses (an
    /// in-memory `Stream`, not a real spawned process) — the parser's internal reader is file-private,
    /// so this is the accessible surface, same as the shipped unit tests. `maxFrameBytes` varies with
    /// the input's first byte so both branches of the size check (accepted vs. `Content-Length ...
    /// exceeds the N-byte limit`) get coverage; the payload is the whole input, unsliced, so every seed
    /// corpus file stays a valid protocol stream regardless of which bucket its first byte selects.
    let framing (input: ReadOnlySpan<byte>) : unit =
        if input.Length <= maxInputBytes then
            let maxFrameBytes = 1 + (selector input 0) * 257
            let payload = input.ToArray()

            (task {
                let stdout = new MemoryStream(payload, false)

                let host: RunningHost =
                    { Config = (Command.create "fuzz-framing").Config
                      Pid = None
                      Stdout = Some(stdout :> Stream)
                      Stderr = None
                      Stdin = None
                      StartTime = DateTime.UtcNow
                      StartedTimestamp = Stopwatch.GetTimestamp()
                      StartTimeIdentity = None
                      Wait = fun () -> TaskCompletionSource<Outcome>().Task
                      StdinError = RunningHost.NoStdinError
                      StdinFeedComplete = ignore
                      StartKill = ignore
                      Signal = fun _ -> Ok()
                      GracefulKill = fun _ -> Task.CompletedTask
                      ResizePty = None
                      TreeStats = None
                      Teardown =
                        fun () ->
                            (stdout :> IDisposable).Dispose()
                            ValueTask() }

                use running = new RunningProcess(host)
                let session = ContentLengthSession(running, maxFrameBytes)
                let enumerator = session.FramesAsync().GetAsyncEnumerator()

                try
                    try
                        let mutable reading = true

                        while reading do
                            let! moved = enumerator.MoveNextAsync()

                            if moved then
                                let frame = enumerator.Current

                                if frame.Length > maxFrameBytes then
                                    invalidOp
                                        $"accepted frame exceeded MaxFrameBytes ({frame.Length} > {maxFrameBytes})"
                            else
                                reading <- false
                    with :? ProcessException as ex ->
                        match ex.Error with
                        | ProcessError.Parse _
                        | ProcessError.Io _ ->
                            // A malformed header, a non-CRLF line ending, an unterminated header, or a
                            // Content-Length over the configured limit is a documented parse failure
                            // (typed `ProcessError.Parse`); a genuine stream fault surfaces as
                            // `ProcessError.Io`. These are the only two typed outcomes the framing parser
                            // documents — anything else escaping the parser is the fuzz-worthy failure.
                            ()
                        | other -> invalidOp $"framing target raised an undocumented ProcessError: {other}"
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            })
                .GetAwaiter()
                .GetResult()

    /// The characters `AnsiEscapeFilter` treats as the start of an escape/CSI/OSC/control-string
    /// sequence (or, for U+009C, as the terminator it silently consumes) rather than printable text
    /// -- mirrors the `AnsiFilterState.Text` transitions in `RunningProcess.fs` exactly, so a string
    /// with none of these characters is guaranteed to reach the filtered output unchanged.
    let private isAnsiFilterSpecial (ch: char) =
        match ch with
        | '\u001b'
        | '\u009b'
        | '\u009d'
        | '\u0090'
        | '\u0098'
        | '\u009e'
        | '\u009f'
        | '\u009c' -> true
        | _ -> false

    // Chunk sizes (in `char`s) the `ansi` target splits its decoded input across when feeding
    // `AppendFiltered` — varied and mutually coprime-ish so a fuzzer-discovered escape sequence lands on
    // a chunk boundary at many different offsets over a corpus, exercising the filter's
    // straddles-any-read-boundary state machine (its own doc comment's stated contract).
    let private ansiChunkSizes = [| 1; 2; 3; 5; 8; 13; 21 |]

    let private feedFiltered (filter: AnsiEscapeFilter) (window: ExpectWindow) (text: string) =
        let mutable index = 0
        let mutable chunkIndex = 0

        while index < text.Length do
            let take =
                min ansiChunkSizes[chunkIndex % ansiChunkSizes.Length] (text.Length - index)

            window.AppendFiltered(filter, text.Substring(index, take))
            index <- index + take
            chunkIndex <- chunkIndex + 1

    /// Same chunking as `feedFiltered`, but drives a second, UNCAPPED `AnsiEscapeFilter` over the
    /// identical chunks to build a ground-truth filtered stream (`oracle`), then — after EVERY chunk, not
    /// only at the end — checks `window.Pending`/`window.Transcript` against the oracle's tail capped at
    /// `windowChars`/`transcriptChars`, and that the truncation flags flip exactly when the oracle has
    /// actually outgrown the corresponding cap. A final-size-only check (the caller's previous shape)
    /// cannot see mid-stream duplication, loss, or an incorrectly-timed truncation flag once the window
    /// itself is back under its cap on a later chunk — this compares the full trajectory instead (R-02).
    let private feedFilteredWithOracle
        (filter: AnsiEscapeFilter)
        (window: ExpectWindow)
        (windowChars: int)
        (transcriptChars: int)
        (text: string)
        =
        let oracleFilter = AnsiEscapeFilter()
        let oracle = StringBuilder()
        let mutable index = 0
        let mutable chunkIndex = 0

        while index < text.Length do
            let take =
                min ansiChunkSizes[chunkIndex % ansiChunkSizes.Length] (text.Length - index)

            let chunk = text.Substring(index, take)
            window.AppendFiltered(filter, chunk)
            oracleFilter.Append(chunk.AsSpan(), oracle)

            let oracleText = oracle.ToString()

            let expectedWindow =
                if oracleText.Length > windowChars then
                    oracleText.Substring(oracleText.Length - windowChars)
                else
                    oracleText

            if window.Pending <> expectedWindow then
                invalidOp "AppendFiltered's window diverged from the independent filtering oracle mid-stream"

            let expectedTranscript =
                if oracleText.Length > transcriptChars then
                    oracleText.Substring(oracleText.Length - transcriptChars)
                else
                    oracleText

            if window.Transcript <> expectedTranscript then
                invalidOp "AppendFiltered's transcript diverged from the independent filtering oracle mid-stream"

            if oracleText.Length > windowChars && not window.WindowTruncated then
                invalidOp "expect window exceeded its cap without setting WindowTruncated"

            if oracleText.Length > transcriptChars && not window.TranscriptTruncated then
                invalidOp "expect transcript exceeded its cap without setting TranscriptTruncated"

            if oracleText.Length <= windowChars && window.WindowTruncated then
                invalidOp "expect window truncation flag set before the cap was actually exceeded"

            if oracleText.Length <= transcriptChars && window.TranscriptTruncated then
                invalidOp "expect transcript truncation flag set before the cap was actually exceeded"

            index <- index + take
            chunkIndex <- chunkIndex + 1

    /// Feeds arbitrary decoded text to the internal `AnsiEscapeFilter` through `ExpectWindow.AppendFiltered`
    /// — the same accessible path `PtySession`'s filtered expect-window uses — in varying chunk sizes, so a
    /// straddling escape sequence and the window/transcript caps both get exercised together. Checks three
    /// invariants: the window/transcript's state after every chunk (not only the last) matches an
    /// independent, uncapped filtering oracle — catching mid-stream duplication, loss, or a
    /// wrongly-timed truncation flag (R-02); the bounded window and transcript never grow past their
    /// configured caps; and text with none of the filter's special control characters round-trips
    /// byte-for-byte regardless of where it was chunked.
    let ansi (input: ReadOnlySpan<byte>) : unit =
        if input.Length <= maxInputBytes then
            let text = Encoding.UTF8.GetString(input.ToArray())
            let windowChars = 4096
            let transcriptChars = 8192

            let filter = AnsiEscapeFilter()
            let window = ExpectWindow(windowChars, Some transcriptChars)
            feedFilteredWithOracle filter window windowChars transcriptChars text

            if window.Pending.Length > windowChars then
                invalidOp $"expect window exceeded its {windowChars}-char cap"

            if window.Transcript.Length > transcriptChars then
                invalidOp $"expect transcript exceeded its {transcriptChars}-char cap"

            let clean = String(text.ToCharArray() |> Array.filter (isAnsiFilterSpecial >> not))

            if clean.Length > 0 then
                let cleanFilter = AnsiEscapeFilter()
                let cleanWindow = ExpectWindow(clean.Length, None)
                feedFiltered cleanFilter cleanWindow clean

                if cleanWindow.Pending <> clean then
                    invalidOp "AnsiEscapeFilter lost or duplicated printable text carrying no escape sequences"

    let private exerciseReplay (replayer: RecordReplayRunner) : unit =
        (task {
            let runner = replayer :> IProcessRunner
            let command = Command.create "tool"
            let! _ = runner.CaptureStringAsync(command, CancellationToken.None)
            let! _ = runner.CaptureBytesAsync(command, CancellationToken.None)

            match! runner.SpawnAsync(command, CancellationToken.None) with
            | Error _ -> ()
            | Ok running ->
                use running = running
                let enumerator = running.OutputEventsAsync().GetAsyncEnumerator()

                try
                    let mutable reading = true

                    while reading do
                        let! hasNext = enumerator.MoveNextAsync()
                        reading <- hasNext
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

                let! _ = running.FinishAsync()
                ()
        })
            .GetAwaiter()
            .GetResult()

    let cassette (path: string) (input: ReadOnlySpan<byte>) : unit =
        if input.Length <= maxInputBytes then
            do
                use destination =
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)

                destination.Write input
                destination.Flush true

            match RecordReplayRunner.Replay path with
            | Error _ -> ()
            | Ok replayer ->
                use replayer = replayer
                exerciseReplay replayer

module Program =

    [<EntryPoint>]
    let main _ =
        TestHostErrorMode.suppressModalDialogs ()

        match Environment.GetEnvironmentVariable "PROCESSKIT_FUZZ_TARGET" with
        | "pump" -> Fuzzer.LibFuzzer.Run(ReadOnlySpanAction Targets.pump)
        | "framing" -> Fuzzer.LibFuzzer.Run(ReadOnlySpanAction Targets.framing)
        | "ansi" -> Fuzzer.LibFuzzer.Run(ReadOnlySpanAction Targets.ansi)
        | "cassette" ->
            let path =
                Path.Combine(Path.GetTempPath(), $"processkit-fuzz-{Environment.ProcessId}.json")

            try
                Fuzzer.LibFuzzer.Run(ReadOnlySpanAction(Targets.cassette path))
            finally
                try
                    File.Delete path
                with
                | :? IOException ->
                    // The fuzzer may still have the input open while the process is shutting down;
                    // a stale temp fixture is preferable to masking the actual fuzzing result.
                    ()
                | :? UnauthorizedAccessException ->
                    // Endpoint security may briefly hold the temp fixture during process teardown.
                    ()
        | value -> invalidArg "PROCESSKIT_FUZZ_TARGET" $"unknown fuzz target '{value}'"

        0
