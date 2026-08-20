namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// Params/result shapes for the typed overloads. Public, because STJ's constructor-based
/// deserialization needs an accessible constructor (same reason as `JsonVerbTests.Widget`).
type RpcHoverParams = { File: string; Line: int }

type RpcHoverResult = { Contents: string }

/// The child's stdout, driven by the test's fake peer: each `Emit` is bytes the peer wrote, and
/// `Complete` is the peer closing its output (EOF).
type private PeerOutputStream() =
    inherit Stream()

    let chunks =
        Channel.CreateUnbounded<byte[]>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    let mutable current: byte[] = Array.empty
    let mutable offset = 0

    /// Hand the session more bytes (a whole Content-Length frame, in these tests).
    member _.Emit(payload: byte[]) =
        chunks.Writer.TryWrite payload |> ignore

    /// The peer closed its output: the next read reports end-of-stream.
    member _.Complete() = chunks.Writer.TryComplete() |> ignore

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

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        let read =
            task {
                let mutable waiting = offset >= current.Length

                while waiting do
                    let! more = chunks.Reader.WaitToReadAsync cancellationToken

                    if not more then
                        waiting <- false
                    else
                        match chunks.Reader.TryRead() with
                        | true, next ->
                            current <- next
                            offset <- 0
                            waiting <- false
                        | _ -> ()

                if offset >= current.Length then
                    return 0
                else
                    let count = min buffer.Length (current.Length - offset)
                    current.AsMemory(offset, count).CopyTo buffer
                    offset <- offset + count
                    return count
            }

        ValueTask<int> read

    override this.Dispose(disposing) =
        this.Complete()
        base.Dispose disposing

/// The child's stdin: everything the session writes is reassembled into the Content-Length frames the
/// test's fake peer reads back, so a test can answer the exact request that was actually sent.
type private PeerInputStream() =
    inherit Stream()

    let frames =
        Channel.CreateUnbounded<byte[]>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    // One entry per write that has begun waiting on the stall, so a test can observe that point instead
    // of racing it with a sleep.
    let stalledWrites =
        Channel.CreateUnbounded<bool>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    let buffered = ResizeArray<byte>()
    let gate = obj ()

    // `None` while the peer drains its stdin; a pending completion while it does not, which every write
    // waits on — the way a real write blocks once the OS pipe buffer fills behind a peer that stopped
    // reading — until `ResumeWrites` or the write's own cancellation token ends the wait.
    let mutable stall: TaskCompletionSource option = None

    let headerEnd () =
        let mutable found = -1
        let mutable index = 0

        while found < 0 && index + 3 < buffered.Count do
            if
                buffered[index] = 13uy
                && buffered[index + 1] = 10uy
                && buffered[index + 2] = 13uy
                && buffered[index + 3] = 10uy
            then
                found <- index

            index <- index + 1

        found

    let contentLength (header: string) =
        let line =
            header.Split "\r\n"
            |> Array.find (fun candidate -> candidate.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))

        Int32.Parse(line.Substring("Content-Length:".Length).Trim(), Globalization.CultureInfo.InvariantCulture)

    let rec extract () =
        let terminator = headerEnd ()

        if terminator >= 0 then
            let header = Encoding.ASCII.GetString(buffered.GetRange(0, terminator).ToArray())
            let length = contentLength header

            if buffered.Count >= terminator + 4 + length then
                let payload = buffered.GetRange(terminator + 4, length).ToArray()
                buffered.RemoveRange(0, terminator + 4 + length)
                frames.Writer.TryWrite payload |> ignore
                extract ()

    let append (payload: byte[]) =
        lock gate (fun () ->
            buffered.AddRange payload
            extract ())

    let writeAsync (payload: byte[]) (cancellationToken: CancellationToken) : Task =
        match lock gate (fun () -> stall) with
        | None ->
            append payload
            Task.CompletedTask
        | Some pending ->
            // Announced before the wait, so a test can be sure a write is stuck here — and its caller
            // therefore holding the session's send gate — rather than about to be.
            stalledWrites.Writer.TryWrite true |> ignore

            task {
                do! pending.Task.WaitAsync cancellationToken
                append payload
            }
            :> Task

    /// The next whole frame the session sent.
    member _.NextFrameAsync() : Task<byte[]> = frames.Reader.ReadAsync().AsTask()

    /// Stop draining this pipe, as a peer that stopped reading its own stdin does once the OS buffer
    /// fills: every later write waits until `ResumeWrites` or its cancellation token ends it.
    member _.StallWrites() =
        lock gate (fun () ->
            if stall.IsNone then
                stall <- Some(TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))

    /// Start draining again, as a peer that resumed reading its stdin: the waiting writes go through.
    member _.ResumeWrites() =
        let waiting =
            lock gate (fun () ->
                let current = stall
                stall <- None
                current)

        match waiting with
        | Some pending -> pending.TrySetResult() |> ignore
        | None -> ()

    /// Completes once a write has begun waiting on the stall.
    member _.NextStalledWriteAsync() : Task =
        stalledWrites.Reader.ReadAsync().AsTask()

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.FlushAsync(_cancellationToken) = Task.CompletedTask
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) = raise (NotSupportedException())

    override _.Write(buffer: byte[], offset: int, count: int) =
        append (buffer.AsSpan(offset, count).ToArray())

    // Both async shapes are overridden, and both honour the stall: `ProcessStdin.WriteAsync` writes
    // through the array overload, whose base implementation would otherwise fall back to the synchronous
    // `Write` above and quietly accept bytes a stalled peer never read.
    override _.WriteAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) : Task =
        writeAsync (buffer.AsSpan(offset, count).ToArray()) cancellationToken

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) : ValueTask =
        ValueTask(writeAsync (buffer.ToArray()) cancellationToken)

