namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// A capturing `ILogger`, scoped to this file's pipeline observability tests (mirrors
/// `LoggingTests.CapturingLogger` — a `private` type there is not reachable from this file, and
/// `PipelineTests.fs` compiles before `LoggingTests.fs` regardless).
type private PipelineCapturingLogger() =
    let records = ConcurrentQueue<string>()
    member _.Text = String.Join("\n", records)

    interface ILogger with
        member _.Log(_logLevel, _eventId, state, error, formatter) =
            records.Enqueue(formatter.Invoke(state, error))

        member _.IsEnabled(_logLevel) = true

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

/// A write-only tee whose `WriteAsync` parks the pipeline's stdout capture until the test releases it —
/// the "slow tee" of T-071 that keeps the last stage's capture (and, before the fix, an armed whole-chain
/// deadline) alive well after every stage has already exited. `FirstWrite` completes the instant the
/// capture first reaches the tee, so a test can time the deadline window deterministically against it.
type private BlockingTee() =
    inherit Stream()

    let firstWrite =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes when the capture first writes into the tee (the last stage produced output).
    member _.FirstWrite: Task = firstWrite.Task

    /// Unblock every parked (and future) write, letting the capture drain to completion.
    member _.Release() = release.TrySetResult() |> ignore

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Write(_, _, _) = ()

    override _.WriteAsync(_: byte[], _: int, _: int, _: CancellationToken) =
        firstWrite.TrySetResult() |> ignore
        release.Task

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        firstWrite.TrySetResult() |> ignore
        ValueTask(release.Task)

/// A relay-source double that yields one complete payload and then raises a real upstream read error.
/// The pipeline test seam installs it only for the inter-stage source, while the downstream stage and
/// both public completion paths remain real process plumbing. An EMPTY payload fails the very first
/// read instead (an empty read would otherwise be an ordinary EOF, not a fault), which is what the
/// relay-fault liveness tests need: the failure must land while the upstream stage is still alive.
type private RelayReadFaultStream(payload: byte[], message: string) =
    inherit Stream()

    let mutable payloadPending = payload.Length > 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _: CancellationToken) : ValueTask<int> =
        if payloadPending then
            payloadPending <- false
            payload.AsSpan().CopyTo(buffer.Span)
            ValueTask<int>(payload.Length)
        else
            raise (IOException message)

/// A relay-source double that PARKS its first read until the test releases it, and only then raises a
/// real upstream read error. It lets a test place the fault deterministically after the whole chain has
/// already begun tearing down (an external cancellation, or the chain deadline), which is where the
/// relay must classify it as a routine teardown race rather than a genuine failure — so the run keeps
/// reporting `Cancelled`/`TimedOut` instead of the relay's `ProcessError.Io`.
type private GatedRelayFaultStream(release: Task, message: string) =
    inherit Stream()

    let parked =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once the relay's read has entered — and parked in — this source.
    member _.Parked: Task = parked.Task

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(_: Memory<byte>, _: CancellationToken) : ValueTask<int> =
        parked.TrySetResult() |> ignore

        let faulted: Task<int> =
            task {
                do! release
                return raise (IOException message)
            }

        ValueTask<int>(faulted)

// --- T-069 stdin-feeder test doubles, shared across PipelineTests / ProcessControlTests / PumpTests.
// Defined here (non-private) because PipelineTests is the earliest of the three in the .fsproj compile
// order, so the later two can reuse these instead of redefining them. Interfaces are fully qualified to
// avoid `open System.Collections.Generic` shadowing the F# `List` module used throughout this file.

/// The enumeration stage at which a stdin-source double faults — proving a fault at ANY stage (not just
/// `MoveNext`/`ReadAsync`) surfaces as `ProcessError.Stdin` instead of a benign broken pipe.
type StdinFaultStage =
    | AtGetEnumerator
    | AtMoveNext
    | AtCurrent

/// A `FromLines` source (`seq<string>`) that throws at the requested enumeration stage.
type FaultyStdinLines(stage: StdinFaultStage) =
    interface System.Collections.Generic.IEnumerable<string> with
        member _.GetEnumerator() : System.Collections.Generic.IEnumerator<string> =
            if stage = AtGetEnumerator then
                raise (InvalidOperationException "sync-get-enumerator-boom")

            { new System.Collections.Generic.IEnumerator<string> with
                member _.Current =
                    if stage = AtCurrent then
                        raise (InvalidOperationException "sync-current-boom")
                    else
                        ""
              interface System.Collections.IEnumerator with
                  member _.Current = box ""

                  member _.MoveNext() =
                      if stage = AtMoveNext then
                          raise (InvalidOperationException "sync-move-next-boom")

                      true

                  member _.Reset() = ()
              interface IDisposable with
                  member _.Dispose() = () }

    interface System.Collections.IEnumerable with
        member this.GetEnumerator() : System.Collections.IEnumerator =
            (this :> System.Collections.Generic.IEnumerable<string>).GetEnumerator() :> System.Collections.IEnumerator

/// A `FromAsyncLines` source that throws at the requested enumeration stage.
type FaultyStdinAsyncLines(stage: StdinFaultStage) =
    interface System.Collections.Generic.IAsyncEnumerable<string> with
        member _.GetAsyncEnumerator(_: CancellationToken) : System.Collections.Generic.IAsyncEnumerator<string> =
            if stage = AtGetEnumerator then
                raise (InvalidOperationException "async-get-enumerator-boom")

            { new System.Collections.Generic.IAsyncEnumerator<string> with
                member _.Current =
                    if stage = AtCurrent then
                        raise (InvalidOperationException "async-current-boom")
                    else
                        ""

                member _.MoveNextAsync() : ValueTask<bool> =
                    if stage = AtMoveNext then
                        raise (InvalidOperationException "async-move-next-boom")

                    ValueTask<bool>(true)
              interface IAsyncDisposable with
                  member _.DisposeAsync() : ValueTask = ValueTask() }

/// A `FromAsyncLines` source whose `MoveNextAsync` parks until the enumerator's cancellation token
/// fires, recording when enumeration started and when the enumerator was disposed — so a test can prove
/// that stopping the feeder (teardown / cancellation / early child exit) cancels a hung feed AND
/// disposes the user's enumerator.
type HangingStdinAsyncLines() =
    let started =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let disposed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once `MoveNextAsync` has been entered (the feed is parked).
    member _.Started: Task = started.Task

    /// Completes once the enumerator's `DisposeAsync` has run.
    member _.Disposed: Task = disposed.Task

    interface System.Collections.Generic.IAsyncEnumerable<string> with
        member _.GetAsyncEnumerator
            (cancellationToken: CancellationToken)
            : System.Collections.Generic.IAsyncEnumerator<string> =
            { new System.Collections.Generic.IAsyncEnumerator<string> with
                member _.Current = ""

                member _.MoveNextAsync() : ValueTask<bool> =
                    started.TrySetResult() |> ignore

                    ValueTask<bool>(
                        task {
                            // Park until the feeder's lifecycle token cancels this feed.
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                            return true
                        }
                    )
              interface IAsyncDisposable with
                  member _.DisposeAsync() : ValueTask =
                      disposed.TrySetResult() |> ignore
                      ValueTask() }

/// A `FromAsyncLines` source that parks in its FIRST `MoveNextAsync` — before yielding anything — until a
/// test calls `Release`, then yields a single line and ends. It signals `Parked` the moment the feed is
/// parked (nothing written yet), so a test can prove, without timing guesswork, that a `KeepStdinOpen`
/// source feed has NOT finished — and therefore `RunningProcess.TakeStdin` has NOT yet handed the pipe to
/// the caller — until the source is fully drained (T-123: `Stdin(source)` + `KeepStdinOpen`).
type GatedStdinAsyncLines(line: string) =
    let parked =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable yielded = false

    /// Completes once the feed has entered `MoveNextAsync` and is parked, before writing anything.
    member _.Parked: Task = parked.Task

    /// Let the parked feed proceed: it yields the single line, then ends.
    member _.Release() = release.TrySetResult() |> ignore

    interface System.Collections.Generic.IAsyncEnumerable<string> with
        member _.GetAsyncEnumerator(_: CancellationToken) : System.Collections.Generic.IAsyncEnumerator<string> =
            { new System.Collections.Generic.IAsyncEnumerator<string> with
                member _.Current = line

                member _.MoveNextAsync() : ValueTask<bool> =
                    if yielded then
                        ValueTask<bool>(false)
                    else
                        yielded <- true
                        parked.TrySetResult() |> ignore

                        ValueTask<bool>(
                            task {
                                // Park until the test releases the source; only then is the line yielded and
                                // the feed allowed to complete.
                                do! release.Task
                                return true
                            }
                        )
              interface IAsyncDisposable with
                  member _.DisposeAsync() : ValueTask = ValueTask() }

