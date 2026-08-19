namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native
open ProcessKit.Testing

type private ReadinessHttpHandler() =
    inherit HttpMessageHandler()

    let mutable requests = 0
    let mutable disposed = 0
    let mutable probeHeader: string option = None

    member _.Requests = Volatile.Read(&requests)
    member _.Disposed = Volatile.Read(&disposed) <> 0
    member _.ProbeHeader = probeHeader

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Interlocked.Increment(&requests) |> ignore

        probeHeader <-
            if request.Headers.Contains "X-ProcessKit-Probe" then
                request.Headers.GetValues "X-ProcessKit-Probe" |> Seq.tryHead
            else
                None

        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))

    override this.Dispose(disposing: bool) =
        if disposing then
            Interlocked.Exchange(&disposed, 1) |> ignore

        base.Dispose disposing

/// An `HttpMessageHandler` whose FIRST request parks until the test releases it and whose every later
/// request answers 200 — the HTTP shape of "the stale health check is still in flight when the child
/// publishes readiness and exits". Parking the first request is what makes the post-exit re-check tests
/// deterministic rather than timing-dependent: the polling loop provably cannot reach a second request
/// on its own while that first one is unanswered, so a second request can only come from the single
/// re-check `RunningProcess.raceReadinessAgainstExit` performs after observing the exit.
type private LateReadyHttpHandler() =
    inherit HttpMessageHandler()

    let mutable requests = 0

    let firstRequestStarted =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let firstRequestReleased =
        TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// How many requests the client has sent through this handler.
    member _.Requests = Volatile.Read(&requests)

    /// Completes once the first (parked) request has been sent.
    member _.FirstRequestStarted = firstRequestStarted.Task

    /// Unpark the first request with a "not ready" answer, so the abandoned probe attempt behind it can
    /// finish instead of being left pending for the rest of the test run.
    member _.ReleaseFirstRequest() =
        firstRequestReleased.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        |> ignore

    override _.SendAsync(_request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        if Interlocked.Increment(&requests) = 1 then
            firstRequestStarted.TrySetResult() |> ignore
            firstRequestReleased.Task
        else
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))

/// A timer that never fires — see `ManualReadinessClock` for why the readiness tests want one.
type private InertTimer() =
    interface ITimer with
        member _.Change(_dueTime, _period) = true

        member _.Dispose() = ()

        member _.DisposeAsync() = ValueTask()

/// A `TimeProvider` whose clock only moves when the test moves it and whose timers never fire at all.
/// Readiness code observes the deadline two independent ways: the poll core arms a
/// `CancellationTokenSource` on a provider timer, while `RunningProcess.raceReadinessAgainstExit`
/// measures the spent budget with `GetElapsedTime`. Advancing the clock WITHOUT firing timers isolates
/// the second, which is exactly what the "a spent deadline suppresses the post-exit re-check" test needs:
/// the budget is observably gone, yet nothing has ended the run on the test's behalf.
///
/// `TimestampFrequency` is fixed at `TimeSpan.TicksPerSecond` so an `Advance` maps one-to-one onto
/// elapsed time; the tests using it advance by far more than the budget under test, so they stay correct
/// even where the base class converts through a different timestamp frequency.
type private ManualReadinessClock() =
    inherit TimeProvider()

    let mutable timestamp = 0L

    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    override _.GetTimestamp() = Volatile.Read(&timestamp)
    override _.CreateTimer(_callback, _state, _dueTime, _period) = new InertTimer() :> ITimer

    member _.Advance(amount: TimeSpan) =
        Interlocked.Add(&timestamp, amount.Ticks) |> ignore