/// A manually-fired timer provider for request deadline tests. The timer callback runs on the caller's
/// thread, so a test can fire the deadline from the router's pending-claim hook without sleeping.
type private JsonRpcManualTimer(callback: TimerCallback, state: obj | null, fired: TaskCompletionSource<unit>) =
    let mutable disposed = 0

    member _.Fire() =
        if Volatile.Read(&disposed) = 0 then
            callback.Invoke state
            fired.TrySetResult() |> ignore

    interface ITimer with
        member _.Change(_dueTime, _period) = Volatile.Read(&disposed) = 0

        member _.Dispose() =
            Interlocked.Exchange(&disposed, 1) |> ignore

        member _.DisposeAsync() =
            Interlocked.Exchange(&disposed, 1) |> ignore
            ValueTask()

type private ManualTimeProvider() =
    inherit TimeProvider()

    let timerCreated =
        TaskCompletionSource<JsonRpcManualTimer>(TaskCreationOptions.RunContinuationsAsynchronously)

    let deadlineFired =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    override _.CreateTimer(callback, state, _dueTime, _period) =
        let timer = new JsonRpcManualTimer(callback, state, deadlineFired)
        timerCreated.TrySetResult(timer) |> ignore
        timer :> ITimer

    member _.TimerCreated = timerCreated.Task
    member _.DeadlineFired = deadlineFired.Task

    member _.Advance() =
        timerCreated.Task.GetAwaiter().GetResult().Fire()

/// A live handle over the fake peer, plus the two ends the test drives it through.
type private PeerHandle =
    { Running: RunningProcess
      Stdout: PeerOutputStream
      Stdin: PeerInputStream }

