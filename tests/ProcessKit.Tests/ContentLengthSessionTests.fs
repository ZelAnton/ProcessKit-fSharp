namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

type private ParkedContentLengthStream() =
    inherit Stream()

    let parked =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Parked = parked.Task

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())

    override _.ReadAsync(_buffer: Memory<byte>, _cancellationToken: CancellationToken) =
        let read =
            task {
                parked.TrySetResult() |> ignore
                do! released.Task
                return raise (ObjectDisposedException "Stream")
            }

        ValueTask<int> read

    override _.Dispose(disposing) =
        released.TrySetResult() |> ignore
        base.Dispose disposing

type private FaultingContentLengthStream() =
    inherit MemoryStream()

    override _.ReadAsync(_buffer: Memory<byte>, _cancellationToken: CancellationToken) =
        ValueTask<int>(Task.FromException<int>(IOException "synthetic read failure"))

/// A stdout double that hands out exactly ONE pre-encoded frame per `ReadAsync`, so a test can pin down
/// how far the framed parser has actually got instead of guessing with a delay: the parser asks for the
/// next chunk only after it has parsed the previous one AND enqueued it, so "chunk n has been served"
/// proves the loop is now committed to enqueueing frame n. Returns 0 (EOF) once every chunk is served.
type private ChunkedFrameStream(chunks: byte[][]) =
    inherit Stream()

    let served =
        Array.init chunks.Length (fun _ ->
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))

    let mutable index = 0

    /// Completes once the `count`-th chunk (1-based) has been handed to the reader. The same `Task`
    /// instance every call, so a test can compare it by reference against a `Task.WhenAny` winner.
    member _.ServedAsync(count: int) : Task<unit> = served[count - 1].Task

    /// Release every outstanding signal, so a failing test can never leave a helper parked on one.
    member _.ReleaseSignals() =
        for signal in served do
            signal.TrySetResult() |> ignore

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) =
        if index >= chunks.Length then
            ValueTask<int> 0
        else
            let chunk = chunks[index]

            if buffer.Length < chunk.Length then
                // A test-harness invariant, not a library one: one read must carry a whole frame, and the
                // framed reader's own buffer (8 KiB) dwarfs the tiny frames these tests script.
                raise (InvalidOperationException "the framed reader's buffer must hold a whole test chunk")

            chunk.AsSpan().CopyTo(buffer.Span)
            index <- index + 1
            served[index - 1].TrySetResult() |> ignore
            ValueTask<int> chunk.Length

/// A stdin double whose writes never finish on their own: the first one announces itself and then waits
/// for its own cancellation token, the way a real pipe blocks once the child stops reading. Lets a test
/// pin down a send interrupted WHILE it was writing, as opposed to one interrupted before its first byte.
type private StallingStdinStream() =
    inherit Stream()

    let writing =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once a write has parked in this stream.
    member _.Writing = writing.Task

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())

    // Never the synchronous path: `ProcessStdin.WriteAsync` writes through the array-based async overload,
    // and a base-class fallback to `Write` would silently accept bytes this test needs held.
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())

    override _.WriteAsync(_buffer: byte[], _offset: int, _count: int, cancellationToken: CancellationToken) : Task =
        writing.TrySetResult() |> ignore
        Task.Delay(Timeout.Infinite, cancellationToken)

