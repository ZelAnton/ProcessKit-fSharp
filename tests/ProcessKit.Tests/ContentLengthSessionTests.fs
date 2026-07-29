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