/// Covers `JsonRpcSession` end to end without spawning anything: the peer is a pair of in-memory
/// streams behind a synthetic `RunningHost`, exactly as `ContentLengthSessionTests` models a framed
/// child, so every id-correlation, timeout, EOF, and protocol-failure path is deterministic.
[<TestFixture>]
type JsonRpcSessionTests() =

    // F# cannot run Roslyn source generators, so the source-generated metadata the AOT overloads take
    // is built from the reflection resolver here; the overloads under test see the same public
    // `JsonTypeInfo<'T>` contract either way (same approach as `JsonVerbTests`).
    let hoverParamsTypeInfo =
        JsonSerializerOptions.Default.GetTypeInfo(typeof<RpcHoverParams>) :?> JsonTypeInfo<RpcHoverParams>

    let hoverResultTypeInfo =
        JsonSerializerOptions.Default.GetTypeInfo(typeof<RpcHoverResult>) :?> JsonTypeInfo<RpcHoverResult>

    // `feedComplete` stands in for `RunningHost.StdinFeedComplete` — the blocking wait for a
    // `Command.Stdin(source)` feeder to finish draining, which is what holds the interactive pipe back on
    // such a run. `ignore` models the interactive-only stdin every other test here wants; a blocking one
    // models the window in which a send is parked before it can write a single byte.
    let peerHandleFor (command: Command) (feedComplete: unit -> unit) =
        let stdout = new PeerOutputStream()
        let stdin = new PeerInputStream()

        let exit =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        let host: RunningHost =
            { Config = command.KeepStdinOpen().Config
              Pid = None
              Stdout = Some(stdout :> Stream)
              Stderr = None
              Stdin = Some(stdin :> Stream)
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> exit.Task
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = feedComplete
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown =
                fun () ->
                    stdout.Complete()
                    exit.TrySetResult(Outcome.Exited 0) |> ignore
                    ValueTask() }

        { Running = new RunningProcess(host)
          Stdout = stdout
          Stdin = stdin }

    let peerHandleFeeding (feedComplete: unit -> unit) =
        peerHandleFor (Command.create "language-server") feedComplete

    let peerHandle () = peerHandleFeeding ignore

    let peerHandleWithTimeProvider (timeProvider: TimeProvider) =
        peerHandleFor ((Command.create "language-server").TimeProvider timeProvider) ignore

    let framed (json: string) =
        let payload = Encoding.UTF8.GetBytes json
        Array.append (Encoding.ASCII.GetBytes $"Content-Length: {payload.Length}\r\n\r\n") payload

    // Built by concatenation rather than interpolation: JSON is mostly braces and quotes, which an
    // interpolated F# string turns into an escaping puzzle (KB K-026).
    let responseJson (id: string) (result: string) =
        String.Concat("{\"jsonrpc\":\"2.0\",\"id\":", id, ",\"result\":", result, "}")

    let errorResponseJson (id: string) (code: int) (message: string) (data: string) =
        String.Concat(
            "{\"jsonrpc\":\"2.0\",\"id\":",
            id,
            ",\"error\":{\"code\":",
            string code,
            ",\"message\":\"",
            message,
            "\",\"data\":",
            data,
            "}}"
        )

    let nextFrame (stdin: PeerInputStream) : Task<JsonDocument> =
        task {
            let! payload = stdin.NextFrameAsync()
            return JsonDocument.Parse(ReadOnlyMemory<byte> payload)
        }

    // Asserts on the payload rather than on the whole `Result`: a bare `Is.EqualTo(Ok "...")` infers
    // `Result<string, obj>`, which never equals a `Result<string, ProcessError>` (KB K-039).
    let assertRaw (expected: string) (answer: Result<string, ProcessError>) =
        match answer with
        | Ok text -> Assert.That(text, Is.EqualTo expected)
        | Error error -> Assert.Fail $"expected the result {expected}, got {error}"

    let rawId (document: JsonDocument) =
        document.RootElement.GetProperty("id").GetRawText()

    let methodOf (document: JsonDocument) =
        match document.RootElement.GetProperty("method").GetString() with
        | null -> ""
        | text -> text

    [<Test>]
    member _.``a request is answered by the response carrying the same id``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let call =
                session.RequestAsync<RpcHoverParams, RpcHoverResult>(
                    "textDocument/hover",
                    { File = "Program.fs"; Line = 12 },
                    hoverParamsTypeInfo,
                    hoverResultTypeInfo
                )

            use! request = nextFrame peer.Stdin

            Assert.That(methodOf request, Is.EqualTo "textDocument/hover")
            Assert.That(request.RootElement.GetProperty("jsonrpc").GetString(), Is.EqualTo "2.0")

            let sentParams = request.RootElement.GetProperty "params"
            Assert.That(sentParams.GetProperty("File").GetString(), Is.EqualTo "Program.fs")
            Assert.That(sentParams.GetProperty("Line").GetInt32(), Is.EqualTo 12)

            peer.Stdout.Emit(framed (responseJson (rawId request) "{\"Contents\":\"a docstring\"}"))

            match! call with
            | Ok result -> Assert.That(result.Contents, Is.EqualTo "a docstring")
            | Error error -> Assert.Fail $"expected a hover result, got {error}"
        }
        :> Task

    [<Test>]
    member _.``concurrent requests are answered by id, never by arrival order``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let first = session.RequestRawAsync("first", null)
            let second = session.RequestRawAsync("second", null)

            use! sentA = nextFrame peer.Stdin
            use! sentB = nextFrame peer.Stdin

            // Each answer carries the method name of the request that id belongs to, and they are sent
            // in the OPPOSITE order to the requests: a session that paired answers by arrival instead of
            // by id would hand each call the other one's result.
            peer.Stdout.Emit(framed (responseJson (rawId sentB) ("\"" + methodOf sentB + "\"")))
            peer.Stdout.Emit(framed (responseJson (rawId sentA) ("\"" + methodOf sentA + "\"")))

            let! firstAnswer = first
            let! secondAnswer = second

            assertRaw "\"first\"" firstAnswer
            assertRaw "\"second\"" secondAnswer
            Assert.That(rawId sentA, Is.Not.EqualTo(rawId sentB))
        }
        :> Task

    [<Test>]
    member _.``an error answer is a typed failure, not a result``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let call =
                session.RequestAsync<RpcHoverParams, RpcHoverResult>(
                    "textDocument/hover",
                    { File = "Program.fs"; Line = 1 },
                    hoverParamsTypeInfo,
                    hoverResultTypeInfo
                )

            use! request = nextFrame peer.Stdin

            peer.Stdout.Emit(framed (errorResponseJson (rawId request) -32601 "Method not found" "{\"retry\":false}"))

            match! call with
            | Error(ProcessError.JsonRpc(program, methodName, code, detail, data)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(methodName, Is.EqualTo "textDocument/hover")
                Assert.That(code, Is.EqualTo -32601)
                Assert.That(detail, Is.EqualTo "Method not found")
                Assert.That(data, Is.EqualTo(Some "{\"retry\":false}"))
            | other -> Assert.Fail $"expected a JSON-RPC error answer, got {other}"
        }
        :> Task

    [<Test>]
    member _.``an unanswered request fails with a typed timeout instead of hanging``() : Task =
        task {
            let clock = ManualTimeProvider()
            let peer = peerHandleWithTimeProvider clock
            use running = peer.Running
            let session = JsonRpcSession(running)

            let call =
                session.RequestRawAsync("never/answered", null, TimeSpan.FromMilliseconds 200.0)

            let! _ = clock.TimerCreated
            use! abandoned = nextFrame peer.Stdin
            clock.Advance()
            do! clock.DeadlineFired
            let! answer = call

            match answer with
            | Error(ProcessError.Timeout(program, timeout, _, _)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds 200.0))
            | other -> Assert.Fail $"expected a timeout, got {other}"

            // The late answer belongs to a call that has already been told it failed: it is discarded,
            // and the session keeps working for the next request.
            peer.Stdout.Emit(framed (responseJson (rawId abandoned) "\"too late\""))

            let next = session.RequestRawAsync("still/works", null, TimeSpan.FromSeconds 30.0)
            use! sent = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId sent) "\"answered\""))

            let! nextAnswer = next
            assertRaw "\"answered\"" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``an answer claimed before deadline publication keeps its success``() : Task =
        task {
            let clock = ManualTimeProvider()
            let mutable hookCalls = 0

            // The internal hook runs while the router owns the pending entry. Firing the manual deadline
            // from here makes the timeout path contend for that same ownership without relying on a sleep.
            let beforePendingCompletion =
                Action<int64>(fun _ ->
                    if Interlocked.Exchange(&hookCalls, 1) = 0 then
                        clock.Advance()
                        clock.DeadlineFired.GetAwaiter().GetResult())

            let peer = peerHandleWithTimeProvider clock
            use running = peer.Running

            let session =
                JsonRpcSession(running, 16 * 1024 * 1024, 1024, beforePendingCompletion)

            let call =
                session.RequestRawAsync("race/answer-before-deadline", null, TimeSpan.FromMilliseconds 200.0)

            let! _ = clock.TimerCreated
            use! request = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId request) "\"answer wins\""))

            let! answer = call
            assertRaw "\"answer wins\"" answer
            Assert.That(clock.DeadlineFired.IsCompleted, Is.True)

            // The answer claimed exactly one pending entry; the session must still be able to allocate,
            // send, and complete a later request after the deadline was fired.
            let next =
                session.RequestRawAsync("after/answer-deadline-race", null, TimeSpan.FromSeconds 30.0)

            use! nextRequest = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId nextRequest) "\"next answer\""))

            let! nextAnswer = next
            assertRaw "\"next answer\"" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``an answer claimed before cancellation publication keeps its success``() : Task =
        task {
            use cancellation = new CancellationTokenSource()

            let cancellationObserved =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let cancellationFinished =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            use _ =
                cancellation.Token.Register(Action(fun () -> cancellationObserved.TrySetResult() |> ignore))

            let mutable hookCalls = 0

            // The internal hook runs while the router owns the pending entry. Cancelling from another
            // thread makes the cancellation path contend for that same ownership, while the waits below
            // make the interleaving deterministic instead of relying on a timing-sensitive sleep.
            let beforePendingCompletion =
                Action<int64>(fun _ ->
                    if Interlocked.Exchange(&hookCalls, 1) = 0 then
                        Task.Run(
                            Action(fun () ->
                                cancellation.Cancel()
                                cancellationFinished.TrySetResult() |> ignore)
                        )
                        |> ignore

                        cancellationObserved.Task.GetAwaiter().GetResult()
                        cancellationFinished.Task.GetAwaiter().GetResult())

            let peer = peerHandle ()
            use running = peer.Running

            let session =
                JsonRpcSession(running, 16 * 1024 * 1024, 1024, beforePendingCompletion)

            let call =
                session.RequestRawAsync(
                    "race/answer-before-cancellation",
                    null,
                    TimeSpan.FromSeconds 30.0,
                    cancellation.Token
                )

            use! request = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId request) "\"answer wins\""))

            let! answer = call
            assertRaw "\"answer wins\"" answer
            Assert.That(cancellation.IsCancellationRequested, Is.True)

            // The response claimed exactly one pending entry; the session must still be able to allocate,
            // send, and complete a later request after the losing cancellation continuation has run.
            let next =
                session.RequestRawAsync("after/answer-race", null, TimeSpan.FromSeconds 30.0)

            use! nextRequest = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId nextRequest) "\"next answer\""))

            let! nextAnswer = next
            assertRaw "\"next answer\"" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``the timeout also bounds writing the frame to a peer that stopped reading its stdin``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            // One frame the peer does read first, so the run's interactive stdin has certainly been
            // claimed and handed over before the stall below: what the deadline then interrupts can only
            // be the write itself, never the claim ahead of it (which tears nothing and fails alone).
            match! session.NotifyRawAsync("initialized", null) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"the first notification must reach the peer, got {error}"

            use! _claimed = nextFrame peer.Stdin

            // The peer stopped draining its stdin, so the write itself blocks — the half of the call a
            // deadline armed only around the wait for an answer would never bound.
            peer.Stdin.StallWrites()

            match! session.RequestRawAsync("initialize", null, TimeSpan.FromMilliseconds 200.0) with
            | Error(ProcessError.Timeout(program, timeout, _, _)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds 200.0))
            | other -> Assert.Fail $"expected a typed timeout on the blocked send, got {other}"

            // The peer may have received that frame truncated, and cannot resynchronize from one: the
            // session is over rather than pretending the next message would be understood. Probed with a
            // notification, which never waits for an answer, so a session that wrongly stayed usable
            // fails this assertion instead of hanging the test on an answer that will not come.
            match! session.NotifyRawAsync("exit", null) with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected the torn session to refuse a later send, got {other}"

            // Only the OUTGOING frame was torn, so what the peer says still arrives: a session that also
            // shut its inbound half would be discarding messages it can still be trusted to deliver.
            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}")
            let messages = session.MessagesAsync().GetAsyncEnumerator()
            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            Assert.That(messages.Current.Method, Is.EqualTo "window/logMessage")
            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a send the caller's token ends is a cancellation, not a timeout``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            use cancellation = new CancellationTokenSource()

            peer.Stdin.StallWrites()

            // A 30-second budget that cannot have elapsed: whichever token ends the write, the failure
            // must name the one that actually fired.
            let call =
                session.RequestRawAsync("initialize", null, TimeSpan.FromSeconds 30.0, cancellation.Token)

            // Cancelled only once the frame is genuinely being written — the stalled peer is holding that
            // write — so this pins the torn-frame path instead of racing the send's own start-up, where
            // the same token would (correctly) fail the call alone.
            do! peer.Stdin.NextStalledWriteAsync()
            cancellation.Cancel()

            match! call with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected a cancellation on the blocked send, got {other}"

            match! session.NotifyRawAsync("exit", null) with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected the torn session to refuse a later send, got {other}"
        }
        :> Task

    [<Test>]
    member _.``an already-cancelled token fails only its own send, never the conversation``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            // The token was already cancelled when either call began, so neither can have written a byte
            // into the peer's stdin: nothing was torn, and the conversation must survive both.
            match! session.RequestRawAsync("textDocument/completion", null, cancellation.Token) with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected a cancellation, got {other}"

            match! session.NotifyRawAsync("$/cancelRequest", null, cancellation.Token) with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected a cancellation, got {other}"

            // A send with a live token is the cheap probe, and it comes first: a session wrongly ended by
            // either call above refuses it outright, failing this test instead of hanging it on a frame
            // that would never be written.
            match! session.NotifyRawAsync("initialized", null) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"a cancelled send must not end the conversation, got {error}"

            // It is also the FIRST frame the peer receives, which is what proves the two cancelled calls
            // wrote nothing at all.
            use! probe = nextFrame peer.Stdin
            Assert.That(methodOf probe, Is.EqualTo "initialized")

            // ...and the conversation still works end to end.
            let call =
                session.RequestRawAsync("textDocument/hover", null, TimeSpan.FromSeconds 30.0)

            use! sent = nextFrame peer.Stdin
            Assert.That(methodOf sent, Is.EqualTo "textDocument/hover")
            peer.Stdout.Emit(framed (responseJson (rawId sent) "\"still talking\""))

            let! answer = call
            assertRaw "\"still talking\"" answer
        }
        :> Task

    [<Test>]
    member _.``an interruption while the stdin source feeder still owns the pipe fails alone``() : Task =
        task {
            // A `Stdin(source)` + `KeepStdinOpen` run: the framing layer claims the interactive pipe at
            // once but is handed it only when the source feeder has finished draining, so a send parks
            // there — ahead of every gate and every byte. An interruption in that window reaches this
            // session as the same `Cancelled`/`Timeout` a torn frame does, and treating it as torn would
            // kill a conversation whose stream the peer never even saw move.
            let feeding =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let peer = peerHandleFeeding (fun () -> feeding.Task.GetAwaiter().GetResult())
            use running = peer.Running
            let session = JsonRpcSession(running)
            use cancellation = new CancellationTokenSource()

            try
                // A per-request deadline that elapses while the send is parked on that feeder.
                match! session.RequestRawAsync("textDocument/hover", null, TimeSpan.FromMilliseconds 150.0) with
                | Error(ProcessError.Timeout(program, timeout, _, _)) ->
                    Assert.That(program, Is.EqualTo "language-server")
                    Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds 150.0))
                | other -> Assert.Fail $"expected the parked send to time out alone, got {other}"

                // Probed with an already-cancelled token, which never reaches the pipe and so cannot park
                // behind the feeder: `Cancelled` is that probe's own token and means the conversation is
                // still open, while the deadline above coming back here would mean a call that wrote
                // nothing had ended it.
                use probeToken = new CancellationTokenSource()
                probeToken.Cancel()

                match! session.NotifyRawAsync("$/cancelRequest", null, probeToken.Token) with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"the parked deadline must not have ended the conversation, got {other}"

                // ...and the caller's own token, the completion an editor abandons on the next keystroke.
                let call =
                    session.RequestRawAsync("textDocument/completion", null, cancellation.Token)

                let parked =
                    "the queued send returned at once: either it never waited for the stdin source feeder, or an interruption that wrote nothing had already ended the conversation"

                let! early = Task.WhenAny(call :> Task, Task.Delay 200)
                Assert.That(obj.ReferenceEquals(early, call), Is.False, parked)

                cancellation.Cancel()

                match! call with
                | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
                | other -> Assert.Fail $"expected the parked send to be cancelled alone, got {other}"

                // The feeder finishes and the conversation must be intact: neither interruption above
                // wrote a byte, so neither may have torn a frame.
                feeding.TrySetResult() |> ignore

                match! session.NotifyRawAsync("initialized", null) with
                | Ok() -> ()
                | Error error ->
                    Assert.Fail $"an interruption before the first byte must not end the conversation, got {error}"

                // It is also the FIRST frame the peer receives, which is what proves the two interrupted
                // calls wrote nothing at all.
                use! probe = nextFrame peer.Stdin
                Assert.That(methodOf probe, Is.EqualTo "initialized")

                // ...and the conversation still works end to end.
                let next = session.RequestRawAsync("shutdown", null, TimeSpan.FromSeconds 30.0)

                use! shutdown = nextFrame peer.Stdin
                peer.Stdout.Emit(framed (responseJson (rawId shutdown) "null"))

                let! answer = next
                assertRaw "null" answer
            finally
                // Never leave the modelled feeder parked on a pool thread, whatever an assertion decided.
                feeding.TrySetResult() |> ignore
        }
        :> Task

    [<Test>]
    member _.``a per-request timeout that elapses behind another send fails alone``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            // The peer stops draining its stdin part-way through the first request's frame, so that
            // request holds the session's send gate and everything else queues behind it.
            peer.Stdin.StallWrites()

            let held =
                session.RequestRawAsync("textDocument/formatting", null, TimeSpan.FromSeconds 30.0)

            do! peer.Stdin.NextStalledWriteAsync()

            // The queued request's own 150 ms budget elapses while it is still waiting for that gate:
            // not one byte of ITS frame was written, so it fails alone instead of ending the session.
            match! session.RequestRawAsync("textDocument/hover", null, TimeSpan.FromMilliseconds 150.0) with
            | Error(ProcessError.Timeout(program, timeout, _, _)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds 150.0))
            | other -> Assert.Fail $"expected the queued request to time out alone, got {other}"

            // The peer reads again: the first request's frame finishes and is answered — which a session
            // killed by the timeout above could no longer do.
            peer.Stdin.ResumeWrites()
            use! sent = nextFrame peer.Stdin
            Assert.That(methodOf sent, Is.EqualTo "textDocument/formatting")
            peer.Stdout.Emit(framed (responseJson (rawId sent) "[]"))

            let! answer = held
            assertRaw "[]" answer

            // ...and the conversation carries on past it.
            let next = session.RequestRawAsync("shutdown", null, TimeSpan.FromSeconds 30.0)
            use! shutdown = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId shutdown) "null"))

            let! nextAnswer = next
            assertRaw "null" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``a cancelled request reports Cancelled and stops waiting``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            use cancellation = new CancellationTokenSource()

            let call = session.RequestRawAsync("slow/call", null, cancellation.Token)
            use! sent = nextFrame peer.Stdin
            cancellation.Cancel()

            match! call with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected a cancellation, got {other}"

            // The answer that arrives after cancellation is discarded, and the same session can still
            // correlate a later request instead of letting the old id consume its waiter.
            peer.Stdout.Emit(framed (responseJson (rawId sent) "\"too late after cancellation\""))

            let next =
                session.RequestRawAsync("after/cancellation", null, TimeSpan.FromSeconds 30.0)

            use! nextRequest = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId nextRequest) "\"still works\""))

            let! nextAnswer = next
            assertRaw "\"still works\"" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``the peer's output ending while a request waits is typed, and later calls fail fast``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let call = session.RequestRawAsync("initialize", null)
            use! _sent = nextFrame peer.Stdin
            peer.Stdout.Complete()

            match! call with
            | Error(ProcessError.Io detail) -> Assert.That(detail, Does.Contain "language-server")
            | other -> Assert.Fail $"expected a typed I/O failure, got {other}"

            // No second wait for an answer that can never arrive: the session remembers that the peer's
            // output is over.
            match! session.RequestRawAsync("shutdown", null) with
            | Error(ProcessError.Io _) -> ()
            | other -> Assert.Fail $"expected the ended session to fail fast, got {other}"

            // The background router finishes with the peer's output rather than lingering — and it
            // completes, never faults, so it can never surface as an unobserved task exception.
            do! session.RouterTask
            Assert.That(session.RouterTask.IsCompletedSuccessfully, Is.True)
        }
        :> Task

    [<Test>]
    member _.``notifications and peer requests reach the message stream, not the pending request``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            let call = session.RequestRawAsync("initialize", null)
            use! request = nextFrame peer.Stdin

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\",\"params\":{\"type\":3}}")

            peer.Stdout.Emit(
                framed
                    "{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"workspace/configuration\",\"params\":{\"items\":[]}}"
            )

            peer.Stdout.Emit(framed (responseJson (rawId request) "{\"capabilities\":{}}"))

            // The request is satisfied by its own answer, not by the two messages that arrived first.
            let! answer = call
            assertRaw "{\"capabilities\":{}}" answer

            let! hasNotification = messages.MoveNextAsync()
            Assert.That(hasNotification, Is.True)
            let notification = messages.Current
            Assert.That(notification.Method, Is.EqualTo "window/logMessage")
            Assert.That(notification.IsRequest, Is.False)
            let notificationIdMessage = "a notification carries no id"
            Assert.That(notification.Id.IsNone, Is.True, notificationIdMessage)
            Assert.That(notification.ParamsJson, Is.EqualTo(Some "{\"type\":3}"))

            let! hasRequest = messages.MoveNextAsync()
            Assert.That(hasRequest, Is.True)
            let peerRequest = messages.Current
            Assert.That(peerRequest.Method, Is.EqualTo "workspace/configuration")
            Assert.That(peerRequest.IsRequest, Is.True)
            Assert.That(peerRequest.Id, Is.EqualTo(Some "\"srv-1\""))

            // Answering it echoes the peer's own id verbatim, quotes and all.
            match! session.RespondAsync(peerRequest, { Contents = "settings" }, hoverResultTypeInfo) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the response to be sent, got {error}"

            use! sentResponse = nextFrame peer.Stdin
            Assert.That(rawId sentResponse, Is.EqualTo "\"srv-1\"")

            Assert.That(
                sentResponse.RootElement.GetProperty("result").GetProperty("Contents").GetString(),
                Is.EqualTo "settings"
            )

            // A notification is not a question: there is nothing to answer, and saying so is typed.
            match! session.RespondErrorAsync(notification, -32601, "Method not found") with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "notification")
            | other -> Assert.Fail $"expected answering a notification to be unsupported, got {other}"

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a peer request cannot be answered through a different session``() : Task =
        task {
            let peerA = peerHandle ()
            let peerB = peerHandle ()
            use runningA = peerA.Running
            use runningB = peerB.Running
            let sessionA = JsonRpcSession(runningA)
            let sessionB = JsonRpcSession(runningB)
            let messages = sessionA.MessagesAsync().GetAsyncEnumerator()

            peerA.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"workspace/configuration\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            let request = messages.Current

            match! sessionB.RespondRawAsync(request, "null") with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "different JsonRpcSession")
            | other -> Assert.Fail $"expected a cross-session response to be unsupported, got {other}"

            // A foreign session's rejected attempt must not consume the originating session's one reply.
            match! sessionA.RespondRawAsync(request, "null") with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the originating session to answer, got {error}"

            use! sent = nextFrame peerA.Stdin
            Assert.That(rawId sent, Is.EqualTo "17")

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a response cancelled before writing can be retried``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"workspace/configuration\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            let request = messages.Current
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            match!
                session.RespondAsync(
                    request,
                    { Contents = "cancelled attempt" },
                    hoverResultTypeInfo,
                    cancellation.Token
                )
            with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected the first response attempt to be cancelled, got {other}"

            match! session.RespondAsync(request, { Contents = "settings" }, hoverResultTypeInfo) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the response retry to be sent, got {error}"

            use! sent = nextFrame peer.Stdin
            Assert.That(rawId sent, Is.EqualTo "18")

            Assert.That(
                sent.RootElement.GetProperty("result").GetProperty("Contents").GetString(),
                Is.EqualTo "settings"
            )

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a peer request cannot be answered twice``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"workspace/configuration\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            let request = messages.Current

            match! session.RespondRawAsync(request, "{\"items\":[]}") with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the first response to be sent, got {error}"

            use! first = nextFrame peer.Stdin
            Assert.That(rawId first, Is.EqualTo "18")

            match! session.RespondErrorAsync(request, -32601, "Method not found") with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "more than once")
            | other -> Assert.Fail $"expected the duplicate response to be unsupported, got {other}"

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a peer request can be answered once through its originating session``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":19,\"method\":\"workspace/configuration\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)

            match! session.RespondAsync(messages.Current, { Contents = "settings" }, hoverResultTypeInfo) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the response to be sent, got {error}"

            use! sent = nextFrame peer.Stdin
            Assert.That(rawId sent, Is.EqualTo "19")

            Assert.That(
                sent.RootElement.GetProperty("result").GetProperty("Contents").GetString(),
                Is.EqualTo "settings"
            )

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``an error answer to a peer request carries its code and message``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"client/registerCapability\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)

            match! session.RespondErrorAsync(messages.Current, -32601, "Method not found") with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the error answer to be sent, got {error}"

            use! sent = nextFrame peer.Stdin
            Assert.That(rawId sent, Is.EqualTo "7")
            let error = sent.RootElement.GetProperty "error"
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo -32601)
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo "Method not found")

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a frame that is not a JSON-RPC message ends the session with a typed parse failure``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            let call = session.RequestRawAsync("initialize", null)
            use! _sent = nextFrame peer.Stdin
            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"greeting\":\"hello\"}")

            match! call with
            | Error(ProcessError.Parse(program, _)) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected a typed parse failure, got {other}"

            // The same failure is what a consumer enumerating messages sees, rather than a silent end.
            let faulted =
                Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> messages.MoveNextAsync().AsTask()))

            match faulted with
            | null -> Assert.Fail "expected the message stream to fault"
            | error ->
                match error.Error with
                | ProcessError.Parse _ -> ()
                | other -> Assert.Fail $"expected Parse, got {other}"

            match! session.RequestRawAsync("shutdown", null) with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected the ended session to fail fast, got {other}"

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``malformed JSON-RPC versions are rejected before message routing``() : Task =
        task {
            let malformedFrames =
                [| "missing", """{"method":"invalid/version"}"""
                   "non-string", """{"jsonrpc":2,"method":"invalid/version"}"""
                   "old-version", """{"jsonrpc":"1.0","method":"invalid/version"}"""
                   "other-version", """{"jsonrpc":"2.0-preview","method":"invalid/version"}""" |]

            for caseName, json in malformedFrames do
                let peer = peerHandle ()
                use running = peer.Running
                let session = JsonRpcSession(running)
                let messages = session.MessagesAsync().GetAsyncEnumerator()

                peer.Stdout.Emit(framed json)

                let faulted =
                    Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> messages.MoveNextAsync().AsTask()))

                match faulted with
                | null -> Assert.Fail $"expected the {caseName} JSON-RPC version to fault the message stream"
                | error ->
                    match error.Error with
                    | ProcessError.Parse(program, _) -> Assert.That(program, Is.EqualTo "language-server")
                    | other -> Assert.Fail $"expected Parse for the {caseName} JSON-RPC version, got {other}"

                do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``malformed JSON-RPC versions cannot complete a pending request``() : Task =
        task {
            let malformedResponses =
                [| "missing", fun id -> String.Concat("""{"id":""", id, ""","result":null}""")
                   "non-string", fun id -> String.Concat("""{"jsonrpc":2,"id":""", id, ""","result":null}""")
                   "old-version", fun id -> String.Concat("""{"jsonrpc":"1.0","id":""", id, ""","result":null}""")
                   "other-version",
                   fun id -> String.Concat("""{"jsonrpc":"2.0-preview","id":""", id, ""","result":null}""") |]

            for caseName, createResponse in malformedResponses do
                let peer = peerHandle ()
                use running = peer.Running
                let session = JsonRpcSession(running)
                let call = session.RequestRawAsync("initialize", null)

                use! request = nextFrame peer.Stdin
                peer.Stdout.Emit(framed (createResponse (rawId request)))

                match! call with
                | Error(ProcessError.Parse(program, _)) -> Assert.That(program, Is.EqualTo "language-server")
                | Ok result ->
                    Assert.Fail $"the {caseName} JSON-RPC version incorrectly completed the request with {result}"
                | Error other -> Assert.Fail $"expected Parse for the {caseName} JSON-RPC version, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a notification is sent without an id and never waits for an answer``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            match!
                session.NotifyAsync("textDocument/didOpen", { File = "Program.fs"; Line = 0 }, hoverParamsTypeInfo)
            with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the notification to be sent, got {error}"

            use! sent = nextFrame peer.Stdin
            Assert.That(methodOf sent, Is.EqualTo "textDocument/didOpen")

            let hasId, _ = sent.RootElement.TryGetProperty "id"
            let idMessage = "a notification must not carry an id"
            Assert.That(hasId, Is.False, idMessage)
            Assert.That(sent.RootElement.GetProperty("params").GetProperty("File").GetString(), Is.EqualTo "Program.fs")
        }
        :> Task

    [<Test>]
    member _.``the session owns the handle's stdout and hands out its messages once``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            match! running.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected the JSON-RPC session to own stdout, got {other}"

            session.MessagesAsync() |> ignore

            Assert.Throws<InvalidOperationException>(Action(fun () -> session.MessagesAsync() |> ignore))
            |> ignore

            Assert.That(session.MaxFrameBytes, Is.EqualTo(16 * 1024 * 1024))
        }
        :> Task

    [<Test>]
    member _.``a full inbound backlog drops the oldest notifications and counts them``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running, 16 * 1024 * 1024, 1)

            let call = session.RequestRawAsync("initialize", null)
            use! request = nextFrame peer.Stdin

            for index in 1..3 do
                peer.Stdout.Emit(framed (String.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"note/", string index, "\"}")))

            // The router handles frames in order, so the answer arriving proves all three notifications
            // have already been routed — no sleep needed to observe the drops.
            peer.Stdout.Emit(framed (responseJson (rawId request) "null"))
            let! answer = call
            assertRaw "null" answer

            let dropped = session.DroppedMessages
            let droppedMessage = "a one-message backlog must drop the two older notifications"
            Assert.That(dropped, Is.EqualTo 2L, droppedMessage)

            let messages = session.MessagesAsync().GetAsyncEnumerator()
            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            Assert.That(messages.Current.Method, Is.EqualTo "note/3")
            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a full inbound backlog drops a notification to retain a peer request``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running, 16 * 1024 * 1024, 1)

            let call = session.RequestRawAsync("initialize", null)
            use! localRequest = nextFrame peer.Stdin

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"note/1\"}")

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":\"peer-1\",\"method\":\"workspace/configuration\"}")

            // This answer is a deterministic routing fence: the router handles it only after both
            // decoded messages above, and responses never enter or wait on the message backlog.
            peer.Stdout.Emit(framed (responseJson (rawId localRequest) "null"))
            let! answer = call
            assertRaw "null" answer

            Assert.That(session.DroppedMessages, Is.EqualTo 1L)

            let messages = session.MessagesAsync().GetAsyncEnumerator()
            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)

            let peerRequest = messages.Current
            Assert.That(peerRequest.Method, Is.EqualTo "workspace/configuration")
            Assert.That(peerRequest.Id, Is.EqualTo(Some "\"peer-1\""))
            Assert.That(peerRequest.IsRequest, Is.True)

            match! session.RespondRawAsync(peerRequest, "null") with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the retained peer request to remain answerable, got {error}"

            use! peerResponse = nextFrame peer.Stdin
            Assert.That(rawId peerResponse, Is.EqualTo "\"peer-1\"")
            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``evicting a peer request faults a mixed inbound backlog instead of losing it silently``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running, 16 * 1024 * 1024, 2)

            let call = session.RequestRawAsync("initialize", null)
            use! _localRequest = nextFrame peer.Stdin

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"note/1\"}")

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"workspace/configuration\"}")

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"note/2\"}")
            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"method\":\"note/3\"}")

            // The pending local request is completed by endSession, so this is a deterministic fence
            // for the overflow without a delay or a response frame the faulted router could not read.
            match! call with
            | Error(ProcessError.OutputTooLarge(program, None, None, totalMessages, totalBytes)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(totalMessages, Is.EqualTo 3)
                Assert.That(totalBytes, Is.EqualTo 0)
            | other -> Assert.Fail $"expected a typed decoded-backlog overflow, got {other}"

            let droppedMessage = "only the admissibly lossy notification is counted as dropped"
            Assert.That(session.DroppedMessages, Is.EqualTo 1L, droppedMessage)

            match! session.RequestRawAsync("shutdown", null) with
            | Error(ProcessError.OutputTooLarge _) -> ()
            | other -> Assert.Fail $"expected the overflow to remain the session's terminal error, got {other}"

            let messages = session.MessagesAsync().GetAsyncEnumerator()

            for expected in [ "note/2"; "note/3" ] do
                let! received = messages.MoveNextAsync()
                Assert.That(received, Is.True)
                Assert.That(messages.Current.Method, Is.EqualTo expected)

            let faulted =
                Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> messages.MoveNextAsync().AsTask()))

            match faulted with
            | null -> Assert.Fail "expected the decoded-message stream to fault after draining retained notifications"
            | error ->
                match error.Error with
                | ProcessError.OutputTooLarge(_, None, None, 3, 0) -> ()
                | other -> Assert.Fail $"expected the same OutputTooLarge terminal error, got {other}"

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a lossy frame backlog is refused when the session is constructed``() : Task =
        task {
            // The OTHER backlog: `messageBacklog` above bounds decoded messages and may drop the oldest,
            // while `Command.StreamBuffer` bounds the raw frames underneath and may not — a dropped frame
            // is a message the peer is still correlating with a request. This session inherits that
            // refusal from the framing layer it owns, so the refusal surfaces out of ITS constructor.
            let command =
                (Command.create "language-server")
                    .KeepStdinOpen()
                    .StreamBuffer(StreamBufferPolicy.Bounded(2, StreamFullMode.DropNewest))

            use running =
                FakeProcess.OfCommand(command).WithContentLengthFrames([ Array.create 4 7uy ]).Build()

            let refusal =
                Assert.Throws<ProcessException>(Action(fun () -> JsonRpcSession running |> ignore))

            match refusal with
            | null -> Assert.Fail "expected a lossy frame backlog to be refused with a typed ProcessException"
            | error ->
                match error.Error with
                | ProcessError.Unsupported detail -> Assert.That(detail, Does.Contain "DropNewest")
                | other -> Assert.Fail $"expected Unsupported, got {other}"

            // Refused before anything is claimed, so the handle is left exactly as it was found.
            match! running.OutputStringAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"a refused session must leave stdout unclaimed, got {error}"
        }
        :> Task

    [<Test>]
    member _.``raw and reflected request paths agree with the typed one``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            // Reflection overload: params serialized and result deserialized without any JsonTypeInfo.
            let reflected =
                session.RequestAsync<RpcHoverParams, RpcHoverResult>(
                    "textDocument/hover",
                    { File = "Program.fs"; Line = 5 }
                )

            use! request = nextFrame peer.Stdin
            Assert.That(request.RootElement.GetProperty("params").GetProperty("Line").GetInt32(), Is.EqualTo 5)
            peer.Stdout.Emit(framed (responseJson (rawId request) "{\"Contents\":\"reflected\"}"))

            match! reflected with
            | Ok result -> Assert.That(result.Contents, Is.EqualTo "reflected")
            | Error error -> Assert.Fail $"expected the reflected result, got {error}"

            // Raw overload: JSON text in, JSON text out — including a `null` result, which a typed read
            // would reject but a raw one reports honestly.
            let raw = session.RequestRawAsync("shutdown", null)
            use! shutdown = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId shutdown) "null"))

            let! rawAnswer = raw
            assertRaw "null" rawAnswer

            // A result that does not fit the requested type is a parse failure, not a fabricated value.
            let mismatched =
                session.RequestAsync<RpcHoverParams, RpcHoverResult>(
                    "textDocument/hover",
                    { File = "Program.fs"; Line = 9 },
                    hoverParamsTypeInfo,
                    hoverResultTypeInfo
                )

            use! third = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId third) "\"not an object\""))

            match! mismatched with
            | Error(ProcessError.Parse(_, detail)) -> Assert.That(detail, Does.Contain "textDocument/hover")
            | other -> Assert.Fail $"expected a typed parse failure, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a request id that is not a string, number, or null ends the session with a typed parse failure``
        ()
        : Task =
        task {
            let malformedFrames =
                [| "boolean", """{"jsonrpc":"2.0","id":true,"method":"invalid/id"}"""
                   "object", """{"jsonrpc":"2.0","id":{"a":1},"method":"invalid/id"}"""
                   "array", """{"jsonrpc":"2.0","id":[1],"method":"invalid/id"}""" |]

            for caseName, json in malformedFrames do
                let peer = peerHandle ()
                use running = peer.Running
                let session = JsonRpcSession(running)
                let messages = session.MessagesAsync().GetAsyncEnumerator()

                peer.Stdout.Emit(framed json)

                let faulted =
                    Assert.ThrowsAsync<ProcessException>(Func<Task>(fun () -> messages.MoveNextAsync().AsTask()))

                match faulted with
                | null -> Assert.Fail $"expected the {caseName} id to fault the message stream"
                | error ->
                    match error.Error with
                    | ProcessError.Parse(program, _) -> Assert.That(program, Is.EqualTo "language-server")
                    | other -> Assert.Fail $"expected Parse for the {caseName} id, got {other}"

                do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``an explicit null id is delivered as a request, not folded into a notification``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            let messages = session.MessagesAsync().GetAsyncEnumerator()

            peer.Stdout.Emit(framed "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"window/showMessageRequest\"}")

            let! received = messages.MoveNextAsync()
            Assert.That(received, Is.True)
            let request = messages.Current
            Assert.That(request.IsRequest, Is.True, "an explicit null id must still be a request")
            Assert.That(request.Id, Is.EqualTo(Some "null"))

            match! session.RespondRawAsync(request, "null") with
            | Ok() -> ()
            | Error error -> Assert.Fail $"expected the null-id request to be answerable, got {error}"

            use! sent = nextFrame peer.Stdin
            Assert.That(rawId sent, Is.EqualTo "null")

            do! messages.DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a peer response id string must round-trip exactly, not just parse as the numeric id``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let call = session.RequestRawAsync("initialize", null)
            use! request = nextFrame peer.Stdin
            let realId = rawId request

            // Both decoys are strings `Int64.TryParse` would still accept — a leading sign, surrounding
            // whitespace — but neither is the id's own canonical decimal text, so neither may complete
            // the pending call. Only the genuine numeric-id answer, sent last, may.
            peer.Stdout.Emit(framed (responseJson ("\"+" + realId + "\"") "\"decoy-signed\""))
            peer.Stdout.Emit(framed (responseJson ("\" " + realId + " \"") "\"decoy-padded\""))
            peer.Stdout.Emit(framed (responseJson realId "\"genuine\""))

            let! answer = call
            assertRaw "\"genuine\"" answer
        }
        :> Task