[<TestFixture>]
type ContentLengthSessionTests() =

    let hostFor (stdout: Stream) (teardown: unit -> ValueTask) : RunningHost =
        { Config = (Command.create "server").Config
          Pid = None
          Stdout = Some stdout
          Stderr = None
          Stdin = None
          StartTime = DateTime.UtcNow
          StartedTimestamp = Stopwatch.GetTimestamp()
          StartTimeIdentity = None
          Wait = fun () -> TaskCompletionSource<Outcome>().Task
          StdinError = fun () -> None
          StdinFeedComplete = ignore
          StartKill = ignore
          Signal = fun _ -> Ok()
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          TreeStats = None
          Teardown = teardown }

    // A host for the sessions whose configuration matters (a bounded `StreamBuffer`, `KeepStdinOpen`) and
    // whose stdin feeder has to be modelled: `feedComplete` stands in for `ProcessGroup`'s own blocking
    // `stdinFeeder.Task.GetAwaiter().GetResult()`. `Wait` completes at once so disposal never depends on a
    // parked pump, exactly as the `FakeProcess`-backed tests above run.
    let hostForCommand
        (command: Command)
        (stdout: Stream)
        (stdin: Stream option)
        (feedComplete: unit -> unit)
        (teardown: unit -> ValueTask)
        : RunningHost =
        { Config = command.Config
          Pid = None
          Stdout = Some stdout
          Stderr = None
          Stdin = stdin
          StartTime = DateTime.UtcNow
          StartedTimestamp = Stopwatch.GetTimestamp()
          StartTimeIdentity = None
          Wait = fun () -> Task.FromResult(Outcome.Exited 0)
          StdinError = fun () -> None
          StdinFeedComplete = feedComplete
          StartKill = ignore
          Signal = fun _ -> Ok()
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          TreeStats = None
          Teardown = teardown }

    let collect (source: IAsyncEnumerable<byte[]>) : Task<byte[][]> =
        task {
            let frames = ResizeArray<byte[]>()
            let enumerator = source.GetAsyncEnumerator()

            try
                let mutable reading = true

                while reading do
                    let! moved = enumerator.MoveNextAsync()

                    if moved then
                        frames.Add enumerator.Current
                    else
                        reading <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return frames.ToArray()
        }

    let encoded (payload: byte[]) =
        Array.concat
            [ Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n")
              payload ]

    [<Test>]
    member _.``fake Content-Length frames round-trip byte-exactly across buffer boundaries``() : Task =
        task {
            let small = [| 0uy; 255uy; 1uy; 2uy |]
            let large = Array.init 9000 (fun index -> byte (index % 251))

            let fake =
                FakeProcess.Create("language-server").WithContentLengthFrames([ small; large ])

            use running = fake.Build()
            let session = ContentLengthSession(running, 10000)
            let! actual = collect (session.FramesAsync())

            Assert.That(actual.Length, Is.EqualTo 2)
            Assert.That(Convert.ToHexString actual[0], Is.EqualTo(Convert.ToHexString small))
            Assert.That(Convert.ToHexString actual[1], Is.EqualTo(Convert.ToHexString large))
        }
        :> Task

    [<Test>]
    member _.``a bounded StreamBuffer backpressures the frame parser instead of an unbounded backlog``() : Task =
        task {
            let payloads = [| for value in 0uy .. 4uy -> Array.create 4 value |]

            let command =
                (Command.create "language-server").StreamBuffer(StreamBufferPolicy.Bounded(2))

            let fake = FakeProcess.OfCommand(command).WithContentLengthFrames(payloads)

            use running = fake.Build()
            let session = ContentLengthSession running

            // Nobody is reading `FramesAsync()` yet, and the in-memory producer has no real I/O delay of
            // its own — with an honestly bounded channel (capacity 2) the parser can enqueue at most 2 of
            // the 5 scripted frames before its `WriteAsync` on the 3rd genuinely blocks, so the session's
            // combined outcome (which that parse loop's completion resolves) cannot have finished yet. An
            // unbounded backlog (today's bug) would instead let the parser race ahead and finish
            // instantly, with nobody ever having paced it.
            let! stillPending = Task.WhenAny(running.ExitTask, Task.Delay 300)

            Assert.That(
                obj.ReferenceEquals(stillPending, running.ExitTask),
                Is.False,
                "an unbounded channel would already have let the parser finish without any consumer"
            )

            let! actual = collect (session.FramesAsync())

            Assert.That(actual.Length, Is.EqualTo payloads.Length)

            for index in 0 .. payloads.Length - 1 do
                Assert.That(Convert.ToHexString actual[index], Is.EqualTo(Convert.ToHexString payloads[index]))

            // Backpressure only paces the parser; it must still deliver every frame, byte-exact, once a
            // consumer starts draining.
            let! outcome = running.ExitTask
            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member _.``lossy StreamBuffer full modes are refused instead of silently dropping frames``() : Task =
        task {
            let refused (mode: StreamFullMode) (name: string) =
                task {
                    let command =
                        (Command.create "language-server").StreamBuffer(StreamBufferPolicy.Bounded(2, mode))

                    use running =
                        FakeProcess.OfCommand(command).WithContentLengthFrames([ Array.create 4 7uy ]).Build()

                    let refusal =
                        Assert.Throws<ProcessException>(Action(fun () -> ContentLengthSession running |> ignore))

                    let missing = $"expected {name} to be refused with a typed ProcessException"

                    match refusal with
                    | null -> Assert.Fail missing
                    | error ->
                        match error.Error with
                        | ProcessError.Unsupported detail ->
                            // Naming the refused mode and the lossless way forward: dropping a frame is
                            // undetectable corruption of a protocol stream, so the session refuses the knob
                            // instead of quietly downgrading it (the stance detached launches already take).
                            Assert.That(detail, Does.Contain name)
                            Assert.That(detail, Does.Contain "Backpressure")
                        | other -> Assert.Fail $"expected Unsupported, got {other}"

                    // The refusal lands before the session claims stdout, so the handle is left exactly as
                    // it was found — a refused knob must fail loudly without consuming the run.
                    match! running.OutputStringAsync() with
                    | Ok _ -> ()
                    | Error error -> Assert.Fail $"a refused framed session must leave stdout unclaimed, got {error}"
                }

            do! refused StreamFullMode.DropOldest "DropOldest"
            do! refused StreamFullMode.DropNewest "DropNewest"
        }
        :> Task

    [<Test>]
    member _.``disposal while the bounded frame backlog is full ends the stream without a fault``() : Task =
        task {
            let payloads = [| for value in 1uy .. 3uy -> Array.create 4 value |]
            let stdout = new ChunkedFrameStream(payloads |> Array.map encoded)

            let command =
                (Command.create "language-server").StreamBuffer(StreamBufferPolicy.Bounded 1)

            let running =
                new RunningProcess(
                    hostForCommand command (stdout :> Stream) None ignore (fun () ->
                        (stdout :> IDisposable).Dispose()
                        ValueTask())
                )

            let session = ContentLengthSession running
            let frames = session.FramesAsync().GetAsyncEnumerator()

            // Capacity 1 with nobody draining: the parser enqueues frame 1, then parks in the bounded
            // channel's `WriteAsync` on frame 2 — and it cannot ask the stream for anything more until that
            // write lands, so once chunk 2 has been served the parked write is the only place it can be (a
            // disposal that beats it there just makes the same `WriteAsync` throw on the cancelled token).
            let parked = "the framed parser never reached its second frame"
            let! served = Task.WhenAny(stdout.ServedAsync 2 :> Task, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(served, stdout.ServedAsync 2), Is.True, parked)

            do! (running :> IAsyncDisposable).DisposeAsync().AsTask()

            // Teardown cancels `DisposalToken` — the very token that parked frame write is bounded to. That
            // is graceful disposal, not a parser fault: the consumer must still receive what was queued and
            // then see a clean end of stream, never a spurious cancellation out of `MoveNextAsync`.
            let delivered = "the frame queued before the disposal must still be delivered"
            let! first = frames.MoveNextAsync()
            Assert.That(first, Is.True, delivered)
            Assert.That(Convert.ToHexString frames.Current, Is.EqualTo(Convert.ToHexString payloads[0]))

            let clean =
                "a disposal landing on a full frame backlog must end the frame stream cleanly, not fault it"

            let! ended = frames.MoveNextAsync()
            Assert.That(ended, Is.False, clean)
            do! frames.DisposeAsync().AsTask()
        }
        :> Task

    [<Test>]
    member _.``a bounded frame backlog never deadlocks a session over a fed stdin source``() : Task =
        task {
            let payloads = [| for value in 1uy .. 6uy -> Array.create 4 value |]
            let stdout = new ChunkedFrameStream(payloads |> Array.map encoded)
            let stdin = new MemoryStream()
            let initialize = Encoding.UTF8.GetBytes "{\"method\":\"initialize\"}"

            // The stdin source feeder, wired exactly as `ProcessGroup` wires it: a BLOCKING wait that
            // finishes only once the child has consumed the source. A real child consumes it while draining
            // its own stdout writes, so gate it on the parser having pulled the 4th frame out of the stream
            // — progress a capacity-1 backlog makes impossible until somebody drains `FramesAsync()`.
            let feedComplete () =
                stdout.ServedAsync(4).GetAwaiter().GetResult()

            let command =
                (Command.create "language-server").KeepStdinOpen().StreamBuffer(StreamBufferPolicy.Bounded 1)

            use running =
                new RunningProcess(
                    hostForCommand command (stdout :> Stream) (Some(stdin :> Stream)) feedComplete (fun () ->
                        ValueTask())
                )

            try
                // The constructor claims stdin (so no racing `TakeStdin` can steal it) but must NOT wait for
                // that feeder: it has just started the parse loop that fills the bounded backlog, whose only
                // consumer is a `FramesAsync()` the caller cannot reach until construction returns. Waiting
                // here is a four-way deadlock — parser parked on a full channel, child blocked writing
                // stdout, child therefore not reading stdin, feeder never done.
                let construction = Task.Run(fun () -> ContentLengthSession running)

                let deadlocked =
                    "the framed session constructor deadlocked: it waited for the stdin feeder while its own bounded frame backlog held the child back"

                let! constructed = Task.WhenAny(construction :> Task, Task.Delay 10000)
                Assert.That(obj.ReferenceEquals(constructed, construction), Is.True, deadlocked)

                let! session = construction

                // The wait is preserved, only moved: the interactive writer still may not touch the pipe
                // while the source feeder is running, so the send stays pending while the backlog is unread.
                let sending = session.SendAsync initialize

                let tooEarly =
                    "a send must still wait for the stdin source feeder before writing the pipe"

                let! early = Task.WhenAny(sending :> Task, Task.Delay 200)
                Assert.That(obj.ReferenceEquals(early, sending), Is.False, tooEarly)

                // Draining the frames releases the whole chain: parser -> child -> feeder -> send.
                let! frames = collect (session.FramesAsync())
                Assert.That(frames.Length, Is.EqualTo payloads.Length)

                for index in 0 .. payloads.Length - 1 do
                    Assert.That(Convert.ToHexString frames[index], Is.EqualTo(Convert.ToHexString payloads[index]))

                match! sending with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the deferred send must complete once the feeder finishes, got {error}"

                Assert.That(Convert.ToHexString(stdin.ToArray()), Is.EqualTo(Convert.ToHexString(encoded initialize)))
            finally
                // Never leave the modelled feeder parked on a signal, whatever an assertion above decided.
                stdout.ReleaseSignals()
        }
        :> Task

    [<Test>]
    member _.``an interrupted send reports whether it could have reached the child``() : Task =
        task {
            // Both interruptions below are the same `ProcessError.Cancelled` to a caller of `SendAsync`,
            // which stays conservative ("abandon the session"). The staged form separates them, and that
            // separation is load-bearing one layer up: `JsonRpcSession` ends a conversation only for a
            // frame that was genuinely being written, never for a call parked ahead of the pipe.
            let feeding =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let stdout = new MemoryStream()
            let stdin = new StallingStdinStream()
            let command = (Command.create "language-server").KeepStdinOpen()

            use running =
                new RunningProcess(
                    hostForCommand
                        command
                        (stdout :> Stream)
                        (Some(stdin :> Stream))
                        (fun () -> feeding.Task.GetAwaiter().GetResult())
                        (fun () -> ValueTask())
                )

            let session = ContentLengthSession running

            try
                use claimCancellation = new CancellationTokenSource()

                // Parked waiting for the stdin source feeder: no gate taken, no byte written.
                let claiming = session.SendStagedAsync([| 1uy |], claimCancellation.Token)

                let parked = "a send must wait for the stdin source feeder before writing the pipe"
                let! early = Task.WhenAny(claiming :> Task, Task.Delay 200)
                Assert.That(obj.ReferenceEquals(early, claiming), Is.False, parked)

                claimCancellation.Cancel()

                match! claiming with
                | Error(ProcessError.Cancelled _, FramedSendStage.BeforeWrite) -> ()
                | other -> Assert.Fail $"expected a cancellation reported from before the first byte, got {other}"

                // The feeder finishes, so the next send reaches the pipe and parks INSIDE the write —
                // where the very same cancellation may have delivered the child a prefix.
                feeding.TrySetResult() |> ignore
                use writeCancellation = new CancellationTokenSource()
                let writing = session.SendStagedAsync([| 2uy |], writeCancellation.Token)

                do! stdin.Writing
                writeCancellation.Cancel()

                match! writing with
                | Error(ProcessError.Cancelled _, FramedSendStage.Writing) -> ()
                | other -> Assert.Fail $"expected a cancellation reported from inside the write, got {other}"
            finally
                // Never leave the modelled feeder parked on a pool thread, whatever an assertion decided.
                feeding.TrySetResult() |> ignore
        }
        :> Task

    [<Test>]
    member _.``concurrent sends remain complete frames and record through FakeProcess stdin``() : Task =
        task {
            let first = Encoding.UTF8.GetBytes "first"
            let second = Encoding.UTF8.GetBytes "second"

            let fake =
                FakeProcess.Create("debug-adapter").WithStdinOpen().WithContentLengthFrames(Array.empty<byte[]>)

            use running = fake.Build()
            let session = ContentLengthSession running
            let firstSend = session.SendAsync first
            let secondSend = session.SendAsync second
            let! results = Task.WhenAll(firstSend, secondSend)

            Assert.That(results |> Array.forall Result.isOk, Is.True)

            let firstThenSecond = Array.append (encoded first) (encoded second)
            let secondThenFirst = Array.append (encoded second) (encoded first)

            let actual = Convert.ToHexString fake.StdinBytes
            let orderedFirst = Convert.ToHexString firstThenSecond
            let orderedSecond = Convert.ToHexString secondThenFirst

            Assert.That(actual = orderedFirst || actual = orderedSecond, Is.True, "frames must remain serialized")
        }
        :> Task

    [<Test>]
    member _.``malformed and oversized frames surface as typed parse failures``() : Task =
        task {
            let malformed =
                FakeProcess.Create("server").WithStdout("Content-Length: nope\r\n\r\n")

            use malformedRun = malformed.Build()
            let malformedSession = ContentLengthSession malformedRun
            let malformedFrames = malformedSession.FramesAsync().GetAsyncEnumerator()

            let malformedError =
                Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> malformedFrames.MoveNextAsync().AsTask()))

            match malformedError with
            | null -> Assert.Fail "expected a malformed-frame ProcessException"
            | error ->
                match error.Error with
                | ProcessError.Parse _ -> ()
                | other -> Assert.Fail $"expected Parse, got {other}"

            do! malformedFrames.DisposeAsync().AsTask()

            let oversized =
                FakeProcess.Create("server").WithStdout("Content-Length: 5\r\n\r\nhello")

            use oversizedRun = oversized.Build()
            let oversizedSession = ContentLengthSession(oversizedRun, 4)
            let oversizedFrames = oversizedSession.FramesAsync().GetAsyncEnumerator()

            let oversizedError =
                Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> oversizedFrames.MoveNextAsync().AsTask()))

            match oversizedError with
            | null -> Assert.Fail "expected an oversized-frame ProcessException"
            | error ->
                match error.Error with
                | ProcessError.Parse _ -> ()
                | other -> Assert.Fail $"expected Parse, got {other}"

            do! oversizedFrames.DisposeAsync().AsTask()
        }
        :> Task

    [<Test>]
    member _.``session owns stdout once and sending without kept stdin is typed unsupported``() : Task =
        task {
            use running =
                FakeProcess.Create("server").WithContentLengthFrames(Array.empty<byte[]>).Build()

            let session = ContentLengthSession running

            match! running.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected the framed session to own stdout, got {other}"

            match! session.SendAsync(Array.empty<byte>) with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "KeepStdinOpen")
            | other -> Assert.Fail $"expected unsupported stdin, got {other}"

            session.FramesAsync() |> ignore

            Assert.Throws<InvalidOperationException>(Action(fun () -> session.FramesAsync() |> ignore))
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``disposing a live framed session completes its enumerator without a false read fault``() : Task =
        task {
            let stdout = new ParkedContentLengthStream()

            let host =
                hostFor (stdout :> Stream) (fun () ->
                    (stdout :> IDisposable).Dispose()
                    ValueTask())

            let running = new RunningProcess(host)
            let session = ContentLengthSession running
            let frames = session.FramesAsync().GetAsyncEnumerator()
            let pending = frames.MoveNextAsync().AsTask()
            let! parked = Task.WhenAny(stdout.Parked, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(parked, stdout.Parked), Is.True, "the framed reader never parked")

            do! (running :> IAsyncDisposable).DisposeAsync().AsTask()

            let! moved = pending
            Assert.That(moved, Is.False)
            do! frames.DisposeAsync().AsTask()
        }
        :> Task

    [<Test>]
    member _.``a genuine framed stdout read failure surfaces as typed I/O``() : Task =
        task {
            let stdout = new FaultingContentLengthStream()

            use running =
                new RunningProcess(
                    hostFor (stdout :> Stream) (fun () ->
                        (stdout :> IDisposable).Dispose()
                        ValueTask())
                )

            let frames = ContentLengthSession(running).FramesAsync().GetAsyncEnumerator()

            let error =
                Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> frames.MoveNextAsync().AsTask()))

            match error with
            | null -> Assert.Fail "expected a framed read ProcessException"
            | error ->
                match error.Error with
                | ProcessError.Io detail -> Assert.That(detail, Does.Contain "synthetic read failure")
                | other -> Assert.Fail $"expected Io, got {other}"

            do! frames.DisposeAsync().AsTask()
        }
        :> Task