[<TestFixture>]
type PipelineTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // `sort` exists on both Windows (System32) and Unix; with no file argument it reads stdin,
    // sorts the lines, and writes them to stdout — a portable, shell-free pipeline stage.
    let sortStage = Command.create "sort"

    // Emit `lines` in the given order (unsorted) so a downstream `sort` has work to do.
    let emit (lines: string list) =
        if isWindows then
            shell (lines |> List.map (sprintf "echo %s") |> String.concat "&")
        else
            shell (lines |> List.map (sprintf "echo %s") |> String.concat "; ")

    // Any non-clean exit is a genuine checked failure for the `observeStages` observation tests below
    // (a stage's OkCodes default to {0}, so anything but `Exited 0` is a blame-worthy failure).
    let checkedFailure = (fun (_: int) (o: Outcome) -> o <> Outcome.Exited 0)

    // Split captured output into trimmed, non-empty lines (newline + CR agnostic).
    let lines (text: string) =
        text.Split('\n')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.toList

    // Run a two-stage `emit | sort` pipeline, optionally capping the LAST stage's OutputBuffer, and
    // capture its stdout as raw bytes. The sorted output is deterministic across runs, so an uncapped
    // run is a stable oracle for the capped tail/head. Used by the last-stage byte-cap tests (T-011).
    let pipelineBytes (lastPolicy: OutputBufferPolicy option) =
        let last =
            match lastPolicy with
            | Some policy -> sortStage |> Command.outputBuffer policy
            | None -> sortStage

        ((emit [ "banana"; "apple" ]).Pipe last).OutputBytesAsync()

    // A stage that writes `lineCount` lines to stderr (never stdout), then exits non-zero — used to
    // prove a chatty stage's stderr is bounded by that stage's own `OutputBuffer` byte cap (T-034).
    // Optionally capped via `cap`; `None` keeps the previous unbounded behaviour.
    let noisyFailingStage (lineCount: int) (cap: int option) =
        let line = String.replicate 32 "x"
        let echoErr = sprintf "echo %s 1>&2" line
        // A space-padded separator on Windows keeps cmd.exe from misparsing the trailing `1>&2`
        // redirection against an immediately-following `&` command separator.
        let separator = if isWindows then " & " else "; "

        let script =
            (List.replicate lineCount echoErr |> String.concat separator)
            + separator
            + "exit 3"

        let stage = shell script

        match cap with
        | Some maxBytes ->
            stage
            |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes maxBytes)
        | None -> stage

    // A stage that writes `errLineCount` 32-char lines to stderr (never stdout) then exits 0, capturing
    // its stderr under `policy` — drives a stage's OWN stderr past a fail-loud (`Error`) byte cap so the
    // pipeline must surface it (T-062). Exiting 0 keeps pipefail silent, so an `OutputTooLarge` can only
    // come from the stderr overflow, not the exit code.
    let stderrStage (errLineCount: int) (policy: OutputBufferPolicy) =
        let line = String.replicate 32 "x"
        // Space-padded on Windows so cmd.exe does not misparse a trailing `1>&2` against the next `&`.
        let sep = if isWindows then " & " else "; "

        let script =
            List.replicate errLineCount (sprintf "echo %s 1>&2" line) |> String.concat sep

        shell script |> Command.outputBuffer policy

    // A stage that writes `stdoutLines` to stdout AND `errLineCount` 32-char lines to stderr, then exits
    // 0, under `policy` — the collision fixture (T-062): its stderr can overflow while it still feeds the
    // downstream stage enough stdout for the LAST stage's own stdout cap to overflow too, so both a
    // stage's stderr and the final stdout trip at once.
    let dualStreamStage (stdoutLines: string list) (errLineCount: int) (policy: OutputBufferPolicy) =
        let errLine = String.replicate 32 "x"
        let sep = if isWindows then " & " else "; "
        let stdoutCmds = stdoutLines |> List.map (sprintf "echo %s")
        let errCmds = List.replicate errLineCount (sprintf "echo %s 1>&2" errLine)
        let script = (stdoutCmds @ errCmds) |> String.concat sep
        shell script |> Command.outputBuffer policy

    // A silent producer that never writes (and would run a long time) — the stage whose empty,
    // still-open stdout blocks the relay's read, so proactive teardown, not a broken pipe, is what
    // must end it once a downstream stage fails.
    let slowSilentStage =
        if isWindows then
            shell "ping -n 30 127.0.0.1 >nul"
        else
            shell "sleep 30"

    // Race a pipeline run against a generous deadline: assert it finished by teardown (won the race)
    // rather than by outliving the slow stage (which would let the delay win). 15s is far below the
    // 30s slow stage, so a hang is unmistakable, yet far above the sub-second proactive teardown.
    let assertFinishesPromptly (run: Task<'T>) : Task =
        task {
            let! finished = Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds 15.0))

            Assert.That(
                finished,
                Is.SameAs run,
                "the pipeline must tear the chain down proactively, not wait for the slow/silent stage"
            )
        }
        :> Task

    // Await a control signal raised by the code under test (a teardown callback firing, a verdict being
    // settled) with a hard bound: a regression that never raises it then fails this test with the given
    // reason instead of parking the whole suite on a task that can no longer complete.
    let awaitSignal (reason: string) (signal: Task) : Task =
        task {
            let! finished = Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds 15.0))
            Assert.That(finished, Is.SameAs signal, reason)
        }
        :> Task

    // Listen to every `int64` measurement on ProcessKit's meter for the pipeline observability
    // tests below — mirrors `LoggingTests.listenToRunMetrics` (not reachable from this file).
    let listenToRunMetrics () =
        let activeDeltas = ConcurrentQueue<int64>()
        let mutable startedCount = 0L
        let mutable completedCount = 0L

        let listener = new MeterListener()

        listener.InstrumentPublished <-
            (fun instrument l ->
                if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                    l.EnableMeasurementEvents instrument)

        listener.SetMeasurementEventCallback<int64>(
            MeasurementCallback<int64>(fun instrument value _tags _state ->
                match instrument.Name with
                | "processkit.runs.active" -> activeDeltas.Enqueue value
                | "processkit.runs.started" -> Interlocked.Add(&startedCount, value) |> ignore
                | "processkit.runs.completed" -> Interlocked.Add(&completedCount, value) |> ignore
                | _ -> ())
        )

        listener.Start()
        listener, activeDeltas, (fun () -> startedCount), (fun () -> completedCount)

    // Drain a streaming session's line/event enumerable to a list (mirrors `StreamingTests.collect`;
    // that fixture's private copy is not reachable from this file). `IAsyncEnumerable` is fully
    // qualified rather than `open`ed — `open System.Collections.Generic` would shadow the F# `List`
    // module used throughout this file (see the note by the stdin-feeder doubles above).
    let collect (source: System.Collections.Generic.IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let enumerator = source.GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then acc.Add enumerator.Current else more <- false

            do! enumerator.DisposeAsync()
            return acc
        }

    [<Test>]
    member _.``two-stage pipeline wires stdout into the next stage's stdin``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``three-stage pipeline chains every stage``() : Task =
        task {
            let pipeline =
                (emit [ "cherry"; "apple"; "banana" ]).Pipe(sortStage).Pipe(sortStage)

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana"; "cherry" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes captures the last stage's raw stdout``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                let text = Encoding.UTF8.GetString result.Stdout
                Assert.That(lines text, Is.EqualTo(box [ "apple"; "banana" ]))
                Assert.That(result.Truncated, Is.False) // no cap on the last stage -> nothing truncated
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- The last stage's OutputBuffer byte cap bounds the captured pipeline stdout (T-011) ---

    [<Test>]
    member _.``pipeline last-stage DropOldest keeps the tail and flags truncation``() : Task =
        task {
            let cap = 4
            let! full = pipelineBytes None
            let! result = pipelineBytes (Some(OutputBufferPolicy.Unbounded.WithMaxBytes cap))

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the sorted output must exceed the cap")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[full.Stdout.Length - cap ..], result.Stdout) // the tail
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline last-stage DropNewest keeps the head and flags truncation``() : Task =
        task {
            let cap = 4
            let! full = pipelineBytes None

            let! result =
                pipelineBytes (
                    Some((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.DropNewest)
                )

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the sorted output must exceed the cap")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[.. cap - 1], result.Stdout) // the head
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline last-stage Error trips OutputTooLarge once the byte cap is exceeded``() : Task =
        task {
            let policy =
                OutputBufferPolicy.Unbounded.WithMaxLines(1).WithMaxBytes(3).WithOverflow OverflowMode.Error

            match! pipelineBytes (Some policy) with
            | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, totalBytes)) ->
                Assert.That(lineLimit, Is.EqualTo(None), "a raw stdout capture does not enforce MaxLines")
                Assert.That(byteLimit, Is.EqualTo(Some 3))
                Assert.That(totalLines, Is.EqualTo 0, "a raw stdout capture has no line structure")
                Assert.That(totalBytes, Is.GreaterThan 3)
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline OutputString also errors OutputTooLarge under the last-stage byte cap``() : Task =
        task {
            // The string verb decodes the raw byte capture, so the same byte cap Error trips it too.
            let last =
                sortStage
                |> Command.outputBuffer ((OutputBufferPolicy.Unbounded.WithMaxBytes 3).WithOverflow OverflowMode.Error)

            match! ((emit [ "banana"; "apple" ]).Pipe last).OutputStringAsync() with
            | Error(ProcessError.OutputTooLarge _) -> Assert.Pass()
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    // --- Every stage's stderr is bounded by that stage's own OutputBuffer byte cap (T-034) ---

    [<Test>]
    member _.``representative pipefail stderr truncation is reported by buffered text and bytes``() : Task =
        task {
            let cap = 16

            for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest ] do
                let policy = (OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow overflow

                let noisy = noisyFailingStage 50 None |> Command.outputBuffer policy

                match! (noisy.Pipe sortStage).OutputBytesAsync() with
                | Ok result ->
                    Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))
                    Assert.That(result.Stderr, Is.Not.Empty)
                    Assert.That(Encoding.UTF8.GetByteCount result.Stderr, Is.LessThanOrEqualTo cap)
                    Assert.That(result.Truncated, Is.True)
                | Error error -> Assert.Fail $"buffered bytes failed for {overflow}: {error}"

                match! (noisy.Pipe sortStage).OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))
                    Assert.That(result.Stderr, Is.Not.Empty)
                    Assert.That(Encoding.UTF8.GetByteCount result.Stderr, Is.LessThanOrEqualTo cap)
                    Assert.That(result.Truncated, Is.True)
                | Error error -> Assert.Fail $"buffered text failed for {overflow}: {error}"
        }
        :> Task

    [<Test>]
    member _.``non-representative stderr truncation does not mark the pipeline result``() : Task =
        task {
            let policy =
                (OutputBufferPolicy.Unbounded.WithMaxBytes 16).WithOverflow OverflowMode.DropNewest

            let noisySuccessful = stderrStage 50 policy

            match! (noisySuccessful.Pipe sortStage).OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(result.Stderr, Is.Empty, "the representative last stage owns the published stderr")
                Assert.That(result.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``last-stage stderr truncation marks the pipeline result``() : Task =
        task {
            let cap = 16

            let noisyLast =
                stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.DropOldest)

            match! ((shell "exit 0").Pipe noisyLast).OutputBytesAsync() with
            | Ok result ->
                Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(result.Stderr, Is.Not.Empty)
                Assert.That(Encoding.UTF8.GetByteCount result.Stderr, Is.LessThanOrEqualTo cap)
                Assert.That(result.Truncated, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``streaming Finish reports representative pipefail stderr truncation``() : Task =
        task {
            let cap = 16

            for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest ] do
                let policy = (OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow overflow

                let noisy = noisyFailingStage 50 None |> Command.outputBuffer policy

                match! (noisy.Pipe sortStage).StartAsync() with
                | Error error -> Assert.Fail $"streaming start failed for {overflow}: {error}"
                | Ok session ->
                    use session = session
                    let! _ = collect (session.StdoutLinesAsync())

                    match! session.FinishAsync() with
                    | Ok finished ->
                        Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 3))
                        Assert.That(finished.Stderr, Is.Not.Empty)
                        Assert.That(Encoding.UTF8.GetByteCount finished.Stderr, Is.LessThanOrEqualTo cap)
                        Assert.That(finished.Truncated, Is.True)
                    | Error error -> Assert.Fail $"streaming finish failed for {overflow}: {error}"
        }
        :> Task

    [<Test>]
    member _.``a chatty stage's stderr is bounded by its own OutputBuffer byte cap``() : Task =
        task {
            let cap = 64
            let noisy = noisyFailingStage 50 (Some cap)
            let pipeline = noisy.Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                Assert.That(
                    result.Outcome,
                    Is.EqualTo(Outcome.Exited 3),
                    "the noisy failing stage must be the pipefail representative carrying the capped stderr"
                )

                let retainedBytes = Encoding.UTF8.GetByteCount result.Stderr
                Assert.That(retainedBytes, Is.LessThanOrEqualTo cap, "retained stderr must never exceed its byte cap")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a stage without an OutputBuffer cap keeps its stderr unbounded, as before``() : Task =
        task {
            let noisy = noisyFailingStage 50 None
            let pipeline = noisy.Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))

                let retainedLines =
                    result.Stderr.Split('\n')
                    |> Array.filter (fun l -> l.Trim().Length > 0)
                    |> Array.length

                Assert.That(retainedLines, Is.EqualTo 50, "an uncapped stage's stderr must retain every line")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- A fail-loud (`OverflowMode.Error`) stderr overflow on ANY stage surfaces OutputTooLarge (T-062) ---

    [<Test>]
    member _.``an intermediate stage's fail-loud stderr overflow surfaces OutputTooLarge naming that stage``() : Task =
        task {
            // Stage 0's stderr overflows its own fail-loud byte cap; the last stage (sort) is uncapped and
            // exits 0, so nothing else can fail the run — the surfaced error must be stage 0's stderr.
            let cap = 16

            let noisy =
                stderrStage
                    50
                    (OutputBufferPolicy.Unbounded.WithMaxLines(1).WithMaxBytes(cap).WithOverflow OverflowMode.Error)

            match! (noisy.Pipe sortStage).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, lineLimit, byteLimit, totalLines, totalBytes)) ->
                Assert.That(program, Is.EqualTo noisy.Program, "the error must name the overflowing stage")
                Assert.That(lineLimit, Is.EqualTo(None), "a raw stderr capture does not enforce MaxLines")
                Assert.That(byteLimit, Is.EqualTo(Some cap), "the limit must be the offending stage's own cap")
                Assert.That(totalLines, Is.EqualTo 0, "a raw stderr byte capture has no line structure")
                Assert.That(totalBytes, Is.GreaterThan cap, "the totals must reflect the overflow past the cap")
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``the last stage's fail-loud stderr overflow surfaces OutputTooLarge naming the last stage``() : Task =
        task {
            // The last stage overflows its OWN stderr cap (its stdout stays empty, so the final-stdout path
            // is not what trips); the run must still fail loud, naming the last stage's own cap.
            let cap = 16

            let noisyLast =
                stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.Error)

            match! ((emit [ "banana"; "apple" ]).Pipe noisyLast).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, _, byteLimit, _, totalBytes)) ->
                Assert.That(program, Is.EqualTo noisyLast.Program)
                Assert.That(byteLimit, Is.EqualTo(Some cap), "the last stage's own stderr cap must be reported")
                Assert.That(totalBytes, Is.GreaterThan cap)
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``an earlier stage's stderr overflow outranks a simultaneous final-stdout overflow``() : Task =
        task {
            // first-offending-stage-in-pipeline-order: stage 0's stderr AND the last stage's stdout both
            // trip their fail-loud caps at once; the leftmost stage (stage 0's stderr) must win, reported
            // with ITS cap (16) and program — never the last stage's stdout cap (3).
            let stderrCap = 16
            let stdoutCap = 3

            let stage0 =
                dualStreamStage
                    [ "banana"; "apple" ]
                    50
                    ((OutputBufferPolicy.Unbounded.WithMaxBytes stderrCap).WithOverflow OverflowMode.Error)

            let last =
                sortStage
                |> Command.outputBuffer (
                    (OutputBufferPolicy.Unbounded.WithMaxBytes stdoutCap).WithOverflow OverflowMode.Error
                )

            match! (stage0.Pipe last).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, _, byteLimit, _, _)) ->
                Assert.That(program, Is.EqualTo stage0.Program, "the leftmost offending stage must be blamed")

                Assert.That(
                    byteLimit,
                    Is.EqualTo(Some stderrCap),
                    "the leftmost stage's stderr cap must be reported, not the final stdout's"
                )
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a DropOldest/DropNewest stderr overflow on a stage stays lossy, never an error``() : Task =
        task {
            let mutable okCount = 0

            // The same stderr flood that fails loud under Error stays lossy-but-Ok under a drop mode —
            // no new Error path for the bounded drop modes.
            for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest ] do
                let noisy =
                    stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes 16).WithOverflow overflow)

                match! (noisy.Pipe sortStage).OutputBytesAsync() with
                | Ok _ -> okCount <- okCount + 1
                | Error error -> Assert.Fail $"a dropping-mode stderr overflow must not error, got {error}"

            Assert.That(okCount, Is.EqualTo 2, "both drop modes must succeed without a new Error path")
        }
        :> Task

    [<Test>]
    member _.``pipefail fails the pipeline on a checked stage's non-zero exit``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.RunAsync() with
            | Error(ProcessError.Exit(_, 3, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected Exit 3, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputString keeps a non-zero pipefail exit as data``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.OutputStringAsync() with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``UncheckedInPipe lets a stage fail without failing the pipeline``() : Task =
        task {
            let failing = (shell "exit 3").UncheckedInPipe()
            let pipeline = failing.Pipe(shell "echo done")

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(output, Does.Contain "done")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``an unchecked failing last stage does not fail the pipeline``() : Task =
        task {
            let failingLast = (shell "exit 5").UncheckedInPipe()
            let pipeline = (emit [ "x" ]).Pipe failingLast

            match! pipeline.RunAsync() with
            | Ok _ -> Assert.Pass()
            | Error error -> Assert.Fail $"expected success (last stage unchecked), got {error}"
        }
        :> Task

    [<Test>]
    member _.``buffered pipeline surfaces an upstream relay read fault and leaves normal runs healthy``() : Task =
        task {
            let upstream = shell "exit 0"
            let pipeline = upstream.Pipe sortStage
            let faultMessage = "injected upstream relay read failure"

            PipelineRunner.relaySourceTestHook <-
                Some(fun _ _ ->
                    new RelayReadFaultStream(Encoding.UTF8.GetBytes "relay-payload\n", faultMessage) :> Stream)

            try
                match! pipeline.OutputStringAsync() with
                | Error(ProcessError.Io detail) ->
                    Assert.That(detail, Does.Contain upstream.Program)
                    Assert.That(detail, Does.Contain faultMessage)
                | Error error -> Assert.Fail $"expected ProcessError.Io, got {error}"
                | Ok output -> Assert.Fail $"a relay read fault must not become successful truncated output: {output}"
            finally
                PipelineRunner.relaySourceTestHook <- None

            // The faulted chain has completed both public stages before returning. A fresh real pipeline
            // must still transfer data and reap normally, catching a lost Task.WhenAll/reap lifecycle.
            match! (emit [ "banana"; "apple" ]).Pipe(sortStage).OutputStringAsync() with
            | Ok output -> Assert.That(lines output.Stdout, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"a later normal pipeline failed after relay cleanup: {error}"
        }
        :> Task

    [<Test>]
    member _.``streaming pipeline FinishAsync surfaces an upstream relay read fault and cleans up``() : Task =
        task {
            let upstream = shell "exit 0"
            let pipeline = upstream.Pipe sortStage
            let faultMessage = "injected upstream relay read failure"

            PipelineRunner.relaySourceTestHook <-
                Some(fun _ _ ->
                    new RelayReadFaultStream(Encoding.UTF8.GetBytes "relay-payload\n", faultMessage) :> Stream)

            try
                match! pipeline.StartAsync() with
                | Error error -> Assert.Fail $"streaming pipeline failed to start: {error}"
                | Ok session ->
                    use session = session
                    let! streamed = collect (session.StdoutLinesAsync())

                    let got =
                        streamed
                        |> Seq.map (fun line -> line.Trim())
                        |> Seq.filter (fun line -> line.Length > 0)
                        |> Seq.toList

                    // The streamed prefix is best-effort by design: the relay fault hard-kills the whole
                    // chain the moment it is seen (T-343), so the downstream stage may be torn down before
                    // it flushes the payload it did receive. Whatever arrives must still be that payload
                    // and nothing else — a truncated stream, never a different or extra one.
                    Assert.That(
                        got,
                        Is.EqualTo(box List.empty<string>).Or.EqualTo(box [ "relay-payload" ]),
                        "the streamed output is a (possibly empty) prefix of the relayed payload"
                    )

                    match! session.FinishAsync() with
                    | Error(ProcessError.Io detail) ->
                        Assert.That(detail, Does.Contain upstream.Program)
                        Assert.That(detail, Does.Contain faultMessage)
                    | Error error -> Assert.Fail $"expected ProcessError.Io from FinishAsync, got {error}"
                    | Ok finished ->
                        Assert.Fail $"FinishAsync must not accept a downstream-successful truncated stream: {finished}"
            finally
                PipelineRunner.relaySourceTestHook <- None

            // Dispose/FinishAsync must have reaped the whole chain; prove the next public run is clean.
            match! (emit [ "cherry"; "apple" ]).Pipe(sortStage).OutputStringAsync() with
            | Ok output -> Assert.That(lines output.Stdout, Is.EqualTo(box [ "apple"; "cherry" ]))
            | Error error -> Assert.Fail $"a later normal pipeline failed after streaming cleanup: {error}"
        }
        :> Task

    [<Test>]
    member _.``a relay read fault tears down a silent upstream instead of waiting for its natural exit``() : Task =
        // Regression (T-343). Stage 0 never writes and would run ~30s, so it can never die of a broken pipe
        // when the relay closes the pipe ends after its read fails. The old phase order — wait for every
        // stage's terminal `Outcome`, THEN read the relay verdicts — therefore sat on stage 0's natural exit
        // while holding an already-diagnosed `ProcessError.Io`, and with no chain deadline set it would have
        // sat there forever. The completion-order choke must instead tear the chain down at once.
        task {
            let faultMessage = "injected relay read failure with a live silent upstream"

            PipelineRunner.relaySourceTestHook <-
                Some(fun _ _ -> new RelayReadFaultStream(Array.empty, faultMessage) :> Stream)

            try
                let pipeline = slowSilentStage.Pipe sortStage
                let run = pipeline.OutputStringAsync()
                do! assertFinishesPromptly run

                match! run with
                | Error(ProcessError.Io detail) ->
                    Assert.That(detail, Does.Contain faultMessage)
                    Assert.That(detail, Does.Contain slowSilentStage.Program)
                | Error error -> Assert.Fail $"expected ProcessError.Io, got {error}"
                | Ok result -> Assert.Fail $"a relay read fault must not become a successful run: {result.Outcome}"
            finally
                PipelineRunner.relaySourceTestHook <- None

            // The torn-down chain must also have been reaped and its pipes closed: a fresh real pipeline
            // still transfers data and completes normally afterwards.
            match! (emit [ "banana"; "apple" ]).Pipe(sortStage).OutputStringAsync() with
            | Ok output -> Assert.That(lines output.Stdout, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"a later normal pipeline failed after the relay teardown: {error}"
        }
        :> Task

    [<Test>]
    member _.``a streaming pipeline tears down a silent upstream when the relay read faults``() : Task =
        // The streaming session must not have its own, slower answer to the same race: `FinishAsync` goes
        // through the SAME completion-order choke, so it completes promptly with the relay's own
        // `ProcessError.Io` instead of waiting out the silent stage-0 producer.
        task {
            let faultMessage =
                "injected streaming relay read failure with a live silent upstream"

            PipelineRunner.relaySourceTestHook <-
                Some(fun _ _ -> new RelayReadFaultStream(Array.empty, faultMessage) :> Stream)

            try
                match! (slowSilentStage.Pipe sortStage).StartAsync() with
                | Error error -> Assert.Fail $"streaming pipeline failed to start: {error}"
                | Ok session ->
                    use session = session
                    let finish = session.FinishAsync()
                    do! assertFinishesPromptly finish

                    match! finish with
                    | Error(ProcessError.Io detail) ->
                        Assert.That(detail, Does.Contain faultMessage)
                        Assert.That(detail, Does.Contain slowSilentStage.Program)
                    | Error error -> Assert.Fail $"expected ProcessError.Io from FinishAsync, got {error}"
                    | Ok finished -> Assert.Fail $"a relay read fault must not become a successful session: {finished}"
            finally
                PipelineRunner.relaySourceTestHook <- None

            match! (emit [ "cherry"; "apple" ]).Pipe(sortStage).OutputStringAsync() with
            | Ok output -> Assert.That(lines output.Stdout, Is.EqualTo(box [ "apple"; "cherry" ]))
            | Error error -> Assert.Fail $"a later normal pipeline failed after the streaming relay teardown: {error}"
        }
        :> Task

    [<Test>]
    member _.``a later relay fault is not held up by an earlier stage that is still running``() : Task =
        // Three stages, the first two silent ~30s producers. The relay between stages 1 and 2 fails while
        // BOTH earlier stages are still pending, so nothing about this fault can be reached by draining the
        // stage exits first — the choke has to see it among them. The earlier relay (stage 0 -> 1) is left
        // as real plumbing and simply ends at EOF once the teardown kills its producer.
        task {
            let faultMessage = "injected relay read failure on the later relay"

            PipelineRunner.relaySourceTestHook <-
                Some(fun upstreamIndex upstream ->
                    if upstreamIndex = 1 then
                        new RelayReadFaultStream(Array.empty, faultMessage) :> Stream
                    else
                        upstream)

            try
                let pipeline = slowSilentStage.Pipe(slowSilentStage).Pipe(sortStage)
                let run = pipeline.OutputStringAsync()
                do! assertFinishesPromptly run

                match! run with
                | Error(ProcessError.Io detail) -> Assert.That(detail, Does.Contain faultMessage)
                | Error error -> Assert.Fail $"expected ProcessError.Io, got {error}"
                | Ok result -> Assert.Fail $"a relay read fault must not become a successful run: {result.Outcome}"
            finally
                PipelineRunner.relaySourceTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``cancellation keeps priority over a relay fault raised during the teardown``() : Task =
        // A relay exception raised into a teardown that has ALREADY begun is a routine race, not a genuine
        // failure — so the whole-chain cancellation still decides the result. Deterministic: cancelling the
        // token runs the chain's registration synchronously, so the teardown is in flight before the test
        // releases the parked read.
        task {
            let release =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let source =
                new GatedRelayFaultStream(release.Task, "relay fault raised into an in-flight teardown")

            PipelineRunner.relaySourceTestHook <- Some(fun _ _ -> source :> Stream)

            try
                use cts = new CancellationTokenSource()
                let run = (slowSilentStage.Pipe sortStage).OutputStringAsync(cts.Token)

                // The relay is parked in the injected source; nothing has faulted yet.
                do! awaitSignal "the relay must reach the injected source" source.Parked
                cts.Cancel() // ... and the chain's teardown has begun by the time this returns
                release.SetResult() // only NOW let the read fail

                do! assertFinishesPromptly run

                match! run with
                | Error(ProcessError.Cancelled _) -> Assert.Pass()
                | Error error -> Assert.Fail $"expected Cancelled to outrank the teardown-race relay fault, got {error}"
                | Ok result -> Assert.Fail $"expected Cancelled, got {result.Outcome}"
            finally
                PipelineRunner.relaySourceTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``the chain deadline keeps priority over a relay fault raised during its teardown``() : Task =
        // The timeout counterpart of the test above, and the end-to-end form of "the verdict is fixed at the
        // last stage exit": the 300ms deadline tears the chain down and settles the verdict while the relay
        // is still parked, and the fault released a full 2s later (a ~7x margin over the deadline) is a
        // teardown race that neither reports `Io` nor hangs the run past the relay's own completion.
        task {
            let release =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let source =
                new GatedRelayFaultStream(release.Task, "relay fault raised into the deadline teardown")

            PipelineRunner.relaySourceTestHook <- Some(fun _ _ -> source :> Stream)

            try
                let pipeline =
                    (slowSilentStage.Pipe sortStage).Timeout(TimeSpan.FromMilliseconds 300.0)

                let run = pipeline.OutputStringAsync()

                do! awaitSignal "the relay must reach the injected source" source.Parked
                do! Task.Delay(TimeSpan.FromSeconds 2.0)
                release.SetResult()

                do! assertFinishesPromptly run

                match! run with
                | Ok result -> Assert.That(result.Outcome, Is.EqualTo Outcome.TimedOut)
                | Error error ->
                    Assert.Fail $"expected the deadline to outrank the teardown-race relay fault, got {error}"
            finally
                PipelineRunner.relaySourceTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``an early-exiting consumer does not hang the producer``() : Task =
        task {
            if isWindows then
                // Windows pipes have no SIGPIPE: a producer that ignores write errors cannot be
                // unblocked by closing the read end, so this guarantee is POSIX-only.
                Assert.Ignore "POSIX-only (no SIGPIPE on Windows)"
            else
                // `yes` writes forever; `head -n 1` reads one line and exits. Closing the read end
                // must SIGPIPE `yes` so the pipeline completes instead of blocking on a full pipe.
                let yes = (Command.create "yes").UncheckedInPipe()
                let head = Command.create "head" |> Command.args [ "-n"; "1" ]
                let pipeline = (yes.Pipe head).Timeout(TimeSpan.FromSeconds 15.0)

                match! pipeline.RunAsync() with
                | Ok output -> Assert.That(output.Trim(), Is.EqualTo "y")
                | Error error -> Assert.Fail $"expected the pipeline to complete, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a failed downstream stage tears down a silent upstream instead of waiting for pipe EOF``() : Task =
        task {
            // Stage 0 never writes and would run ~30s; the relay copying its (empty) stdout blocks
            // indefinitely, and a producer that never writes never dies of a broken pipe. Stage 1 fails
            // fast (checked). Before proactive teardown the chain hung on stage 0's natural exit; now the
            // checked failure kills the whole chain at once — the pipefail representative stays stage 1.
            let pipeline = slowSilentStage.Pipe(shell "exit 7")
            let run = pipeline.OutputStringAsync()
            do! assertFinishesPromptly run

            match! run with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 7))
            | Error error -> Assert.Fail $"expected the failing checked stage as data, got {error}"
        }
        :> Task

    [<Test>]
    member _.``an upstream checked failure tears down a slow downstream but still blames the upstream``() : Task =
        task {
            // Stage 0 fails fast (checked exit 3). Stage 1 ignores its stdin and would run ~30s, so a
            // teardown victim's signal-kill lands to the RIGHT of the real failure. The pipefail result
            // must still be the upstream's exit 3, proving the torn-down downstream never steals blame.
            let pipeline = (shell "exit 3").Pipe slowSilentStage
            let run = pipeline.OutputStringAsync()
            do! assertFinishesPromptly run

            match! run with
            | Ok result ->
                Assert.That(
                    result.Outcome,
                    Is.EqualTo(Outcome.Exited 3),
                    "pipefail blames the upstream's real failure, not the torn-down downstream victim"
                )
            | Error error -> Assert.Fail $"expected exit 3 as data, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout reports TimedOut and fails Run``() : Task =
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                (emit [ "hi" ]).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout error preserves its configured duration through delayed drain``() : Task =
        task {
            let configured = TimeSpan.FromMilliseconds 200.0
            use tee = new BlockingTee()

            let last =
                (if isWindows then
                     shell "echo ready&ping -n 6 127.0.0.1 >nul"
                 else
                     shell "echo ready; sleep 5")
                |> Command.stdoutTee tee

            let pipeline = ((emit [ "input" ]).Pipe last).Timeout configured
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let runTask = pipeline.OutputStringAsync()

            try
                do! tee.FirstWrite.WaitAsync(TimeSpan.FromSeconds 3.0)
                do! Task.Delay 700
            finally
                tee.Release()

            match! runTask with
            | Ok result ->
                stopwatch.Stop()
                Assert.That(result.IsTimedOut, Is.True)
                Assert.That(result.Duration, Is.GreaterThan configured)
                Assert.That(stopwatch.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds 600.0))

                match result.EnsureSuccess() with
                | Error(ProcessError.Timeout(program, actual, _, _) as error) ->
                    Assert.That(actual, Is.EqualTo configured)
                    Assert.That(error.Message, Is.EqualTo($"'{program}' timed out after {configured.TotalSeconds}s"))
                | other -> Assert.Fail $"expected Timeout, got {other}"
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a cancelled token cancels the whole pipeline``() : Task =
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline = (emit [ "hi" ]).Pipe sleeper
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync cts.Token with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``cancellation between stage spawns starts no later stage and reaps promptly``() : Task =
        // Regression (T-061): the staging loop registered ONE `linkedCts` callback that KillTree's the
        // stages then running, but never re-checked the token between spawns. `KillTree` leaves the group
        // usable, so a cancellation landing in the window between two spawns killed the running stages,
        // then the loop spawned the NEXT stage right afterwards — a stage the one-shot sweep never
        // targeted, which therefore outlived the pipeline (the caller either lost track of it or blocked
        // on its natural exit). The seam fires cancellation in exactly that window; the fix must start no
        // later stage and return a prompt `Cancelled`, never wait on the long-running escapee.
        task {
            let longStage =
                if isWindows then
                    shell "ping -n 30 127.0.0.1 >nul"
                else
                    shell "sleep 30"

            // Two long stages: without the fix, stage 1 (spawned right after the sweep) escapes and the
            // run blocks ~30s on its natural exit; with the fix it never starts and the run is prompt.
            let pipeline = longStage.Pipe longStage
            use cts = new CancellationTokenSource()

            // Fire cancellation the instant stage 0 has spawned — i.e. between the two spawns, the exact
            // race window. `Cancel` runs the linked KillTree callback inline before the loop reaches
            // stage 1, reproducing "the sweep fired, now the loop wants to start the next stage".
            PipelineRunner.stageSpawnedTestHook <-
                Some(fun index ->
                    if index = 0 then
                        cts.Cancel())

            try
                let run = pipeline.RunAsync cts.Token
                // Won the race against a 15s deadline (far below the 30s stage): a stage started after the
                // sweep would have escaped it and blocked the run on its ~30s natural exit.
                do! assertFinishesPromptly run

                match! run with
                | Error(ProcessError.Cancelled _) -> Assert.Pass()
                | other -> Assert.Fail $"expected a prompt Cancelled once staging was cancelled, got {other}"
            finally
                PipelineRunner.stageSpawnedTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``Pipeline module builders compose a pipeline``() : Task =
        task {
            // Builders pipe (module); the terminal verb is an instance method.
            let pipeline =
                Pipeline.create (emit [ "banana"; "apple" ]) sortStage
                |> Pipeline.timeout (TimeSpan.FromSeconds 30.0)

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline parse converts the trimmed pipefail output to a typed value``() : Task =
        task {
            let pipeline = (emit [ "42" ]).Pipe sortStage

            match! pipeline.ParseAsync(fun s -> int (s.Trim())) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline tryParse uses the TryParser delegate and maps a thrown parser to Parse``() : Task =
        task {
            let pipeline = (emit [ "42" ]).Pipe sortStage

            // Success: the C#-friendly delegate parses the trimmed pipefail output.
            let tryInt =
                TryParser(fun (s: string) (v: byref<int>) -> Int32.TryParse(s.Trim(), &v))

            match! pipeline.TryParseAsync tryInt with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"

            // A parser that throws is surfaced as ProcessError.Parse, not a faulted task.
            let tryThrow = TryParser(fun (_: string) (_: byref<int>) -> failwith "boom")

            match! pipeline.TryParseAsync tryThrow with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    // --- Fail-fast: per-stage config a pipeline cannot honour is rejected when the stage is piped
    //     (T-012). A pipeline spawns stages directly and rewires stdin, so these settings never took
    //     effect; rejecting at build time replaces the previous silent drop. ---

    [<Test>]
    member _.``a Stdin source on a stage after the first is rejected when piped``() =
        let withStdin () =
            sortStage |> Command.stdin (Stdin.FromString "y\n")

        // The two-argument Pipe: the appended (second) stage — index 1 — carries a source.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withStdin ()) |> ignore))
        |> ignore

        // A later stage appended through Pipeline.Pipe — index 2 — is caught the same way.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(sortStage).Pipe(withStdin ()) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage Timeout is rejected on any stage when piped``() =
        let withTimeout cmd =
            cmd |> Command.timeout (TimeSpan.FromSeconds 5.0)

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withTimeout (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withTimeout sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage IdleTimeout is rejected on any stage when piped``() =
        // A pipeline captures only the last stage's output and does not monitor per-stage output
        // activity, so a stage's own idle deadline could never fire — reject it at build time (like the
        // per-stage Timeout) rather than silently dropping it.
        let withIdle cmd =
            cmd |> Command.idleTimeout (TimeSpan.FromSeconds 2.0)

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withIdle (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withIdle sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage Retry is rejected when piped``() =
        let withRetry cmd =
            cmd |> Command.retry 3 (TimeSpan.FromMilliseconds 10.0) (fun _ -> true)

        // On the first stage...
        Assert.Throws<ArgumentException>(Action(fun () -> (withRetry (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withRetry sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage CancelOn is rejected on any stage when piped``() =
        use cts = new CancellationTokenSource()

        let withCancelOn cmd = cmd |> Command.cancelOn cts.Token

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withCancelOn (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withCancelOn sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``KeepStdinOpen is rejected on any stage when piped``() =
        let keepOpen (command: Command) = command.KeepStdinOpen()

        // Regression: stage 0 has no Stdin source, so accepting KeepStdinOpen used to leave its pipe
        // open forever with no writer and hang the chain instead of delivering EOF.
        match
            Assert.Throws<ArgumentException>(Action(fun () -> (keepOpen (emit [ "x" ])).Pipe sortStage |> ignore))
        with
        | null -> Assert.Fail("Expected KeepStdinOpen to be rejected on pipeline stage 0")
        | stage0Error ->
            Assert.That(stage0Error.Message, Does.Contain("pipeline stage 0"))
            Assert.That(stage0Error.Message, Does.Contain((emit [ "x" ]).Program))
            Assert.That(stage0Error.Message, Does.Contain("KeepStdinOpen"))

        // The initial Pipe extension rejects the second stage (index 1) too.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(keepOpen sortStage) |> ignore))
        |> ignore

        // Appending through Pipeline.Pipe exercises the instance builder for a still-later stage.
        Assert.Throws<ArgumentException>(
            Action(fun () -> (emit [ "x" ]).Pipe(sortStage).Pipe(keepOpen sortStage) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``stage zero owns a pipeline custom StopSignal and later stages cannot override it``() =
        // A pipeline has one containment group and therefore one soft-signal broadcast. Stage 0 owns
        // that pipeline-wide setting, like its logger; accepting a later override would silently ignore it.
        (emit [ "x" ] |> Command.stopSignal Signal.Usr1).Pipe(sortStage) |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> (emit [ "x" ]).Pipe(sortStage |> Command.stopSignal Signal.Usr1) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a pipeline rejects per-stage stdout destinations it would otherwise replace``() =
        let withFile command =
            command |> Command.stdoutToFile "pipeline-output.txt" false

        let withNull command =
            command |> Command.stdout StdioMode.Null

        let withInherited command =
            command |> Command.stdout StdioMode.Inherit

        // Validate both the first and an appended stage, so the public two-command builder and
        // Pipeline.Pipe cannot silently reset a destination to Piped.
        for configure in [ withFile; withNull; withInherited ] do
            Assert.Throws<ArgumentException>(Action(fun () -> (configure (emit [ "x" ])).Pipe sortStage |> ignore))
            |> ignore

            Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(configure sortStage) |> ignore))
            |> ignore

    [<Test>]
    member _.``a pipeline honours per-stage StderrToFile while preserving stdout wiring``() : Task =
        task {
            let path =
                Path.Combine(Path.GetTempPath(), $"processkit-pipeline-stderr-{Guid.NewGuid():N}.txt")

            let first =
                if isWindows then
                    shell "echo payload & echo diagnostic 1>&2"
                else
                    shell "printf 'payload\\n'; printf 'diagnostic\\n' >&2"

            try
                let pipeline = (first |> Command.stderrToFile path false).Pipe sortStage

                match! pipeline.OutputStringAsync() with
                | Ok output ->
                    Assert.That(lines output.Stdout, Is.EqualTo(box [ "payload" ]))
                    Assert.That(File.ReadAllText path, Does.Contain "diagnostic")
                | Error error -> Assert.Fail $"{error}"
            finally
                if File.Exists path then
                    File.Delete path
        }
        :> Task

    [<Test>]
    member _.``chain-level Pipeline.CancelOn cancels the whole pipeline``() : Task =
        // The chain-level builder is a distinct, un-guarded method (unlike a per-stage Command.CancelOn),
        // so it must keep cancelling the whole chain exactly as before.
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)
            let pipeline = ((emit [ "hi" ]).Pipe sleeper).CancelOn cts.Token

            match! pipeline.RunAsync() with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a Stdin source on stage 0 stays allowed and feeds the whole chain``() : Task =
        task {
            // Regression: only stages AFTER the first reject a source; stage 0 feeds the chain.
            let pipeline =
                (sortStage |> Command.stdin (Stdin.FromString "banana\napple\n")).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- T-069: the pipeline call site of the stdin feeder (stage 0's source) ---

    [<Test>]
    member _.``a pipeline stops a hung stage-0 async stdin feed and disposes its enumerator``() : Task =
        task {
            // Stage 0 exits at once without reading stdin, so the `FromAsyncLines` feed is left parked
            // in `MoveNextAsync`. When the pipeline reaches its terminal state it must Stop the feeder —
            // cancelling the hung feed and disposing the user's enumerator — rather than leaking it.
            let source = HangingStdinAsyncLines()

            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).Pipe sortStage

            match! pipeline.OutputStringAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"expected a successful pipeline, got {error}"

            // The run tore down, so the feeder was stopped: its enumerator disposes shortly after.
            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(completed, Is.SameAs source.Disposed, "the hung stage-0 async enumerator was never disposed")
        }
        :> Task

    [<Test>]
    member _.``a full-spawn cancelled pipeline stops a hung stage-0 async stdin feed``() : Task =
        task {
            // The terminal stdout tee holds the post-reap drain open. Cancellation therefore reaches the
            // full-spawn completion branch rather than the mid-staging abort path, while the stage-0 feed
            // remains parked in its source's MoveNextAsync.
            use cts = new CancellationTokenSource()
            use tee = new BlockingTee()
            let source = HangingStdinAsyncLines()

            let first = (shell "echo ready") |> Command.stdin (Stdin.FromAsyncLines source)

            let last = sortStage |> Command.stdoutTee tee
            let run = first.Pipe(last).RunAsync cts.Token

            do! source.Started
            do! tee.FirstWrite
            cts.Cancel()
            tee.Release()

            match! run with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected Cancelled after the completed pipeline drain, got {other}"

            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)

            Assert.That(
                completed,
                Is.SameAs source.Disposed,
                "the cancelled pipeline left the async enumerator undisposed"
            )
        }
        :> Task

    [<Test>]
    member _.``a pipeline surfaces a stage-0 async source that faults at GetAsyncEnumerator as ProcessError.Stdin``
        ()
        : Task =
        task {
            // The first stage's source faults acquiring its async enumerator — a stage the pre-fix
            // classification let slip into the benign-broken-pipe bucket. On an otherwise-successful
            // pipeline it must surface as `ProcessError.Stdin`.
            let pipeline =
                ((shell "exit 0")
                 |> Command.stdin (Stdin.FromAsyncLines(FaultyStdinAsyncLines AtGetEnumerator)))
                    .Pipe
                    sortStage

            match! pipeline.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a stage-0 async source fault to surface as ProcessError.Stdin"
        }
        :> Task

    // --- T-329: a stage-0 stdin source that fails only AFTER the chain exits ----------------------
    //
    // The pipeline carried its own copy of the single-command race: it peeked once at stage 0's feeder
    // the moment every stage was terminal, so a source still reading then was read as "no failure" and
    // the chain reported a silent success. Both pipeline paths — the buffered `run` and the streaming
    // session's whole-chain wait — go through the SAME bounded final observation as a single command, so
    // both are pinned here, along with the precedence a pipefail failure keeps over a stdin failure.

    [<Test>]
    member _.``a pipeline surfaces a stage-0 stdin source that fails only after the chain exits``() : Task =
        task {
            // Stage 0 exits at once without reading stdin; the source concludes only when the bounded
            // observation window opens — strictly after every stage is terminal and the drains are done.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use _observation =
                new StdinFinalObservationScope((fun () -> source.Release()), None)

            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).Pipe sortStage

            match! pipeline.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "a stage-0 source failing after the chain exited must not report success"
        }
        :> Task

    [<Test>]
    member _.``a streaming pipeline session surfaces a stage-0 stdin source that fails after the chain exits``
        ()
        : Task =
        task {
            // The session classifies from the whole-chain capture stashed by the chain's own wait, so the
            // observation has to happen there too — otherwise the streaming path keeps the bug the
            // buffered path just lost.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use _observation =
                new StdinFinalObservationScope((fun () -> source.Release()), None)

            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).Pipe sortStage

            match! pipeline.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                let! _ = collect (session.StdoutLinesAsync())

                match! session.FinishAsync() with
                | Error(ProcessError.Stdin _) -> ()
                | Error other -> Assert.Fail $"expected ProcessError.Stdin from FinishAsync, got {other.Message}"
                | Ok _ -> Assert.Fail "a stage-0 source failing after the chain exited must not finish clean"
        }
        :> Task

    [<Test>]
    member _.``a pipefail failure still wins over a stage-0 stdin source that fails after exit``() : Task =
        task {
            // Precedence is unchanged, and decided before the window: the last stage's unaccepted exit is
            // the realer failure, so it passes through as data and no observation window opens at all.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation = new StdinFinalObservationScope((fun () -> source.Release()), None)

            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).Pipe(shell "exit 4")

            match! pipeline.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 4 -> ()
                | other -> Assert.Fail $"expected pipefail exit 4 to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a pipefail failure must win over the delayed stdin failure, not surface Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"

            Assert.That(
                observation.Windows,
                Is.EqualTo 0,
                "a failing pipeline must not pay for the bounded observation window at all"
            )
        }
        :> Task

    [<Test>]
    member _.``a hung stage-0 stdin source cannot hold a pipeline open past the bounded budget``() : Task =
        task {
            // The hung-source bound, on the pipeline path: the source never concludes, so the window must
            // close on its budget, stop the feed, and let the chain report its honest success.
            let source = DelayedStdinAsyncLines FailWhenReleased // never released

            use _observation =
                new StdinFinalObservationScope(ignore, Some(TimeSpan.FromMilliseconds 200.0))

            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).Pipe sortStage

            let run = pipeline.OutputStringAsync()
            do! assertFinishesPromptly run

            match! run with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"a stopped hung stage-0 source must not fail the chain, got {error}"

            let! stopped = Task.WhenAny(source.Cancelled, Task.Delay 15000)
            Assert.That(stopped, Is.SameAs source.Cancelled, "the hung stage-0 source was never stopped")
        }
        :> Task

    [<Test>]
    member _.``a fail-loud stderr overflow costs a pipeline no bounded stdin observation window``() : Task =
        task {
            // A fail-loud output overflow outranks `ProcessError.Stdin` in the pipeline's classification
            // exactly as it does for a single command (which returns `OutputTooLarge` before it ever
            // consults its stdin feeder), so an already-decided overflowing chain must not wait on stage 0's
            // still-reading source for a fault it could never surface. The source is never released: only a
            // gate that honours the overflow step keeps the window count at zero.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation =
                new StdinFinalObservationScope(ignore, Some(TimeSpan.FromMilliseconds 200.0))

            let noisy =
                stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes 16).WithOverflow OverflowMode.Error)
                |> Command.stdin (Stdin.FromAsyncLines source)

            match! (noisy.Pipe sortStage).OutputStringAsync() with
            | Error(ProcessError.OutputTooLarge _) -> ()
            | Error other -> Assert.Fail $"expected OutputTooLarge, got {other.Message}"
            | Ok _ -> Assert.Fail "a fail-loud stderr overflow must fail the pipeline"

            Assert.That(
                observation.Windows,
                Is.EqualTo 0,
                "an overflowing pipeline must not pay for the bounded observation window at all"
            )
        }
        :> Task

    [<Test>]
    member _.``a cancelled streaming pipeline costs no bounded stdin observation window``() : Task =
        task {
            // Whole-chain cancellation is the streaming session's FIRST classification step, above the
            // stage-0 stdin rule, so a cancelled session must not pay the window either. Both stages are
            // unchecked, so the pipefail representative stays "accepted" however the cancellation kill
            // caught them — leaving the cancellation check as the only thing that can keep the window shut.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation =
                new StdinFinalObservationScope(ignore, Some(TimeSpan.FromMilliseconds 200.0))

            use cts = new CancellationTokenSource()

            let first =
                ((shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)).UncheckedInPipe()

            match! (first.Pipe(sortStage.UncheckedInPipe())).StartAsync cts.Token with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                // Parked inside the user's source: the feed is still running, so a window could be opened.
                do! source.Parked
                cts.Cancel()

                match! session.FinishAsync() with
                | Error(ProcessError.Cancelled _) -> ()
                | Error other -> Assert.Fail $"expected Cancelled from FinishAsync, got {other.Message}"
                | Ok _ -> Assert.Fail "a cancelled session must not finish clean"

                Assert.That(
                    observation.Windows,
                    Is.EqualTo 0,
                    "a cancelled pipeline must not pay for the bounded observation window at all"
                )
        }
        :> Task

    // --- Observability: a pipeline run is whole-chain, not per-stage (T-013) ---

    [<Test>]
    member _.``a successful pipeline run logs spawn and exit under one shared run id``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let pipeline =
                ((emit [ "banana"; "apple" ]) |> Command.logger logger).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok _ ->
                Assert.That(logger.Text, Does.Contain "spawned")
                Assert.That(logger.Text, Does.Contain "finished")

                let runIdOf (m: string) =
                    Regex.Match(m, @"run ([0-9a-f]+)").Groups[1].Value

                let spawnLine =
                    logger.Text.Split('\n') |> Array.find (fun l -> l.Contains "spawned")

                let exitLine =
                    logger.Text.Split('\n') |> Array.find (fun l -> l.Contains "finished")

                let spawnRunId = runIdOf spawnLine
                Assert.That(spawnRunId, Is.Not.Empty, "spawn carries a run id")
                Assert.That(runIdOf exitLine, Is.EqualTo spawnRunId, "spawn and exit share the run id")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a successful pipeline run's program label composes every stage's name``() : Task =
        task {
            let logger = PipelineCapturingLogger()
            let stage0 = (emit [ "banana"; "apple" ]) |> Command.logger logger
            let pipeline = stage0.Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok _ -> Assert.That(logger.Text, Does.Contain(stage0.Program + " | " + sortStage.Program))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout is logged and reports TimedOut``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                ((emit [ "hi" ]) |> Command.logger logger).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) -> Assert.That(logger.Text, Does.Contain "timed out")
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``argv is never logged for a pipeline run``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let secretStage =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args [ (if isWindows then "/c" else "-c"); "echo ok"; "--token=SUPERSECRET" ]
                |> Command.logger logger

            let pipeline = secretStage.Pipe sortStage

            let! _ = pipeline.RunAsync()
            Assert.That(logger.Text, Does.Not.Contain "SUPERSECRET")
            Assert.That(logger.Text, Does.Contain "spawned")
        }
        :> Task

    [<Test>]
    member _.``a successful pipeline run emits one runs.started/completed pair and settles active at zero``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.RunAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok _ ->
                Assert.That(started (), Is.EqualTo 1L, "expected exactly one runs.started for the whole chain")
                Assert.That(completed (), Is.EqualTo 1L, "expected exactly one runs.completed for the whole chain")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero")
        }
        :> Task

    [<Test>]
    member _.``a timed-out pipeline still settles runs.active at zero without counting extra completions``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                (emit [ "hi" ]).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) ->
                Assert.That(started (), Is.EqualTo 1L)
                Assert.That(completed (), Is.EqualTo 1L, "a timed-out run still reaches a terminal state")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero on timeout")
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a partial spawn failure past stage 0 clears runs.active without counting as completed``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Stage 0 spawns fine; the second stage's program does not exist, so the chain fails to
            // spawn fully. `runs.started`/`runs.active` were already armed by stage 0 — they must be
            // closed, but this must never count as `runs.completed` (a spawn failure, not a run that
            // reached a terminal verb).
            let pipeline =
                (shell "echo hello").Pipe(Command.create "pk-definitely-not-a-program-xyz")

            match! pipeline.RunAsync() with
            | Error _ ->
                Assert.That(started (), Is.EqualTo 1L, "stage 0 did spawn, so the chain counts as started")
                Assert.That(completed (), Is.EqualTo 0L, "a spawn failure must not count as completed")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must not leak on a partial spawn")
            | Ok _ -> Assert.Fail "expected an error from the missing pipeline stage"
        }
        :> Task

    [<Test>]
    member _.``a stage-0 spawn failure emits no run metrics at all``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Stage 0 itself never spawns, so — mirroring a single command's own spawn failure never
            // counting as a run — no `runs.started`/`runs.active` mark is ever armed for this pipeline.
            let pipeline =
                (Command.create "pk-definitely-not-a-program-xyz").Pipe(shell "echo hello")

            match! pipeline.RunAsync() with
            | Error _ ->
                Assert.That(started (), Is.EqualTo 0L, "stage 0 never spawned, so the run never started")
                Assert.That(completed (), Is.EqualTo 0L)
                Assert.That(activeDeltas |> Seq.isEmpty, Is.True, "no runs.active mark was ever armed")
            | Ok _ -> Assert.Fail "expected an error from the missing stage-0 program"
        }
        :> Task

    // --- Causally-stable terminal classification: the timeout verdict and the teardown-victim labelling
    //     are decided by the stages' real completion order at the deadline, not by the order stray wait
    //     continuations happen to run in (T-071) — and the inter-stage relays are raced in that SAME
    //     completion-order choke, so a genuine relay fault tears the chain down the moment it is seen
    //     rather than sitting behind a still-pending stage (T-343). These drive
    //     `PipelineRunner.observeChain` directly with controllable wait/relay tasks, so every race
    //     reproduces deterministically with no wall-clock timing. ---

    [<Test>]
    member _.``observeChain does not flip a settled success when the deadline fires after all stages exit``() : Task =
        // The core of the timeout-after-success race: every stage exits cleanly BEFORE the deadline, so the
        // verdict is fixed as "not timed out" and the timer is disarmed — a deadline that only fires during
        // the later slow tee/drain (modelled by flipping `timerFired` afterwards) can no longer time it out.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let w1 = TaskCompletionSource<Outcome>()
            let mutable timerFired = false
            let mutable disarmed = 0
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task; w1.Task |]
                    Array.empty
                    checkedFailure
                    (fun () -> kills <- kills + 1)
                    (fun () -> false)
                    (fun () -> timerFired)
                    (fun () -> disarmed <- disarmed + 1)

            w0.SetResult(Outcome.Exited 0)
            w1.SetResult(Outcome.Exited 0)

            let! result = observation

            // The deadline fires only now — after the run has already settled (the slow-tee window).
            timerFired <- true

            Assert.That(result.TimedOut, Is.False, "a deadline firing after the run settled must not time it out")
            Assert.That(result.TornDown, Is.EqualTo(box [| false; false |]))
            Assert.That(disarmed, Is.EqualTo 1, "the deadline timer is disarmed once every stage is terminal")
            Assert.That(kills, Is.EqualTo 0, "a fully successful chain never fires the proactive teardown")
        }
        :> Task

    [<Test>]
    member _.``observeChain reports TimedOut when the deadline had already fired at terminal``() : Task =
        // The contrasting genuine timeout: the deadline caught the stages still running, so the whole chain
        // is being torn down and the verdict sampled at terminal is TimedOut.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let w1 = TaskCompletionSource<Outcome>()
            let mutable disarmed = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task; w1.Task |]
                    Array.empty
                    checkedFailure
                    (fun () -> ())
                    (fun () -> true) // the whole chain is already being torn down by the timeout
                    (fun () -> true) // the deadline has fired
                    (fun () -> disarmed <- disarmed + 1)

            w0.SetResult Outcome.TimedOut
            w1.SetResult Outcome.TimedOut

            let! result = observation

            Assert.That(
                result.TimedOut,
                Is.True,
                "a deadline that fired before all stages finished times the chain out"
            )

            Assert.That(disarmed, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``observeChain blames the checked failure seen first and torns down the later sibling``() : Task =
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let w1 = TaskCompletionSource<Outcome>()
            let teardown = TaskCompletionSource<unit>()
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task; w1.Task |]
                    Array.empty
                    checkedFailure
                    (fun () ->
                        kills <- kills + 1
                        teardown.TrySetResult() |> ignore)
                    (fun () -> false)
                    (fun () -> false)
                    (fun () -> ())

            // Stage 0 (left) is SEEN first: it is the genuine culprit and fires the teardown.
            w0.SetResult(Outcome.Exited 3)
            do! teardown.Task // the teardown has fired before stage 1 is even observed

            // Stage 1 (right) is only seen after the teardown fired -> it is the victim.
            w1.SetResult(Outcome.Exited 3)

            let! result = observation
            Assert.That(result.TornDown[0], Is.False, "the failure seen first is the culprit, never a victim")
            Assert.That(result.TornDown[1], Is.True, "a sibling seen only after the teardown fired is a victim")
            Assert.That(kills, Is.EqualTo 1, "the proactive teardown fires exactly once")
        }
        :> Task

    [<Test>]
    member _.``observeChain fixes the victim by real completion order, not by stage position``() : Task =
        // Regression (T-071): the torn-down victim was decided by reading a shared teardown token AFTER each
        // stage's wait continuation, so a stage that actually finished BEFORE a sibling's failure could be
        // mislabelled a victim purely because its continuation ran later — and pipefail would then blame the
        // wrong stage. Observing exits in real completion order fixes it: whichever checked failure is SEEN
        // first is the culprit, regardless of its position — here the RIGHTMOST stage finishes first.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let w1 = TaskCompletionSource<Outcome>()
            let teardown = TaskCompletionSource<unit>()
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task; w1.Task |]
                    Array.empty
                    checkedFailure
                    (fun () ->
                        kills <- kills + 1
                        teardown.TrySetResult() |> ignore)
                    (fun () -> false)
                    (fun () -> false)
                    (fun () -> ())

            // Stage 1 (RIGHT) finishes first this time and is the culprit, even though it is the last stage.
            w1.SetResult(Outcome.Exited 3)
            do! teardown.Task
            // Stage 0 (left) is seen only after the teardown fired -> it is the torn-down victim.
            w0.SetResult(Outcome.Exited 3)

            let! result = observation

            Assert.That(
                result.TornDown[1],
                Is.False,
                "the failure seen first is the culprit even when it is the last stage"
            )

            Assert.That(result.TornDown[0], Is.True, "the stage seen only after the teardown fired is the victim")
            Assert.That(kills, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``observeChain tears the chain down as soon as a relay fault is seen``() : Task =
        // Regression (T-343): the relay verdicts used to be read only AFTER every stage's exit had been
        // awaited, so a genuine relay failure could not tear anything down while a stage was still running —
        // and a silent upstream that stops writing never earns a broken pipe, so the chain hung on a failure
        // it had already diagnosed. Here NEITHER stage has exited when the relay fails: the fault must fire
        // the whole-chain teardown by itself, which is the only thing that lets the stages become terminal.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let w1 = TaskCompletionSource<Outcome>()
            let relay = TaskCompletionSource<ProcessError option>()
            let teardown = TaskCompletionSource<unit>()
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task; w1.Task |]
                    [| relay.Task |]
                    checkedFailure
                    (fun () ->
                        kills <- kills + 1
                        teardown.TrySetResult() |> ignore)
                    (fun () -> false)
                    (fun () -> false)
                    (fun () -> ())

            relay.SetResult(Some(ProcessError.Io "upstream stdout read failed"))

            // The relay fault alone must fire the whole-chain kill — nothing else can, since no stage has
            // reached a terminal state yet.
            do! awaitSignal "a genuine relay fault must fire the whole-chain teardown by itself" teardown.Task

            // The kill it fired is what ends the stages; both are seen only afterwards, so both are victims.
            w0.SetResult(Outcome.Signalled None)
            w1.SetResult(Outcome.Exited 0)

            let! result = observation

            match result.RelayFault with
            | Some(ProcessError.Io detail) -> Assert.That(detail, Is.EqualTo "upstream stdout read failed")
            | other -> Assert.Fail $"expected the relay's own Io fault, got {other}"

            Assert.That(kills, Is.EqualTo 1, "the relay fault fires the idempotent whole-chain teardown once")

            Assert.That(
                result.TornDown,
                Is.EqualTo(box [| true; true |]),
                "a stage seen only after the relay teardown fired is a victim, never the culprit"
            )
        }
        :> Task

    [<Test>]
    member _.``observeChain keeps the relay fault seen first, not the leftmost relay``() : Task =
        // Several relays can fail while the chain unwinds; the reported `ProcessError.Io` is the one the
        // choke actually saw first (which is also the one that fired the teardown), not whichever happens to
        // sit furthest left in the chain — a positional pick would report a fault caused by the teardown the
        // first one triggered.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let relay0 = TaskCompletionSource<ProcessError option>()
            let relay1 = TaskCompletionSource<ProcessError option>()
            let teardown = TaskCompletionSource<unit>()
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task |]
                    [| relay0.Task; relay1.Task |]
                    checkedFailure
                    (fun () ->
                        kills <- kills + 1
                        teardown.TrySetResult() |> ignore)
                    (fun () -> false)
                    (fun () -> false)
                    (fun () -> ())

            // The RIGHT relay fails first and fires the teardown; the left one only fails afterwards.
            relay1.SetResult(Some(ProcessError.Io "right relay failed first"))
            do! awaitSignal "the first relay fault seen must fire the teardown" teardown.Task
            relay0.SetResult(Some(ProcessError.Io "left relay failed later"))
            w0.SetResult(Outcome.Signalled None)

            let! result = observation

            match result.RelayFault with
            | Some(ProcessError.Io detail) -> Assert.That(detail, Is.EqualTo "right relay failed first")
            | other -> Assert.Fail $"expected the first fault seen, got {other}"

            Assert.That(kills, Is.EqualTo 1, "the second fault does not fire a second teardown")
        }
        :> Task

    [<Test>]
    member _.``observeChain lets a cancel or timeout teardown keep priority over a relay fault``() : Task =
        // The whole chain is already being torn down by its own deadline / an external cancellation, so a
        // relay fault raised into that teardown must not fire a second, redundant kill, and must not
        // re-label the stages it catches as proactive-teardown victims — that louder verdict still decides
        // the run's result.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let relay = TaskCompletionSource<ProcessError option>()
            let mutable kills = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task |]
                    [| relay.Task |]
                    checkedFailure
                    (fun () -> kills <- kills + 1)
                    (fun () -> true) // the chain is already being torn down by the timeout/cancellation
                    (fun () -> true)
                    (fun () -> ())

            relay.SetResult(Some(ProcessError.Io "fault raised into an in-flight teardown"))
            w0.SetResult(Outcome.Signalled None)

            let! result = observation

            Assert.That(kills, Is.EqualTo 0, "a chain already being torn down must not be killed a second time")
            Assert.That(result.TimedOut, Is.True)
            Assert.That(result.TornDown, Is.EqualTo(box [| false |]), "a cancel/timeout kill is not a pipefail victim")
        }
        :> Task

    [<Test>]
    member _.``observeChain fixes the timeout verdict at the last stage exit while a relay still drains``() : Task =
        // The T-071 verdict rule survives racing the relays in the same choke: the deadline is sampled and
        // disarmed the instant the last STAGE is terminal, even though the relays are still draining, so a
        // slow relay can no longer let a late timer turn an already-settled success into a timeout. The
        // disarm callback is the test's own signal that the stages have settled, so no wall clock is needed.
        task {
            let w0 = TaskCompletionSource<Outcome>()
            let relay = TaskCompletionSource<ProcessError option>()
            let settled = TaskCompletionSource<unit>()
            let mutable timerFired = false
            let mutable disarmed = 0

            let observation =
                PipelineRunner.observeChain
                    [| w0.Task |]
                    [| relay.Task |]
                    checkedFailure
                    (fun () -> ())
                    (fun () -> false)
                    (fun () -> timerFired)
                    (fun () ->
                        disarmed <- disarmed + 1
                        settled.TrySetResult() |> ignore)

            w0.SetResult(Outcome.Exited 0)
            do! awaitSignal "the deadline must be disarmed as soon as the last stage exits" settled.Task

            // The deadline fires only now: the stages are settled, but the relay is still in flight.
            timerFired <- true
            relay.SetResult None

            let! result = observation

            Assert.That(result.TimedOut, Is.False, "a deadline firing during the relay drain must not time the run out")
            Assert.That(disarmed, Is.EqualTo 1, "the timer is disarmed exactly once, at the last stage exit")
            Assert.That(result.RelayFault.IsNone, Is.True, "a clean relay verdict is observed and is not a fault")
        }
        :> Task

    [<Test>]
    member _.``a slow stdout tee does not let the deadline turn a settled success into a timeout``() : Task =
        // End-to-end (T-071): the whole-chain deadline used to stay armed through the last stage's stdout
        // tee/drain, so a slow user tee running AFTER every stage had already exited could let the timer fire
        // and retroactively report a successful pipeline as TimedOut. `echo | sort` both exit in a few ms —
        // far below the 700ms deadline, which the timer would trip mid-tee on the old behaviour — yet the
        // test holds the tee for 1.5s, proving a late timer can no longer flip the already-finished success.
        task {
            use tee = new BlockingTee()
            let last = sortStage |> Command.stdoutTee tee

            let pipeline =
                (emit [ "banana"; "apple" ]).Pipe(last).Timeout(TimeSpan.FromMilliseconds 700.0)

            let run = pipeline.RunAsync()

            do! tee.FirstWrite
            do! Task.Delay(TimeSpan.FromSeconds 1.5) // well past the 700ms deadline the old timer would trip
            tee.Release()

            match! run with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"a slow tee must not time out a settled success, got {error}"
        }
        :> Task

    // --- Streaming pipeline session (T-168): Pipeline.StartAsync -> PipelineSession ---

    [<Test>]
    member _.``StartAsync streams the final stage's stdout line by line``() : Task =
        // Happy path: the session yields the LAST stage's stdout (here the sorted output of `emit | sort`)
        // through `StdoutLinesAsync`, and `FinishAsync` then reports the whole chain's clean exit.
        task {
            let pipeline = (emit [ "banana"; "apple"; "cherry" ]).Pipe sortStage

            match! pipeline.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                let! streamed = collect (session.StdoutLinesAsync())

                let got =
                    streamed
                    |> Seq.map (fun s -> s.Trim())
                    |> Seq.filter (fun s -> s.Length > 0)
                    |> Seq.toList

                Assert.That(got, Is.EqualTo(box [ "apple"; "banana"; "cherry" ]))

                match! session.FinishAsync() with
                | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                | Error error -> Assert.Fail $"finish failed: {error}"
        }
        :> Task

    [<Test>]
    member _.``OutputEventsAsync yields the final stage's stdout as Stdout events``() : Task =
        // A pipeline captures only the final stage's stdout, so every merged event is a `Stdout` event
        // (no separate `Stderr` events — the stderr of each stage is drained for the pipefail result).
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                let! events = collect (session.OutputEventsAsync())

                let onlyStdout =
                    events
                    |> Seq.forall (fun e ->
                        match e with
                        | OutputEvent.Stdout _ -> true
                        | OutputEvent.Stderr _ -> false)

                Assert.That(onlyStdout, Is.True, "a pipeline session emits only Stdout events")

                let got =
                    events
                    |> Seq.map (fun e -> e.Text.Trim())
                    |> Seq.filter (fun s -> s.Length > 0)
                    |> Seq.toList

                Assert.That(got, Is.EqualTo(box [ "apple"; "banana" ]))
                let! _ = session.StopAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``StopAsync tears down every stage of the chain, not just the last``() : Task =
        // The whole-chain teardown criterion: stage 0 is a long-lived silent producer and the last stage
        // (`sort`) blocks reading its stdin, so both are alive when the session starts. `StopAsync` must
        // reap the ENTIRE chain — if it killed only the last stage, the reap would block on stage 0's
        // still-running 30s process. Finishing far inside the 15s window proves stage 0 was killed too.
        task {
            let pipeline = slowSilentStage.Pipe sortStage

            match! pipeline.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                do! assertFinishesPromptly (session.StopAsync())
        }
        :> Task

    [<Test>]
    member _.``StartAsync tears down a partially started chain when a later stage fails to spawn``() : Task =
        // The "partially started chain" case: stage 0 (a live 30s silent producer) has already spawned when
        // a later stage's program is not found. Staging aborts, the chain is torn down, and an error is
        // returned — with no orphan left behind (finishing inside the window proves stage 0 was reaped, not
        // left to run its 30s).
        task {
            let missing = Command.create "processkit-nonexistent-stage-xyz"
            let pipeline = slowSilentStage.Pipe(missing).Pipe(sortStage)

            let start = pipeline.StartAsync()
            do! assertFinishesPromptly start

            match! start with
            | Ok session ->
                do! (session :> IAsyncDisposable).DisposeAsync()
                Assert.Fail "a middle stage that cannot spawn must fail StartAsync, not return a session"
            | Error _ -> () // the started stage-0 producer was reaped during the abort teardown (prompt above)
        }
        :> Task

    [<Test>]
    member _.``StartAsync preserves a timeout that expires between stage spawns``() : Task =
        // Regression (T-200): delay the staging thread immediately after stage 0 has spawned, long enough
        // for the pipeline deadline to fire before stage 1 can start. The staging gate must halt stage 1,
        // reap stage 0, and return the deadline's typed error rather than silently reporting Cancelled.
        task {
            let configured = TimeSpan.FromMilliseconds 100.0
            let pipeline = slowSilentStage.Pipe(slowSilentStage).Timeout configured

            PipelineRunner.stageSpawnedTestHook <-
                Some(fun index ->
                    if index = 0 then
                        Thread.Sleep 500)

            try
                let start = pipeline.StartAsync()
                do! assertFinishesPromptly start

                match! start with
                | Error(ProcessError.Timeout(_, actual, _, _)) -> Assert.That(actual, Is.EqualTo configured)
                | Error(ProcessError.Cancelled _) ->
                    Assert.Fail "a staging deadline must not be downgraded to Cancelled"
                | other -> Assert.Fail $"expected Timeout, got {other}"
            finally
                PipelineRunner.stageSpawnedTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``StartAsync reports external cancellation between stage spawns as Cancelled``() : Task =
        // The same staging gate also receives `Pipeline.CancelOn`; it remains an external cancellation,
        // not a timeout, even though the abort branch handles both after the common partial-chain teardown.
        task {
            use cts = new CancellationTokenSource()
            let pipeline = slowSilentStage.Pipe(slowSilentStage).CancelOn cts.Token

            PipelineRunner.stageSpawnedTestHook <-
                Some(fun index ->
                    if index = 0 then
                        cts.Cancel())

            try
                let start = pipeline.StartAsync()
                do! assertFinishesPromptly start

                match! start with
                | Error(ProcessError.Cancelled _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Cancelled, got {other}"
            finally
                PipelineRunner.stageSpawnedTestHook <- None
        }
        :> Task

    [<Test>]
    member _.``a streamed pipeline preserves the buffered pipefail outcome classification``() : Task =
        // The classification-parity criterion: the streaming `FinishAsync` reports the SAME pipefail
        // representative outcome the buffering `OutputStringAsync` does. Stage 0 exits 3 (a checked
        // failure) and the last stage exits 0, so pipefail blames stage 0 (`Exited 3`) on both paths.
        task {
            let buffered = (shell "exit 3").Pipe sortStage

            let! bufferedResult = buffered.OutputStringAsync()

            let bufferedOutcome =
                match bufferedResult with
                | Ok result -> result.Outcome
                | Error error ->
                    Assert.Fail $"buffered run errored unexpectedly: {error}"
                    Outcome.Exited 0

            let streamed = (shell "exit 3").Pipe sortStage

            match! streamed.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                let! _ = collect (session.StdoutLinesAsync())

                match! session.FinishAsync() with
                | Ok finished ->
                    Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 3))

                    Assert.That(
                        finished.Outcome,
                        Is.EqualTo bufferedOutcome,
                        "streamed and buffered pipefail must agree"
                    )
                | Error error -> Assert.Fail $"finish classified a pipefail exit as an error: {error}"
        }
        :> Task

    [<Test>]
    member _.``a streaming pipeline session emits exactly one whole-chain telemetry triple``() : Task =
        // Telemetry parity criterion: a whole streaming run counts as ONE run (composite `a | b` label),
        // not one per stage and not double-counted between the staging and the session's RunningProcess.
        task {
            let listener, activeDeltas, getStarted, getCompleted = listenToRunMetrics ()
            use _listener = listener

            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.StartAsync() with
            | Error error -> Assert.Fail $"start failed: {error}"
            | Ok session ->
                use session = session
                let! _ = collect (session.StdoutLinesAsync())
                let! _ = session.FinishAsync()
                ()

            Assert.That(getStarted (), Is.EqualTo 1L, "exactly one run started for the whole chain")
            Assert.That(getCompleted (), Is.EqualTo 1L, "exactly one run completed for the whole chain")
            Assert.That(Seq.sum activeDeltas, Is.EqualTo 0L, "the in-flight run count returns to zero")
        }
        :> Task
