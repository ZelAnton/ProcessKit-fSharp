namespace ProcessKit

open System
open System.Buffers
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.Globalization
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// One JSON-RPC message the peer sent that is **not** an answer to a request this session made: a
/// notification (no `id`), or a request the peer wants answered (`id` present, so `IsRequest` is true —
/// LSP servers use these for `workspace/configuration`, `window/showMessageRequest`, and similar
/// call-backs). Answers to this session's own requests never appear here: they are routed straight to
/// the `RequestAsync` call waiting on them.
///
/// The message is kept as raw JSON text rather than a deserialized value, because only the caller knows
/// what type a given `method` carries. Use `ParamsAs<'T>` for a typed read, or `ParamsJson`/`Payload` to
/// handle it by hand.
[<Sealed; NoComparison>]
type JsonRpcMessage
    internal (program: string, methodName: string, id: string option, parametersJson: string option, payload: byte[]) =

    let parseFailure (detail: string) = ProcessError.Parse(program, detail)

    // Shared by both `ParamsAs` overloads so the reflection and source-generated paths can never
    // disagree about the missing-params and malformed-params contracts.
    let readParams (deserialize: string -> 'T) : Result<'T, ProcessError> =
        match parametersJson with
        | None -> Error(parseFailure $"the '{methodName}' message carried no params")
        | Some text ->
            try
                Ok(deserialize text)
            with
            | :? JsonException as ex ->
                Error(parseFailure $"could not deserialize the params of '{methodName}': {ex.Message}")
            | :? NotSupportedException as ex ->
                Error(parseFailure $"could not deserialize the params of '{methodName}': {ex.Message}")

    /// The `method` member — what the peer is notifying about or asking for.
    member _.Method = methodName

    /// The peer's `id`, as the raw JSON text it sent (`7`, `"abc"`), or `None` for a notification.
    /// `RespondAsync`/`RespondErrorAsync` echo it back byte-for-byte, which is what the peer correlates
    /// its own pending call against.
    member _.Id = id

    /// True when the peer expects an answer (the message carried an `id`) — answer it with
    /// `RespondAsync` or `RespondErrorAsync`. False for a notification, which must not be answered.
    member _.IsRequest = id.IsSome

    /// The raw JSON text of the `params` member; `None` when the message carried none.
    member _.ParamsJson = parametersJson

    /// The frame this message was decoded from, byte-exact — for logging, or for a hand-rolled read of
    /// members this type does not surface.
    member _.Payload = payload

    /// Deserialize `params` into a `'T` using source-generated `JsonTypeInfo&lt;'T&gt;` metadata, so this
    /// overload is safe for trimmed and NativeAOT applications. A message with no `params`, or one whose
    /// `params` do not fit `'T`, is `ProcessError.Parse` — never a raw exception.
    member _.ParamsAs<'T>(typeInfo: JsonTypeInfo<'T>) : Result<'T, ProcessError> =
        ArgumentNullException.ThrowIfNull(typeInfo, nameof typeInfo)

        // Through the non-generic `JsonTypeInfo` base overload for the same reason
        // `RunningProcess.StdoutJsonLinesAsync` uses it: the BCL's generic overload returns a
        // `TValue?`-annotated value the F# nullness checker cannot reconcile against an unconstrained
        // `'T`. A JSON `null` raises here and becomes `ProcessError.Parse`, as a malformed document does.
        readParams (fun text ->
            match JsonSerializer.Deserialize(text, typeInfo :> JsonTypeInfo) with
            | null -> raise (JsonException "the params deserialized to null")
            | value -> unbox<'T> value)

    /// Deserialize `params` into a `'T` via reflection-based `System.Text.Json` (`options` omitted uses
    /// the BCL defaults). Same missing-/malformed-params contract as the `JsonTypeInfo` overload.
    ///
    /// **Trimming / AOT:** not trim-/AOT-safe — pass a `JsonTypeInfo&lt;'T&gt;` through the other
    /// overload in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes params by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this member, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes params by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this member, in a NativeAOT app.">]
    member _.ParamsAs<'T>([<Optional>] options: JsonSerializerOptions | null) : Result<'T, ProcessError> =
        let optionsArg = Option.ofObj options |> Option.toObj

        readParams (fun text ->
            match JsonSerializer.Deserialize(text, typeof<'T>, optionsArg) with
            | null -> raise (JsonException "the params deserialized to null")
            | value -> unbox<'T> value)

/// One request this session sent and has not been answered yet. The method is remembered alongside the
/// waiter because a JSON-RPC response carries only the `id` — without this, an `error` answer could not
/// say which call it belongs to.
type private PendingRequest =
    { Method: string
      Completion: TaskCompletionSource<Result<string, ProcessError>> }

/// Counts messages the inbound backlog dropped. A dedicated object rather than a `let mutable` field:
/// the channel's drop callback is a closure, and `Interlocked` needs a stable address to increment.
type private DropCounter() =
    let mutable value = 0L

    member _.Increment() = Interlocked.Increment(&value) |> ignore
    member _.Value = Interlocked.Read(&value)

/// A typed JSON-RPC 2.0 conversation with a child process that speaks `Content-Length`-framed JSON — a
/// language server (LSP), a build server (BSP), or an MCP-style tool. It is the layer above
/// `ContentLengthSession`: that type frames bytes, this one serializes values, allocates and correlates
/// request ids, separates answers from notifications, and bounds a call end to end.
///
/// ```fsharp
/// task {
///     let command = (Command.create "language-server").KeepStdinOpen()
///
///     match! command.StartAsync() with
///     | Error err -> eprintfn $"{err.Message}"
///     | Ok proc ->
///         use proc = proc
///         let session = JsonRpcSession(proc)
///
///         match! session.RequestAsync<InitializeParams, InitializeResult>("initialize", parameters, Lsp.Default.InitializeParams, Lsp.Default.InitializeResult, TimeSpan.FromSeconds 30.0) with
///         | Ok result -> printfn $"{result.ServerInfo}"
///         | Error err -> eprintfn $"{err.Message}"
/// }
/// ```
///
/// **Not a debug-adapter (DAP) client.** DAP borrows LSP's `Content-Length` framing but not its
/// envelope: its messages look like `{"seq":1,"type":"request","command":"next","arguments":{}}` — no
/// `jsonrpc`, no `method`, no `id` — so they are not JSON-RPC, and this session ends on the first one
/// with `ProcessError.Parse` rather than guessing. Drive a debug adapter with `ContentLengthSession`
/// directly and decode that envelope yourself.
///
/// **This session owns the run's framed transport.** Constructing it creates the one
/// `ContentLengthSession` over the handle and immediately claims its frames, so the handle's stdout
/// belongs to this session: capturing/streaming verbs, a `PtySession`, or a second framed session on the
/// same `RunningProcess` are refused afterwards, and there is deliberately no way to enumerate the raw
/// frames alongside it — a second reader would tear the peer's messages between two consumers. Dispose
/// the `RunningProcess` (or its owning `ProcessGroup`) to reap the tree; the session itself holds no OS
/// resource of its own. Build the command with `Command.KeepStdinOpen()`, or every send reports a typed
/// `ProcessError.Unsupported`.
///
/// **Concurrency.** Requests may be issued concurrently: each gets its own `id`, and the router matches
/// every answer against the request that is waiting for that exact `id`. Sends are serialized by the
/// framing layer, so two concurrent requests can never interleave inside one frame.
///
/// **Failures are typed, never raw exceptions and never a silent hang** (see `ProcessError`):
///
/// - the peer answered with an `error` object — `ProcessError.JsonRpc`, carrying its `code`, `message`,
///   and the raw JSON of `data`;
/// - the request timed out — `ProcessError.Timeout` (per-request overloads only; the budget covers the
///   whole call, writing the frame included, so a peer that stopped reading its own stdin cannot hang
///   one. Without a timeout a request waits until the peer answers, the peer's output ends, or the
///   caller's token fires);
/// - the caller's `CancellationToken` fired — `ProcessError.Cancelled`;
/// - either of those two interrupted a send: the frame may have reached the peer truncated, and no peer
///   can resynchronize from that, so the failure also ends the conversation — pending requests fail with
///   it and every later request/send reports it instead of writing into a stream the peer can no longer
///   read. A torn *outgoing* frame does not corrupt what the peer says, so `MessagesAsync` keeps
///   delivering incoming messages until the peer's output ends;
/// - the peer's framed output ended (it exited, or closed stdout) while a request was pending —
///   `ProcessError.Io`, and every later verb fails the same way instead of waiting forever;
/// - the peer sent something that is not a JSON-RPC message — `ProcessError.Parse`, which ends the whole
///   session: a peer that is not speaking the protocol cannot be understood message by message. Pending
///   requests all fail with it, and `MessagesAsync` faults with `ProcessException` carrying it.
///
/// An answer whose `id` matches no waiting request — typically a late reply to a request that already
/// timed out — is discarded, since the caller has already been told the request failed.
[<Sealed>]
type JsonRpcSession(running: RunningProcess, maxFrameBytes: int, messageBacklog: int) =
    do ArgumentNullException.ThrowIfNull(running, nameof running)
    do ArgumentOutOfRangeException.ThrowIfLessThan(messageBacklog, 1, nameof messageBacklog)

    let config = running.Config
    let program = config.Program

    // Constructing the transport claims the handle's stdout (`ContentLengthSession` refuses a handle
    // another verb already owns), and this session is its only consumer: `FramesAsync()` is enumerated
    // exactly once, by the router below, and never handed out (KB K-031).
    let transport = ContentLengthSession(running, maxFrameBytes)

    let gate = obj ()
    let pending = Dictionary<int64, PendingRequest>()
    let dropped = DropCounter()
    let mutable nextId = 0L
    let mutable ended: ProcessError option = None
    let mutable messagesClaimed = 0

    // Bounded, dropping the OLDEST message when full, so a caller that never enumerates `MessagesAsync`
    // (or reads it slowly) cannot grow this queue without limit — a chatty LSP server emits `$/progress`
    // and `window/logMessage` continuously. Blocking the router instead would be worse: it would also
    // stall the answers `RequestAsync` is waiting for, deadlocking the conversation. Drops are counted in
    // `DroppedMessages` rather than hidden.
    let messages =
        Channel.CreateBounded<JsonRpcMessage>(
            BoundedChannelOptions(
                messageBacklog,
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            ),
            (fun _ -> dropped.Increment())
        )

    let parseFailure (detail: string) = ProcessError.Parse(program, detail)

    // A frame the peer sent that is not a JSON-RPC message. Raised out of the frame handler, caught by
    // the router, and turned into the session's terminal error there.
    let protocolFault (detail: string) : exn =
        ProcessException(parseFailure detail) :> exn

    let readId (element: JsonElement) : int64 option =
        match element.ValueKind with
        | JsonValueKind.Number ->
            match element.TryGetInt64() with
            | true, value -> Some value
            | _ -> None
        // This session only ever sends numeric ids, but a peer echoing one back as a string is common
        // enough in the wild to accept: a string that is exactly one of our ids still identifies it.
        | JsonValueKind.String ->
            match element.GetString() with
            | null -> None
            | text ->
                match Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, value -> Some value
                | _ -> None
        | _ -> None

    let readErrorObject (element: JsonElement) : int * string * string option =
        if element.ValueKind <> JsonValueKind.Object then
            raise (protocolFault "the peer answered with an 'error' member that is not an object")

        let code =
            match element.TryGetProperty "code" with
            | true, value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, code -> code
                | _ -> raise (protocolFault "the peer's JSON-RPC error code is not a 32-bit integer")
            | _ -> raise (protocolFault "the peer's JSON-RPC error object carries no numeric 'code'")

        // `message` is required by the spec, but a missing or non-string one is not worth killing a
        // conversation over: the code already identifies the failure, so an empty text is reported.
        let detail =
            match element.TryGetProperty "message" with
            | true, value when value.ValueKind = JsonValueKind.String ->
                match value.GetString() with
                | null -> ""
                | text -> text
            | _ -> ""

        let data =
            match element.TryGetProperty "data" with
            | true, value -> Some(value.GetRawText())
            | _ -> None

        code, detail, data

    let takePending (id: int64) : PendingRequest option =
        lock gate (fun () ->
            match pending.TryGetValue id with
            | true, entry ->
                pending.Remove id |> ignore
                Some entry
            | _ -> None)

    // Register a waiter, unless the conversation is already over — checked and stored under the same
    // lock the router ends the session with, so a request racing the peer's exit either joins the
    // routing table (and is failed by `endSession`) or fails immediately. It can never be left waiting
    // for an answer nobody will deliver.
    let registerPending
        (id: int64)
        (methodName: string)
        (completion: TaskCompletionSource<Result<string, ProcessError>>)
        : Result<unit, ProcessError> =
        lock gate (fun () ->
            match ended with
            | Some error -> Error error
            | None ->
                pending[id] <-
                    { Method = methodName
                      Completion = completion }

                Ok())

    let endSession (error: ProcessError) =
        let waiters =
            lock gate (fun () ->
                if ended.IsNone then
                    ended <- Some error

                let waiters = pending.Values |> Seq.toArray
                pending.Clear()
                waiters)

        for waiter in waiters do
            waiter.Completion.TrySetResult(Error error) |> ignore

    let currentFault () = lock gate (fun () -> ended)

    // A send the caller's token or a request's deadline interrupted may have written only PART of a
    // frame — the framing layer says exactly that ("abandon the session after it"), and a peer stuck
    // mid-parse of a truncated payload reads whatever follows as the rest of that payload. Nothing sent
    // afterwards can be understood, so the interruption becomes the session's terminal failure instead
    // of a per-call error that pretends the conversation can continue. Other send failures tore nothing:
    // `Unsupported` never wrote a byte, and a broken pipe is reported by the router when the peer's
    // output ends.
    let endTornSend (error: ProcessError) =
        match error with
        | ProcessError.Cancelled _
        | ProcessError.Timeout _ -> endSession error
        | _ -> ()

    // Decode one frame and either complete the request waiting for it or queue it for `MessagesAsync`.
    // Anything that is not a JSON-RPC message raises, ending the session (see the type's doc comment).
    let handleFrame (payload: byte[]) =
        let document =
            try
                JsonDocument.Parse(ReadOnlyMemory<byte> payload)
            with :? JsonException as ex ->
                raise (protocolFault $"the peer sent a frame that is not valid JSON: {ex.Message}")

        use document = document
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            // A JSON-RPC batch (an array) lands here too: LSP forbids batching, and answering half a
            // batch would be worse than refusing it.
            raise (protocolFault "the peer sent a frame whose JSON root is not an object")

        let property (name: string) =
            match root.TryGetProperty name with
            | true, value -> Some value
            | _ -> None

        let id =
            match property "id" with
            | Some value when value.ValueKind <> JsonValueKind.Null -> Some value
            | _ -> None

        match property "method" with
        | Some methodElement ->
            // A `method` member makes it a notification (no id) or a request the peer wants answered.
            let methodName =
                if methodElement.ValueKind <> JsonValueKind.String then
                    raise (protocolFault "the peer sent a message whose 'method' is not a string")
                else
                    match methodElement.GetString() with
                    | null -> raise (protocolFault "the peer sent a message whose 'method' is not a string")
                    | text -> text

            let message =
                JsonRpcMessage(
                    program,
                    methodName,
                    id |> Option.map (fun value -> value.GetRawText()),
                    property "params" |> Option.map (fun value -> value.GetRawText()),
                    payload
                )

            // Never blocks (see the channel's drop policy), so a consumer that is not reading its
            // messages can never stall the answers other calls are waiting for.
            messages.Writer.TryWrite message |> ignore
        | None ->
            match id with
            | None ->
                raise (protocolFault "the peer sent a frame that is neither a request, a notification, nor a response")
            | Some idElement ->
                let error = property "error"
                let result = property "result"

                if error.IsSome && result.IsSome then
                    raise (protocolFault "the peer answered with both a 'result' and an 'error'")

                // Read the error object BEFORE claiming the waiter: a malformed one ends the session,
                // and a waiter already removed from the table would then never be completed by
                // `endSession` — it would wait forever.
                let answered =
                    match error with
                    | Some element ->
                        let code, detail, data = readErrorObject element
                        Choice1Of2(code, detail, data)
                    | None ->
                        match result with
                        | Some element -> Choice2Of2(element.GetRawText())
                        | None -> raise (protocolFault "the peer answered with neither a 'result' nor an 'error'")

                match readId idElement |> Option.bind takePending with
                // Nobody is waiting for this id: a late answer to a request that already timed out or
                // was cancelled, or an id this session never issued. Discarded, not a session failure.
                | None -> ()
                | Some waiter ->
                    let outcome =
                        match answered with
                        | Choice1Of2(code, detail, data) ->
                            Error(ProcessError.JsonRpc(program, waiter.Method, code, detail, data))
                        | Choice2Of2 text -> Ok text

                    waiter.Completion.TrySetResult outcome |> ignore

    // The single reader of the framed transport. `backgroundTask` (KB K-009), not `task`: a caller may
    // block on a request from a single-threaded SynchronizationContext (a UI thread, classic ASP.NET),
    // and a router that captured that context would then be waiting to post its continuation back to the
    // very thread that is blocked on it.
    let router =
        backgroundTask {
            let frames = transport.FramesAsync().GetAsyncEnumerator()
            let mutable fault: exn option = None

            try
                let mutable reading = true

                while reading do
                    let! moved = frames.MoveNextAsync()

                    if moved then
                        handleFrame frames.Current
                    else
                        reading <- false
            with ex ->
                fault <- Some ex

            try
                do! frames.DisposeAsync()
            with ex ->
                // The enumerator's own teardown failed after the stream already ended; the fault that
                // ended it (or the clean end of it) is the honest report, so this one is not promoted.
                if fault.IsNone then
                    fault <- Some ex

            match fault with
            | None ->
                // A clean end of the peer's output. Requests still waiting can never be answered now,
                // and neither can any later one.
                endSession (ProcessError.Io $"the framed output of '{program}' ended; no JSON-RPC answer can arrive")

                messages.Writer.TryComplete() |> ignore
            | Some ex ->
                let error =
                    match ex with
                    | :? ProcessException as processException -> processException.Error
                    | :? OperationCanceledException -> ProcessError.Cancelled program
                    | _ -> ProcessError.Io ex.Message

                endSession error
                // Faulting the reader is what makes a protocol failure visible to a consumer that is
                // enumerating messages rather than making requests.
                messages.Writer.TryComplete ex |> ignore
        }
        :> Task

    /// A session over `running` with a custom maximum frame size, using the default 1024-message
    /// inbound backlog.
    new(running: RunningProcess, maxFrameBytes: int) = JsonRpcSession(running, maxFrameBytes, 1024)

    /// A session over `running` using the framing layer's default 16 MiB maximum frame size and a
    /// 1024-message inbound backlog.
    new(running: RunningProcess) = JsonRpcSession(running, 16 * 1024 * 1024, 1024)

    // ---- message construction -------------------------------------------------------------------

    // Every outgoing message is `{"jsonrpc":"2.0", ...}`; `writeBody` adds what makes it a request, a
    // notification, or a response. One builder, so the three can never disagree about the envelope.
    member private _.Encode(writeBody: Utf8JsonWriter -> unit) : byte[] =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("jsonrpc", "2.0")
        writeBody writer
        writer.WriteEndObject()
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    // Every outgoing frame goes through here so an interrupted write ends the session exactly once, in
    // one place, whatever it was carrying (see `endTornSend`).
    member private _.SendFramed
        (payload: byte[], cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            match! transport.SendAsync(payload, cancellationToken) with
            | Ok() -> return Ok()
            | Error error ->
                endTornSend error
                return Error error
        }

    // Wait for the answer to an already-sent request under the call's own deadline (`armed`, ticking
    // since before the frame was written) and the caller's token, both already folded into `linkedToken`.
    // Whichever ends the wait, the waiter is removed so a late answer is discarded rather than completing
    // a call that has already reported a failure.
    member private _.AwaitAnswer
        (
            id: int64,
            completion: TaskCompletionSource<Result<string, ProcessError>>,
            armed: TimeSpan option,
            linkedToken: CancellationToken,
            cancellationToken: CancellationToken
        ) : Task<Result<string, ProcessError>> =
        task {
            try
                return! completion.Task.WaitAsync linkedToken
            with :? OperationCanceledException ->
                takePending id |> ignore

                // The answer can land in the instant between the deadline firing and this handler
                // running; reporting a timeout for an answer that did arrive would be a lie.
                if completion.Task.IsCompletedSuccessfully then
                    return completion.Task.Result
                elif cancellationToken.IsCancellationRequested then
                    return Error(ProcessError.Cancelled program)
                else
                    match armed with
                    | Some span -> return Error(ProcessError.Timeout(program, span, "", ""))
                    | None -> return Error(ProcessError.Cancelled program)
        }

    // The one request path: allocate an id, register the waiter, arm the call's deadline, send the frame,
    // wait for the answer.
    member private this.RequestCore
        (
            methodName: string,
            writeParams: (Utf8JsonWriter -> unit) option,
            timeout: TimeSpan option,
            cancellationToken: CancellationToken
        ) : Task<Result<string, ProcessError>> =
        let id = Interlocked.Increment &nextId

        // Encoded BEFORE the waiter is registered, so a `parameters` value the serializer refuses (or
        // raw params text that is not JSON) raises out of this verb like any other invalid argument,
        // leaving no waiter behind for an answer that will never be asked for.
        let payload =
            this.Encode(fun writer ->
                writer.WriteNumber("id", id)
                writer.WriteString("method", methodName)

                match writeParams with
                | Some write ->
                    writer.WritePropertyName "params"
                    write writer
                | None -> ())

        let completion =
            TaskCompletionSource<Result<string, ProcessError>>(TaskCreationOptions.RunContinuationsAsynchronously)

        match registerPending id methodName completion with
        | Error error -> Task.FromResult(Error error)
        | Ok() ->
            task {
                // Clamped so an out-of-range span cannot throw out of the CTS constructor, and the
                // CLAMPED value is what `ProcessError.Timeout` reports — the same rule the readiness
                // probes follow, so a reported budget is always the one that was actually enforced.
                let armed = timeout |> Option.map Timeouts.clampArmable

                // Armed BEFORE the frame is written, and kept for the wait that follows, so ONE budget
                // covers the whole call. Writing is not free time: a peer that stopped reading its stdin
                // blocks the write as soon as the pipe buffer fills, and a deadline that started only
                // after a successful send would never fire on the very peer it was passed for.
                use deadline =
                    match armed with
                    | Some span -> new CancellationTokenSource(span, config.TimeProvider)
                    | None -> new CancellationTokenSource()

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken)

                match! transport.SendAsync(payload, linked.Token) with
                | Error error ->
                    takePending id |> ignore

                    // The framing layer reports both interruptions as `Cancelled` — it only ever sees one
                    // token — so who ended the send is decided here: this call's deadline is a timeout,
                    // the caller's token a cancellation.
                    let reported =
                        match error, armed with
                        | ProcessError.Cancelled _, Some span when
                            deadline.IsCancellationRequested
                            && not cancellationToken.IsCancellationRequested
                            ->
                            ProcessError.Timeout(program, span, "", "")
                        | _ -> error

                    endTornSend reported
                    return Error reported
                | Ok() -> return! this.AwaitAnswer(id, completion, armed, linked.Token, cancellationToken)
            }

    // Deserialize a request's raw `result` text into `'R`, reporting a value that does not fit as
    // `ProcessError.Parse` rather than letting the serializer's exception escape.
    member private this.RequestTyped
        (
            methodName: string,
            writeParams: (Utf8JsonWriter -> unit) option,
            timeout: TimeSpan option,
            cancellationToken: CancellationToken,
            deserialize: string -> 'R
        ) : Task<Result<'R, ProcessError>> =
        task {
            match! this.RequestCore(methodName, writeParams, timeout, cancellationToken) with
            | Error error -> return Error error
            | Ok text ->
                try
                    return Ok(deserialize text)
                with
                | :? JsonException as ex ->
                    return Error(parseFailure $"could not deserialize the result of '{methodName}': {ex.Message}")
                | :? NotSupportedException as ex ->
                    return Error(parseFailure $"could not deserialize the result of '{methodName}': {ex.Message}")
        }

    member private this.SendNotification
        (methodName: string, writeParams: (Utf8JsonWriter -> unit) option, cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        match currentFault () with
        | Some error -> Task.FromResult(Error error)
        | None ->
            let payload =
                this.Encode(fun writer ->
                    writer.WriteString("method", methodName)

                    match writeParams with
                    | Some write ->
                        writer.WritePropertyName "params"
                        write writer
                    | None -> ())

            this.SendFramed(payload, cancellationToken)

    member private this.SendResponse
        (request: JsonRpcMessage, writeBody: Utf8JsonWriter -> unit, cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(request, nameof request)

        match request.Id with
        | None ->
            Task.FromResult(
                Error(
                    ProcessError.Unsupported
                        "answering a JSON-RPC notification (a message with no id, so the peer is not waiting for a reply)"
                )
            )
        | Some id ->
            // Checked here as it is for a notification: once the conversation is over — the peer stopped
            // speaking the protocol, its output ended, or a send was torn mid-frame — an answer written
            // into that stream would be pretending otherwise.
            match currentFault () with
            | Some error -> Task.FromResult(Error error)
            | None ->
                let payload =
                    this.Encode(fun writer ->
                        writer.WritePropertyName "id"
                        // Already-valid JSON: it came verbatim from the peer's own frame, so it is echoed
                        // without re-validating (a string id keeps its quotes, a numeric one its shape).
                        writer.WriteRawValue(id, true)
                        writeBody writer)

                this.SendFramed(payload, cancellationToken)

    // ---- session state --------------------------------------------------------------------------

    /// The maximum framed payload size in either direction, in bytes.
    member _.MaxFrameBytes = transport.MaxFrameBytes

    /// How many incoming notifications/peer requests were dropped because the inbound backlog was full
    /// — a consumer that is not enumerating `MessagesAsync`, or is falling behind the peer. Always `0`
    /// while the consumer keeps up. Answers to this session's own requests are never dropped.
    member _.DroppedMessages = dropped.Value

    /// Enumerate the peer's notifications and its own requests, in arrival order. Answers to this
    /// session's requests never appear here — they go to the `RequestAsync` call waiting for them, so
    /// this enumeration and the request verbs never compete for the same message.
    ///
    /// The enumeration ends when the peer's framed output ends, and faults with `ProcessException` when
    /// the peer sends something that is not a JSON-RPC message. This single-consumer method may be
    /// called only once.
    member _.MessagesAsync() : IAsyncEnumerable<JsonRpcMessage> =
        if Interlocked.Exchange(&messagesClaimed, 1) <> 0 then
            raise (InvalidOperationException "this JsonRpcSession's incoming messages have already been consumed")

        messages.Reader.ReadAllAsync()

    /// Close the peer's framed input so it observes EOF — the usual last step of an LSP `shutdown`/`exit`
    /// sequence. Unsupported when the command did not keep stdin open.
    member _.FinishInputAsync() : Task<Result<unit, ProcessError>> = transport.FinishInputAsync()

    // ---- requests -------------------------------------------------------------------------------

    /// Send a request whose `params` are already JSON text (`null` for a request with none) and return
    /// the raw JSON text of the peer's `result`. The serializer-free path: nothing is reflected over and
    /// no `JsonTypeInfo` is needed, so it is always trim-/NativeAOT-safe, and a `result` of `null` comes
    /// back as the text `"null"` rather than failing a typed read.
    ///
    /// Without a timeout the call ends when the peer answers, its output ends, or `cancellationToken`
    /// fires — pass a timeout (or a token) for a peer that may go silent while still running.
    member this.RequestRawAsync
        (methodName: string, parametersJson: string | null, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<string, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        this.RequestCore(methodName, JsonRpcSession.RawParams parametersJson, None, cancellationToken)

    /// Like the overload above, but the whole call is bounded by `timeout` — writing the frame as well as
    /// waiting for the answer, so a peer that stopped reading its stdin cannot hang it either. The call
    /// then fails with `ProcessError.Timeout` and its waiter is dropped, so a late answer is discarded; a
    /// send the deadline interrupted may have been truncated, and ends the session (see the type's docs).
    member this.RequestRawAsync
        (
            methodName: string,
            parametersJson: string | null,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<string, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        this.RequestCore(methodName, JsonRpcSession.RawParams parametersJson, Some timeout, cancellationToken)

    /// Send a request, serializing `parameters` and deserializing the peer's `result` with
    /// source-generated `JsonTypeInfo` metadata — the trim-/NativeAOT-safe path. Give both type
    /// arguments explicitly when they cannot be inferred, e.g.
    /// `session.RequestAsync&lt;InitializeParams, InitializeResult&gt;(...)`.
    ///
    /// The peer's `error` answer is `ProcessError.JsonRpc`, never a successful result; a `result` that
    /// does not fit `'R` (including a JSON `null`) is `ProcessError.Parse`. A `parameters` value the
    /// serializer cannot encode raises, like any other invalid argument.
    member this.RequestAsync<'P, 'R>
        (
            methodName: string,
            parameters: 'P,
            paramsTypeInfo: JsonTypeInfo<'P>,
            resultTypeInfo: JsonTypeInfo<'R>,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<'R, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        ArgumentNullException.ThrowIfNull(paramsTypeInfo, nameof paramsTypeInfo)
        ArgumentNullException.ThrowIfNull(resultTypeInfo, nameof resultTypeInfo)

        this.RequestTyped(
            methodName,
            JsonRpcSession.TypedParams(parameters, paramsTypeInfo),
            None,
            cancellationToken,
            JsonRpcSession.TypedResult resultTypeInfo
        )

    /// Like the overload above, but the whole call is bounded by `timeout` — writing the frame as well as
    /// waiting for the answer (`ProcessError.Timeout` when it elapses).
    member this.RequestAsync<'P, 'R>
        (
            methodName: string,
            parameters: 'P,
            paramsTypeInfo: JsonTypeInfo<'P>,
            resultTypeInfo: JsonTypeInfo<'R>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<'R, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        ArgumentNullException.ThrowIfNull(paramsTypeInfo, nameof paramsTypeInfo)
        ArgumentNullException.ThrowIfNull(resultTypeInfo, nameof resultTypeInfo)

        this.RequestTyped(
            methodName,
            JsonRpcSession.TypedParams(parameters, paramsTypeInfo),
            Some timeout,
            cancellationToken,
            JsonRpcSession.TypedResult resultTypeInfo
        )

    /// Send a request, serializing `parameters` and deserializing the `result` via reflection-based
    /// `System.Text.Json` (`options` omitted uses the BCL defaults). Same result/error contract as the
    /// `JsonTypeInfo` overload.
    ///
    /// **Trimming / AOT:** not trim-/AOT-safe — use the `JsonTypeInfo` overloads (or `RequestRawAsync`)
    /// in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Serializes params and deserializes the result by reflection via System.Text.Json; give the JsonTypeInfo overloads, or RequestRawAsync, in a trimmed app.">]
    [<RequiresDynamicCode "Serializes params and deserializes the result by reflection via System.Text.Json; give the JsonTypeInfo overloads, or RequestRawAsync, in a NativeAOT app.">]
    member this.RequestAsync<'P, 'R>
        (
            methodName: string,
            parameters: 'P,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<'R, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)

        this.RequestTyped(
            methodName,
            JsonRpcSession.ReflectedParams(parameters, options),
            None,
            cancellationToken,
            JsonRpcSession.ReflectedResult options
        )

    /// Like the overload above, but the whole call is bounded by `timeout` — writing the frame as well as
    /// waiting for the answer (`ProcessError.Timeout` when it elapses).
    ///
    /// **Trimming / AOT:** not trim-/AOT-safe — use the `JsonTypeInfo` overloads (or `RequestRawAsync`)
    /// in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Serializes params and deserializes the result by reflection via System.Text.Json; give the JsonTypeInfo overloads, or RequestRawAsync, in a trimmed app.">]
    [<RequiresDynamicCode "Serializes params and deserializes the result by reflection via System.Text.Json; give the JsonTypeInfo overloads, or RequestRawAsync, in a NativeAOT app.">]
    member this.RequestAsync<'P, 'R>
        (
            methodName: string,
            parameters: 'P,
            options: JsonSerializerOptions | null,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<'R, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)

        this.RequestTyped(
            methodName,
            JsonRpcSession.ReflectedParams(parameters, options),
            Some timeout,
            cancellationToken,
            JsonRpcSession.ReflectedResult options
        )

    // ---- notifications --------------------------------------------------------------------------

    /// Send a notification whose `params` are already JSON text (`null` for none) — no `id`, no answer,
    /// and no serializer, so this overload is always trim-/NativeAOT-safe.
    member this.NotifyRawAsync
        (methodName: string, parametersJson: string | null, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        this.SendNotification(methodName, JsonRpcSession.RawParams parametersJson, cancellationToken)

    /// Send a notification, serializing `parameters` with source-generated `JsonTypeInfo` metadata (the
    /// trim-/NativeAOT-safe path). A notification carries no `id`: the peer never answers it, so the
    /// returned `Result` reports only whether the frame was written.
    member this.NotifyAsync<'P>
        (
            methodName: string,
            parameters: 'P,
            paramsTypeInfo: JsonTypeInfo<'P>,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        ArgumentNullException.ThrowIfNull(paramsTypeInfo, nameof paramsTypeInfo)
        this.SendNotification(methodName, JsonRpcSession.TypedParams(parameters, paramsTypeInfo), cancellationToken)

    /// Send a notification, serializing `parameters` via reflection-based `System.Text.Json` (`options`
    /// omitted uses the BCL defaults).
    ///
    /// **Trimming / AOT:** not trim-/AOT-safe — use the `JsonTypeInfo` overload (or `NotifyRawAsync`) in
    /// a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Serializes params by reflection via System.Text.Json; give the JsonTypeInfo overload, or NotifyRawAsync, in a trimmed app.">]
    [<RequiresDynamicCode "Serializes params by reflection via System.Text.Json; give the JsonTypeInfo overload, or NotifyRawAsync, in a NativeAOT app.">]
    member this.NotifyAsync<'P>
        (
            methodName: string,
            parameters: 'P,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ArgumentException.ThrowIfNullOrEmpty(methodName, nameof methodName)
        this.SendNotification(methodName, JsonRpcSession.ReflectedParams(parameters, options), cancellationToken)

    // ---- answering the peer ---------------------------------------------------------------------

    /// Answer a peer request (`JsonRpcMessage.IsRequest`) with a `result` that is already JSON text —
    /// serializer-free, so always trim-/NativeAOT-safe. Answering a notification is a typed
    /// `ProcessError.Unsupported`: the peer is not waiting for one.
    member this.RespondRawAsync
        (request: JsonRpcMessage, resultJson: string, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(request, nameof request)
        ArgumentException.ThrowIfNullOrEmpty(resultJson, nameof resultJson)

        this.SendResponse(
            request,
            (fun writer ->
                writer.WritePropertyName "result"
                writer.WriteRawValue resultJson),
            cancellationToken
        )

    /// Answer a peer request with a `result` serialized through source-generated `JsonTypeInfo` metadata
    /// (the trim-/NativeAOT-safe path).
    member this.RespondAsync<'R>
        (
            request: JsonRpcMessage,
            result: 'R,
            resultTypeInfo: JsonTypeInfo<'R>,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(request, nameof request)
        ArgumentNullException.ThrowIfNull(resultTypeInfo, nameof resultTypeInfo)

        this.SendResponse(
            request,
            (fun writer ->
                writer.WritePropertyName "result"
                JsonSerializer.Serialize(writer, result, resultTypeInfo)),
            cancellationToken
        )

    /// Answer a peer request with a `result` serialized via reflection-based `System.Text.Json`
    /// (`options` omitted uses the BCL defaults).
    ///
    /// **Trimming / AOT:** not trim-/AOT-safe — use the `JsonTypeInfo` overload (or `RespondRawAsync`)
    /// in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Serializes the result by reflection via System.Text.Json; give the JsonTypeInfo overload, or RespondRawAsync, in a trimmed app.">]
    [<RequiresDynamicCode "Serializes the result by reflection via System.Text.Json; give the JsonTypeInfo overload, or RespondRawAsync, in a NativeAOT app.">]
    member this.RespondAsync<'R>
        (
            request: JsonRpcMessage,
            result: 'R,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(request, nameof request)
        let optionsArg = Option.ofObj options |> Option.toObj

        this.SendResponse(
            request,
            (fun writer ->
                writer.WritePropertyName "result"
                JsonSerializer.Serialize(writer, box result, typeof<'R>, optionsArg)),
            cancellationToken
        )

    /// Answer a peer request with a JSON-RPC `error` object — the honest reply when the peer asks for
    /// something this client cannot do (`-32601` "method not found", `-32602` "invalid params", and the
    /// rest of the reserved range are the conventional codes).
    member this.RespondErrorAsync
        (request: JsonRpcMessage, code: int, message: string, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull(request, nameof request)
        ArgumentNullException.ThrowIfNull(message, nameof message)

        this.SendResponse(
            request,
            (fun writer ->
                writer.WritePropertyName "error"
                writer.WriteStartObject()
                writer.WriteNumber("code", code)
                writer.WriteString("message", message)
                writer.WriteEndObject()),
            cancellationToken
        )

    // ---- params/result encoders ------------------------------------------------------------------
    // Static so the three send paths share one encoding rule per flavour instead of repeating it.

    static member private RawParams(parametersJson: string | null) : (Utf8JsonWriter -> unit) option =
        match Option.ofObj parametersJson with
        | None -> None
        // Validated rather than trusted: a caller-built string is not necessarily JSON, and writing it
        // unchecked would produce a frame the peer cannot parse. Invalid text raises `JsonException`,
        // like any other malformed argument.
        | Some text -> Some(fun writer -> writer.WriteRawValue text)

    static member private TypedParams(parameters: 'P, paramsTypeInfo: JsonTypeInfo<'P>) =
        Some(fun (writer: Utf8JsonWriter) -> JsonSerializer.Serialize(writer, parameters, paramsTypeInfo))

    [<RequiresUnreferencedCode "Serializes params by reflection via System.Text.Json.">]
    [<RequiresDynamicCode "Serializes params by reflection via System.Text.Json.">]
    static member private ReflectedParams(parameters: 'P, options: JsonSerializerOptions | null) =
        let optionsArg = Option.ofObj options |> Option.toObj
        Some(fun (writer: Utf8JsonWriter) -> JsonSerializer.Serialize(writer, box parameters, typeof<'P>, optionsArg))

    // Both result readers go through the non-generic `JsonTypeInfo`/`Type` overloads for the same reason
    // `RunningProcess.StdoutJsonLinesAsync` does: the BCL's generic overloads return a `TValue?`-annotated
    // value the F# nullness checker cannot reconcile against an unconstrained `'R`.
    static member private TypedResult(resultTypeInfo: JsonTypeInfo<'R>) : string -> 'R =
        fun text ->
            match JsonSerializer.Deserialize(text, resultTypeInfo :> JsonTypeInfo) with
            | null -> raise (JsonException "the result deserialized to null")
            | value -> unbox<'R> value

    [<RequiresUnreferencedCode "Deserializes the result by reflection via System.Text.Json.">]
    [<RequiresDynamicCode "Deserializes the result by reflection via System.Text.Json.">]
    static member private ReflectedResult(options: JsonSerializerOptions | null) : string -> 'R =
        let optionsArg = Option.ofObj options |> Option.toObj

        fun text ->
            match JsonSerializer.Deserialize(text, typeof<'R>, optionsArg) with
            | null -> raise (JsonException "the result deserialized to null")
            | value -> unbox<'R> value

    /// The router task, for tests: it completes once the peer's framed output has ended and every
    /// pending request has been failed. Not public — a consumer observes the same end through the
    /// verbs' typed errors and `MessagesAsync` completing.
    member internal _.RouterTask: Task = router