[<TestFixture>]
type ReadinessTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Stays alive a few seconds without producing the awaited signal.
    let lingering () =
        if isWindows then
            shell "ping 127.0.0.1 -n 5 >NUL"
        else
            shell "sleep 4"

    // A synthetic `RunningProcess` over two BOUNDED, backpressure-honouring stdout/stderr pipes (a
    // small `System.IO.Pipelines.Pipe` per stream, not a real OS pipe/subprocess) — deterministic
    // across the CI matrix, unlike racing a real child against real OS pipe buffering (see
    // `StreamingTests.syntheticStdoutProcess`'s comment for the same rationale). `Wait` never
    // completes, modelling a child that stays running throughout the probe: the readiness probes now
    // race the child's own exit (`RunningProcess.raceReadinessAgainstExit`), so an immediately-resolving
    // `Wait` would fire that early-exit path and report `NotReady` before the burst below could finish
    // — which is not what these "becomes ready while chatty" tests exercise. Returns the process plus
    // the two writer-side `Stream`s the test uses to play "the child" writing its startup burst.
    let syntheticBackpressureProcess (config: CommandConfig) : RunningProcess * Stream * Stream =
        // A tiny pause/resume threshold (well under the >64 KiB burst the tests below write) so a
        // writer that outpaces the reader genuinely blocks in `WriteAsync`/`FlushAsync` — the same
        // shape a real ~64 KiB OS pipe buffer forces on a chatty child — until something reads.
        let pipeOptions =
            PipeOptions(pauseWriterThreshold = 8_192L, resumeWriterThreshold = 4_096L)

        let stdoutPipe = Pipe pipeOptions
        let stderrPipe = Pipe pipeOptions

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some(stdoutPipe.Reader.AsStream())
              Stderr = Some(stderrPipe.Reader.AsStream())
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
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host), stdoutPipe.Writer.AsStream(), stderrPipe.Writer.AsStream()

    // Play "a chatty child": write `totalBytes` into `stream` in small chunks, blocking on
    // backpressure exactly like a real child blocked in `write()` on a full OS pipe until the
    // background drain (or nothing, pre-fix) reads it.
    let writeBurst (stream: Stream) (totalBytes: int) : Task =
        task {
            let chunk = Array.create 4_096 (byte 'x')
            let mutable written = 0

            while written < totalBytes do
                let toWrite = min chunk.Length (totalBytes - written)
                do! stream.WriteAsync(chunk.AsMemory(0, toWrite))
                written <- written + toWrite

            do! stream.FlushAsync()
        }

    let startHttpListener (statusForRequest: int -> int) : TcpListener * Uri * (unit -> int) =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let mutable requests = 0

        let server =
            task {
                try
                    while true do
                        use! client = listener.AcceptTcpClientAsync()
                        requests <- requests + 1
                        use stream = client.GetStream()
                        let status = statusForRequest requests

                        let response =
                            Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 {status} Readiness{Environment.NewLine}Content-Length: 0{Environment.NewLine}Connection: close{Environment.NewLine}{Environment.NewLine}"
                            )

                        do! stream.WriteAsync(response)
                        do! stream.FlushAsync()
                with
                | :? SocketException ->
                    // Stopping the listener unblocks a pending accept during test cleanup.
                    ()
                | :? ObjectDisposedException ->
                    // Disposal can race Stop during test cleanup; no connection remains to serve.
                    ()
            }

        server.ContinueWith(
            (fun (finished: Task) -> finished.Exception |> ignore),
            TaskContinuationOptions.OnlyOnFaulted
            ||| TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

        listener, Uri($"http://127.0.0.1:{port}/"), (fun () -> requests)

    // A minimal accept-and-close Unix domain socket server bound to a fresh path under the temp
    // directory (mirrors `startHttpListener`'s shape). Every accepted connection is immediately
    // shut down — `WaitForSocketAsync` only probes that a connection succeeds, it never exchanges
    // data. The caller is responsible for calling `Dispose()` on the returned `Socket` and deleting
    // the socket path afterwards (Unix domain sockets leave a filesystem entry that closing the
    // listener does not remove).
    let startUnixSocketListener () : Socket * string =
        let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.sock")

        let listener =
            new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

        listener.Bind(UnixDomainSocketEndPoint path)
        listener.Listen(1)

        let server =
            task {
                try
                    while true do
                        use! client = listener.AcceptAsync()

                        try
                            client.Shutdown(SocketShutdown.Both)
                        with :? SocketException ->
                            // The peer may already have closed its end; nothing left to shut down.
                            ()
                with
                | :? SocketException ->
                    // Stopping/disposing the listener unblocks a pending accept during test cleanup.
                    ()
                | :? ObjectDisposedException ->
                    // Disposal can race the accept loop during test cleanup; no connection remains.
                    ()
            }

        server.ContinueWith(
            (fun (finished: Task) -> finished.Exception |> ignore),
            TaskContinuationOptions.OnlyOnFaulted
            ||| TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

        listener, path

    // Deletes a Unix domain socket path left behind by `startUnixSocketListener`, tolerating a path
    // that was never bound (the "nothing is listening" tests) or already removed.
    let deleteSocketPathIfPresent (path: string) : unit =
        try
            File.Delete path
        with :? IOException ->
            // Best-effort cleanup only; leaving a stray temp file behind is not a test failure.
            ()

    // A minimal single-connection Windows named pipe server bound to a fresh, unique pipe name (mirrors
    // `startUnixSocketListener`'s shape). `WaitForNamedPipeAsync` only probes that a client-side open
    // succeeds; it never exchanges data or keeps the connection. The caller is responsible for disposing
    // the returned `NamedPipeServerStream`. Windows-only — every call site guards on
    // `RuntimeInformation.IsOSPlatform OSPlatform.Windows` first.
    let startNamedPipeListener () : System.IO.Pipes.NamedPipeServerStream * string =
        let name = $"processkit-{Guid.NewGuid():N}"

        let server =
            new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1)

        let accept =
            task {
                try
                    do! server.WaitForConnectionAsync()
                with
                | :? ObjectDisposedException
                | :? IOException ->
                    // Disposed during test cleanup, possibly before a client ever connected; nothing
                    // left to observe.
                    ()
            }

        accept.ContinueWith(
            (fun (finished: Task) -> finished.Exception |> ignore),
            TaskContinuationOptions.OnlyOnFaulted
            ||| TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

        server, name

    // `syntheticProcess` over a caller-supplied `CommandConfig` — the only reason to reach for this
    // overload is to hand the handle a non-default `TimeProvider` (see `ManualReadinessClock`), since the
    // readiness deadline arithmetic reads the clock through the command's config.
    let syntheticProcessWith (config: CommandConfig) (exitTask: Task<Outcome>) : RunningProcess =
        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = None
              Stderr = None
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> exitTask
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host)

    // A synthetic `RunningProcess` with no piped streams whose exit is fully driven by `exitTask`:
    // pass a never-completing task to model a child that stays running, or `Task.FromResult(Outcome...)`
    // to model one that has already exited. Used to drive the readiness probes' exit-race path
    // (HTTP/port/custom) deterministically, without a real subprocess.
    let syntheticProcess (exitTask: Task<Outcome>) : RunningProcess =
        syntheticProcessWith (Command.create "test").Config exitTask

    // A `Wait` delegate for `syntheticProcess` that faults ~300ms after being invoked — asynchronously,
    // through `task { }`, never synchronously from the call to `exitFaultsLate()` itself (KB K-058/
    // K-016: a synchronous throw would never reach `ensureBufferedWait`'s memoized `Task<Outcome>` at
    // all, so it would not reproduce what a real faulted exit wait looks like). Used by the T-212
    // regression pair below: one readiness probe outruns this fault (never awaits it), one verb awaits
    // it directly.
    let exitFaultsLate () : Task<Outcome> =
        task {
            do! Task.Delay 300
            return failwith "synthetic exit-wait fault"
        }

    [<Test>]
    member _.``WaitForLine matches a stdout line, then dispose reaps the rest``() : Task =
        task {
            let command =
                if isWindows then
                    shell "echo ready&ping 127.0.0.1 -n 5 >NUL"
                else
                    shell "echo ready; sleep 4"

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match! running.WaitForLineAsync((fun line -> line.Contains "ready"), TimeSpan.FromSeconds 5.0) with
                | Ok line -> Assert.That(line, Does.Contain "ready")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForLine times out with NotReady when the line never appears``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match!
                    running.WaitForLineAsync((fun line -> line.Contains "never"), TimeSpan.FromMilliseconds 300.0)
                with
                | Error(ProcessError.NotReady _) -> Assert.Pass()
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForLine reports the clamped (armable) timeout in NotReady, not an over-long raw one``() : Task =
        task {
            // An over-long timeout can't be armed on a BCL timer as-is; `WaitForLineAsync` arms the CTS
            // with `Timeouts.clampArmable timeout` and must report that same clamped value in `NotReady`
            // — uniform with `WaitForPortAsync`/`WaitForAsync` — never the raw, un-clamped request.
            let config = (Command.create "test").Config
            let running, stdoutWriter, _stderrWriter = syntheticBackpressureProcess config
            use running = running

            // Closing stdout with nothing written is a clean EOF: the predicate never matches, so this
            // resolves to `NotReady` immediately via channel completion rather than actually waiting out
            // the (unarmably long) requested timeout.
            stdoutWriter.Close()

            match! running.WaitForLineAsync((fun line -> line.Contains "never"), TimeSpan.MaxValue) with
            | Error(ProcessError.NotReady(_, reportedTimeout)) ->
                Assert.That(reportedTimeout, Is.EqualTo(Timeouts.maxArmable))
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPort connects to a listening port``() : Task =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        task {
            try
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running
                    let endpoint = IPEndPoint(IPAddress.Loopback, port)

                    match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 3.0) with
                    | Ok() -> Assert.Pass()
                    | Error error -> Assert.Fail $"{error}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.WaitForHttpRetriesUnsuccessfulResponsesUntilALoopbackEndpointIsReady() : Task =
        task {
            let listener, uri, requestCount =
                startHttpListener (fun attempt -> if attempt > 2 then 200 else 503)

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.That(requestCount (), Is.GreaterThanOrEqualTo 3)
                | Error error -> Assert.Fail $"{error}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.``HTTP readiness arguments fail fast at the API boundary``() : Task =
        task {
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let relative = Uri("health", UriKind.Relative)
            let timeout = TimeSpan.FromSeconds 1.0
            use client = new HttpClient(new ReadinessHttpHandler())

            Assert.Throws<ArgumentException>(Action(fun () -> running.WaitForHttpAsync(relative, timeout) |> ignore))
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () -> running.WaitForHttpAsync(relative, Seq.singleton 200, timeout) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    running.WaitForHttpAsync(relative, Func<HttpResponseMessage, bool>(fun _ -> true), timeout)
                    |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    running.WaitForHttpAsync(Uri "http://localhost/", Seq.empty<int>, timeout)
                    |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () -> running.WaitForHttpAsync(relative, client, timeout) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () -> running.WaitForHttpAsync(relative, client, Seq.singleton 200, timeout) |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    running.WaitForHttpAsync(relative, client, Func<HttpResponseMessage, bool>(fun _ -> true), timeout)
                    |> ignore)
            )
            |> ignore

            Assert.Throws<ArgumentException>(
                Action(fun () ->
                    running.WaitForHttpAsync(Uri "http://localhost/", client, Seq.empty<int>, timeout)
                    |> ignore)
            )
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``HTTP readiness uses but never disposes a caller-owned client``() : Task =
        task {
            let handler = new ReadinessHttpHandler()
            use client = new HttpClient(handler)
            client.DefaultRequestHeaders.Add("X-ProcessKit-Probe", "configured")
            let uri = Uri "https://self-signed.test/health"
            let timeout = TimeSpan.FromSeconds 1.0

            use defaultProbe = syntheticProcess (TaskCompletionSource<Outcome>().Task)

            match! defaultProbe.WaitForHttpAsync(uri, client, timeout) with
            | Error error -> Assert.Fail $"default client overload failed: {error}"
            | Ok() -> ()

            use statusProbe = syntheticProcess (TaskCompletionSource<Outcome>().Task)

            match! statusProbe.WaitForHttpAsync(uri, client, Seq.singleton 204, timeout) with
            | Error error -> Assert.Fail $"status client overload failed: {error}"
            | Ok() -> ()

            use predicateProbe = syntheticProcess (TaskCompletionSource<Outcome>().Task)

            match!
                predicateProbe.WaitForHttpAsync(
                    uri,
                    client,
                    Func<HttpResponseMessage, bool>(fun response -> response.StatusCode = HttpStatusCode.NoContent),
                    timeout
                )
            with
            | Error error -> Assert.Fail $"predicate client overload failed: {error}"
            | Ok() -> ()

            Assert.That(handler.Requests, Is.EqualTo 3)
            Assert.That(handler.ProbeHeader, Is.EqualTo(Some "configured"))
            Assert.That(handler.Disposed, Is.False, "ProcessKit must not dispose a caller-owned HttpClient")

            use! response = client.GetAsync uri
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.NoContent)
        }
        :> Task

    [<TestCase(199, false)>]
    [<TestCase(200, true)>]
    [<TestCase(299, true)>]
    [<TestCase(300, false)>]
    member _.``the shared default HTTP predicate accepts exactly 2xx``(statusCode: int, expected: bool) =
        use response = new HttpResponseMessage(enum<HttpStatusCode> statusCode)
        Assert.That(ReadinessProbe.defaultHttpSuccess.Invoke response, Is.EqualTo expected)

    [<Test>]
    member _.WaitForHttpReturnsNotReadyWhenItsEndpointNeverReturnsASatisfactoryResponse() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 503)

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForHttpAsync(uri, TimeSpan.FromMilliseconds 250.0) with
                | Error(ProcessError.NotReady _) -> Assert.Pass()
                | other -> Assert.Fail $"expected NotReady, got {other}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.WaitForHttpReturnsNotReadyPromptlyWhenTheChildExitsBeforeReadiness() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 503)

            try
                use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
                let elapsed = Stopwatch.StartNew()

                match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 5.0) with
                | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
                | other -> Assert.Fail $"expected early NotReady, got {other}"
            finally
                listener.Stop()
        }
        :> Task

    // Regression for KB K-016 / R-01: the ONLY readiness probe that races the child's own exit
    // (`WaitForHttpAsync`'s early-exit detection) previously did so via a raw, independent
    // `host.Wait()` instead of the memoized reap-once wait the rest of `RunningProcess` shares.
    // On POSIX, `host.Wait()`/`waitPosix` REAPS the child and consumes its exit status; a second,
    // uncoordinated `host.Wait()` afterward (e.g. from `WaitAsync`) then races an already-reaped
    // pid and fabricates `Outcome.Unobserved` instead of the real exit code. This exercises a REAL
    // spawned child (not the synthetic `RunningHost` used by the tests above), so it reproduces the
    // real reap machinery rather than a stand-in `Wait` delegate.
    [<Test>]
    member _.``WaitForHttp then WaitAsync reports the real exit code after an early child exit``() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 503)

            try
                match! runner.StartAsync(shell "exit 7", CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running

                    match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 5.0) with
                    | Error(ProcessError.NotReady _) -> ()
                    | other -> Assert.Fail $"expected NotReady, got {other}"

                    // Before the fix, this second `host.Wait()` (via `WaitAsync`) raced an already-reaped
                    // pid on POSIX and fabricated `Outcome.Unobserved` instead of the real exit code.
                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Exited code -> Assert.That(code, Is.EqualTo 7)
                    | other -> Assert.Fail $"expected Exited 7, got {other}"
            finally
                listener.Stop()
        }
        :> Task

    // Regression for R-02: `ExitTask` (the internal member backing `WaitAnyAsync`/`WaitAllAsync`)
    // has its own `Fresh` branch, separate from the `WaitAsync` path R-01 fixed above, and that
    // branch previously called `waitWithTimeout()` directly instead of `ensureBufferedWait()` — so
    // it assumed `consumption = Fresh` always meant "no wait has started yet", when in fact
    // `WaitForHttpAsync`'s early-exit detection deliberately starts the shared wait via
    // `ensureBufferedWait()` while leaving `consumption` at `Fresh` (so a later buffered verb can
    // still claim the pipes). Calling `WaitAnyAsync`/`WaitAllAsync` instead of a buffered verb after
    // such a probe therefore started a second, independent `host.Wait()`, reproducing the exact same
    // KB K-016 reap-once bug the R-01 fix addressed, just through this other path. Exercises a REAL
    // spawned child so it reproduces the real reap machinery rather than a stand-in `Wait` delegate.
    [<Test>]
    member _.``WaitForHttp then WaitAnyAsync reports the real exit code after an early child exit``() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 503)

            try
                match! runner.StartAsync(shell "exit 7", CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running

                    match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 5.0) with
                    | Error(ProcessError.NotReady _) -> ()
                    | other -> Assert.Fail $"expected NotReady, got {other}"

                    // Before the fix, ExitTask's Fresh branch started a second `host.Wait()` here (via
                    // `WaitAnyAsync`), racing an already-reaped pid on POSIX and fabricating
                    // `Outcome.Unobserved` instead of the real exit code.
                    let! result = RunningProcess.WaitAnyAsync [| running |]

                    match result.Outcome with
                    | Outcome.Exited code -> Assert.That(code, Is.EqualTo 7)
                    | other -> Assert.Fail $"expected Exited 7, got {other}"
            finally
                listener.Stop()
        }
        :> Task

    // Companion regression for the "child becomes ready" branch: the shared exit wait the probe now
    // starts (`ensureBufferedWait`, reused from a real verb afterward) must not leave an
    // uncoordinated, un-reaped wait registration hanging around, nor claim the handle's pipes —
    // `StopAsync` afterward must still complete promptly against a REAL still-running child.
    [<Test>]
    member _.``WaitForHttp then StopAsync still completes promptly once the child becomes ready``() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 200)

            try
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running

                    match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 5.0) with
                    | Ok() -> ()
                    | other -> Assert.Fail $"expected Ok, got {other}"

                    let stopTask = running.StopAsync()
                    let watchdog = Task.Delay(TimeSpan.FromSeconds 5.0)
                    let! winner = Task.WhenAny(stopTask :> Task, watchdog)

                    Assert.That(
                        obj.ReferenceEquals(winner, watchdog),
                        Is.False,
                        "StopAsync hung after WaitForHttpAsync's ready path"
                    )

                    let! _ = stopTask
                    ()
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.WaitForHttpSupportsExplicitStatusCodesAndResponsePredicates() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 418)

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForHttpAsync(uri, seq { 418 }, TimeSpan.FromSeconds 3.0) with
                | Error error -> Assert.Fail $"status-code overload failed: {error}"
                | Ok() ->
                    match!
                        running.WaitForHttpAsync(
                            uri,
                            Func<HttpResponseMessage, bool>(fun response -> int response.StatusCode = 418),
                            TimeSpan.FromSeconds 3.0
                        )
                    with
                    | Ok() -> Assert.Pass()
                    | Error error -> Assert.Fail $"predicate overload failed: {error}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.WaitForHttpObservesExternalCancellation() : Task =
        task {
            let listener, uri, _requestCount = startHttpListener (fun _ -> 503)

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
                use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)

                match! running.WaitForHttpAsync(uri, TimeSpan.FromSeconds 30.0, cts.Token) with
                | Error(ProcessError.Cancelled _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Cancelled, got {other}"
            finally
                listener.Stop()
        }
        :> Task

    [<Test>]
    member _.``WaitFor polls a custom predicate``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let started = DateTime.UtcNow

                let probe () =
                    Task.FromResult((DateTime.UtcNow - started).TotalMilliseconds > 200.0)

                match! running.WaitForAsync(probe, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitFor succeeds when the child writes over 64 KiB to stdout and stderr before ready``() : Task =
        task {
            let config = (Command.create "test").Config
            let running, stdoutWriter, stderrWriter = syntheticBackpressureProcess config
            use running = running

            // "Ready" only flips once BOTH bursts have been written in full — with the pause
            // threshold set to 8 KiB (see `syntheticBackpressureProcess`), a writer outpacing an
            // undrained reader stalls well before the 100 KiB burst finishes. Pre-fix, `WaitForAsync`
            // never read these pipes, so the writers would still be blocked when the deadline below
            // elapses and this reports a spurious `NotReady`; the background drain this task adds is
            // what lets both bursts (and so `ready`) actually complete.
            let mutable ready = false

            let burst =
                task {
                    do! Task.WhenAll(writeBurst stdoutWriter 100_000, writeBurst stderrWriter 100_000)
                    ready <- true
                }

            let probe () = Task.FromResult ready

            match! running.WaitForAsync(probe, TimeSpan.FromSeconds 5.0) with
            | Ok() -> Assert.That(burst.IsCompletedSuccessfully, Is.True, "the >64 KiB burst never finished writing")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPort succeeds when the child writes over 64 KiB to stdout and stderr before ready``() : Task =
        task {
            let config = (Command.create "test").Config
            let running, stdoutWriter, stderrWriter = syntheticBackpressureProcess config
            use running = running

            // Bind (reserving a port) without listening yet: a connect attempt against a bound-but-
            // not-listening socket is refused, exactly like a server that hasn't opened its port yet.
            // Holding the same bound socket the whole time (rather than a Start-then-Stop-then-reuse
            // dance) makes the port number race-free.
            use gate =
                new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

            gate.Bind(IPEndPoint(IPAddress.Loopback, 0))

            let endpoint =
                match gate.LocalEndPoint with
                | :? IPEndPoint as ep -> ep
                | _ -> failwith "expected an IPEndPoint from Socket.LocalEndPoint after Bind"

            // "Ready" (the port opens for real) only once BOTH >64 KiB bursts finish writing — see
            // the `WaitFor` sibling test above for why a pre-fix `WaitForPortAsync` would stall here.
            let burst =
                task {
                    do! Task.WhenAll(writeBurst stdoutWriter 100_000, writeBurst stderrWriter 100_000)
                    gate.Listen(1)
                }

            match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 5.0) with
            | Ok() -> Assert.That(burst.IsCompletedSuccessfully, Is.True, "the >64 KiB burst never finished writing")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitFor reports NotReady at the shared deadline even though the predicate never completes``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let neverReady () = TaskCompletionSource<bool>().Task
                let elapsed = Stopwatch.StartNew()

                let waitTask = running.WaitForAsync(neverReady, TimeSpan.FromMilliseconds 200.0)
                // Independent watchdog: if the deadline logic is broken and `waitTask` never
                // completes, fail fast with a clear assertion instead of hanging the test run.
                let watchdog = Task.Delay(TimeSpan.FromSeconds 5.0)
                let! winner = Task.WhenAny(waitTask :> Task, watchdog)

                Assert.That(
                    obj.ReferenceEquals(winner, watchdog),
                    Is.False,
                    "WaitForAsync did not honor the shared deadline within the watchdog window"
                )

                match! waitTask with
                | Error(ProcessError.NotReady _) ->
                    // Bounded by the shared deadline, not left hanging on the never-completing task.
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitFor honors the deadline even when the predicate blocks synchronously and never returns a task``
        ()
        : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let elapsed = Stopwatch.StartNew()

                // A probe that blocks *synchronously* inside `Invoke` — it never even returns a task
                // until its long sleep self-releases (comfortably past the watchdog below, so a broken
                // implementation that awaits the invocation on the polling thread fails deterministically
                // rather than merely running slow). Only isolating the invocation on the thread pool
                // (Task.Run) keeps such a probe from pinning the polling loop and defeating the deadline.
                // The sleep is finite so the pool thread is not pinned for the whole test process.
                let blocksSynchronously () : Task<bool> =
                    Thread.Sleep(TimeSpan.FromSeconds 10.0)
                    Task.FromResult true

                let waitTask =
                    running.WaitForAsync(blocksSynchronously, TimeSpan.FromMilliseconds 200.0)

                let watchdog = Task.Delay(TimeSpan.FromSeconds 5.0)
                let! winner = Task.WhenAny(waitTask :> Task, watchdog)

                Assert.That(
                    obj.ReferenceEquals(winner, watchdog),
                    Is.False,
                    "WaitForAsync did not honor the deadline against a synchronously-blocking probe"
                )

                match! waitTask with
                | Error(ProcessError.NotReady _) ->
                    // Returned at the deadline, not after the probe's own 10s synchronous block.
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitFor is cancelled by the external token even though the predicate never completes``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
                let neverReady () = TaskCompletionSource<bool>().Task
                let elapsed = Stopwatch.StartNew()

                let waitTask =
                    running.WaitForAsync(neverReady, TimeSpan.FromSeconds 30.0, cts.Token)
                // Independent watchdog: if cancellation propagation is broken and `waitTask` never
                // completes, fail fast with a clear assertion instead of hanging the test run.
                let watchdog = Task.Delay(TimeSpan.FromSeconds 5.0)
                let! winner = Task.WhenAny(waitTask :> Task, watchdog)

                Assert.That(
                    obj.ReferenceEquals(winner, watchdog),
                    Is.False,
                    "WaitForAsync did not honor external cancellation within the watchdog window"
                )

                match! waitTask with
                | Error(ProcessError.Cancelled _) ->
                    // The external token wins over the (much longer) overall timeout.
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
                | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitFor succeeds when the predicate flips true just before the deadline``() : Task =
        task {
            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let started = Stopwatch.StartNew()
                // Flips true at 85% of the 2s budget (1.7s), leaving only ~300ms of margin — close
                // enough to the deadline to exercise the edge case, while still tolerant of scheduler
                // jitter given the implementation's own 50ms polling granularity.
                let timeout = TimeSpan.FromSeconds 2.0
                let readyAt = TimeSpan.FromMilliseconds 1700.0

                let almostReady () =
                    Task.FromResult(started.Elapsed > readyAt)

                match! running.WaitForAsync(almostReady, timeout) with
                | Ok() -> Assert.That(started.Elapsed, Is.LessThan timeout)
                | other -> Assert.Fail $"expected Ok, got {other}"
        }
        :> Task

    /// Runs `WaitForAsync` against a predicate whose task fault arrives after the deadline, then hands
    /// back only a `WeakReference` to that abandoned predicate task — never the task itself. Isolated in
    /// a `NoInlining` helper (and not returning the strong reference) so nothing in the calling test
    /// method's frame can keep the task rooted, which would otherwise let debug-mode locals or JIT
    /// tiering silently invalidate the GC-based verification below.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member private _.RunFaultingProbeAndGetWeakRef(running: RunningProcess) : Task<WeakReference> =
        task {
            let mutable probeTaskRef: Task<bool> = Unchecked.defaultof<Task<bool>>

            let faultsLate () : Task<bool> =
                let t =
                    task {
                        do! Task.Delay 300
                        return failwith "late predicate fault"
                    }

                probeTaskRef <- t
                t

            match! running.WaitForAsync(faultsLate, TimeSpan.FromMilliseconds 100.0) with
            | Error(ProcessError.NotReady _) -> ()
            | other -> Assert.Fail $"expected NotReady, got {other}"

            let weak = WeakReference(box probeTaskRef)
            probeTaskRef <- Unchecked.defaultof<Task<bool>>
            return weak
        }

    [<Test>]
    member this.``WaitFor observes a late fault from the abandoned predicate task instead of leaving it unobserved``
        ()
        : Task =
        task {
            let mutable unobserved = false

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved <- true
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running
                    let! weakProbeTask = this.RunFaultingProbeAndGetWeakRef running

                    // Let the abandoned predicate task fault, then force a GC pass: the CLR reports a
                    // still-unobserved task fault from the finalizer once the task itself is collected.
                    do! Task.Delay 500
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()

                    // Verify the test's own methodology is sound first: if the abandoned probe task was
                    // never actually collected, the absence of UnobservedTaskException below would be a
                    // false pass rather than proof that the implementation observed the fault.
                    Assert.That(
                        weakProbeTask.IsAlive,
                        Is.False,
                        "abandoned predicate task was not collected — GC-based verification is inconclusive"
                    )

                    Assert.That(unobserved, Is.False)
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
        }
        :> Task

    [<Test>]
    member _.``WaitForPort reports NotReady close to a short timeout, not a fixed connect-attempt window``() : Task =
        task {
            // Reserve a loopback port, then release it: nothing is listening, so every connect attempt
            // is refused rather than hanging — this isolates the "no fixed 1s-per-attempt overrun" fix.
            let probeListener = new TcpListener(IPAddress.Loopback, 0)
            probeListener.Start()
            let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
            probeListener.Stop()

            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let endpoint = IPEndPoint(IPAddress.Loopback, port)
                let elapsed = Stopwatch.StartNew()

                match! running.WaitForPortAsync(endpoint, TimeSpan.FromMilliseconds 150.0) with
                | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.0))
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPort respects the shared deadline while a connect attempt itself is still in flight``() : Task =
        task {
            // Deterministic stand-in for a connect attempt that never completes on its own, exercised
            // directly against `ReadinessProbe.waitForPortUsing` (bypassing the real `TcpClient` wiring
            // in `waitForPort`). This exercises deadline cancellation of an in-flight connect, not just
            // the refused-connection retry/backoff loop covered above — without depending on real network
            // behaviour (e.g. an unassigned TEST-NET-1 address), which is neither refused nor accepted
            // consistently across sandboxed CI environments and previously left this test able to pass
            // without ever exercising the in-flight case it claims to cover.
            let neverConnects (_: IPEndPoint) (ct: CancellationToken) : Task = Task.Delay(Timeout.Infinite, ct)

            let endpoint = IPEndPoint(IPAddress.Loopback, 1)
            let elapsed = Stopwatch.StartNew()

            match!
                ReadinessProbe.waitForPortUsing
                    TimeProvider.System
                    neverConnects
                    "test"
                    endpoint
                    ReadinessAttempts.PollUntilDeadline
                    (TimeSpan.FromMilliseconds 200.0)
                    CancellationToken.None
            with
            | Error(ProcessError.NotReady _) ->
                // Bounded below (the connect only stops once the deadline actually fires) and above (the
                // shared deadline, not the never-completing connect, decides when this returns).
                Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds 150.0))
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.0))
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPort reports NotReady at the deadline without waiting for a non-cooperative connect to finish``
        ()
        : Task =
        task {
            // `lateConnect` ignores the cancellation token it is handed (as a real, already-in-flight
            // `TcpClient.ConnectAsync` effectively does once the OS has committed to completing the
            // handshake) and only ever succeeds — 500ms after invocation, well past the 100ms shared
            // deadline and past `deadlineOverrunBound` on every platform (400ms on macOS and 250ms
            // elsewhere, K-038), so the two stay comfortably apart. Regression test
            // for two things at once: (1) the deadline is honored even though the connect itself never
            // observes cancellation — this must not block for the connect's own 500ms, only race it
            // against the deadline, and (2) once the abandoned connect does complete in the background,
            // its stale success is still reported as NotReady, never surfaced as a late `Ok` from this
            // call (which has already returned).
            //
            // `deadlineOverrunBound` is a platform-aware upper bound for that racing assertion: macOS CI
            // runners have shown materially more scheduler slack than other platforms between the shared
            // deadline firing and this call actually returning — K-038 measured a real 263ms against the
            // previous flat 250ms bound (a ~13ms overrun) on a macOS CI leg, with nothing in the diff
            // under review touching the deadline mechanism (`ReadinessProbe.waitForCoreUsing`'s
            // `Task.WhenAny` race). The K-038 CI history records no corresponding non-macOS deadline
            // overrun, so those platforms retain the original 250ms bound instead of gaining an
            // unobserved 50ms margin. Only the platform that has actually shown the variance gets a
            // wider bound; the connect fake's completion delay stays well clear of it, so this still
            // proves the deadline is genuinely raced rather than silently waited out on every platform.
            let deadlineOverrunBound =
                if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
                    TimeSpan.FromMilliseconds 400.0
                else
                    TimeSpan.FromMilliseconds 250.0

            let mutable lateConnectTask: Task = Unchecked.defaultof<Task>

            let lateConnect (_: IPEndPoint) (_: CancellationToken) : Task =
                let t = task { do! Task.Delay 500 }
                lateConnectTask <- t
                t

            let endpoint = IPEndPoint(IPAddress.Loopback, 1)
            let elapsed = Stopwatch.StartNew()

            match!
                ReadinessProbe.waitForPortUsing
                    TimeProvider.System
                    lateConnect
                    "test"
                    endpoint
                    ReadinessAttempts.PollUntilDeadline
                    (TimeSpan.FromMilliseconds 100.0)
                    CancellationToken.None
            with
            | Error(ProcessError.NotReady _) ->
                // Bounded well below the connect's own 500ms — the fix under test is that a
                // non-cooperative connect is raced against the shared deadline rather than blocked on.
                Assert.That(elapsed.Elapsed, Is.LessThan(deadlineOverrunBound))
                // The abandoned connect keeps running past the deadline in the background; wait for it
                // to actually finish and confirm it completed successfully (not faulted), demonstrating
                // its stale success never reaches this already-returned call as an `Ok`.
                do! lateConnectTask
                Assert.That(lateConnectTask.IsCompletedSuccessfully, Is.True)
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    /// Runs `waitForPortUsing` against a connect stub whose task fault arrives after the deadline, then
    /// hands back only a `WeakReference` to that abandoned connect task — never the task itself. Isolated
    /// in a `NoInlining` helper (and not returning the strong reference) for the same reason as
    /// `RunFaultingProbeAndGetWeakRef` above: nothing in the calling test method's frame may keep the task
    /// rooted, or the GC-based verification below would be silently invalidated.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member private _.RunFaultingConnectAndGetWeakRef() : Task<WeakReference> =
        task {
            let mutable connectTaskRef: Task = Unchecked.defaultof<Task>

            let faultsLate (_: IPEndPoint) (_: CancellationToken) : Task =
                let t: Task =
                    task {
                        do! Task.Delay 300
                        failwith "late connect fault"
                    }

                connectTaskRef <- t
                t

            let endpoint = IPEndPoint(IPAddress.Loopback, 1)

            match!
                ReadinessProbe.waitForPortUsing
                    TimeProvider.System
                    faultsLate
                    "test"
                    endpoint
                    ReadinessAttempts.PollUntilDeadline
                    (TimeSpan.FromMilliseconds 100.0)
                    CancellationToken.None
            with
            | Error(ProcessError.NotReady _) -> ()
            | other -> Assert.Fail $"expected NotReady, got {other}"

            let weak = WeakReference(box connectTaskRef)
            connectTaskRef <- Unchecked.defaultof<Task>
            return weak
        }

    [<Test>]
    member this.``WaitForPort observes a late fault from the abandoned connect task instead of leaving it unobserved``
        ()
        : Task =
        task {
            let mutable unobserved = false

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved <- true
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                let! weakConnectTask = this.RunFaultingConnectAndGetWeakRef()

                // Let the abandoned connect task fault, then force a GC pass: the CLR reports a
                // still-unobserved task fault from the finalizer once the task itself is collected.
                do! Task.Delay 500
                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                // Verify the test's own methodology is sound first: if the abandoned connect task was
                // never actually collected, the absence of UnobservedTaskException below would be a
                // false pass rather than proof that the implementation observed the fault.
                Assert.That(
                    weakConnectTask.IsAlive,
                    Is.False,
                    "abandoned connect task was not collected — GC-based verification is inconclusive"
                )

                Assert.That(unobserved, Is.False)
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
        }
        :> Task

    [<Test>]
    member _.``WaitForPort connects once a slow-to-start listener comes up, before the deadline``() : Task =
        task {
            let probeListener = new TcpListener(IPAddress.Loopback, 0)
            probeListener.Start()
            let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
            probeListener.Stop()

            let mutable listener = Unchecked.defaultof<TcpListener>
            use startLateCts = new CancellationTokenSource()

            let startLate =
                task {
                    do! Task.Delay(200, startLateCts.Token)
                    listener <- new TcpListener(IPAddress.Loopback, port)
                    listener.Start()
                }

            try
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    use running = running
                    let endpoint = IPEndPoint(IPAddress.Loopback, port)

                    let waitTask = running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 3.0)
                    do! startLate

                    match! waitTask with
                    | Ok() -> Assert.Pass()
                    | error -> Assert.Fail $"expected Ok, got {error}"
            finally
                // `startLate` is a hot task started before this `try`, so a failure earlier in the try
                // (e.g. `runner.StartAsync` returning `Error`) can reach here before its 200ms delay
                // elapses. Cancel it so it can never create/start a listener after this teardown runs —
                // without this, the listener assignment below would race a background task that outlives
                // the test, orphaning a bound socket. Swallow the resulting `OperationCanceledException`
                // from `startLate` itself; it only signals that teardown preempted it.
                startLateCts.Cancel()

                try
                    startLate.GetAwaiter().GetResult()
                with :? OperationCanceledException ->
                    ()

                if not (isNull (box listener)) then
                    listener.Stop()
        }
        :> Task

    [<Test>]
    member _.``WaitForPort is cancelled by the external token while polling an unreachable port``() : Task =
        task {
            let probeListener = new TcpListener(IPAddress.Loopback, 0)
            probeListener.Start()
            let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
            probeListener.Stop()

            match! runner.StartAsync(lingering (), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
                let endpoint = IPEndPoint(IPAddress.Loopback, port)
                let elapsed = Stopwatch.StartNew()

                match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 30.0, cts.Token) with
                | Error(ProcessError.Cancelled _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
                | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    // Early-exit contract, port probe: a child that has already exited must resolve to `NotReady`
    // promptly rather than polling the port out for the full timeout — the same behaviour
    // `WaitForHttpAsync` gives (see `WaitForHttpReturnsNotReadyPromptlyWhenTheChildExitsBeforeReadiness`).
    // `Wait` resolves to an exited outcome, so the shared exit wait the probe races against completes at
    // once and cancels the still-polling port probe.
    [<Test>]
    member _.``WaitForPort returns NotReady promptly when the child has already exited``() : Task =
        task {
            // A port nothing is listening on (reserve then release it) so every connect is refused and,
            // absent early-exit detection, the probe would poll the full 5s timeout.
            let probeListener = new TcpListener(IPAddress.Loopback, 0)
            probeListener.Start()
            let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
            probeListener.Stop()

            use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
            let endpoint = IPEndPoint(IPAddress.Loopback, port)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected early NotReady, got {other}"
        }
        :> Task

    // Early-exit contract, custom probe: a predicate that never flips true against an already-exited
    // child must resolve to `NotReady` promptly, not poll out the full timeout — the `WaitForAsync`
    // sibling of the port test above. Also the guard on the ceiling over the post-exit re-check
    // (`postExitRecheckGrace`, T-331): this predicate never answers at all, so without that ceiling the
    // one final observation would run until the 5s deadline and turn prompt early-exit detection back
    // into waiting the budget out.
    [<Test>]
    member _.``WaitFor returns NotReady promptly when the child has already exited``() : Task =
        task {
            use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
            let neverReady () = TaskCompletionSource<bool>().Task
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForAsync(neverReady, TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected early NotReady, got {other}"
        }
        :> Task

    // --- T-331: the single post-exit re-check ------------------------------------------------------
    // Observing the child's exit is not proof that the condition never came true. The polling probe's
    // in-flight attempt can observe a stale `false` and then yield long enough for the child to publish
    // readiness (a sentinel, an open port/socket, a health endpoint) and terminate; reporting `NotReady`
    // at that point erases a state that genuinely exists. `raceReadinessAgainstExit`'s exit branch
    // therefore takes exactly ONE more bounded look before answering. The five tests below pin every
    // part of that contract — a late success is kept (through two different probe kinds, custom and
    // HTTP, to show the behaviour comes from the shared choke point rather than one verb), a genuine
    // terminal false still ends as `NotReady` after exactly one extra look, and cancellation and a
    // spent deadline each still outrank the re-check.
    //
    // Each drives the ordering with a PARKED first attempt rather than a sleep: while the first
    // observation is unanswered the polling loop provably cannot start a second one, so a second
    // invocation can only be the post-exit re-check. That makes the attempt counts below exact rather
    // than timing-dependent. The synthetic host models "still running" with a never-completing `Wait`
    // (KB K-044), driven by the test's own `TaskCompletionSource` so the exit happens exactly when the
    // scenario calls for it.
    [<Test>]
    member _.``WaitFor observes readiness published immediately before the child exits``() : Task =
        task {
            let exitSignal =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = syntheticProcess exitSignal.Task

            let firstAttemptStarted =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let firstAttemptAnswer =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let attempts = ref 0
            let published = ref 0

            let probe () : Task<bool> =
                if Interlocked.Increment(&attempts.contents) = 1 then
                    // The stale observation: started before the sentinel existed and still in flight when
                    // the child publishes it and dies.
                    firstAttemptStarted.TrySetResult() |> ignore
                    firstAttemptAnswer.Task
                else
                    Task.FromResult(Volatile.Read(&published.contents) = 1)

            let waitTask = running.WaitForAsync(probe, TimeSpan.FromSeconds 5.0)
            do! firstAttemptStarted.Task

            // The child becomes ready and then immediately exits — the race this fix exists for.
            Volatile.Write(&published.contents, 1)
            exitSignal.SetResult(Outcome.Exited 0)

            match! waitTask with
            | Ok() ->
                Assert.That(
                    Volatile.Read(&attempts.contents),
                    Is.EqualTo 2,
                    "the exit branch must re-check the condition exactly once, not poll on"
                )
            | other -> Assert.Fail $"expected Ok from the post-exit re-check, got {other}"

            // Let the abandoned first attempt conclude instead of leaving it pending for the whole run.
            firstAttemptAnswer.TrySetResult false |> ignore
        }
        :> Task

    // The same race through a different probe kind, proving the fix lives in the shared choke point
    // rather than in one verb: the health endpoint starts answering while the first request is still
    // unanswered, and the child exits before that answer arrives.
    [<Test>]
    member _.``WaitForHttp observes an endpoint that starts answering immediately before the child exits``() : Task =
        task {
            let exitSignal =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = syntheticProcess exitSignal.Task
            use handler = new LateReadyHttpHandler()
            use client = new HttpClient(handler, Timeout = Timeout.InfiniteTimeSpan)
            let uri = Uri "http://127.0.0.1:1/health"

            let waitTask = running.WaitForHttpAsync(uri, client, TimeSpan.FromSeconds 5.0)
            do! handler.FirstRequestStarted
            exitSignal.SetResult(Outcome.Exited 0)

            match! waitTask with
            | Ok() ->
                Assert.That(
                    handler.Requests,
                    Is.EqualTo 2,
                    "the exit branch must send exactly one more request, not resume polling"
                )
            | other -> Assert.Fail $"expected Ok from the post-exit re-check, got {other}"

            handler.ReleaseFirstRequest()
        }
        :> Task

    // A genuine terminal false: the condition never became true, so the one re-check confirms it and the
    // result stays `NotReady` — reached with exactly one extra observation (never a resumed poll loop)
    // and without waiting out the deadline.
    [<Test>]
    member _.``WaitFor reports NotReady after a single post-exit re-check when the condition never holds``() : Task =
        task {
            let exitSignal =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = syntheticProcess exitSignal.Task

            let firstAttemptStarted =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let firstAttemptAnswer =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let attempts = ref 0

            let probe () : Task<bool> =
                if Interlocked.Increment(&attempts.contents) = 1 then
                    firstAttemptStarted.TrySetResult() |> ignore
                    firstAttemptAnswer.Task
                else
                    Task.FromResult false

            let elapsed = Stopwatch.StartNew()
            let waitTask = running.WaitForAsync(probe, TimeSpan.FromSeconds 30.0)
            do! firstAttemptStarted.Task
            exitSignal.SetResult(Outcome.Exited 1)

            match! waitTask with
            | Error(ProcessError.NotReady(_, reported)) ->
                Assert.That(
                    Volatile.Read(&attempts.contents),
                    Is.EqualTo 2,
                    "a terminal false must cost exactly one extra observation"
                )

                Assert.That(reported, Is.EqualTo(TimeSpan.FromSeconds 30.0), "NotReady reports the whole budget")
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0), "the re-check is not a new poll")
            | other -> Assert.Fail $"expected NotReady, got {other}"

            firstAttemptAnswer.TrySetResult false |> ignore
        }
        :> Task

    // Cancellation still outranks readiness, including the re-check's own verdict: the caller's token
    // fires while that final observation is parked, and the parked observation would answer `true` if it
    // were ever allowed to finish — so `Cancelled` here proves the re-check is bounded by the caller's
    // token rather than awaited to completion.
    //
    // The exit is signalled only after the first attempt has provably STARTED, which is what makes the
    // attempt numbering trustworthy: a poll attempt abandoned at cancellation still runs its queued body
    // afterwards, so a probe that branches on an attempt counter without that gate can see the re-check
    // and the abandoned poll attempt arrive in either order.
    [<Test>]
    member _.``WaitFor reports Cancelled when the caller's token fires during the post-exit re-check``() : Task =
        task {
            let exitSignal =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = syntheticProcess exitSignal.Task
            use cts = new CancellationTokenSource()

            let firstAttemptStarted =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let parkedAnswer =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let attempts = ref 0

            let probe () : Task<bool> =
                if Interlocked.Increment(&attempts.contents) = 1 then
                    firstAttemptStarted.TrySetResult() |> ignore
                else
                    // The caller cancels from inside the one post-exit observation, so the cancellation
                    // is guaranteed to land while that observation is in flight — no window for the grace
                    // ceiling to decide the result first, and no dependence on scheduling latency.
                    cts.Cancel()

                // Both the stale observation and the re-check park; only the cancellation above can end
                // this wait, and it would answer `true` (below) if it were ever awaited to completion.
                parkedAnswer.Task

            let waitTask = running.WaitForAsync(probe, TimeSpan.FromSeconds 30.0, cts.Token)
            do! firstAttemptStarted.Task
            exitSignal.SetResult(Outcome.Exited 1)

            match! waitTask with
            | Error(ProcessError.Cancelled _) ->
                Assert.That(
                    Volatile.Read(&attempts.contents),
                    Is.EqualTo 2,
                    "the cancelled result must come from the re-check itself, not from skipping it"
                )
            | other -> Assert.Fail $"expected Cancelled, got %A{other}"

            parkedAnswer.TrySetResult true |> ignore
        }
        :> Task

    // Regression for R-01: once the shared exit race has obtained Ok, a caller cancellation observed before
    // the final return must still win over that success.
    [<Test>]
    member _.``WaitFor reports Cancelled when caller cancellation follows a raced readiness success``() : Task =
        task {
            use cts = new CancellationTokenSource()
            let raced = Ok()
            cts.Cancel()

            match ReadinessRace.preferCancellation "test" cts.Token raced with
            | Error(ProcessError.Cancelled "test") -> ()
            | other -> Assert.Fail $"expected Cancelled after raced Ok, got {other}"
        }
        :> Task

    // The other half of the priority rule: a deadline that is already spent when the exit is observed
    // must not buy one more observation. The clock is advanced far past the budget while the first
    // attempt is still parked, and the manual provider's timers never fire (see `ManualReadinessClock`),
    // so the spent budget is the only thing left that can decide the result — and it must decide it
    // without calling the probe again, even though a second call would answer `true`. This pins the
    // contract, not one line of it: the re-check is bounded by what remains of the caller's `timeout`,
    // so handing it a fresh window instead would show up here as a second invocation and an `Ok`.
    [<Test>]
    member _.``WaitFor skips the post-exit re-check once the deadline is already spent``() : Task =
        task {
            let clock = ManualReadinessClock()
            let config = (Command.create "test" |> Command.timeProvider clock).Config

            let exitSignal =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = syntheticProcessWith config exitSignal.Task

            let firstAttemptStarted =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let firstAttemptAnswer =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let attempts = ref 0

            let probe () : Task<bool> =
                if Interlocked.Increment(&attempts.contents) = 1 then
                    firstAttemptStarted.TrySetResult() |> ignore
                    firstAttemptAnswer.Task
                else
                    Task.FromResult true

            let waitTask = running.WaitForAsync(probe, TimeSpan.FromSeconds 5.0)
            do! firstAttemptStarted.Task
            clock.Advance(TimeSpan.FromHours 1.0)
            exitSignal.SetResult(Outcome.Exited 0)

            match! waitTask with
            | Error(ProcessError.NotReady(_, reported)) ->
                Assert.That(
                    Volatile.Read(&attempts.contents),
                    Is.EqualTo 1,
                    "a spent deadline must not start another observation"
                )

                Assert.That(reported, Is.EqualTo(TimeSpan.FromSeconds 5.0))
            | other -> Assert.Fail $"expected NotReady, got {other}"

            firstAttemptAnswer.TrySetResult false |> ignore
        }
        :> Task

    // Reap-once (KB K-016) regression for the port probe's new early-exit path, mirroring the existing
    // `WaitForHttp then WaitAsync reports the real exit code` test: the port probe now races the child's
    // exit via the memoized reap-once wait (`ensureBufferedWait`), not a raw independent `host.Wait()`.
    // If it started a second, uncoordinated `host.Wait()` on POSIX, the child would be reaped twice and
    // the follow-up `WaitAsync` would race an already-reaped pid and fabricate `Outcome.Unobserved`
    // instead of the real exit code. Exercises a REAL spawned child so it drives the real reap machinery.
    [<Test>]
    member _.``WaitForPort then WaitAsync reports the real exit code after an early child exit``() : Task =
        task {
            let probeListener = new TcpListener(IPAddress.Loopback, 0)
            probeListener.Start()
            let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
            probeListener.Stop()

            match! runner.StartAsync(shell "exit 7", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let endpoint = IPEndPoint(IPAddress.Loopback, port)

                match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 5.0) with
                | Error(ProcessError.NotReady _) -> ()
                | other -> Assert.Fail $"expected NotReady, got {other}"

                // Before the shared-wait fix, this second `host.Wait()` (via `WaitAsync`) raced an
                // already-reaped pid on POSIX and fabricated `Outcome.Unobserved` instead of the real code.
                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.Exited code -> Assert.That(code, Is.EqualTo 7)
                | other -> Assert.Fail $"expected Exited 7, got {other}"
        }
        :> Task

    // Regression for T-212: `raceReadinessAgainstExit` (below `WaitForPortAsync`/`WaitForHttpAsync`/etc.)
    // starts the shared, memoized `bufferedOutcome` (`ensureBufferedWait`) but never awaits it when the
    // probe itself wins the race — so a fault surfacing later on that same memoized wait (e.g.
    // `waitWithTimeout`'s timeout-race `onTimeout` hook calling native kill syscalls) previously had no
    // observer at all on a probe-only handle: probe → dispose, with no consuming verb ever reaching
    // `awaitBufferedOutcome`/`ExitTask`. `ensureBufferedWait` now attaches `observeFault` the moment it
    // creates the wait, so the fault is marked observed for the CLR unconditionally — before this fix,
    // it would surface here as `TaskScheduler.UnobservedTaskException` once the faulted task was
    // finalized.
    //
    // Runs the probe-only scenario (probe, which resolves to `NotReady` well before `exitFaultsLate`'s
    // ~300ms fault, then dispose, with no consuming verb ever touching the memoized exit wait) and hands
    // back only a `WeakReference` to the handle — never `running` itself. Isolated in a `NoInlining`
    // helper (and not returning the strong reference) so nothing in the calling test method's frame can
    // keep `running` — and, through its memoized `bufferedOutcome` field, the faulted task itself —
    // rooted for the rest of the method body. Before this split, `running` stayed reachable through the
    // enclosing `task { }` state machine across the `GC.Collect()` calls below (it was still in lexical
    // scope, `use`-bound or not), so the faulted task could never actually be collected and the
    // assertion passed trivially regardless of whether `observeFault` did anything (R-01).
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member private _.RunProbeOnlyHandleAndGetWeakRef(port: int) : Task<WeakReference> =
        task {
            let running = syntheticProcess (exitFaultsLate ())
            let endpoint = IPEndPoint(IPAddress.Loopback, port)

            match! running.WaitForPortAsync(endpoint, TimeSpan.FromMilliseconds 100.0) with
            | Error(ProcessError.NotReady _) -> ()
            | other -> Assert.Fail $"expected NotReady, got {other}"

            // No consuming verb ever touches the memoized exit wait from here on — dispose the handle
            // (which does not await it either, see `RunningProcess.DisposeAsync`) and let the still-
            // pending fault land with nothing else watching it.
            do! (running :> IAsyncDisposable).DisposeAsync()

            return WeakReference(box running)
        }

    [<Test>]
    member this.``A late fault on the memoized buffered exit wait does not surface as unobserved on a probe-only handle``
        ()
        : Task =
        task {
            let mutable unobserved = false

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved <- true
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                // Nothing is listening on this port, so the probe itself resolves to `NotReady` well
                // before `exitFaultsLate`'s ~300ms fault below — the probe, not the exit wait, wins
                // `raceReadinessAgainstExit`'s race, so `childExitTask` is never awaited there.
                let probeListener = new TcpListener(IPAddress.Loopback, 0)
                probeListener.Start()
                let port = (probeListener.LocalEndpoint :?> IPEndPoint).Port
                probeListener.Stop()

                let! weakHandle = this.RunProbeOnlyHandleAndGetWeakRef port

                // Let the exit wait's fault actually happen, then force a GC pass: an unobserved faulted
                // task reports itself from the finalizer once collected.
                do! Task.Delay 500
                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                // Verify the test's own methodology is sound first: if the handle — and with it the
                // memoized `bufferedOutcome` field holding the faulted task — was never actually
                // collected, the absence of `UnobservedTaskException` below would be a false pass rather
                // than proof that the implementation observed the fault (R-01).
                Assert.That(
                    weakHandle.IsAlive,
                    Is.False,
                    "probe-only handle was not collected — GC-based verification is inconclusive"
                )

                Assert.That(unobserved, Is.False)
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
        }
        :> Task

    // Companion to the regression above: `observeFault`'s `ContinueWith` is purely observational — it
    // must not swallow or replace the fault for a verb that genuinely awaits this same memoized wait.
    // `WaitAsync` reuses `ensureBufferedWait()` (`awaitBufferedOutcome`), so it must still see and
    // re-throw the original fault exactly as before this fix.
    [<Test>]
    member _.``A late fault on the memoized buffered exit wait still reaches a verb that awaits it``() : Task =
        task {
            let running = syntheticProcess (exitFaultsLate ())

            try
                let! _ = running.WaitAsync()
                Assert.Fail "expected the exit wait's fault to propagate to WaitAsync"
            with ex ->
                Assert.That(ex.Message, Does.Contain "synthetic exit-wait fault")

            do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    // The platform-support gate (`ReadinessProbe.unixDomainSocketsSupported`) is factored out as a
    // predicate specifically so both branches are testable without depending on the actual host's real
    // `AF_UNIX` support — see its doc comment. These two tests exercise the gate directly, deterministic
    // on every CI host regardless of whether that host truly supports `AF_UNIX`.
    [<Test>]
    member _.``WaitForSocket support gate reports Unsupported when the host has no AF_UNIX support``() =
        match ReadinessProbe.unixDomainSocketsSupported (fun () -> false) with
        | Error(ProcessError.Unsupported _) -> Assert.Pass()
        | other -> Assert.Fail $"expected Unsupported, got {other}"

    [<Test>]
    member _.``WaitForSocket support gate passes through when the host supports AF_UNIX``() =
        match ReadinessProbe.unixDomainSocketsSupported (fun () -> true) with
        | Ok() -> Assert.Pass()
        | other -> Assert.Fail $"expected Ok, got {other}"

    [<Test>]
    member _.``WaitForSocket connects to a listening Unix domain socket``() : Task =
        task {
            if not Socket.OSSupportsUnixDomainSockets then
                Assert.Ignore "this host has no AF_UNIX support"

            let listener, path = startUnixSocketListener ()

            try
                // A never-completing `Wait` models a child that stays running throughout the probe
                // (KB K-044): an immediately-resolving one would spuriously trigger the early-exit race
                // this test is not exercising.
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForSocketAsync(path, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                listener.Dispose()
                deleteSocketPathIfPresent path
        }
        :> Task

    [<Test>]
    member _.``WaitForSocket returns NotReady when nothing is listening on the socket path``() : Task =
        task {
            if not Socket.OSSupportsUnixDomainSockets then
                Assert.Ignore "this host has no AF_UNIX support"

            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.sock")
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForSocketAsync(path, TimeSpan.FromMilliseconds 250.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForSocket rejects an endpoint path that cannot fit in sun_path``() : Task =
        task {
            if not Socket.OSSupportsUnixDomainSockets then
                Assert.Ignore "this host has no AF_UNIX support"

            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let path = String('x', 512)

            Assert.Throws<ArgumentOutOfRangeException>(
                Action(fun () -> running.WaitForSocketAsync(path, TimeSpan.FromSeconds 1.0) |> ignore)
            )
            |> ignore
        }
        :> Task

    // Early-exit contract, socket probe: a child that has already exited must resolve to `NotReady`
    // promptly rather than polling the socket path out for the full timeout — the same behaviour
    // `WaitForPortAsync`/`WaitForHttpAsync` give (see the equivalent port test above). `Wait` resolves to
    // an exited outcome, so the shared exit wait the probe races against completes at once and cancels
    // the still-polling socket probe.
    [<Test>]
    member _.``WaitForSocket returns NotReady promptly when the child has already exited``() : Task =
        task {
            if not Socket.OSSupportsUnixDomainSockets then
                Assert.Ignore "this host has no AF_UNIX support"

            // Nothing is bound at this path, so every connect attempt fails and, absent early-exit
            // detection, the probe would poll out the full 5s timeout.
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.sock")
            use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForSocketAsync(path, TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected early NotReady, got {other}"
        }
        :> Task

    // `RunningProcess.WaitForSocketAsync` wires the injectable gate above to the real
    // `Socket.OSSupportsUnixDomainSockets`; the two real-dial tests above already prove that wiring
    // doesn't short-circuit to `Unsupported` on a host that does support `AF_UNIX` (they'd fail on
    // `Assert.Fail` for an unexpected `Unsupported`, not just skip). This test instead proves the gate
    // fires BEFORE the shared exit race starts (KB K-043/K-016): a synthetic host whose `Wait` never
    // completes would hang if `WaitForSocketAsync` ever awaited `raceReadinessAgainstExit`'s exit race
    // for it, so a prompt result here (whichever branch the real host takes) demonstrates the gate is
    // checked up front. Only meaningful on a host WITHOUT `AF_UNIX` support — skipped otherwise, since
    // the two tests above already cover the supported-host wiring.
    [<Test>]
    member _.``WaitForSocket reports Unsupported promptly, without racing the child's exit, when AF_UNIX is unavailable``
        ()
        : Task =
        task {
            if Socket.OSSupportsUnixDomainSockets then
                Assert.Ignore "this host supports AF_UNIX; covered instead by the real-dial tests above"

            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForSocketAsync("/nonexistent/path.sock", TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.Unsupported _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.0))
            | other -> Assert.Fail $"expected Unsupported, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPath succeeds immediately when the path already exists``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.ready")
            File.WriteAllText(path, "")

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                File.Delete path
        }
        :> Task

    [<Test>]
    member _.``WaitForPath succeeds once a file is created while polling``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.ready")

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                let createLate =
                    task {
                        do! Task.Delay 200
                        File.WriteAllText(path, "")
                    }

                match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.That(createLate.IsCompletedSuccessfully, Is.True, "the file was never created")
                | Error error -> Assert.Fail $"{error}"
            finally
                File.Delete path
        }
        :> Task

    // The existence-only contract (task.md/K-043): a directory counts as "ready" too, not just a
    // regular file — mirrors `wait_for_path`'s Rust contract this port follows.
    [<Test>]
    member _.``WaitForPath counts an existing directory as ready, not files only``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.dir")
            Directory.CreateDirectory path |> ignore

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                Directory.Delete path
        }
        :> Task

    [<Test>]
    member _.``WaitForPath returns NotReady when the path never appears``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.never")
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForPathAsync(path, TimeSpan.FromMilliseconds 250.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPath is cancelled by the external token while the path never appears``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.never")
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 30.0, cts.Token) with
            | Error(ProcessError.Cancelled _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    // Early-exit contract, path probe: a child that has already exited must resolve to `NotReady`
    // promptly rather than polling the path out for the full timeout — the same behaviour
    // `WaitForPortAsync`/`WaitForSocketAsync` give (see the equivalent tests above). `Wait` resolves to
    // an exited outcome, so the shared exit wait the probe races against completes at once and cancels
    // the still-polling path probe.
    [<Test>]
    member _.``WaitForPath returns NotReady promptly when the child has already exited``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.never")
            use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected early NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForPath rejects a null path``() : Task =
        task {
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let nullPath = Unchecked.defaultof<string>

            Assert.Throws<ArgumentNullException>(
                Action(fun () -> running.WaitForPathAsync(nullPath, TimeSpan.FromSeconds 1.0) |> ignore)
            )
            |> ignore
        }
        :> Task

    // A filesystem lookup failure is treated as "not yet ready", not a probe fault: `waitForPathUsing`
    // is exercised directly (bypassing the real `File.Exists`/`Directory.Exists` wiring `waitForPath`
    // does) with an `exists` stand-in that throws on its first two calls, mirroring how
    // `WaitForPort respects the shared deadline while a connect attempt itself is still in flight`
    // above exercises the injectable connect seam directly rather than through the production wiring.
    [<Test>]
    member _.``WaitForPath treats an exists-check failure as not-ready and keeps retrying``() : Task =
        task {
            let mutable calls = 0

            let flakyExists (_: string) : bool =
                calls <- calls + 1

                if calls < 3 then
                    raise (IOException "synthetic lookup failure")
                else
                    true

            match!
                ReadinessProbe.waitForPathUsing
                    TimeProvider.System
                    flakyExists
                    "test"
                    "irrelevant"
                    ReadinessAttempts.PollUntilDeadline
                    (TimeSpan.FromSeconds 3.0)
                    CancellationToken.None
            with
            | Ok() -> Assert.That(calls, Is.GreaterThanOrEqualTo 3)
            | other -> Assert.Fail $"expected Ok, got {other}"
        }
        :> Task

    // FakeProcess/cassette parity (T-374): a fake handle IS a real `RunningProcess` built over
    // in-memory streams (`FakeProcess.Build`), so it inherits every readiness verb — including this
    // new one — through the same construction path `WaitForPort`/`WaitForSocket` already rely on, with
    // no separate wiring needed in `FakeProcess`/`Cassette` themselves.
    [<Test>]
    member _.``WaitForPath works against a FakeProcess double, same as a real handle``() : Task =
        task {
            let path = Path.Combine(Path.GetTempPath(), $"processkit-{Guid.NewGuid():N}.ready")
            File.WriteAllText(path, "")

            try
                use running = FakeProcess.Create().Build()

                match! running.WaitForPathAsync(path, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                File.Delete path
        }
        :> Task

    // Relative-path resolution contract (R-01): a relative `path` resolves against the run's own
    // configured `Command.CurrentDir`, the CHILD's working directory — not against whatever directory
    // this test process happens to be running from — the same rule `Command.PreferLocal` already
    // applies to its own relative entries.
    [<Test>]
    member _.``WaitForPath resolves a relative path against the configured CurrentDir``() : Task =
        task {
            let childDir = Directory.CreateTempSubdirectory("processkit-waitforpath-").FullName
            let relativeName = $"processkit-{Guid.NewGuid():N}.ready"
            File.WriteAllText(Path.Combine(childDir, relativeName), "")

            try
                let config = (Command.create "test" |> Command.currentDir childDir).Config
                use running = syntheticProcessWith config (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForPathAsync(relativeName, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                Directory.Delete(childDir, true)
        }
        :> Task

    // The other half of the same contract: a same-named relative path sitting in a DIFFERENT
    // directory must not be mistaken for the configured `CurrentDir`'s own sentinel — proving the
    // resolution looks exactly where R-01 ratified it should, not "somewhere" that happens to match.
    [<Test>]
    member _.``WaitForPath does not consider a relative path ready when it exists only outside the configured CurrentDir``
        ()
        : Task =
        task {
            let childDir =
                Directory.CreateTempSubdirectory("processkit-waitforpath-child-").FullName

            let elsewhereDir =
                Directory.CreateTempSubdirectory("processkit-waitforpath-elsewhere-").FullName

            let relativeName = $"processkit-{Guid.NewGuid():N}.ready"
            File.WriteAllText(Path.Combine(elsewhereDir, relativeName), "")

            try
                let config = (Command.create "test" |> Command.currentDir childDir).Config
                use running = syntheticProcessWith config (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForPathAsync(relativeName, TimeSpan.FromMilliseconds 250.0) with
                | Error(ProcessError.NotReady _) -> Assert.Pass()
                | other -> Assert.Fail $"expected NotReady (elsewhere-directory hit must not count), got {other}"
            finally
                Directory.Delete(childDir, true)
                Directory.Delete(elsewhereDir, true)
        }
        :> Task

    // The platform-support gate (`ReadinessProbe.namedPipeSupported`) is factored out as a predicate
    // specifically so both branches are testable without depending on the actual host's OS — see its
    // doc comment. These two tests exercise the gate directly, deterministic on every CI host.
    [<Test>]
    member _.``WaitForNamedPipe support gate reports Unsupported when the host has no Windows named-pipe support``() =
        match ReadinessProbe.namedPipeSupported (fun () -> false) with
        | Error(ProcessError.Unsupported _) -> Assert.Pass()
        | other -> Assert.Fail $"expected Unsupported, got {other}"

    [<Test>]
    member _.``WaitForNamedPipe support gate passes through when the host supports Windows named pipes``() =
        match ReadinessProbe.namedPipeSupported (fun () -> true) with
        | Ok() -> Assert.Pass()
        | other -> Assert.Fail $"expected Ok, got {other}"

    // Name resolution (`ReadinessProbe.resolveNamedPipeName`), pure and cross-platform: a bare name is
    // resolved under the local `\\.\pipe\` namespace, while an already-qualified path — local or a
    // remote server's UNC form — passes through unchanged. Mirrors the source Rust crate's
    // `wait_for_pipe` resolution rule.
    [<Test>]
    member _.``WaitForNamedPipe resolves a bare name under the local pipe namespace``() =
        Assert.That(ReadinessProbe.resolveNamedPipeName "my-service", Is.EqualTo(@"\\.\pipe\my-service"))

    [<Test>]
    member _.``WaitForNamedPipe leaves an already-qualified local pipe path unchanged``() =
        let path = @"\\.\pipe\my-service"
        Assert.That(ReadinessProbe.resolveNamedPipeName path, Is.EqualTo path)

    [<Test>]
    member _.``WaitForNamedPipe leaves an already-qualified remote pipe path unchanged``() =
        let path = @"\\server\pipe\my-service"
        Assert.That(ReadinessProbe.resolveNamedPipeName path, Is.EqualTo path)

    // The native seam (`Native.Windows.classifyNamedPipeOpen`), pure and cross-platform: a testable seed
    // that classifies a `CreateFileW` attempt's outcome from synthetic `handle`/`lastError` inputs, with
    // no real named-pipe server involved. See its doc comment for what each case means and why
    // `ERROR_PIPE_BUSY` classifies as `Ready` rather than `Missing` or a fourth, distinct case.
    [<Test>]
    member _.``classifyNamedPipeOpen reports Ready for a valid handle``() =
        match Windows.classifyNamedPipeOpen (nativeint 1234) 0 with
        | Windows.NamedPipeProbeOutcome.Ready -> Assert.Pass()
        | other -> Assert.Fail $"expected Ready, got {other}"

    [<Test>]
    member _.``classifyNamedPipeOpen reports Ready for ERROR_PIPE_BUSY even though the handle is invalid``() =
        match Windows.classifyNamedPipeOpen IntPtr.Zero 231 with
        | Windows.NamedPipeProbeOutcome.Ready -> Assert.Pass()
        | other -> Assert.Fail $"expected Ready (ERROR_PIPE_BUSY proves a server exists), got {other}"

    [<Test>]
    member _.``classifyNamedPipeOpen reports Missing for ERROR_FILE_NOT_FOUND``() =
        match Windows.classifyNamedPipeOpen IntPtr.Zero 2 with
        | Windows.NamedPipeProbeOutcome.Missing -> Assert.Pass()
        | other -> Assert.Fail $"expected Missing, got {other}"

    [<Test>]
    member _.``classifyNamedPipeOpen reports Missing for ERROR_PATH_NOT_FOUND``() =
        match Windows.classifyNamedPipeOpen IntPtr.Zero 3 with
        | Windows.NamedPipeProbeOutcome.Missing -> Assert.Pass()
        | other -> Assert.Fail $"expected Missing, got {other}"

    [<Test>]
    member _.``classifyNamedPipeOpen reports a distinct OpenFailed for any other error, never folded into Missing``() =
        // ERROR_ACCESS_DENIED (5) — a genuinely different failure from "not created yet", which must stay
        // separately classified rather than being silently collapsed into `Missing`.
        match Windows.classifyNamedPipeOpen IntPtr.Zero 5 with
        | Windows.NamedPipeProbeOutcome.OpenFailed 5 -> Assert.Pass()
        | other -> Assert.Fail $"expected OpenFailed 5, got {other}"

    [<Test>]
    member _.``WaitForNamedPipe connects to a listening named pipe``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let server, name = startNamedPipeListener ()

            try
                // A never-completing `Wait` models a child that stays running throughout the probe (KB
                // K-044): an immediately-resolving one would spuriously trigger the early-exit race this
                // test is not exercising.
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForNamedPipeAsync(@"\\.\pipe\" + name, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                server.Dispose()
        }
        :> Task

    [<Test>]
    member _.``WaitForNamedPipe accepts a bare pipe name, resolved under the local namespace``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let server, name = startNamedPipeListener ()

            try
                use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

                match! running.WaitForNamedPipeAsync(name, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                server.Dispose()
        }
        :> Task

    // `ERROR_PIPE_BUSY` still counts as ready (K-043-style choke point, and the source Rust crate's own
    // contract): every instance of a single-instance pipe is occupied by another client, but that PROVES
    // a server created the pipe, so a probe must not report it as absent.
    [<Test>]
    member _.``WaitForNamedPipe treats a busy single-instance pipe as ready``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let name = $"processkit-{Guid.NewGuid():N}"

            use server =
                new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1)

            let acceptTask = server.WaitForConnectionAsync()

            use occupied =
                new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut)

            do! occupied.ConnectAsync(2000)
            do! acceptTask

            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)

            match! running.WaitForNamedPipeAsync(name, TimeSpan.FromSeconds 3.0) with
            | Ok() -> Assert.Pass()
            | Error error -> Assert.Fail $"expected ERROR_PIPE_BUSY to count as ready, got {error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForNamedPipe returns NotReady when nothing is listening on the pipe name``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let name = $"processkit-{Guid.NewGuid():N}"
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForNamedPipeAsync(name, TimeSpan.FromMilliseconds 250.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    // Early-exit contract, named-pipe probe: a child that has already exited must resolve to `NotReady`
    // promptly rather than polling the pipe name out for the full timeout — the same behaviour
    // `WaitForPortAsync`/`WaitForSocketAsync`/`WaitForPathAsync` give (see the equivalent tests above).
    [<Test>]
    member _.``WaitForNamedPipe returns NotReady promptly when the child has already exited``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let name = $"processkit-{Guid.NewGuid():N}"
            use running = syntheticProcess (Task.FromResult(Outcome.Exited 1))
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForNamedPipeAsync(name, TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.NotReady _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected early NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForNamedPipe is cancelled by the external token while the pipe never appears``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let name = $"processkit-{Guid.NewGuid():N}"
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForNamedPipeAsync(name, TimeSpan.FromSeconds 30.0, cts.Token) with
            | Error(ProcessError.Cancelled _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 3.0))
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForNamedPipe rejects a null pipe name``() : Task =
        task {
            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let nullName = Unchecked.defaultof<string>

            Assert.Throws<ArgumentNullException>(
                Action(fun () -> running.WaitForNamedPipeAsync(nullName, TimeSpan.FromSeconds 1.0) |> ignore)
            )
            |> ignore
        }
        :> Task

    // FakeProcess parity (mirrors the WaitForPath test above): a fake handle IS a real `RunningProcess`
    // built over in-memory streams (`FakeProcess.Build`), so it inherits every readiness verb —
    // including this new one — through the same construction path, with no separate wiring needed in
    // `FakeProcess`/`Cassette` themselves.
    [<Test>]
    member _.``WaitForNamedPipe works against a FakeProcess double, same as a real handle``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                Assert.Ignore "Windows-only: exercises the named-pipe readiness probe"

            let server, name = startNamedPipeListener ()

            try
                use running = FakeProcess.Create().Build()

                match! running.WaitForNamedPipeAsync(name, TimeSpan.FromSeconds 3.0) with
                | Ok() -> Assert.Pass()
                | Error error -> Assert.Fail $"{error}"
            finally
                server.Dispose()
        }
        :> Task

    // The platform gate fires BEFORE the shared exit race starts (KB K-043/K-016), the same contract
    // `WaitForSocket reports Unsupported promptly...` proves for `AF_UNIX`: a synthetic host whose `Wait`
    // never completes would hang if `WaitForNamedPipeAsync` ever awaited `raceReadinessAgainstExit`'s
    // exit race for it, so a prompt result here demonstrates the gate is checked up front. Only
    // meaningful on a non-Windows host — skipped on Windows, where the tests above already cover the
    // supported-host wiring.
    [<Test>]
    member _.``WaitForNamedPipe reports Unsupported promptly, without racing the child's exit, on a non-Windows host``
        ()
        : Task =
        task {
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                Assert.Ignore "this host is Windows; covered instead by the real-dial tests above"

            use running = syntheticProcess (TaskCompletionSource<Outcome>().Task)
            let elapsed = Stopwatch.StartNew()

            match! running.WaitForNamedPipeAsync("nonexistent-pipe", TimeSpan.FromSeconds 5.0) with
            | Error(ProcessError.Unsupported _) -> Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.0))
            | other -> Assert.Fail $"expected Unsupported, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitAny returns the first process to exit``() : Task =
        task {
            match! runner.StartAsync(shell "exit 0", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok fast ->
                match! runner.StartAsync(lingering (), CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok slow ->
                    use fast = fast
                    use slow = slow

                    let! result = RunningProcess.WaitAnyAsync [| fast; slow |]
                    Assert.That(result.Index, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``WaitAll waits for every process``() : Task =
        task {
            match! runner.StartAsync(shell "exit 3", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok first ->
                match! runner.StartAsync(shell "exit 0", CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok second ->
                    use first = first
                    use second = second
                    let! outcomes = RunningProcess.WaitAllAsync [| first; second |]
                    Assert.That(outcomes.Length, Is.EqualTo 2)
        }
        :> Task

/// The STDERR readiness surface (T-379): `WaitForStderrLineAsync` (complete framed lines) and
/// `WaitForStderrTailAsync` (the unterminated tail, for a prompt that carries no line terminator).
///
/// A fixture of its own rather than more members on `ReadinessTests` above: these waits are driven by
/// the stderr line pump rather than by the polling core the probes above share, so they need their own
/// synthetic child (one that writes stderr on the test's cue, byte for byte, with no line terminator
/// when that is the point) instead of a listener/endpoint to dial.
[<TestFixture>]
type StderrReadinessTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A synthetic `RunningProcess` over two in-memory pipes (not a real OS pipe or subprocess) plus a
    // caller-driven exit — the deterministic way to play "the child writes exactly this to stderr, now"
    // on every OS in the matrix, including output that deliberately carries no line terminator (which
    // `cmd.exe`'s `echo` cannot produce). Returns the handle and the two writer-side streams the test
    // uses as the child.
    let syntheticChild (config: CommandConfig) (exitTask: Task<Outcome>) : RunningProcess * Stream * Stream =
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some(stdoutPipe.Reader.AsStream())
              Stderr = Some(stderrPipe.Reader.AsStream())
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> exitTask
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host), stdoutPipe.Writer.AsStream(), stderrPipe.Writer.AsStream()

    // A child that never exits on its own, so a wait ends on what the test writes (or on its own
    // deadline) rather than on a synthetic exit racing it (KB K-044).
    let liveChild (config: CommandConfig) =
        syntheticChild config (TaskCompletionSource<Outcome>().Task)

    // Write `text` as the child would: encoded with the given encoding, flushed so the pump's next read
    // sees it, and with NO line terminator added — the caller writes one if it wants one.
    let write (stream: Stream) (encoding: Encoding) (text: string) : Task =
        task {
            let bytes = encoding.GetBytes text
            do! stream.WriteAsync(ReadOnlyMemory bytes)
            do! stream.FlushAsync()
        }
        :> Task

    let writeUtf8 (stream: Stream) (text: string) = write stream Encoding.UTF8 text

    [<Test>]
    member _.``WaitForStderrLine matches a stderr line from a real child``() : Task =
        task {
            let command =
                if isWindows then
                    shell "echo ready 1>&2&ping 127.0.0.1 -n 5 >NUL"
                else
                    shell "echo ready >&2; sleep 4"

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match!
                    running.WaitForStderrLineAsync((fun line -> line.Contains "ready"), TimeSpan.FromSeconds 10.0)
                with
                | Ok line -> Assert.That(line, Does.Contain "ready")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrLine times out with NotReady when the line never appears``() : Task =
        task {
            let running, _stdout, stderr = liveChild (Command.create "test").Config
            use running = running
            do! writeUtf8 stderr "starting up\n"

            match!
                running.WaitForStderrLineAsync((fun line -> line.Contains "never"), TimeSpan.FromMilliseconds 300.0)
            with
            | Error(ProcessError.NotReady _) -> Assert.Pass()
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrLine reports the clamped (armable) timeout in NotReady, not an over-long raw one``
        ()
        : Task =
        task {
            // Same clamp contract as `WaitForLineAsync`: an over-long timeout cannot be armed on a BCL
            // timer as-is, so `Timeouts.clampArmable` is what gets armed AND what `NotReady` must
            // report. Closing stderr with nothing written is a clean EOF, so this resolves at once
            // instead of waiting out an unarmably long deadline.
            let running, _stdout, stderr = liveChild (Command.create "test").Config
            use running = running
            stderr.Close()

            match! running.WaitForStderrLineAsync((fun _ -> true), TimeSpan.MaxValue) with
            | Error(ProcessError.NotReady(_, reportedTimeout)) ->
                Assert.That(reportedTimeout, Is.EqualTo Timeouts.maxArmable)
            | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrLine reports Cancelled when the caller's token fires first``() : Task =
        task {
            let running, _stdout, _stderr = liveChild (Command.create "test").Config
            use running = running
            use cts = new CancellationTokenSource()

            let waiting =
                running.WaitForStderrLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0, cts.Token)

            cts.Cancel()

            match! waiting with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrLine reports NotReady when the child exits before the line arrives``() : Task =
        task {
            // A child that writes something else to stderr and exits. The wait must end on that EOF,
            // not sit out its (much longer) deadline.
            let command =
                if isWindows then
                    shell "echo other 1>&2"
                else
                    shell "echo other >&2"

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let clock = Stopwatch.StartNew()

                match!
                    running.WaitForStderrLineAsync((fun line -> line.Contains "ready"), TimeSpan.FromSeconds 30.0)
                with
                | Error(ProcessError.NotReady _) ->
                    Assert.That(
                        clock.Elapsed,
                        Is.LessThan(TimeSpan.FromSeconds 20.0),
                        "the child's stderr reached EOF, so this must not wait out the deadline"
                    )
                | other -> Assert.Fail $"expected NotReady, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrTail matches a newline-free stderr prompt``() : Task =
        task {
            let running, _stdout, stderr = liveChild (Command.create "test").Config
            use running = running

            // No line terminator anywhere: a line wait could never see this, which is the whole point
            // of the partial-tail verb. Written in two pieces so the match also proves the tail is
            // ACCUMULATED across reads rather than matched per chunk.
            do! writeUtf8 stderr "Enter "
            do! writeUtf8 stderr "password: "

            match!
                running.WaitForStderrTailAsync((fun tail -> tail.EndsWith "password: "), TimeSpan.FromSeconds 10.0)
            with
            | Ok tail -> Assert.That(tail, Is.EqualTo "Enter password: ")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrTail matches a newline-free stderr prompt from a real child``() : Task =
        task {
            if isWindows then
                // `cmd.exe`'s `echo` always terminates its line, and the `set /p` trick that does not
                // needs an interactive-console shape this suite deliberately avoids. The synthetic and
                // test-double regressions around this one cover the same path on every OS, here
                // included.
                Assert.Ignore "no portable newline-free writer in cmd.exe; covered by the synthetic child"

            match! runner.StartAsync(shell "printf 'Password: ' >&2; sleep 4", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match!
                    running.WaitForStderrTailAsync((fun tail -> tail.EndsWith "Password: "), TimeSpan.FromSeconds 10.0)
                with
                | Ok tail -> Assert.That(tail, Is.EqualTo "Password: ")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrTail matches a newline-free prompt scripted on a FakeProcess``() : Task =
        task {
            // Test-double parity: the same newline-free stderr prompt a real child publishes is
            // scriptable on the double, and the same verb finds it there.
            use running = FakeProcess.Create("fake").WithStderr("Password: ").Build()

            match!
                running.WaitForStderrTailAsync((fun tail -> tail.EndsWith "Password: "), TimeSpan.FromSeconds 10.0)
            with
            | Ok tail -> Assert.That(tail, Is.EqualTo "Password: ")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``WaitForStderrTail matches a newline-free prompt scripted through ScriptedRunner``() : Task =
        task {
            // The same parity one layer up: a `Reply`'s stderr is replayed verbatim through the
            // `IProcessRunner` seam, so code that waits on a newline-free stderr prompt is testable
            // against the double exactly as it runs against a real spawn.
            let scripted =
                ScriptedRunner().Fallback(Reply.Ok("").WithStderr "Password: ") :> IProcessRunner

            match! scripted.SpawnAsync(Command.create "installer", CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                match!
                    running.WaitForStderrTailAsync((fun tail -> tail.EndsWith "Password: "), TimeSpan.FromSeconds 10.0)
                with
                | Ok tail -> Assert.That(tail, Is.EqualTo "Password: ")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a partial tail matched by a wait still reaches the capture exactly once, as one line``() : Task =
        task {
            // The invariant the partial-tail verb must not break: the tail it matched is NOT a line and
            // is delivered nowhere twice — it arrives once, later, inside the line the pump frames it
            // into. `OnStderrLine` counts what the framing produced; `Finished.Stderr` is what the
            // capture kept.
            let framed = ResizeArray<string>()
            let exit = TaskCompletionSource<Outcome>()

            let config =
                (Command.create "test"
                 |> Command.onStderrLine (fun line -> lock framed (fun () -> framed.Add line)))
                    .Config

            let running, stdout, stderr = syntheticChild config exit.Task
            use running = running
            do! writeUtf8 stderr "Password: "

            match!
                running.WaitForStderrTailAsync((fun tail -> tail.EndsWith "Password: "), TimeSpan.FromSeconds 10.0)
            with
            | Ok tail -> Assert.That(tail, Is.EqualTo "Password: ")
            | Error error -> Assert.Fail $"{error}"

            // The child finishes the very line whose tail was matched, then exits.
            do! writeUtf8 stderr "\ndone\n"
            stdout.Close()
            stderr.Close()
            exit.SetResult(Outcome.Exited 0)

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished ->
                Assert.That(finished.Stderr, Is.EqualTo "Password: \ndone")
                Assert.That(running.StderrLineCount, Is.EqualTo 2)

            // Joined rather than compared as a sequence: `Assert.That(list, Is.EqualTo [ ... ])` cannot
            // resolve its overload from F# (see the note in `FreeBsdReaperTests`), and the joined form
            // shows the exact framing this assertion is about.
            Assert.That(String.Join("|", framed), Is.EqualTo "Password: |done")
        }
        :> Task

    [<Test>]
    member _.``a stderr line a wait consumed is not re-delivered to the next wait``() : Task =
        task {
            // The stderr form of "consumed lines are not re-delivered": a wait consumes what it read,
            // matching or not, exactly as a `WaitForLineAsync` consumer consumes the stdout lines it
            // takes off the channel.
            let running, _stdout, stderr = liveChild (Command.create "test").Config
            use running = running
            do! writeUtf8 stderr "alpha\nbravo\ncharlie\n"

            match! running.WaitForStderrLineAsync((fun line -> line = "bravo"), TimeSpan.FromSeconds 10.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "bravo")
            | Error error -> Assert.Fail $"{error}"

            // `alpha` was read (and skipped) on the way to `bravo`, so it is gone: this wait can only
            // time out. It reads (and so consumes) `charlie` on its way to that timeout, exactly as a
            // timed-out `WaitForLineAsync` consumes the stdout lines it took off the channel.
            match! running.WaitForStderrLineAsync((fun line -> line = "alpha"), TimeSpan.FromMilliseconds 300.0) with
            | Error(ProcessError.NotReady _) -> ()
            | other -> Assert.Fail $"expected a consumed line NOT to be re-delivered, got {other}"

            // The watch is still live for what the child writes NEXT — consuming is not closing.
            do! writeUtf8 stderr "delta\n"

            match! running.WaitForStderrLineAsync((fun line -> line = "delta"), TimeSpan.FromSeconds 10.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "delta")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``stderr readiness honours StderrEncoding and StderrLineTerminator, like every other stderr path``
        ()
        : Task =
        task {
            // Parity with the stdout path and with `PumpStderrBuffer`: the same two config knobs decide
            // what a stderr line IS. Latin-1 bytes decoded as UTF-8 would be replacement characters, and
            // a bare '\r' is content — not a terminator — unless `Cr` framing says otherwise.
            let config =
                (Command.create "test"
                 |> Command.stderrEncoding Encoding.Latin1
                 |> Command.stderrLineTerminator LineTerminator.Cr)
                    .Config

            let running, _stdout, stderr = liveChild config
            use running = running

            // The trailing content after the '\r' is what RESOLVES it: this pump defers a carriage
            // return until the next character (or EOF) decides whether it was a lone `\r` or half of a
            // `\r\n` — the same deferral every other stderr consumer on this handle sees, since they
            // all read through the one pump.
            do! write stderr Encoding.Latin1 "café ready\rnext"

            match! running.WaitForStderrLineAsync((fun line -> line = "café ready"), TimeSpan.FromSeconds 10.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "café ready")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``stderr readiness composes with the stdout streaming session it shares``() : Task =
        task {
            let running, stdout, stderr = liveChild (Command.create "test").Config
            use running = running
            do! writeUtf8 stdout "out-1\n"
            do! writeUtf8 stderr "ready\n"

            match! running.WaitForStderrLineAsync((fun line -> line = "ready"), TimeSpan.FromSeconds 10.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "ready")
            | Error error -> Assert.Fail $"{error}"

            // stdout was drained and QUEUED throughout the stderr wait (one session, one pump per
            // pipe), so the stream is still there to take afterwards — nothing was lost or discarded.
            let lines = running.StdoutLinesAsync().GetAsyncEnumerator()

            try
                let! moved = lines.MoveNextAsync()
                Assert.That(moved, Is.True)
                Assert.That(lines.Current, Is.EqualTo "out-1")
            finally
                lines.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
        :> Task

    [<Test>]
    member _.``stderr readiness joins a streaming session another verb already started``() : Task =
        task {
            // The other arming order: `StdoutLinesAsync` claimed the session (and started its pumps)
            // first, so this wait joins that session rather than starting one. It watches stderr from
            // the moment it arms — which is what the child writes next.
            let running, stdout, stderr = liveChild (Command.create "test").Config
            use running = running
            let lines = running.StdoutLinesAsync().GetAsyncEnumerator()

            try
                do! writeUtf8 stdout "out-1\n"
                let! moved = lines.MoveNextAsync()
                Assert.That(moved, Is.True)
                Assert.That(lines.Current, Is.EqualTo "out-1")

                // Armed BEFORE the child writes it: a wait that joins a session already in flight
                // watches stderr from the moment it arms, so the ordering is the test's to control,
                // not a race to hope for.
                let waiting =
                    running.WaitForStderrLineAsync((fun line -> line = "ready"), TimeSpan.FromSeconds 10.0)

                do! writeUtf8 stderr "ready\n"

                match! waiting with
                | Ok line -> Assert.That(line, Is.EqualTo "ready")
                | Error error -> Assert.Fail $"{error}"
            finally
                lines.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }
        :> Task

    [<Test>]
    member _.``stderr readiness is refused after another verb has consumed the pipes``() : Task =
        task {
            // The buffered verbs own the pipes outright, so a later stderr wait must be refused rather
            // than left waiting on a stream it can never be given — the same already-consumed contract
            // `WaitForLineAsync` follows.
            use buffered = FakeProcess.Create("fake").WithStderr("ready\n").Build()

            match! buffered.OutputStringAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok _ -> ()

            let clock = Stopwatch.StartNew()

            match! buffered.WaitForStderrLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported message) ->
                Assert.That(message, Does.Contain "already been consumed")

                Assert.That(
                    clock.Elapsed,
                    Is.LessThan(TimeSpan.FromSeconds 30.0),
                    "the refusal must come from the claim gate, not from waiting out the timeout"
                )
            | other -> Assert.Fail $"expected an already-consumed refusal, got {other}"

            // The stderr BYTE-chunk session is the other stderr reader on this handle; it owns the pipe
            // and its consumer already has those bytes, so a readiness wait over it is refused too.
            use chunked = FakeProcess.Create("fake").WithStderr("ready\n").Build()
            chunked.StderrChunksAsync() |> ignore

            match! chunked.WaitForStderrTailAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "already been consumed")
            | other -> Assert.Fail $"expected an already-consumed refusal, got {other}"
        }
        :> Task

    [<Test>]
    member _.``stderr readiness after a terminal fresh FinishAsync refuses instead of reporting NotReady``() : Task =
        task {
            use running = FakeProcess.Create("fake").WithStderr("ready\n").Build()

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))

            match! running.WaitForStderrLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "already been consumed")
            | other -> Assert.Fail $"expected an already-consumed refusal, got {other}"
        }
        :> Task

    [<Test>]
    member _.``stderr readiness reports Unsupported on a run with no separate stderr stream``() : Task =
        task {
            // `MergeStderr` folds stderr into stdout at the OS level: there is no stderr stream to wait
            // on, and a `NotReady` here would read as "the marker never came" for a stream that never
            // existed. The refusal must name the cause and point at the verb that can see those bytes.
            use merged =
                FakeProcess.OfCommand(Command.create "tool" |> Command.mergeStderr).WithStderr("ready\n").Build()

            match! merged.WaitForStderrLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported message) ->
                Assert.That(message, Does.Contain "WaitForStderrLineAsync")
                Assert.That(message, Does.Contain "MergeStderr")
                Assert.That(message, Does.Contain "WaitForLineAsync")
            | other -> Assert.Fail $"expected Unsupported, got {other}"

            // The refusal happens BEFORE anything is claimed, so every other verb is still available.
            match! merged.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "ready")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStderrLine handler surfaces on a stderr wait, not a spurious NotReady``() : Task =
        task {
            // The stderr twin of the stdout regression: a genuine pump failure must surface as itself
            // rather than as a readiness timeout that would read as "the marker never came".
            let config =
                (Command.create "test"
                 |> Command.onStderrLine (fun _ -> raise (InvalidOperationException "handler boom")))
                    .Config

            let running, _stdout, stderr = liveChild config
            use running = running

            let waiting =
                running.WaitForStderrLineAsync((fun line -> line.Contains "no-such-line"), TimeSpan.FromSeconds 10.0)

            do! writeUtf8 stderr "anything\n"

            let thrown =
                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> waiting :> Task))

            match thrown with
            | null -> Assert.Fail "expected the handler's own exception to surface on the wait"
            | thrown -> Assert.That(thrown.Message, Does.Contain "handler boom")
        }
        :> Task

    [<Test>]
    member _.``a throwing predicate fails only its own wait``() : Task =
        task {
            let running, _stdout, stderr = liveChild (Command.create "test").Config
            use running = running

            let waiting =
                running.WaitForStderrLineAsync(
                    (fun _ -> raise (InvalidOperationException "predicate boom")),
                    TimeSpan.FromSeconds 10.0
                )

            do! writeUtf8 stderr "first\n"

            let thrown =
                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> waiting :> Task))

            match thrown with
            | null -> Assert.Fail "expected the predicate's own exception to fail its wait"
            | thrown -> Assert.That(thrown.Message, Does.Contain "predicate boom")

            // The pump, the session and every other verb carried on: a later wait still works.
            do! writeUtf8 stderr "second\n"

            match! running.WaitForStderrLineAsync((fun line -> line = "second"), TimeSpan.FromSeconds 10.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "second")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``the partial tail retention is capped, and force-flushes at the cap``() : Task =
        task {
            // Bounded output retention: a child that floods stderr with no line terminator at all must
            // not grow the readiness retention without bound. The cap follows this run's own
            // `OutputBufferPolicy.MaxBytes`, and at the cap the tail is force-flushed after being
            // offered one last time — the same rule that force-flushes an unterminated line into a
            // capture.
            let config =
                (Command.create "test"
                 |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes 16))
                    .Config

            let running, _stdout, stderr = liveChild config
            use running = running
            let seen = ResizeArray<string>()

            let waiting =
                running.WaitForStderrTailAsync(
                    (fun tail ->
                        lock seen (fun () -> seen.Add tail)
                        tail.Contains "MARKER"),
                    TimeSpan.FromSeconds 10.0
                )

            // Three 16-byte chunks with no terminator anywhere, then the marker. Every offered tail
            // must stay within a cap's worth of accumulation, so the flood cannot pile up — and the
            // marker still matches once it arrives.
            for _ in 1..3 do
                do! writeUtf8 stderr (String('x', 16))

            do! writeUtf8 stderr "MARKER"

            match! waiting with
            | Ok tail -> Assert.That(tail, Does.Contain "MARKER")
            | Error error -> Assert.Fail $"{error}"

            let offered = lock seen (fun () -> List.ofSeq seen)
            Assert.That(offered, Is.Not.Empty)

            for tail in offered do
                Assert.That(
                    Encoding.UTF8.GetByteCount tail,
                    Is.LessThanOrEqualTo 32,
                    "an offered tail must stay within one cap's worth of accumulation"
                )
        }
        :> Task
