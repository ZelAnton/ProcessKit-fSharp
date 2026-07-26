namespace ProcessKit.Fuzz

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open SharpFuzz
open ProcessKit
open ProcessKit.Testing

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
        match Environment.GetEnvironmentVariable "PROCESSKIT_FUZZ_TARGET" with
        | "pump" -> Fuzzer.LibFuzzer.Run(ReadOnlySpanAction Targets.pump)
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
