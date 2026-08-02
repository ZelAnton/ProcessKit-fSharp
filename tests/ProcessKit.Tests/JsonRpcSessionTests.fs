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

    let buffered = ResizeArray<byte>()
    let gate = obj ()
    let mutable stalled = false

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

    /// The next whole frame the session sent.
    member _.NextFrameAsync() : Task<byte[]> = frames.Reader.ReadAsync().AsTask()

    /// Stop draining this pipe, as a peer that stopped reading its own stdin does once the OS buffer
    /// fills: every later write blocks until its cancellation token ends it.
    member _.StallWrites() = stalled <- true

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
        if stalled then
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        else
            append (buffer.AsSpan(offset, count).ToArray())
            Task.CompletedTask

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) : ValueTask =
        if stalled then
            ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
        else
            append (buffer.ToArray())
            ValueTask()

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

    let peerHandle () =
        let stdout = new PeerOutputStream()
        let stdin = new PeerInputStream()

        let exit =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        let host: RunningHost =
            { Config = (Command.create "language-server").KeepStdinOpen().Config
              Pid = None
              Stdout = Some(stdout :> Stream)
              Stderr = None
              Stdin = Some(stdin :> Stream)
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> exit.Task
              StdinError = fun () -> None
              StdinFeedComplete = ignore
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
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

            let! answer = session.RequestRawAsync("never/answered", null, TimeSpan.FromMilliseconds 200.0)

            match answer with
            | Error(ProcessError.Timeout(program, timeout, _, _)) ->
                Assert.That(program, Is.EqualTo "language-server")
                Assert.That(timeout, Is.EqualTo(TimeSpan.FromMilliseconds 200.0))
            | other -> Assert.Fail $"expected a timeout, got {other}"

            // The late answer belongs to a call that has already been told it failed: it is discarded,
            // and the session keeps working for the next request.
            use! abandoned = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId abandoned) "\"too late\""))

            let next = session.RequestRawAsync("still/works", null, TimeSpan.FromSeconds 30.0)
            use! sent = nextFrame peer.Stdin
            peer.Stdout.Emit(framed (responseJson (rawId sent) "\"answered\""))

            let! nextAnswer = next
            assertRaw "\"answered\"" nextAnswer
        }
        :> Task

    [<Test>]
    member _.``the timeout also bounds writing the frame to a peer that stopped reading its stdin``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)

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
    member _.``a cancelled request reports Cancelled and stops waiting``() : Task =
        task {
            let peer = peerHandle ()
            use running = peer.Running
            let session = JsonRpcSession(running)
            use cancellation = new CancellationTokenSource()

            let call = session.RequestRawAsync("slow/call", null, cancellation.Token)
            use! _sent = nextFrame peer.Stdin
            cancellation.Cancel()

            match! call with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "language-server")
            | other -> Assert.Fail $"expected a cancellation, got {other}"
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
    member _.``a full inbound backlog drops the oldest messages and counts them``() : Task =
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
