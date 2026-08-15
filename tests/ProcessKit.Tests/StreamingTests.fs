namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// A small F# record deserialized through STJ's constructor-based deserialization — the
/// `StdoutJsonLinesAsync<'T>` analogue of `JsonVerbTests.fs`'s `Widget` (not reused directly: that
/// file compiles AFTER this one in `ProcessKit.Tests.fsproj`, so its type isn't visible here yet).
/// Must be public: STJ's constructor-based deserialization needs an accessible constructor.
type JsonLine = { Id: int; Label: string }

type private FixedTimeProvider(timestampUtc: DateTimeOffset) =
    inherit TimeProvider()

    override _.GetUtcNow() = timestampUtc

type private AdjustableTimeProvider(timestampUtc: DateTimeOffset) =
    inherit TimeProvider()

    let mutable current = timestampUtc

    override _.GetUtcNow() = current

    member _.SetUtcNow(value: DateTimeOffset) = current <- value

/// Raw `getrlimit`/`setrlimit` access to `RLIMIT_NOFILE`, used ONLY by the T-028 regression test
/// below (``spawnPosix fails instead of silently inheriting when open(/dev/null) fails``) to pin
/// the test process's file-descriptor ceiling at an exact, deterministic point so a specific
/// `open("/dev/null")` call inside `Native.Posix.spawnPosix` can be made to fail with EMFILE
/// without guessing at an ambient fd count.
module private DevNullExhaustion =

    [<StructLayout(LayoutKind.Sequential)>]
    type RLimit =
        struct
            val mutable Current: int64
            val mutable Max: int64
        end

    [<DllImport("libc", SetLastError = true)>]
    extern int getrlimit(int resource, RLimit& limit)

    [<DllImport("libc", SetLastError = true)>]
    extern int setrlimit(int resource, RLimit& limit)

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int openDevNull(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int close(int fd)

    // Linux value for RLIMIT_NOFILE. The regression test that uses this is Linux-only (see its
    // comment for why macOS is skipped), so no macOS constant is needed here.
    [<Literal>]
    let RLIMIT_NOFILE = 7

    // O_RDONLY = 0 (standard POSIX value)
    [<Literal>]
    let O_RDONLY = 0

    // O_WRONLY = 1 (standard POSIX value), matching the stdout Null open under test below.
    [<Literal>]
    let O_WRONLY = 1

    // Linux errno for per-process file-descriptor exhaustion. This test is Linux-only.
    [<Literal>]
    let EMFILE = 24

/// A stdout/stderr double whose read yields `chunks` (if any), in order, then throws `fault` on the
/// next read — the `RunningProcess`-level analogue of `PumpTests.fs`'s `ErroringReadStream` (T-087),
/// used to prove a genuine mid-stream OS read fault surfaces as `ProcessError.Io` from the
/// completion verbs (`OutputStringAsync`/`WaitAsync`/`ProfileAsync`/`FinishAsync`) instead of a
/// silently truncated capture.
type private ErroringStream(chunks: byte[] list, fault: exn) =
    inherit Stream()
    let mutable remaining = chunks

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        match remaining with
        | chunk :: rest ->
            remaining <- rest
            chunk.AsSpan().CopyTo(buffer.Span)
            ValueTask<int>(chunk.Length)
        | [] -> raise fault

/// A byte stream double that returns caller-selected read boundaries, then cleanly reaches EOF. The
/// chunk-streaming tests use it to distinguish byte preservation from accidental line/decoder framing.
type private ChunkedByteStream(chunks: byte[] list) =
    inherit Stream()
    let mutable remaining = chunks
    let mutable readCount = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    member _.ReadCount = Volatile.Read(&readCount)

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        Interlocked.Increment(&readCount) |> ignore

        match remaining with
        | chunk :: rest ->
            remaining <- rest
            chunk.AsSpan().CopyTo(buffer.Span)
            ValueTask<int>(chunk.Length)
        | [] -> ValueTask<int>(0)

/// A one-chunk stream whose first read waits for `gate`. The sibling-pump regression uses it to let
/// stderr fault and close the shared event channel before stdout attempts its own channel write.
type private GatedByteStream(payload: byte[], gate: Task) =
    inherit Stream()
    let mutable served = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        ValueTask<int>(
            task {
                do! gate

                if Interlocked.Exchange(&served, 1) = 0 then
                    payload.AsSpan().CopyTo(buffer.Span)
                    return payload.Length
                else
                    return 0
            }
        )

/// A parent-side output pipe as it looks once the child that owned it has exited but a DESCENDANT
/// that inherited the write end is still alive (T-360): `payload` arrives normally, and then the read
/// after it simply never returns — there is no EOF, because the pipe still has a writer. This is the
/// shape that used to hang every verb indefinitely, with the leader's outcome already known.
///
/// `respectsCancellation` picks which of the two real transports it models, and the bounded post-exit
/// output drain must handle both:
///   * `true` — what a piped run actually uses (a Windows overlapped named pipe, a POSIX socketpair):
///     the pending read is cancellable, so severing unwinds it and the pump ends at a clean EOF.
///   * `false` — the read the OS will not let go of (a POSIX pty master's blocking `read`, offloaded
///     to the thread pool). Severing cannot wake it, so the drain must ABANDON the pump — observed,
///     never awaited again — and the verb must still return what was captured.
///
/// `ReachedTail` completes once the read that will never answer has been issued, so a test can
/// synchronize on the actual state under test rather than on a sleep.
type internal HeldOpenOutputStream(payload: byte[], respectsCancellation: bool) =
    inherit Stream()

    // Never completed: this models a pipe whose remaining writer never writes and never closes.
    let held =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let reachedTail =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable offset = 0

    /// Completes when the pump has issued the read that can never be answered.
    member _.ReachedTail: Task = reachedTail.Task

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        if offset < payload.Length then
            let count = min buffer.Length (payload.Length - offset)
            payload.AsSpan(offset, count).CopyTo(buffer.Span)
            offset <- offset + count
            ValueTask<int> count
        else
            reachedTail.TrySetResult() |> ignore

            if respectsCancellation then
                ValueTask<int>(held.Task.WaitAsync cancellationToken)
            else
                ValueTask<int> held.Task

/// The THIRD ending a severed pipe can have, and the one no cancellable double can model: the read is
/// aborted by the sever, but the transport reports that abort as an **I/O failure** rather than as an
/// `OperationCanceledException` — what a stream layered over a raw fd does with
/// `ERROR_OPERATION_ABORTED`/`ECANCELED`. `payload` arrives normally first; the read after it parks
/// until the sever token fires and then fails with `fault`.
///
/// It matters because that failure would otherwise leave the pump reporting `ProcessError.Io` and fail
/// a verb whose contract, on exactly this shape, is a truncated capture (T-360 review R-03).
type internal AbortOnSeverStream(payload: byte[], fault: exn) =
    inherit Stream()

    let mutable offset = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        if offset < payload.Length then
            let count = min buffer.Length (payload.Length - offset)
            payload.AsSpan(offset, count).CopyTo(buffer.Span)
            offset <- offset + count
            ValueTask<int> count
        else
            ValueTask<int>(
                task {
                    let aborted =
                        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                    use _registration =
                        cancellationToken.Register(fun () -> aborted.TrySetResult() |> ignore)

                    do! aborted.Task
                    return raise fault
                }
            )

/// A stdout double that GENERATES `lineCount` copies of `line` (each newline-terminated) as it is
/// read, rather than holding the payload. The T-357 bounded-memory regression needs tens of megabytes
/// of stdout to flow past the pumps; materializing that in the test process would defeat the very
/// measurement it makes. Reads fill the caller's buffer completely, so it also exercises the pump's
/// multi-line-per-read path.
type private GeneratedLinesStream(line: string, lineCount: int) =
    inherit Stream()
    let lineBytes = Encoding.UTF8.GetBytes(line + "\n")
    let mutable remaining = lineCount
    let mutable offset = 0

    /// How many bytes a full read to EOF yields — the volume the retention budget is measured against.
    member _.TotalBytes = int64 lineBytes.Length * int64 lineCount

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = ()
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        let mutable written = 0

        while remaining > 0 && written < buffer.Length do
            let take = min (lineBytes.Length - offset) (buffer.Length - written)
            lineBytes.AsSpan(offset, take).CopyTo(buffer.Span.Slice written)
            written <- written + take
            offset <- offset + take

            if offset = lineBytes.Length then
                offset <- 0
                remaining <- remaining - 1

        ValueTask<int> written

// --- T-329 delayed-stdin-source test doubles, shared across StreamingTests / VerbTests / PipelineTests.
// Defined here (non-private) because StreamingTests is the earliest of the three in the .fsproj compile
// order, so the later two can reuse these instead of redefining them.

/// How a `DelayedStdinAsyncLines` source concludes once it is released.
type DelayedStdinEnding =

    /// It raises — the genuine source failure a bounded final observation has to surface.
    | FailWhenReleased

    /// It ends cleanly — nothing to surface, and no false failure to invent either.
    | EndWhenReleased

/// A `FromAsyncLines` stdin source that parks in its FIRST `MoveNextAsync` — before writing anything —
/// until `Release()` is called, and only then fails or ends (per `ending`).
///
/// Released at the exact instant a verb opens its bounded final observation window (see
/// `StdinFinalObservationScope`), it reproduces T-329's race with no timing guesswork: the source
/// concludes strictly AFTER the child exited and the output pumps drained, which is precisely when a
/// single non-blocking peek at the feeder sees no fault at all. Never released, it is instead the hung
/// source that same window must STOP rather than wait for: it parks on the feeder's own lifecycle token,
/// so `StdinFeeder.Stop` unwinds it (a cancelled feed is not a failure) and `Cancelled`/`Disposed` report
/// that it did.
type DelayedStdinAsyncLines(ending: DelayedStdinEnding) =
    let parked =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let cancelled =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let disposed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once the feed has entered `MoveNextAsync` and parked, before writing anything.
    member _.Parked: Task = parked.Task

    /// Completes once the feeder's lifecycle token cancelled this source — i.e. `StdinFeeder.Stop` ran.
    member _.Cancelled: Task = cancelled.Task

    /// Completes once the user's enumerator was disposed (never leaked past the run).
    member _.Disposed: Task = disposed.Task

    /// Let the parked source conclude, failing or ending per its `ending`.
    member _.Release() = release.TrySetResult() |> ignore

    interface IAsyncEnumerable<string> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) : IAsyncEnumerator<string> =
            let registration =
                cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)

            { new IAsyncEnumerator<string> with
                member _.Current = "delayed"

                member _.MoveNextAsync() : ValueTask<bool> =
                    parked.TrySetResult() |> ignore

                    ValueTask<bool>(
                        task {
                            // Park until the test releases the source, or until the feeder's lifecycle
                            // token cancels this feed (`StdinFeeder.Stop`) — a cancelled feed is not the
                            // caller's error, so that path must stay a non-failure.
                            do! release.Task.WaitAsync cancellationToken

                            match ending with
                            | FailWhenReleased -> return raise (InvalidOperationException "delayed-source-boom")
                            | EndWhenReleased -> return false
                        }
                    )
              interface IAsyncDisposable with
                  member _.DisposeAsync() : ValueTask =
                      registration.Dispose()
                      disposed.TrySetResult() |> ignore
                      ValueTask() }

/// Scoped control of the T-329 bounded-final-observation seams: `onWindow` runs at the exact instant a
/// verb opens a bounded window on a still-running stdin feed (so a gated source can be released precisely
/// then), `Windows` counts those openings — zero proves a louder failure won and no wait was paid at all —
/// and `budget` shortens the window so the "a hung source never holds the verb" bound can be proven in
/// milliseconds. Disposal restores both seams, so no later test inherits them; this suite runs
/// sequentially (no `[<Parallelizable>]`), so one scope at a time owns them.
type StdinFinalObservationScope(onWindow: unit -> unit, budget: TimeSpan option) =
    let mutable windows = 0

    do
        Pump.stdinFinalObservationBudgetForTests <- budget

        Pump.stdinFinalObservationTestHook <-
            Some(fun () ->
                Interlocked.Increment(&windows) |> ignore
                onWindow ())

    /// How many bounded observation windows were opened while this scope was installed.
    member _.Windows = Volatile.Read(&windows)

    interface IDisposable with
        member _.Dispose() =
            Pump.stdinFinalObservationTestHook <- None
            Pump.stdinFinalObservationBudgetForTests <- None

/// The `PostExitDrain` budget seam, wrapped so every regression that exercises the bounded post-exit
/// output drain runs it in milliseconds instead of paying the real five-second ceiling (twice — the
/// window before the sever and the window after it). Restored in a `finally`, exactly as the
/// post-kill reap budget seam is used; tests in this repository run sequentially, so the single
/// process-wide seam is safe.
module internal PostExitDrainBudget =

    let withBudget (budget: TimeSpan) (body: unit -> Task<'T>) : Task<'T> =
        task {
            let previous = PostExitDrain.budgetOverrideForTests
            PostExitDrain.budgetOverrideForTests <- Some budget

            try
                return! body ()
            finally
                PostExitDrain.budgetOverrideForTests <- previous
        }

    /// A short budget that still comfortably exceeds the scheduling jitter of a loaded CI runner, so
    /// "the ordinary tail was not cut" stays a statement about the bound, not about timing luck.
    let Short = TimeSpan.FromMilliseconds 250.0

[<TestFixture>]
type StreamingTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let processStdinWriteLineBytes target text =
        task {
            use sink = new MemoryStream()
            let stdin = ProcessStdin(sink, Encoding.UTF8, target)
            do! stdin.WriteLineAsync text
            return sink.ToArray()
        }

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let threeLines =
        if isWindows then
            shell "echo line1&echo line2&echo line3"
        else
            shell "echo line1; echo line2; echo line3"

    // Same three lines, written to stderr instead of stdout — used to prove the stderr capture path
    // shares the stdout path's no-cap `OverflowMode.Error` semantics (T-067, R-2).
    let threeLinesToStderr =
        if isWindows then
            shell "echo line1 1>&2&echo line2 1>&2&echo line3 1>&2"
        else
            shell "echo line1 1>&2; echo line2 1>&2; echo line3 1>&2"

    // A process that emits one line immediately, then stays alive and silent for several seconds — the
    // "hung after a burst" shape a `Command.IdleTimeout` is meant to catch.
    let quietAfterBurst =
        if isWindows then
            shell "echo hi& ping 127.0.0.1 -n 10 >NUL"
        else
            shell "echo hi; sleep 8"

    // A process that keeps dripping output on a sub-idle cadence for longer than the 2s idle window used
    // in the tests below, then exits cleanly — proves the idle deadline is actually reset by each chunk
    // of output. Windows has no sub-second sleep, so its cadence is ~1s `ping` gaps (still well under 2s).
    let idleDrip =
        if isWindows then
            shell
                "echo tick& ping 127.0.0.1 -n 2 >NUL& echo tick& ping 127.0.0.1 -n 2 >NUL& echo tick& ping 127.0.0.1 -n 2 >NUL& echo tick& ping 127.0.0.1 -n 2 >NUL& echo tick"
        else
            shell "for i in 1 2 3 4 5 6 7 8 9 10; do echo tick; sleep 0.3; done"

    // Start `command` and collect its stdout as raw bytes through the byte verb (the verb reaps the
    // tree). Used by the OutputBuffer byte-cap tests below.
    let runBytes (command: Command) : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        task {
            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> return Error error
            | Ok running -> return! running.OutputBytesAsync()
        }

    let collect (lines: IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let enumerator = lines.GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then acc.Add enumerator.Current else more <- false

            do! enumerator.DisposeAsync()
            return acc
        }

    // Race a stream drain against a deadline: a regression that strands the channel reader fails the
    // test in `deadlineMs` instead of hanging the whole run. Returns the completed (here, faulted)
    // drain task so the caller can inspect how it ended.
    let drainWithDeadline (lines: IAsyncEnumerable<'T>) (deadlineMs: int) =
        task {
            let drain = collect lines :> Task
            let! winner = Task.WhenAny(drain, Task.Delay deadlineMs)

            Assert.That(
                obj.ReferenceEquals(winner, drain),
                Is.True,
                "the stream hung instead of surfacing the handler fault"
            )

            return drain
        }

    // Await a drain known to have faulted, returning the surfaced message (the task CE rethrows the
    // original exception, unwrapped from the AggregateException).
    let faultMessage (drain: Task) =
        task {
            try
                do! drain
                return None
            with :? InvalidOperationException as ex ->
                return Some ex.Message
        }

    // A synthetic `RunningProcess` over an in-memory stdout payload — no real subprocess, no OS pipe.
    // The `StreamBuffer` tests below need to control *exactly* when the consumer starts reading
    // relative to the producer, so they can assert on the bounded channel deterministically; racing a
    // real child process's OS pipe buffering would make the same assertions flaky across the CI matrix.
    // `Wait` resolves immediately — nothing here exercises the process's own exit path.
    let syntheticStdoutProcess (config: CommandConfig) (payload: string) : RunningProcess =
        let stdout = new MemoryStream(Encoding.UTF8.GetBytes payload) :> Stream

        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = Some stdout
              Stderr = None
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> Task.FromResult(Outcome.Exited 0)
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host)

    // Like `syntheticStdoutProcess`, but over caller-supplied stdout/stderr streams instead of a
    // fixed in-memory payload — used by the T-087 read-fault tests below to inject an `ErroringStream`
    // that throws a genuine OS-level read error partway through.
    let syntheticProcessOverStreams
        (config: CommandConfig)
        (stdout: Stream option)
        (stderr: Stream option)
        : RunningProcess =
        let host: RunningHost =
            { Config = config
              Pid = None
              Stdout = stdout
              Stderr = stderr
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              StartTimeIdentity = None
              Wait = fun () -> Task.FromResult(Outcome.Exited 0)
              StdinError = RunningHost.NoStdinError
              StdinFeedComplete = ignore
              StartKill = ignore
              Signal = fun _ -> Ok()
              GracefulKill = fun _ -> Task.CompletedTask
              ResizePty = None
              TreeStats = None
              Teardown = fun () -> ValueTask() }

        new RunningProcess(host)

    // `total` newline-terminated lines "line-1" .. "line-<total>".
    let linesPayload (total: int) =
        String.Join("\n", [ 1..total ] |> List.map (sprintf "line-%d")) + "\n"

    // Unwrap the `ProcessException` a faulted streaming enumerator surfaces. `IAsyncEnumerable`
    // consumption (`ReadAllAsync`, what `StdoutLinesAsync`/`OutputEventsAsync` return) surfaces the
    // original exception directly; the single-item `Reader.ReadAsync` (what `WaitForLineAsync` /
    // `Runner.firstLine` use instead) wraps it in a `ChannelClosedException`. Handle both so this
    // helper doesn't depend on which of the two a given verb happens to use internally.
    let processError (drain: Task) =
        task {
            try
                do! drain
                return None
            with
            | :? ProcessException as pe -> return Some pe.Error
            | :? ChannelClosedException as ex ->
                match ex.InnerException with
                | :? ProcessException as pe -> return Some pe.Error
                | _ -> return None
        }

    [<Test>]
    member _.``start then OutputString captures stdout``() : Task =
        task {
            match! runner.StartAsync(threeLines, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "line1")
                    Assert.That(result.Stdout, Does.Contain "line3")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // T-329: `FinishAsync` is the third Result-producing verb classifying through the same feeder
    // observation as the buffered verbs, so the fix must hold for a streaming consumer too.
    [<Test>]
    member _.``Finish surfaces a stdin source that fails only after the child exits``() : Task =
        task {
            // The child exits 0 well before this source concludes, so the old single non-blocking peek at
            // the feeder saw no fault and `FinishAsync` reported a silent success — the stdin failure the
            // caller needed was then thrown away by teardown. The bounded final observation must wait for
            // the source's genuine failure and report it as `ProcessError.Stdin`.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use _observation =
                new StdinFinalObservationScope((fun () -> source.Release()), None)

            let command = shell "exit 0" |> Command.stdin (Stdin.FromAsyncLines source)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! _ = collect (running.StdoutLinesAsync())

                match! running.FinishAsync() with
                | Error(ProcessError.Stdin _) -> ()
                | Error other -> Assert.Fail $"expected ProcessError.Stdin from FinishAsync, got {other}"
                | Ok _ -> Assert.Fail "a source failing after the child exited must not finish as a success"
        }
        :> Task

    [<Test>]
    member _.``StdoutLines streams each line, then Finish reaps``() : Task =
        task {
            match! runner.StartAsync(threeLines, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! lines = collect (running.StdoutLinesAsync())
                let! finished = running.FinishAsync()
                Assert.That(lines, Does.Contain "line1")
                Assert.That(lines.Count, Is.GreaterThanOrEqualTo 3)

                match finished with
                | Ok finished -> Assert.That(finished.Truncated, Is.False)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks preserves binary bytes and read boundaries, then Finish reaps``() : Task =
        task {
            let chunks =
                [ [| 0uy; 0xFFuy; 0uy |]; [| 0xC3uy; 0x28uy |]; [| 0uy; 1uy; 2uy; 255uy |] ]

            use stdout = new ChunkedByteStream(chunks)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            let! actualChunks = collect (running.StdoutChunksAsync())
            CollectionAssert.AreEqual([| 3; 2; 4 |], actualChunks |> Seq.map (fun chunk -> chunk.Length))

            let actual =
                actualChunks |> Seq.collect (fun chunk -> chunk.ToArray()) |> Seq.toArray

            let expected = chunks |> Seq.collect id |> Seq.toArray
            CollectionAssert.AreEqual(expected, actual)

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks is exclusive with buffered and line consumers and is one-shot``() : Task =
        task {
            use running = syntheticStdoutProcess (Command.create "test").Config "binary"
            running.StdoutChunksAsync() |> ignore

            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutChunksAsync() |> ignore))
            |> ignore

            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore))
            |> ignore

            match! running.OutputBytesAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected an explicit consuming-verb refusal, got {other}"
        }
        :> Task

    // T-303: a second `OutputEventsAsync()` on the same handle must not silently hand out a second
    // enumerator over `eventChannel` (`StreamChannel.create` creates it with `SingleReader = true`) — it
    // must throw the same already-consumed `InvalidOperationException` as a repeat `StdoutLinesAsync()`/
    // `StdoutChunksAsync()` call, by the same one-shot guard-flag pattern.
    [<Test>]
    member _.``OutputEventsAsync is one-shot``() : Task =
        task {
            use running =
                syntheticStdoutProcess (Command.create "test").Config "line-1\nline-2\n"

            running.OutputEventsAsync() |> ignore

            Assert.Throws<InvalidOperationException>(Action(fun () -> running.OutputEventsAsync() |> ignore))
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks Backpressure bounds unread chunks without losing bytes``() : Task =
        task {
            let capacity = 3
            let chunks = [ for i in 0..29 -> [| byte i |] ]

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded capacity))
                    .Config

            use stdout = new ChunkedByteStream(chunks)
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None
            let enumerable = running.StdoutChunksAsync()

            do! Task.Delay 200
            Assert.That(stdout.ReadCount, Is.LessThanOrEqualTo(capacity + 2))

            let! actualChunks = collect enumerable

            let actual =
                actualChunks |> Seq.collect (fun chunk -> chunk.ToArray()) |> Seq.toArray

            CollectionAssert.AreEqual(chunks |> Seq.collect id |> Seq.toArray, actual)

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks flushes a buffered tee at clean EOF``() : Task =
        task {
            let payload = [| 0uy; 0xFFuy; 0uy; 1uy |]
            use underlying = new MemoryStream()
            use tee = new BufferedStream(underlying, 65536)
            use stdout = new ChunkedByteStream([ payload ])

            let config = (Command.create "test" |> Command.stdoutTee tee).Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            let! chunks = collect (running.StdoutChunksAsync())
            let actual = chunks |> Seq.collect (fun chunk -> chunk.ToArray()) |> Seq.toArray
            CollectionAssert.AreEqual(payload, actual)
            CollectionAssert.AreEqual(payload, underlying.ToArray())
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks flushes a buffered tee before surfacing a genuine read fault``() : Task =
        task {
            let payload = [| 0uy; 0xFFuy; 0uy; 1uy |]
            use underlying = new MemoryStream()
            use tee = new BufferedStream(underlying, 65536)
            use stdout = new ErroringStream([ payload ], IOException "disk read error")

            let config = (Command.create "test" |> Command.stdoutTee tee).Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            let! drain = drainWithDeadline (running.StdoutChunksAsync()) 5000
            let! error = processError drain

            match error with
            | Some(ProcessError.Io _) -> ()
            | other -> Assert.Fail $"expected ProcessError.Io, got {other}"

            CollectionAssert.AreEqual(payload, underlying.ToArray())
        }
        :> Task

    [<Test>]
    member _.``StopAsync completes after an unread bounded chunk stream is abandoned``() : Task =
        task {
            let capacity = 1
            let chunks = [ for i in 0..31 -> [| byte i |] ]

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded capacity))
                    .Config

            use stdout = new ChunkedByteStream(chunks)
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None
            running.StdoutChunksAsync() |> ignore

            do! Task.Delay 100
            Assert.That(stdout.ReadCount, Is.GreaterThanOrEqualTo(capacity + 1))

            let stop = running.StopAsync(TimeSpan.Zero)
            let! winner = Task.WhenAny(stop :> Task, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(winner, stop), Is.True, "StopAsync hung on abandoned backpressure")
            let! _ = stop
            return ()
        }
        :> Task

    [<Test>]
    member _.``FinishAsync releases an unread bounded line stream and preserves the exit outcome``() : Task =
        task {
            let capacity = 1

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded capacity))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload 32)
            running.StdoutLinesAsync() |> ignore

            do! Task.Delay 100
            Assert.That(running.StdoutLineCount, Is.GreaterThanOrEqualTo(capacity + 1))

            let finish = running.FinishAsync()
            let! winner = Task.WhenAny(finish :> Task, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(winner, finish), Is.True, "FinishAsync hung on abandoned backpressure")

            match! finish with
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
            | Error error -> Assert.Fail $"expected FinishAsync to preserve the process outcome, got {error}"
        }
        :> Task

    // --- T-357: a terminal-only `FinishAsync` must not queue stdout into a channel nobody can read ---

    [<Test>]
    member _.``a fresh FinishAsync drains stdout into a retain-nothing sink``() : Task =
        task {
            let total = 4096

            use running =
                syntheticStdoutProcess (Command.create "test").Config (linesPayload total)

            // No streaming verb ever ran, so `FinishAsync` starts the stdout session itself — and
            // `Finished` carries the outcome and stderr, never stdout, so nothing it pumps can ever be
            // read back. Every line must therefore be dropped as it is framed, not queued (T-357).
            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished ->
                Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(finished.Truncated, Is.False, "a discarded stdout stream drops nothing to report")

            // The lines were still framed and counted — handlers, tee and counters behave exactly as on
            // the streamed path; only the sink changed. Nothing reached a backlog, so nothing could be
            // dropped from one either: this pair is how the retain-nothing sink is observed now that the
            // channel itself can no longer be read back (below). The ~48 MB sibling test measures the
            // heap directly.
            Assert.That(running.StdoutLineCount, Is.EqualTo total)
            Assert.That(running.DroppedStreamLineCount, Is.EqualTo 0)

            // And that discarded stdout is refused rather than served empty. An enumerator that simply
            // completes would be indistinguishable from a child that printed nothing, so the claim gate
            // closes with the sink: asking for a stream this handle can no longer produce is the same
            // already-consumed error a stdout verb gets after `WaitAsync`/`ProfileAsync`.
            match Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore)) with
            | null -> Assert.Fail "expected StdoutLinesAsync to refuse a discarded stdout stream"
            | refused -> Assert.That(refused.Message, Does.Contain "already been consumed")

            // The NDJSON overloads fold into `StdoutLinesAsync`, so they are refused with it.
            Assert.Throws<InvalidOperationException>(
                Action(fun () -> running.StdoutJsonLinesAsync<JsonLine>() |> ignore)
            )
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``WaitForLineAsync after a terminal fresh FinishAsync refuses instead of reporting NotReady``() : Task =
        task {
            use running =
                syntheticStdoutProcess (Command.create "test").Config (linesPayload 64)

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))

            // `NotReady` would be a false diagnosis here: it means "no matching line arrived within the
            // timeout", but this stdout was deliberately discarded and no line can ever arrive on it. The
            // honest answer is the already-consumed refusal — and it must come from the closed claim gate,
            // not from the deadline, so an ample timeout still returns at once.
            let clock = Stopwatch.StartNew()

            match! running.WaitForLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "already been consumed")
            | Error other -> Assert.Fail $"expected an already-consumed refusal, got {other}"
            | Ok line -> Assert.Fail $"a discarded stdout stream cannot deliver a line, got {line}"

            Assert.That(
                clock.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 30.0),
                "the refusal must come from the claim gate, not from waiting out the timeout"
            )
        }
        :> Task

    [<Test>]
    member _.``FinishAsync latches the discard over a WaitForLineAsync session nobody took over``() : Task =
        task {
            let total = 32

            use running =
                syntheticStdoutProcess (Command.create "test").Config (linesPayload total)

            // The OTHER shape that reaches the latch: `WaitForLineAsync` — not `FinishAsync` — started
            // the stdout session, and no `StdoutLinesAsync` caller ever took its enumerator, so the
            // terminal `FinishAsync` rejoins an already-claimed session and must latch there too.
            match! running.WaitForLineAsync((fun line -> line = "line-1"), TimeSpan.FromSeconds 30.0) with
            | Ok line -> Assert.That(line, Is.EqualTo "line-1")
            | Error error -> Assert.Fail $"{error}"

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished ->
                Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(finished.Truncated, Is.False)

            Assert.That(running.StdoutLineCount, Is.EqualTo total)
            Assert.That(running.DroppedStreamLineCount, Is.EqualTo 0)

            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore))
            |> ignore

            match! running.WaitForLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected an already-consumed refusal, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a large unread stdout stays bounded in memory through a fresh FinishAsync``() : Task =
        task {
            // ~48 MB of stdout, generated as it is read. Queued into the (unbounded by default)
            // streaming channel for a reader that cannot exist, it would pin roughly twice that in
            // decoded strings until the handle is disposed — the OOM shape T-357 is about. The budget
            // sits far above the pump's own fixed buffers and far below the retained volume.
            let lineCount = 12_000
            use stdout = new GeneratedLinesStream(String('x', 4000), lineCount)
            let budget = stdout.TotalBytes / 2L

            use running =
                syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

            let before = GC.GetTotalMemory true

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))

            let retainedBytes = GC.GetTotalMemory true - before
            // Read through the handle only AFTER the measurement, so whatever it retained is provably
            // still reachable at the moment the heap is measured — and prove the volume really flowed.
            Assert.That(running.StdoutLineCount, Is.EqualTo lineCount)

            Assert.That(
                retainedBytes,
                Is.LessThan budget,
                "a fresh FinishAsync retained the child's stdout instead of discarding it"
            )
        }
        :> Task

    [<Test>]
    member _.``FinishAsync keeps every line for a stdout stream that was handed out``() : Task =
        task {
            let total = 512

            use running =
                syntheticStdoutProcess (Command.create "test").Config (linesPayload total)

            // Taken but not yet enumerated: this stream has an owner, so the terminal hand-off must keep
            // queueing for it exactly as before. The retain-nothing sink is only for a stream nobody took.
            let lines = running.StdoutLinesAsync()

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))

            let! streamed = collect lines
            Assert.That(streamed.Count, Is.EqualTo total)
            Assert.That(streamed[0], Is.EqualTo "line-1")
            Assert.That(streamed[total - 1], Is.EqualTo $"line-{total}")
        }
        :> Task

    [<Test>]
    member _.``the spawn test double finishes a fresh stdout the same way``() : Task =
        task {
            // `FakeProcess.Build` is also exactly what a cassette Spawn replay reconstructs a handle with
            // (`Cassette.fs::spawnFromEntry`), so this pins the same retain-nothing contract on both test
            // doubles — the real runner is covered by the synthetic-host tests above.
            let total = 256

            use running =
                ProcessKit.Testing.FakeProcess
                    .Create("fake")
                    .WithStdoutLines([ for i in 1..total -> $"line-{i}" ])
                    .WithExit(0)
                    .Build()

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))

            Assert.That(running.StdoutLineCount, Is.EqualTo total)
            Assert.That(running.DroppedStreamLineCount, Is.EqualTo 0)

            // Retaining nothing and refusing afterwards are one contract, so the doubles must publish
            // both halves — a test double that answered with an empty stream here would let a consumer
            // write code the real runner rejects.
            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore))
            |> ignore

            match! running.WaitForLineAsync((fun _ -> true), TimeSpan.FromSeconds 30.0) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected an already-consumed refusal, got {other}"
        }
        :> Task

    // --- T-364: the same latch/gate pair, exercised directly in the module that now owns it ---------

    [<Test>]
    member _.``the claim gate refuses a line enumerator a terminal discard latched between its two acquisitions``() =
        // `StdoutLinesAsync` takes the claim lock TWICE — once to claim (or rejoin) the stdout session,
        // once to hand out its ONE enumerator — and a terminal `FinishAsync` can land in between, see no
        // enumerator handed out, and latch the retain-nothing sink. Driving `ConsumptionGate` directly is
        // what makes that interleaving deterministic; through the public verbs it is a race window, so
        // the sibling tests above can only cover the sequential shape of the same invariant (KB K-163).
        let gate = ConsumptionGate(None, [])
        let mutable sessionsStarted = 0
        let startSession () = sessionsStarted <- sessionsStarted + 1

        // 1. The streaming caller passes the gate — `StdoutLinesAsync`'s FIRST acquisition.
        let claimed = gate.TryClaimStdoutStreaming(false, startSession)
        Assert.That(claimed, Is.True)
        Assert.That(sessionsStarted, Is.EqualTo 1, "the session is built once, under the claiming lock")
        Assert.That(gate.DiscardingStdoutStream, Is.False)

        // 2. A terminal `FinishAsync` rejoins that same session with no enumerator handed out yet, so it
        //    latches the discard — and closes the paired claim gate with it, under that one lock.
        let terminalClaim = gate.TryClaimStdoutStreaming(true, startSession)
        Assert.That(terminalClaim, Is.True)
        Assert.That(sessionsStarted, Is.EqualTo 1, "rejoining an existing session must not build a second")
        Assert.That(gate.DiscardingStdoutStream, Is.True)

        // 3. The streaming caller now reaches its SECOND acquisition. Nothing fills that channel any
        //    more, so the enumerator must be refused rather than handed out to run dry.
        Assert.That(
            gate.TryTakeStdoutLinesEnumerator(),
            Is.False,
            "a latched discard must close the enumerator claim in the second acquisition too"
        )

        // Every later non-terminal stdout-line caller (`StdoutLinesAsync`, `WaitForLineAsync`) is refused
        // at the gate itself, while the terminal hand-off stays the idempotent one it is for a streamed
        // session.
        let lateStreamer = gate.TryClaimStdoutStreaming(false, startSession)
        Assert.That(lateStreamer, Is.False)
        let repeatTerminal = gate.TryClaimStdoutStreaming(true, startSession)
        Assert.That(repeatTerminal, Is.True)
        Assert.That(sessionsStarted, Is.EqualTo 1)

    [<Test>]
    member _.``the claim gate keeps queueing for a line enumerator that was handed out before the terminal verb``() =
        // The contrapositive of the test above, and the reason the latch is conditional: a stream that
        // WAS handed out has an owner, so the terminal hand-off must keep every line queued for it.
        let gate = ConsumptionGate(None, [])
        let startSession () = ()

        let claimed = gate.TryClaimStdoutStreaming(false, startSession)
        Assert.That(claimed, Is.True)
        Assert.That(gate.TryTakeStdoutLinesEnumerator(), Is.True)

        let terminalClaim = gate.TryClaimStdoutStreaming(true, startSession)
        Assert.That(terminalClaim, Is.True)

        Assert.That(
            gate.DiscardingStdoutStream,
            Is.False,
            "a stream with an owner keeps queueing through the terminal hand-off"
        )

        Assert.That(gate.TryTakeStdoutLinesEnumerator(), Is.False, "the one enumerator is handed out once")

    [<Test>]
    member _.``ExitTask releases an unread bounded event stream and preserves the exit outcome``() : Task =
        task {
            let capacity = 1

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded capacity))
                    .Config

            use stdout = new MemoryStream(Encoding.UTF8.GetBytes(linesPayload 16))
            use stderr = new MemoryStream(Encoding.UTF8.GetBytes(linesPayload 16))

            use running =
                syntheticProcessOverStreams config (Some(stdout :> Stream)) (Some(stderr :> Stream))

            running.OutputEventsAsync() |> ignore

            do! Task.Delay 100
            Assert.That(running.StdoutLineCount + running.StderrLineCount, Is.GreaterThan capacity)

            let exit = running.ExitTask
            let! winner = Task.WhenAny(exit :> Task, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(winner, exit), Is.True, "ExitTask hung on abandoned backpressure")
            let! outcome = exit
            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member _.``StdoutChunks surfaces a genuine read fault as ProcessError.Io``() : Task =
        task {
            let fault = IOException "disk read error"
            use stdout = new ErroringStream([ [| 0uy; 255uy; 0uy |] ], fault)

            use running =
                syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

            let! drain = drainWithDeadline (running.StdoutChunksAsync()) 5000
            let! error = processError drain

            match error with
            | Some(ProcessError.Io _) -> ()
            | other -> Assert.Fail $"expected ProcessError.Io, got {other}"

            try
                let! _ = running.FinishAsync()
                Assert.Fail "expected FinishAsync to surface the same genuine read fault"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io from FinishAsync, got {other}"
        }
        :> Task

    // T-297: a raw stdout byte chunk (`StdoutChunksAsync`'s items) has no line structure — one item is
    // whatever the OS handed back on a single read — so the fail-loud `OutputTooLarge` this channel
    // raises on overflow must not claim a `LineLimit`/`TotalLines` it never had, and must carry an
    // honest `TotalBytes` (the cumulative chunk bytes actually pumped) instead of the hardcoded `0`.
    [<Test>]
    member _.``StreamBuffer Error on StdoutChunksAsync reports byte totals, not a false LineLimit``() : Task =
        task {
            let capacity = 2
            let chunks = [ for i in 0..19 -> [| byte i |] ]

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.Error)))
                    .Config

            use stdout = new ChunkedByteStream(chunks)
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            let! drain = drainWithDeadline (running.StdoutChunksAsync()) 5000
            let! error = processError drain

            match error with
            | Some((ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, totalBytes) as overflow)) ->
                Assert.That(lineLimit, Is.EqualTo None, "a raw stdout byte chunk is not a line")
                Assert.That(byteLimit, Is.EqualTo None, "the channel's capacity bounds queued chunks, not bytes")
                Assert.That(totalLines, Is.EqualTo 0)

                Assert.That(
                    totalBytes,
                    Is.GreaterThan 0,
                    "TotalBytes must report the real cumulative chunk bytes, not the hardcoded 0"
                )

                Assert.That(overflow.Message, Does.Contain("produced too much output"))
                Assert.That(overflow.Message, Does.Not.Contain("protocol"))
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    // --- RunningProcess.StdoutJsonLinesAsync (NDJSON / JSON Lines) ---

    [<Test>]
    member _.``StdoutJsonLinesAsync deserializes each NDJSON line and skips blank lines, then Finish reaps``() : Task =
        task {
            let payload =
                "{\"Id\":1,\"Label\":\"a\"}\n\n{\"Id\":2,\"Label\":\"b\"}\n{\"Id\":3,\"Label\":\"c\"}\n"

            use running = syntheticStdoutProcess (Command.create "test").Config payload

            let! lines = collect (running.StdoutJsonLinesAsync<JsonLine>())

            CollectionAssert.AreEqual(
                [ { Id = 1; Label = "a" }; { Id = 2; Label = "b" }; { Id = 3; Label = "c" } ],
                lines
            )

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync surfaces an invalid line as ProcessException carrying ProcessError.Parse``
        ()
        : Task =
        task {
            let payload = "{\"Id\":1,\"Label\":\"a\"}\nnot-json\n{\"Id\":2,\"Label\":\"b\"}\n"
            use running = syntheticStdoutProcess (Command.create "test").Config payload

            let! drain = drainWithDeadline (running.StdoutJsonLinesAsync<JsonLine>()) 5000
            let! error = processError drain

            match error with
            | Some(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected ProcessError.Parse, got {other}"
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync honours JsonSerializerOptions (case-insensitive property matching)``() : Task =
        task {
            let payload = "{\"id\":1,\"label\":\"a\"}\n"
            use running = syntheticStdoutProcess (Command.create "test").Config payload
            let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

            let! lines = collect (running.StdoutJsonLinesAsync<JsonLine>(options))
            CollectionAssert.AreEqual([ { Id = 1; Label = "a" } ], lines)

            match! running.FinishAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync(JsonTypeInfo) overload deserializes the same NDJSON stream``() : Task =
        task {
            let payload = "{\"Id\":1,\"Label\":\"a\"}\n{\"Id\":2,\"Label\":\"b\"}\n"
            use running = syntheticStdoutProcess (Command.create "test").Config payload

            let typeInfo =
                JsonSerializerOptions.Default.GetTypeInfo(typeof<JsonLine>) :?> JsonTypeInfo<JsonLine>

            let! lines = collect (running.StdoutJsonLinesAsync<JsonLine> typeInfo)
            CollectionAssert.AreEqual([ { Id = 1; Label = "a" }; { Id = 2; Label = "b" } ], lines)

            match! running.FinishAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync shares the exclusive-consumption gate with the other consuming verbs``() : Task =
        task {
            let payload = "{\"Id\":1,\"Label\":\"a\"}\n"

            // Claiming the stdout-streaming session through StdoutJsonLinesAsync (like StdoutLinesAsync,
            // it is a companion verb of that ONE session, not a competing claim) refuses a later,
            // different-kind buffered verb with the same clean `Unsupported` error `OutputStringAsync`
            // already reports for a second buffered verb (CorrectnessBugTests.fs).
            use runningJsonFirst = syntheticStdoutProcess (Command.create "test").Config payload
            runningJsonFirst.StdoutJsonLinesAsync<JsonLine>() |> ignore

            match! runningJsonFirst.OutputStringAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected the buffered verb to be refused, got {other}"

            // A buffered verb claimed first refuses a later StdoutJsonLinesAsync — the same
            // already-consumed contract `StdoutLinesAsync()` itself has (a thrown
            // `InvalidOperationException`, not a `Result`).
            use runningBufferedFirst =
                syntheticStdoutProcess (Command.create "test").Config payload

            let! _ = runningBufferedFirst.OutputStringAsync()

            Assert.Throws<InvalidOperationException>(
                Action(fun () -> runningBufferedFirst.StdoutJsonLinesAsync<JsonLine>() |> ignore)
            )
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync called twice on the same handle throws InvalidOperationException``() : Task =
        task {
            let payload = "{\"Id\":1,\"Label\":\"a\"}\n{\"Id\":2,\"Label\":\"b\"}\n"
            use running = syntheticStdoutProcess (Command.create "test").Config payload

            // First call succeeds and claims the stdout-streaming session
            let _enum1 = running.StdoutJsonLinesAsync<JsonLine>()

            // Second call on the same handle should raise InvalidOperationException when trying to claim the session
            // (same as the already-consumed error StdoutLinesAsync() itself documents)
            Assert.Throws<InvalidOperationException>(
                Action(fun () ->
                    let _enum2 = running.StdoutJsonLinesAsync<JsonLine>()
                    ())
            )
            |> ignore

            // The first enumerable should still be valid (the session is owned by it)
            match! running.FinishAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutJsonLinesAsync then StdoutLinesAsync on the same handle throws InvalidOperationException``
        ()
        : Task =
        task {
            let payload =
                "{\"Id\":1,\"Label\":\"a\"}\nline1\n{\"Id\":2,\"Label\":\"b\"}\nline2\n"

            use running = syntheticStdoutProcess (Command.create "test").Config payload

            // First call claims the stdout-streaming session via StdoutJsonLinesAsync
            let _enum1 = running.StdoutJsonLinesAsync<JsonLine>()

            // Second call via StdoutLinesAsync must throw InvalidOperationException, as the session was claimed by StdoutJsonLinesAsync
            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore))
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``StdoutLinesAsync then StdoutJsonLinesAsync on the same handle throws InvalidOperationException``
        ()
        : Task =
        task {
            let payload =
                "line1\n{\"Id\":1,\"Label\":\"a\"}\nline2\n{\"Id\":2,\"Label\":\"b\"}\n"

            use running = syntheticStdoutProcess (Command.create "test").Config payload

            // First call claims the stdout-streaming session via StdoutLinesAsync
            let _lines = running.StdoutLinesAsync()

            // Second call via StdoutJsonLinesAsync must throw InvalidOperationException, as the session was claimed by StdoutLinesAsync
            Assert.Throws<InvalidOperationException>(
                Action(fun () -> running.StdoutJsonLinesAsync<JsonLine>() |> ignore)
            )
            |> ignore
        }
        :> Task

    [<Test>]
    member _.``OutputEvents merges stdout and stderr``() : Task =
        let capturedAt = DateTimeOffset(2026, 7, 26, 12, 34, 56, TimeSpan.Zero)
        let timeProvider = FixedTimeProvider capturedAt

        let script =
            if isWindows then
                "echo out&echo err 1>&2"
            else
                "echo out; echo err 1>&2"

        task {
            let command = shell script |> Command.timeProvider timeProvider

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! events = collect (running.OutputEventsAsync())

                let hasStdout =
                    events
                    |> Seq.exists (fun e ->
                        match e with
                        | OutputEvent.Stdout line -> line.Text.Contains "out"
                        | _ -> false)

                let hasStderr =
                    events
                    |> Seq.exists (fun e ->
                        match e with
                        | OutputEvent.Stderr line -> line.Text.Contains "err"
                        | _ -> false)

                Assert.That(hasStdout, Is.True, "missing stdout event")
                Assert.That(hasStderr, Is.True, "missing stderr event")
                Assert.That(events |> Seq.map (fun e -> e.TimestampUtc), Is.All.EqualTo capturedAt)

                CollectionAssert.AreEqual(
                    [| 1L; 2L |],
                    events |> Seq.map (fun e -> e.Sequence) |> Seq.sort |> Seq.toArray
                )

                events
                |> Seq.iter (fun event ->
                    match event with
                    | OutputEvent.Stdout line
                    | OutputEvent.Stderr line ->
                        Assert.That(line.TimestampUtc, Is.EqualTo event.TimestampUtc)
                        Assert.That(line.Sequence, Is.EqualTo event.Sequence))
        }
        :> Task

    [<Test>]
    member _.``OutputEvents captures metadata before invoking the line handler``() : Task =
        let capturedAt = DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero)
        let changedByHandler = capturedAt.AddHours 1.0
        let timeProvider = AdjustableTimeProvider capturedAt

        task {
            let command =
                shell "echo out"
                |> Command.timeProvider timeProvider
                |> Command.onStdoutLine (fun _ -> timeProvider.SetUtcNow changedByHandler)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! events = collect (running.OutputEventsAsync())
                Assert.That(events, Has.Count.EqualTo 1)
                Assert.That(events[0].TimestampUtc, Is.EqualTo capturedAt)
                Assert.That(events[0].Sequence, Is.EqualTo 1L)
                Assert.That(timeProvider.GetUtcNow(), Is.EqualTo changedByHandler)
        }
        :> Task

    [<Test>]
    member _.``Stdin from a string is delivered to the child``() : Task =
        task {
            let command = shell "sort" |> Command.stdin (Stdin.FromString "hello\n")

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout.Trim(), Is.EqualTo "hello")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin WriteLineAsync appends LF for a pipe``() : Task =
        task {
            let! bytes = processStdinWriteLineBytes ProcessStdinTarget.Pipe "hello"
            Assert.That(bytes, Is.EqualTo<byte[]>(Encoding.UTF8.GetBytes "hello\n"))
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin WriteLineAsync appends LF for a POSIX PTY``() : Task =
        task {
            let! bytes = processStdinWriteLineBytes ProcessStdinTarget.PosixPty "hello"
            Assert.That(bytes, Is.EqualTo<byte[]>(Encoding.UTF8.GetBytes "hello\n"))
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin WriteLineAsync appends CR for a Windows ConPTY``() : Task =
        task {
            let! bytes = processStdinWriteLineBytes ProcessStdinTarget.WindowsConPty "hello"
            Assert.That(bytes, Is.EqualTo<byte[]>(Encoding.UTF8.GetBytes "hello\r"))
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin WriteAsync remains byte exact for a Windows ConPTY``() : Task =
        task {
            use sink = new MemoryStream()
            let stdin = ProcessStdin(sink, Encoding.Unicode, ProcessStdinTarget.WindowsConPty)
            let payload = [| 0x00uy; 0x0Auy; 0xFFuy |]
            do! stdin.WriteAsync payload
            Assert.That(sink.ToArray(), Is.EqualTo<byte[]>(payload))
        }
        :> Task

    [<Test>]
    member _.``interactive stdin via TakeStdin feeds the child``() : Task =
        task {
            let command = shell "sort" |> Command.keepStdinOpen

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    do! stdin.WriteLineAsync "banana"
                    do! stdin.WriteLineAsync "apple"
                    do! stdin.FinishAsync() // close stdin -> sort emits and exits

                    match! running.OutputStringAsync() with
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain "apple")
                        Assert.That(result.Stdout, Does.Contain "banana")
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Stdin source plus KeepStdinOpen feeds the source then accepts interactive writes``() : Task =
        task {
            // T-123: `Command.Stdin(source)` + `Command.KeepStdinOpen()` must feed the source first and then
            // leave the pipe open, so `TakeStdin` hands back a writable handle whose bytes ALSO reach the
            // child. `sort` reads every line then emits them sorted on EOF — so the sorted output containing
            // BOTH the source lines and the interactively-written line proves both halves were delivered
            // (and, since `TakeStdin` returns only after the feed finished, delivered without a write race).
            let command =
                shell "sort"
                |> Command.stdin (Stdin.FromLines [ "banana"; "cherry" ])
                |> Command.keepStdinOpen

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle after the source was fed"
                | Some stdin ->
                    do! stdin.WriteLineAsync "apple"
                    do! stdin.FinishAsync() // close stdin -> sort emits the merged, sorted input and exits

                    match! running.OutputStringAsync() with
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain "apple", "the interactive write must reach the child")
                        Assert.That(result.Stdout, Does.Contain "banana", "the source input must reach the child")
                        Assert.That(result.Stdout, Does.Contain "cherry", "the source input must reach the child")
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // ---- T-354: a completion verb ends a KeepStdinOpen pipe the caller never took ------------------
    //
    // `KeepStdinOpen` holds the parent's write end open for `TakeStdin`. A caller that never takes it and
    // then drives the run to completion leaves a child reading its stdin to EOF waiting forever — and the
    // high-level verbs (`RunAsync`/`OutputString*`/`FirstLine`) never expose the `RunningProcess` at all, so
    // that caller has no way to end the input by hand. Every terminal verb therefore ends an UNTAKEN writer,
    // and none of them touches one the caller already took (the last test below).
    //
    // `sort` is the witness on both platforms: it reads its whole input before printing anything, so it
    // exits — and prints its first line — only once stdin has reached EOF. The generous `Timeout` turns a
    // regression into a fast, honest `Outcome.TimedOut` failure instead of a hung test suite.

    /// `sort`, keeping stdin open for an interactive writer, bounded so a regression fails instead of hangs.
    member private _.KeptOpenSort() =
        shell "sort"
        |> Command.keepStdinOpen
        |> Command.timeout (TimeSpan.FromSeconds 30.0)

    [<Test>]
    member this.``OutputStringAsync ends an untaken KeepStdinOpen stdin so the child exits (T-354)``() : Task =
        task {
            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result ->
                    let exited =
                        "the verb must end the untaken stdin, so the child sees EOF and exits instead of timing out"

                    Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0), exited)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member this.``every other completion verb ends an untaken KeepStdinOpen stdin (T-354)``() : Task =
        task {
            let exited =
                "the verb must end the untaken stdin, so the child sees EOF and exits instead of timing out"

            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputBytesAsync() with
                | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0), exited)
                | Error error -> Assert.Fail $"OutputBytesAsync: {error}"

            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! outcome = running.WaitAsync()
                Assert.That(outcome, Is.EqualTo(Outcome.Exited 0), exited)

            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! profile = running.ProfileAsync(TimeSpan.FromMilliseconds 50.0)
                Assert.That(profile.Outcome, Is.EqualTo(Outcome.Exited 0), exited)

            // The WaitAny/WaitAll ownership path: this handle reaches its terminal wait through
            // `ExitTask` rather than a buffered verb, and owns the pipes exactly the same way.
            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! outcomes = RunningProcess.WaitAllAsync [| running |]
                Assert.That(outcomes[0], Is.EqualTo(Outcome.Exited 0), exited)
                do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``the high-level verbs feed the source then end an untaken KeepStdinOpen stdin (T-354)``() : Task =
        task {
            // Neither verb hands the caller a `RunningProcess`, so nobody could end this input by hand.
            // Sorted output containing BOTH source lines proves the whole source was delivered first and the
            // end of input followed it — a premature EOF would truncate or drop the input entirely.
            let sorting () =
                shell "sort"
                |> Command.stdin (Stdin.FromLines [ "banana"; "apple" ])
                |> Command.keepStdinOpen
                |> Command.timeout (TimeSpan.FromSeconds 30.0)

            match! (sorting ()).RunAsync() with
            | Ok stdout ->
                Assert.That(stdout, Does.Contain "apple", "RunAsync must deliver the whole source before EOF")
                Assert.That(stdout, Does.Contain "banana", "RunAsync must deliver the whole source before EOF")
            | Error error -> Assert.Fail $"RunAsync: {error}"

            // `sort` emits nothing until EOF, so a first line at all is the proof `firstLine` ended the input
            // before it started streaming stdout.
            match! (sorting ()).FirstLineAsync(fun line -> line.Contains "apple") with
            | Ok(Some line) -> Assert.That(line, Does.Contain "apple")
            | Ok None -> Assert.Fail "FirstLineAsync must end the untaken stdin so the child produces a line"
            | Error error -> Assert.Fail $"FirstLineAsync: {error}"
        }
        :> Task

    [<Test>]
    member this.``a completion verb leaves an already-taken stdin to its owner (T-354)``() : Task =
        task {
            match! runner.StartAsync(this.KeptOpenSort(), CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    // The verb runs while the caller still owns the writer, so it must NOT end the input
                    // itself: completion waits for this owner's own `FinishAsync`, and a line written after
                    // the verb began still reaches the child (a verb-side close would fail this write, or
                    // silently cut the line from the child's input).
                    let capture = running.OutputStringAsync()
                    do! stdin.WriteLineAsync "written after the verb started"
                    do! stdin.FinishAsync()

                    match! capture with
                    | Ok result ->
                        let delivered =
                            "a verb must not close a stdin handle the caller took: the later write must still reach the child"

                        Assert.That(result.Stdout, Does.Contain "written after the verb started", delivered)
                        Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``interactive stdin writer verbs accept a CancellationToken``() : Task =
        task {
            // The write verbs each take an optional CancellationToken; passing a live (non-cancelled)
            // token must thread through to the underlying stream and feed the child normally.
            let command = shell "sort" |> Command.keepStdinOpen
            use cts = new CancellationTokenSource()

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    do! stdin.WriteLineAsync("cherry", cts.Token)
                    do! stdin.WriteAsync(System.Text.Encoding.UTF8.GetBytes "date\n", cts.Token)
                    do! stdin.FlushAsync cts.Token
                    do! stdin.FinishAsync() // close -> sort emits (close is uncancellable, like DisposeAsync)

                    match! running.OutputStringAsync() with
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain "cherry")
                        Assert.That(result.Stdout, Does.Contain "date")
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin FinishAsync is idempotent``() : Task =
        task {
            // Closing stdin twice — or once after the run's own teardown has closed it — must be a safe
            // no-op, not throw (mirrors IAsyncDisposable.DisposeAsync).
            let command = shell "sort" |> Command.keepStdinOpen

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    do! stdin.WriteLineAsync "x"
                    do! stdin.FinishAsync()
                    do! stdin.FinishAsync() // second close: no-op, must not throw

                    match! running.OutputStringAsync() with
                    | Ok _ -> ()
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin FinishAsync does not throw against an already-broken pipe``() : Task =
        task {
            // The child exits immediately without ever reading stdin, so by the time we close our
            // end its read side is long gone — a torn-down/broken-pipe close, the same race
            // `Pump.disposeQuietly`/`disposeQuietlyAsync` swallow for every other pipe stream in the
            // project. `FinishAsync` must not surface an `IOException` to the caller here.
            let command = shell "exit 0" |> Command.keepStdinOpen

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    let! _ = running.WaitAsync()
                    do! stdin.FinishAsync() // must not throw despite the child's read end being gone
                    ()
        }
        :> Task

    [<Test>]
    member _.``ProcessStdin write verbs reject a null argument with ArgumentNullException``() : Task =
        task {
            // A C# caller that forgets its own null check must see ArgumentNullException, not a raw
            // NullReferenceException out of the underlying stream write.
            let command = shell "sort" |> Command.keepStdinOpen

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    Assert.Throws<ArgumentNullException>(
                        Action(fun () -> stdin.WriteAsync(Unchecked.defaultof<byte[]>) |> ignore)
                    )
                    |> ignore

                    Assert.Throws<ArgumentNullException>(
                        Action(fun () -> stdin.WriteLineAsync(Unchecked.defaultof<string>) |> ignore)
                    )
                    |> ignore

                    do! stdin.FinishAsync()
                    let! _ = running.OutputStringAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``concurrent stdin runs do not cross-inherit pipes and deadlock``() : Task =
        task {
            let runOne (i: int) =
                task {
                    let command = shell "sort" |> Command.stdin (Stdin.FromString $"value{i}\n")

                    match! runner.OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> return result.Stdout.Trim()
                    | Error error -> return $"ERR:{error}"
                }

            let! results = Task.WhenAll [| for i in 1..8 -> runOne i |]
            Assert.That(results.Length, Is.EqualTo 8)

            for i in 1..8 do
                Assert.That(results, Does.Contain $"value{i}")
        }
        :> Task

    [<Test>]
    member _.``an on-stdout-line handler fires for each line``() : Task =
        task {
            let captured = ResizeArray<string>()

            let command =
                threeLines
                |> Command.onStdoutLine (fun line -> lock captured (fun () -> captured.Add line))

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! _ = running.OutputStringAsync()
                Assert.That(captured, Does.Contain "line1")
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on StdoutLines instead of hanging``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let! drain = drainWithDeadline (running.StdoutLinesAsync()) 10000
                let! message = faultMessage drain
                Assert.That(message, Is.EqualTo(Some "boom"), "expected the throwing handler to surface")
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on OutputEvents instead of hanging``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let! drain = drainWithDeadline (running.OutputEventsAsync()) 10000
                let! message = faultMessage drain
                Assert.That(message, Is.EqualTo(Some "boom"), "expected the throwing handler to surface")
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on Finish``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                // Finish awaits streamOutcome, which the stdout pump faults via the re-raise — so the
                // error must propagate here (not be swallowed into an Ok result).
                let finish = running.FinishAsync() :> Task
                let! winner = Task.WhenAny(finish, Task.Delay 10000)

                Assert.That(
                    obj.ReferenceEquals(winner, finish),
                    Is.True,
                    "Finish hung instead of surfacing the handler fault"
                )

                let! message = faultMessage finish
                Assert.That(message, Is.EqualTo(Some "boom"), "expected Finish to surface the throwing handler")
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on WaitForLine, not a spurious NotReady``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running

                // The handler throws on the first line, faulting the stdout pump before the (never-
                // matching) predicate can match. That fault must surface here — re-raised — not be
                // masked as a spurious `NotReady` that also returns before the 5s deadline.
                let mutable caught = None

                try
                    let! _ =
                        running.WaitForLineAsync((fun line -> line.Contains "no-such-line"), TimeSpan.FromSeconds 5.0)

                    ()
                with ex ->
                    caught <- Some ex.Message

                Assert.That(
                    caught,
                    Is.EqualTo(Some "boom"),
                    "the throwing handler must surface as a fault, not a spurious NotReady"
                )
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on FirstLine, not a raw channel exception``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            // The handler throws on the first line, faulting the stdout pump before the (never-matching)
            // predicate can match. `firstLine` must surface that ORIGINAL fault — re-raised — not leak a
            // raw `ChannelClosedException` wrapper (nor a spurious `Ok None`).
            let mutable caught = None

            try
                let! _ = command.FirstLineAsync(fun _ -> false)
                ()
            with ex ->
                caught <- Some ex.Message

            Assert.That(
                caught,
                Is.EqualTo(Some "boom"),
                "FirstLine must surface the handler fault, not a raw ChannelClosedException"
            )
        }
        :> Task

    [<Test>]
    member _.``a faulted terminal verb still reaps the tree``() : Task =
        task {
            let mutable teardowns = 0

            let baseHost () : RunningHost =
                { Config = (Command.create "test").Config
                  Pid = None
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> Task.FromResult(Outcome.Exited 0)
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
                  Teardown =
                    fun () ->
                        teardowns <- teardowns + 1
                        ValueTask() }

            let oneLine (text: string) =
                Some(new MemoryStream(Encoding.UTF8.GetBytes text) :> Stream)

            let configThatThrows (onLine: (string -> unit) -> Command -> Command) =
                (Command.create "test"
                 |> onLine (fun _ -> raise (InvalidOperationException "boom")))
                    .Config

            let faultsAndReaps (makeHost: unit -> RunningHost) (verb: RunningProcess -> Task) =
                task {
                    teardowns <- 0
                    let running = new RunningProcess(makeHost ())
                    let mutable faulted = false

                    try
                        do! verb running
                    with :? InvalidOperationException ->
                        faulted <- true

                    Assert.That(faulted, Is.True, "the verb should fault")
                    Assert.That(teardowns, Is.GreaterThanOrEqualTo 1, "the faulted verb must still reap the tree")
                }

            // (1) A faulting wait is hit by every terminal verb's `waitWithTimeout()` — all five reap.
            let faultingWait () =
                { baseHost () with
                    Wait = fun () -> Task.FromException<Outcome>(InvalidOperationException "boom") }

            do! faultsAndReaps faultingWait (fun p -> p.OutputStringAsync() :> Task)
            do! faultsAndReaps faultingWait (fun p -> p.OutputBytesAsync() :> Task)
            do! faultsAndReaps faultingWait (fun p -> p.WaitAsync() :> Task)
            do! faultsAndReaps faultingWait (fun p -> p.ProfileAsync(TimeSpan.FromMilliseconds 5.0) :> Task)
            do! faultsAndReaps faultingWait (fun p -> p.FinishAsync() :> Task)

            // (2) A throwing OnStdoutLine with a LIVE stderr pump drives the capture path's two-pump
            //     WhenAll: the verb must fault and still reap. (Like the Profile sampler, the
            //     no-orphaned-sibling guarantee is exercised here but not directly asserted — only
            //     fault + reap is observable from outside.)
            let throwingStdout () =
                { baseHost () with
                    Config = configThatThrows Command.onStdoutLine
                    Stdout = oneLine "line1\n"
                    Stderr = oneLine "err1\n" }

            do! faultsAndReaps throwingStdout (fun p -> p.OutputStringAsync() :> Task)

            // (3) A throwing OnStderrLine faults both capture verbs (stderr is buffered in each), again
            //     with a live stdout pump. For OutputBytes the fault must come through the stderr
            //     buffer pump, since its stdout is a handler-free raw drain.
            let throwingStderr () =
                { baseHost () with
                    Config = configThatThrows Command.onStderrLine
                    Stdout = oneLine "out1\n"
                    Stderr = oneLine "err1\n" }

            do! faultsAndReaps throwingStderr (fun p -> p.OutputStringAsync() :> Task)
            do! faultsAndReaps throwingStderr (fun p -> p.OutputBytesAsync() :> Task)
        }
        :> Task

    [<Test>]
    member _.``a faulted Profile surfaces the error without hanging``() : Task =
        task {
            // A host whose wait faults immediately. Profile must cancel and await its sampler in the
            // cleanup (no hang, no swallowed error) and re-raise the original fault.
            let host: RunningHost =
                { Config = (Command.create "test").Config
                  Pid = None
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> Task.FromException<Outcome>(InvalidOperationException "boom")
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
                  Teardown = fun () -> ValueTask() }

            let profile =
                (new RunningProcess(host)).ProfileAsync(TimeSpan.FromMilliseconds 5.0) :> Task

            let! winner = Task.WhenAny(profile, Task.Delay 10000)

            Assert.That(
                obj.ReferenceEquals(winner, profile),
                Is.True,
                "Profile hung instead of surfacing the faulting wait"
            )

            let! message = faultMessage profile
            Assert.That(message, Is.EqualTo(Some "boom"), "expected Profile to surface the faulting wait")
        }
        :> Task

    [<Test>]
    member _.``StdioMode.Null discards stdout``() : Task =
        task {
            let command = threeLines |> Command.stdout StdioMode.Null

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout, Is.Empty)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // T-028: a failed open("/dev/null") inside `spawnPosix` (POSIX-only; `Native.Posix.openNul`)
    // must fail the spawn honestly (`ProcessError.Spawn`), never silently downgrade to inheriting
    // the parent's stream. Linux-only: pinning the exact fd ceiling below needs /proc/self/fd to
    // read back the process's current fd high-water mark; macOS has no equally cheap, portable way
    // to do that, so there is no way to pin the limit at "one more fd, no further" there without
    // guessing at an ambient fd count (risking starving the whole test process of descriptors).
    //
    // K-047: this exact test was seen on ubuntu-latest CI failing with "socketpair() failed for
    // stderr" instead of the expected "open(/dev/null) failed for stdout" — not ambient CI noise
    // from an unrelated process, but this test's OWN fd budget landing on a different one of
    // `spawnPosixViaSpawn`'s several fd-creating calls than intended. The original command left
    // stdin and stderr at their defaults, so `spawnPosixViaSpawn` made THREE fd-creating calls in
    // order (stdin's `openNul`, stdout's `openNul`, stderr's `socketpair()` for its default
    // `StdioMode.Piped`) while the test's rlimit math only ever accounted for the first two:
    // any stray fd churn from the .NET runtime itself (GC finalizing a handle, thread-pool/JIT
    // background activity) landing in the narrow window between the `/proc/self/fd` snapshot and
    // the actual `spawnPosix` call could free up or consume a slot, letting BOTH of the first two
    // opens (stdin, stdout) succeed unexpectedly and pushing the failure onto stderr's
    // `socketpair()` instead — a real, reproducible ordering hazard, not a flaky assertion. Fixed
    // by removing the ambiguity at its source: `InheritStdin`/`StdioMode.Inherit` are no-ops on
    // Linux (no fd created at all, see `spawnPosixViaSpawn`'s `stdinInherit`/`StdioMode.Inherit`
    // branches), so stdin and stderr are pinned to Inherit here, leaving the stdout `StdioMode.Null`
    // open as the ONLY fd-creating call `spawnPosixViaSpawn` can make for this command on Linux —
    // there is no other call left for a shifted budget to land on. The rlimit budget is tightened
    // to match: zero spare fds (`maxOpenFd + 1`), so that one call is guaranteed to fail regardless
    // of any single fd of background churn either way. A settle pass (`GC.Collect` +
    // `WaitForPendingFinalizers` + `GC.Collect`, the established pattern in this test suite —
    // see e.g. `PosixSpawnCleanupTests.fs`/`RedirectToFileTests.fs`) is also taken right before the
    // `/proc/self/fd` snapshot, to further shrink the odds of the runtime's own fd churn falling in
    // the narrow window that remains.
    [<Test>]
    member _.``spawnPosix fails instead of silently inheriting when open(/dev/null) fails``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises Native.Posix.spawnPosix's open(/dev/null) failure path"

            if not isLinux then
                Assert.Ignore "macOS: no /proc/self/fd to pin the exact rlimit deterministically"

            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            let mutable original = DevNullExhaustion.RLimit()

            Assert.That(
                DevNullExhaustion.getrlimit (DevNullExhaustion.RLIMIT_NOFILE, &original),
                Is.EqualTo 0,
                "getrlimit failed"
            )

            let openFds = Directory.GetFileSystemEntries "/proc/self/fd"
            let usedCount = openFds.Length

            let maxOpenFd =
                openFds |> Array.map (fun path -> int (Path.GetFileName path)) |> Array.max

            // Fill any gaps in the fd table below maxOpenFd so it becomes contiguous.
            // RLIMIT_NOFILE bounds the fd *number*, not the open fd *count*, so pre-existing gaps below
            // the high-water mark can be reused by open() for "free" without consuming the budget.
            // By filling them first, we ensure the "zero spare fds" assumption below is exact.
            // `Directory.GetFileSystemEntries` itself opens /proc/self/fd, and Linux is allowed to omit
            // that directory descriptor from its own listing. Rather than relying on which behavior a
            // runner chooses, the post-limit O_WRONLY probe below must fail with EMFILE before spawn;
            // that proves this budget is truly exhausted for the same open mode as stdout Null.
            let gapCount = (maxOpenFd + 1) - usedCount
            let fillerFds = ResizeArray<int>(gapCount)

            for _ in 1..gapCount do
                let fd = DevNullExhaustion.openDevNull ("/dev/null", DevNullExhaustion.O_RDONLY)

                if fd >= 0 then
                    fillerFds.Add fd
                else
                    Assert.Fail $"failed to fill fd gap (errno {Marshal.GetLastWin32Error()})"

            // Now the fd table 0..maxOpenFd is fully occupied, so the next open would request fd
            // `maxOpenFd + 1`. Allow NO further fd at all (`Current = maxOpenFd + 1`, i.e. the highest
            // legal fd number stays `maxOpenFd`): the command below pins stdin and stderr to Inherit
            // (no-ops on Linux, see the member comment above), so the explicit `StdioMode.Null` stdout
            // open is the only fd-creating call `spawnPosixViaSpawn` makes for it — and with zero spare
            // fds, that call is guaranteed to be the one that fails, independent of any stray
            // background fd churn.
            let mutable exhausted =
                DevNullExhaustion.RLimit(Current = int64 (maxOpenFd + 1), Max = original.Max)

            try
                Assert.That(
                    DevNullExhaustion.setrlimit (DevNullExhaustion.RLIMIT_NOFILE, &exhausted),
                    Is.EqualTo 0,
                    "setrlimit failed"
                )

                let probeFd =
                    DevNullExhaustion.openDevNull ("/dev/null", DevNullExhaustion.O_WRONLY)

                if probeFd >= 0 then
                    DevNullExhaustion.close probeFd |> ignore
                    Assert.Fail "fd budget was not exhausted before spawn"

                let probeErrno = Marshal.GetLastWin32Error()

                let probeErrorMessage: string =
                    "expected the pre-spawn open(/dev/null) probe to fail with EMFILE"

                Assert.That(probeErrno, Is.EqualTo DevNullExhaustion.EMFILE, probeErrorMessage)

                let command =
                    shell "true"
                    |> Command.inheritStdin
                    |> Command.stdout StdioMode.Null
                    |> Command.stderr StdioMode.Inherit

                match Native.Posix.spawnPosix command with
                | Error(ProcessError.Spawn(_, message)) ->
                    Assert.That(
                        message,
                        Does.Contain "/dev/null",
                        "expected the Spawn error to name the failing open(/dev/null)"
                    )
                | other -> Assert.Fail $"expected a Spawn error from the exhausted open(/dev/null), got {other}"
            finally
                // Close all filler fds before restoring the original limit.
                for fd in fillerFds do
                    DevNullExhaustion.close fd |> ignore

                DevNullExhaustion.setrlimit (DevNullExhaustion.RLIMIT_NOFILE, &original)
                |> ignore
        }
        :> Task

    [<Test>]
    member _.``a fail-loud ceiling errors when output exceeds the cap``() : Task =
        task {
            let command = threeLines |> Command.outputBuffer (OutputBufferPolicy.FailLoud 1)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge _) -> Assert.Pass()
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    // --- OverflowMode.Error without a configured cap keeps output unbounded (T-067) ---

    [<Test>]
    member _.``OutputStringAsync Error with no line or byte cap retains all output``() : Task =
        task {
            // `Unbounded.WithOverflow Error` has no `MaxLines`/`MaxBytes`, so there is no ceiling for
            // `Error` to cross — end-to-end through `OutputStringAsync`, this must behave like any
            // other overflow mode on an unbounded policy: retain everything, never `OutputTooLarge`.
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "line1")
                    Assert.That(result.Stdout, Does.Contain "line2")
                    Assert.That(result.Stdout, Does.Contain "line3")
                    Assert.That(result.Truncated, Is.False)
                | Error error -> Assert.Fail $"expected Ok (unbounded Error must not trip), got {error}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with a line cap alone trips OutputTooLarge once exceeded``() : Task =
        task {
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxLines(1).WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, _)) ->
                    Assert.That(lineLimit, Is.EqualTo(Some 1))
                    Assert.That(byteLimit, Is.EqualTo None)
                    Assert.That(totalLines, Is.GreaterThan 1)
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with a byte cap alone trips OutputTooLarge once exceeded``() : Task =
        task {
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes(2).WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, _, totalBytes)) ->
                    Assert.That(lineLimit, Is.EqualTo None)
                    Assert.That(byteLimit, Is.EqualTo(Some 2))
                    Assert.That(totalBytes, Is.GreaterThan 2)
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with combined line and byte caps trips OutputTooLarge``() : Task =
        task {
            let command =
                threeLines
                |> Command.outputBuffer (
                    OutputBufferPolicy.Unbounded.WithMaxLines(2).WithMaxBytes(1_000_000).WithOverflow OverflowMode.Error
                )

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, _)) ->
                    Assert.That(lineLimit, Is.EqualTo(Some 2))
                    Assert.That(byteLimit, Is.EqualTo(Some 1_000_000))
                    Assert.That(totalLines, Is.GreaterThan 2)
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with a zero line cap trips OutputTooLarge on the first line``() : Task =
        task {
            // The fail-loud ceiling must trip "strictly after exceeding" the cap, including a zero cap —
            // the first retained line already exceeds a `MaxLines = Some 0` ceiling.
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxLines(0).WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, _)) ->
                    Assert.That(lineLimit, Is.EqualTo(Some 0))
                    Assert.That(byteLimit, Is.EqualTo None)
                    Assert.That(totalLines, Is.GreaterThan 0)
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with a zero byte cap trips OutputTooLarge on the first byte``() : Task =
        task {
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes(0).WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, _, totalBytes)) ->
                    Assert.That(lineLimit, Is.EqualTo None)
                    Assert.That(byteLimit, Is.EqualTo(Some 0))
                    Assert.That(totalBytes, Is.GreaterThan 0)
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync Error with no cap retains all stderr output``() : Task =
        task {
            // R-2: the no-cap `Error` regression test above only exercised stdout — stderr shares the
            // same `LineBuffer` machinery (see `pumpStderrBuffer`), so it must retain everything too.
            let command =
                threeLinesToStderr
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithOverflow OverflowMode.Error)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result ->
                    Assert.That(result.Stderr, Does.Contain "line1")
                    Assert.That(result.Stderr, Does.Contain "line2")
                    Assert.That(result.Stderr, Does.Contain "line3")
                    Assert.That(result.Truncated, Is.False)
                | Error error -> Assert.Fail $"expected Ok (unbounded Error must not trip), got {error}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync bounds an empty-line flood under a byte cap alone (no MaxLines)``() : Task =
        task {
            // A bare-newline flood with `MaxBytes` set and no `MaxLines`: the pre-fix `LineBuffer`
            // accounting charged an empty line 0 bytes, so this would retain an unbounded number of
            // empty-string line records — defeating the byte cap as a memory bound. The corrected
            // accounting must keep the reassembled stdout genuinely bounded to (roughly) the configured
            // cap, exercised end-to-end through `OutputStringAsync`/`OutputBufferPolicy`, not just the
            // internal `LineBuffer` directly.
            let cap = 64
            let payload = String('\n', 100_000)

            let config =
                (Command.create "test"
                 |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes cap))
                    .Config

            use running = syntheticStdoutProcess config payload

            match! running.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Truncated, Is.True)
                Assert.That(Encoding.UTF8.GetByteCount result.Stdout, Is.LessThanOrEqualTo cap)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutLinesAsync stderr capture bounds a newline-free flood under a byte cap (T-191)``() : Task =
        task {
            // T-191: the stdout streaming session's stderr capture (`FinishAsync`'s `Stderr`, backed by
            // `stderrStreamBuffer`, a `Pump.LineBuffer` under `config.OutputBuffer`) must share
            // `pumpToBuffer`'s in-flight byte cap — `StreamChannel.pumpLines` passes
            // `config.OutputBuffer.MaxBytes` into the stderr pump so a newline-free stderr flood
            // force-flushes segments into that `LineBuffer`'s policy instead of growing the pump's
            // in-flight `StringBuilder` without bound. The stdout channel itself stays uncapped (a
            // consumer-paced whole-line contract, unaffected by this fix) — this test only exercises
            // the stderr side, through `StdoutLinesAsync()` + `FinishAsync()` exactly as a real consumer
            // would use them.
            let cap = 65
            let stdoutPayload = "out-line\n"
            let stderrPayload = String('é', 100_000)

            let config =
                (Command.create "test"
                 |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes cap))
                    .Config

            use stdout = new MemoryStream(Encoding.UTF8.GetBytes stdoutPayload) :> Stream
            use stderr = new MemoryStream(Encoding.UTF8.GetBytes stderrPayload) :> Stream
            use running = syntheticProcessOverStreams config (Some stdout) (Some stderr)

            let! lines = collect (running.StdoutLinesAsync())
            Assert.That(lines, Does.Contain "out-line")

            match! running.FinishAsync() with
            | Ok finished ->
                Assert.That(finished.Stderr, Is.EqualTo(String('é', 32)))
                Assert.That(finished.Truncated, Is.True)

                Assert.That(
                    Encoding.UTF8.GetByteCount finished.Stderr,
                    Is.LessThanOrEqualTo cap,
                    "the in-flight cap must bound the reassembled stderr, not just the streamed stdout"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync applies the byte cap while force-flushing newline-free Unicode stdout``() : Task =
        task {
            let cap = 5

            let config =
                (Command.create "test"
                 |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes cap))
                    .Config

            use running = syntheticStdoutProcess config "ééé"

            match! running.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "é")
                Assert.That(result.Truncated, Is.True)
                Assert.That(Encoding.UTF8.GetByteCount result.Stdout, Is.LessThanOrEqualTo cap)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- OutputBytesAsync honours the OutputBuffer byte cap + overflow (T-011) ---

    [<Test>]
    member _.``OutputBytes with no byte cap captures the full stdout untruncated``() : Task =
        task {
            match! runBytes threeLines with
            | Ok result ->
                Assert.That(result.Stdout.Length, Is.GreaterThan 0)
                Assert.That(result.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes with a byte cap above the output does not truncate``() : Task =
        task {
            // MaxBytes set but never exceeded behaves exactly like no cap: full bytes, Truncated = false.
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes 1_000_000)

            let! full = runBytes threeLines
            let! capped = runBytes command

            match full, capped with
            | Ok full, Ok capped ->
                Assert.That(capped.Truncated, Is.False)
                CollectionAssert.AreEqual(full.Stdout, capped.Stdout)
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes DropOldest keeps the last cap bytes and flags truncation``() : Task =
        task {
            let cap = 5

            let capped =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes cap)

            let! full = runBytes threeLines
            let! result = runBytes capped

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the payload must exceed the cap to truncate")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[full.Stdout.Length - cap ..], result.Stdout) // the tail
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes DropNewest keeps the first cap bytes and flags truncation``() : Task =
        task {
            let cap = 5

            let capped =
                threeLines
                |> Command.outputBuffer (
                    (OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.DropNewest
                )

            let! full = runBytes threeLines
            let! result = runBytes capped

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the payload must exceed the cap to truncate")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[.. cap - 1], result.Stdout) // the head
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes Error with no byte cap retains all raw stdout bytes``() : Task =
        task {
            // R-2: the raw-byte path (`RawBuffer`/`captureRawOrEmpty`) is the other capture path this fix
            // must keep in sync with the line path — `Unbounded.WithOverflow Error` has no `MaxBytes`, so
            // there is no ceiling to cross here either.
            let command =
                threeLines
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithOverflow OverflowMode.Error)

            let! full = runBytes threeLines
            let! result = runBytes command

            match full, result with
            | Ok full, Ok result ->
                Assert.That(result.Truncated, Is.False)
                CollectionAssert.AreEqual(full.Stdout, result.Stdout)
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes Error trips OutputTooLarge once the byte cap is exceeded``() : Task =
        task {
            let command =
                threeLines
                |> Command.outputBuffer ((OutputBufferPolicy.Unbounded.WithMaxBytes 5).WithOverflow OverflowMode.Error)

            match! runBytes command with
            | Error(ProcessError.OutputTooLarge(_, _, byteLimit, _, totalBytes)) ->
                Assert.That(byteLimit, Is.EqualTo(Some 5))
                Assert.That(totalBytes, Is.GreaterThan 5)
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    // --- Command.StreamBuffer (opt-in bounded/backpressure streaming) ---

    [<Test>]
    member _.``without StreamBuffer, streaming stays unbounded and drops nothing``() : Task =
        task {
            match! runner.StartAsync(threeLines, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! lines = collect (running.StdoutLinesAsync())
                let! finished = running.FinishAsync()

                Assert.That(lines.Count, Is.GreaterThanOrEqualTo 3)
                Assert.That(running.DroppedStreamLineCount, Is.EqualTo 0)

                match finished with
                | Ok finished -> Assert.That(finished.Truncated, Is.False)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer Backpressure stalls the producer at capacity until the consumer reads``() : Task =
        task {
            let total = 30
            let capacity = 4

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded capacity))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload total)
            let enumerable = running.StdoutLinesAsync()

            // Nobody reads during this window, so a genuine backpressure producer can only get to
            // `capacity` retained items plus the one it's currently blocked writing — nowhere near
            // `total`. This is deterministic (no OS pipe / scheduler timing involved): the synthetic
            // stdout is already fully in memory, so the pump would race straight to EOF if it weren't
            // being throttled by the bounded channel.
            do! Task.Delay 200

            Assert.That(
                running.StdoutLineCount,
                Is.LessThanOrEqualTo(capacity + 2),
                "a Backpressure producer must stall once the bounded channel fills, not race ahead unread"
            )

            let! lines = collect enumerable
            Assert.That(lines.Count, Is.EqualTo total)
            Assert.That(lines[0], Is.EqualTo "line-1")
            Assert.That(lines[total - 1], Is.EqualTo(sprintf "line-%d" total))
            Assert.That(running.StdoutLineCount, Is.EqualTo total)
            Assert.That(running.DroppedStreamLineCount, Is.EqualTo 0)

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer DropNewest keeps the earliest lines and flags the rest as dropped``() : Task =
        task {
            let total = 20
            let capacity = 5

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.DropNewest)))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload total)
            let enumerable = running.StdoutLinesAsync()

            // Let the (fully synchronous, in-memory) producer run to completion, unread, before we
            // start consuming — otherwise a concurrent read could free capacity and change how many
            // lines end up dropped, making the exact counts below flaky.
            do! Task.Delay 200

            let! lines = collect enumerable

            Assert.That(lines.Count, Is.EqualTo capacity)
            CollectionAssert.AreEqual([ for i in 1..capacity -> sprintf "line-%d" i ], lines)
            Assert.That(running.DroppedStreamLineCount, Is.EqualTo(total - capacity))
            Assert.That(running.StdoutLineCount, Is.EqualTo total)

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer DropOldest keeps the most recent lines and flags the rest as dropped``() : Task =
        task {
            let total = 20
            let capacity = 5

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.DropOldest)))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload total)
            let enumerable = running.StdoutLinesAsync()
            do! Task.Delay 200

            let! lines = collect enumerable

            Assert.That(lines.Count, Is.EqualTo capacity)

            CollectionAssert.AreEqual([ for i in (total - capacity + 1) .. total -> sprintf "line-%d" i ], lines)

            Assert.That(running.DroppedStreamLineCount, Is.EqualTo(total - capacity))
            Assert.That(running.StdoutLineCount, Is.EqualTo total)

            match! running.FinishAsync() with
            | Ok finished -> Assert.That(finished.Truncated, Is.True)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer DropOldest on OutputEvents does not livelock when a sibling pump faults``() : Task =
        task {
            // Regression: the event channel has two writers (stdout + stderr). If DropOldest's
            // eviction retry loop only exits on a successful `TryWrite`, a sibling pump completing the
            // shared channel via its own fault path (a throwing handler) leaves the other pump spinning
            // forever — `TryRead`/`TryWrite` both permanently `false` — livelocking a CPU core and
            // hanging `eventOutcome`/anything awaiting this handle's exit.
            let total = 20
            let capacity = 3

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.DropOldest))
                 |> Command.onStderrLine (fun _ -> raise (InvalidOperationException "boom")))
                    .Config

            let stdout = new MemoryStream(Encoding.UTF8.GetBytes(linesPayload total)) :> Stream
            let stderr = new MemoryStream(Encoding.UTF8.GetBytes "err1\n") :> Stream

            let host: RunningHost =
                { Config = config
                  Pid = None
                  Stdout = Some stdout
                  Stderr = Some stderr
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> Task.FromResult(Outcome.Exited 0)
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
                  Teardown = fun () -> ValueTask() }

            use running = new RunningProcess(host)
            // Must complete within the deadline — a regression here hangs forever, not merely slowly.
            // Whatever it settles as (a clean partial drain or some flavor of fault) is fine; the point
            // of this test is that it settles at all, instead of spinning forever on DropOldest.
            let! drain = drainWithDeadline (running.OutputEventsAsync()) 5000

            try
                do! drain
            with _ ->
                ()
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer Error faults the streaming enumerator with OutputTooLarge at the cap``() : Task =
        task {
            let capacity = 3
            let total = capacity + 1

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.Error)))
                    .Config

            let payload = linesPayload total
            use running = syntheticStdoutProcess config payload
            let lines = running.StdoutLinesAsync()

            // Do not enumerate until the in-memory producer has settled. With no reader, exactly
            // `capacity` items enter the backlog and item `capacity + 1` deterministically trips Error.
            let! exitError = processError (running.ExitTask :> Task)
            Assert.That(exitError.IsSome, Is.True, "the producer should fault before enumeration starts")

            let! drain = drainWithDeadline lines 5000
            let! error = processError drain

            // T-297: one stdout streaming channel item is one framed line, 1:1, so the channel's item
            // capacity genuinely IS a line limit and the running line count genuinely IS the total lines
            // produced — both must stay honest. `TotalBytes` used to be hardcoded `0`; it must now report
            // the real (UTF-8) size of the lines produced before the cap tripped.
            match error with
            | Some(ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, totalBytes)) ->
                Assert.That(lineLimit, Is.EqualTo(Some capacity), "the channel's item capacity is the line limit here")
                Assert.That(byteLimit, Is.EqualTo None)
                Assert.That(totalLines, Is.EqualTo total, "the triggering line must be included exactly once")
                Assert.That(totalLines, Is.GreaterThan capacity)
                Assert.That(totalBytes, Is.EqualTo(Encoding.UTF8.GetByteCount payload))
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``StreamBuffer Error on OutputEventsAsync reports the combined line total, not a false LineLimit``
        ()
        : Task =
        task {
            let capacity = 3
            let stdoutLines = [ "stdout-1"; "stdout-2" ]
            let stderrLines = [ "stderr-1"; "stderr-2" ]
            let payload (lines: string list) = String.Join("\n", lines) + "\n"
            let stdoutPayload = payload stdoutLines
            let stderrPayload = payload stderrLines
            let total = stdoutLines.Length + stderrLines.Length
            let expectedBytes = Encoding.UTF8.GetByteCount(stdoutPayload + stderrPayload)

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.Error)))
                    .Config

            use stdout = new MemoryStream(Encoding.UTF8.GetBytes stdoutPayload)
            use stderr = new MemoryStream(Encoding.UTF8.GetBytes stderrPayload)

            use running =
                syntheticProcessOverStreams config (Some(stdout :> Stream)) (Some(stderr :> Stream))

            let events = running.OutputEventsAsync()
            let! exitError = processError (running.ExitTask :> Task)
            Assert.That(exitError.IsSome, Is.True, "the merged producer should fault before enumeration starts")

            let! drain = drainWithDeadline events 5000
            let! error = processError drain

            // T-297: the event channel merges stdout's and stderr's framed lines into ONE shared backlog,
            // so its item capacity bounds their COMBINED count, never one stream's own line count alone —
            // reporting it as a `LineLimit` (the bug) claimed a per-stream cap that never existed.
            // `TotalLines` still reports something honest and available here: the combined line count both
            // pumps have produced so far, never the hardcoded `0`/mismatched capacity of the old code.
            match error with
            | Some((ProcessError.OutputTooLarge(_, lineLimit, byteLimit, totalLines, totalBytes) as overflow)) ->
                Assert.That(
                    lineLimit,
                    Is.EqualTo None,
                    "the shared event channel's item capacity is not a single stream's line limit"
                )

                Assert.That(byteLimit, Is.EqualTo None)

                Assert.That(totalLines, Is.EqualTo total, "stdout and stderr events must both contribute")
                Assert.That(totalLines, Is.EqualTo(capacity + 1))
                Assert.That(totalBytes, Is.EqualTo expectedBytes)
                Assert.That(overflow.Message, Does.Contain("too many events"))
                Assert.That(overflow.Message, Does.Not.Contain("line output"))
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputEvents preserves a sibling pump fault when the completed channel rejects a write``() : Task =
        task {
            let siblingFault = InvalidOperationException "sibling boom"

            let releaseStdout =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(1, StreamFullMode.Error))
                 |> Command.onStderrLine (fun _ -> raise siblingFault))
                    .Config

            use stdout =
                new GatedByteStream(Encoding.UTF8.GetBytes "out-1\n", releaseStdout.Task)

            use stderr = new MemoryStream(Encoding.UTF8.GetBytes "err-1\n")

            let host: RunningHost =
                { Config = config
                  Pid = None
                  Stdout = Some(stdout :> Stream)
                  Stderr = Some(stderr :> Stream)
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> Task.FromResult(Outcome.Exited 0)
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = fun () -> releaseStdout.TrySetResult() |> ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
                  Teardown = fun () -> ValueTask() }

            use running = new RunningProcess(host)
            running.OutputEventsAsync() |> ignore
            let exit = running.ExitTask
            let! winner = Task.WhenAny(exit :> Task, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(winner, exit), Is.True, "the sibling pumps did not settle")

            try
                let! _ = exit
                Assert.Fail "expected the original sibling fault"
            with
            | :? InvalidOperationException as ex -> Assert.That(ex, Is.SameAs siblingFault)
            | :? ProcessException as ex -> Assert.Fail $"channel closure was misreported as {ex.Error}"
        }
        :> Task

    // --- T-087: a genuine mid-stream stdout/stderr read fault surfaces as ProcessError.Io ---

    [<Test>]
    member _.``OutputStringAsync surfaces a genuine stdout read fault as ProcessError.Io``() : Task =
        task {
            let fault = IOException "disk read error"
            use stdout = new ErroringStream([ Encoding.UTF8.GetBytes "line1\n" ], fault)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            try
                let! _ = running.OutputStringAsync()
                Assert.Fail "expected a genuine read fault to surface"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputStringAsync surfaces a genuine stderr read fault as ProcessError.Io``() : Task =
        task {
            let fault = IOException "disk read error"
            use stderr = new ErroringStream([ Encoding.UTF8.GetBytes "line1\n" ], fault)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config None (Some(stderr :> Stream))

            try
                let! _ = running.OutputStringAsync()
                Assert.Fail "expected a genuine read fault to surface"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitAsync surfaces a genuine stdout read fault as ProcessError.Io``() : Task =
        task {
            let fault = IOException "disk read error"
            use stdout = new ErroringStream([ Encoding.UTF8.GetBytes "line1\n" ], fault)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            try
                let! _ = running.WaitAsync()
                Assert.Fail "expected a genuine read fault to surface"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task

    [<Test>]
    member _.``ProfileAsync surfaces a genuine stdout read fault as ProcessError.Io``() : Task =
        task {
            let fault = IOException "disk read error"
            use stdout = new ErroringStream([ Encoding.UTF8.GetBytes "line1\n" ], fault)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            try
                let! _ = running.ProfileAsync()
                Assert.Fail "expected a genuine read fault to surface"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task

    [<Test>]
    member _.``FinishAsync surfaces a genuine stdout read fault as ProcessError.Io, faulting StdoutLinesAsync too``
        ()
        : Task =
        task {
            let fault = IOException "disk read error"
            use stdout = new ErroringStream([ Encoding.UTF8.GetBytes "line1\n" ], fault)
            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            let! drain = drainWithDeadline (running.StdoutLinesAsync()) 5000
            let! streamError = processError drain

            match streamError with
            | Some(ProcessError.Io _) -> ()
            | other -> Assert.Fail $"expected the streaming enumerator to fault with ProcessError.Io, got {other}"

            try
                let! _ = running.FinishAsync()
                Assert.Fail "expected FinishAsync to surface the same genuine read fault"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task

    [<Test>]
    member _.``WaitForLine works over a bounded StreamBuffer``() : Task =
        task {
            let total = 10
            let capacity = 3

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.DropOldest)))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload total)
            running.StdoutLinesAsync() |> ignore
            // Let the producer race to EOF (and drop everything but the newest `capacity` lines)
            // before we start looking for a match, exactly like the DropOldest test above.
            do! Task.Delay 200

            let target = sprintf "line-%d" total

            match! running.WaitForLineAsync((fun line -> line = target), TimeSpan.FromSeconds 5.0) with
            | Ok line -> Assert.That(line, Is.EqualTo target)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- Command.LineTerminator (carriage-return framing across the line-pumped path) ---

    [<Test>]
    member _.``LineTerminator Cr streams carriage-return progress as per-frame lines``() : Task =
        task {
            // A `\r`-redrawn progress stream (no `\n`) must arrive as separate frames on the streaming
            // path under `Cr`, not pile up into one line as it would under the default `Lf`.
            let config =
                (Command.create "test" |> Command.stdoutLineTerminator LineTerminator.Cr).Config

            use running = syntheticStdoutProcess config "10%\r55%\r100%"
            let! lines = collect (running.StdoutLinesAsync())
            CollectionAssert.AreEqual([ "10%"; "55%"; "100%" ], lines)

            match! running.FinishAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``LineTerminator Cr splits carriage-return progress in the buffered OutputString capture``() : Task =
        task {
            // The buffered verb frames lines by the same rule, joining them with '\n'.
            let config =
                (Command.create "test" |> Command.stdoutLineTerminator LineTerminator.Cr).Config

            use running = syntheticStdoutProcess config "10%\r55%\r100%"

            match! running.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "10%\n55%\n100%")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``LineTerminator leaves OutputBytes and the tee byte-exact``() : Task =
        task {
            // The raw byte path and the tee are independent of the line terminator: both must reproduce
            // the child's stdout exactly, embedded '\r' and all, even under `Cr` framing.
            use tee = new MemoryStream()

            let config =
                (Command.create "test"
                 |> Command.stdoutLineTerminator LineTerminator.Cr
                 |> Command.stdoutTee tee)
                    .Config

            use running = syntheticStdoutProcess config "A\rB\rC"

            match! running.OutputBytesAsync() with
            | Ok result ->
                Assert.That(
                    Encoding.UTF8.GetString result.Stdout,
                    Is.EqualTo "A\rB\rC",
                    "raw bytes must stay byte-exact"
                )

                Assert.That(
                    Encoding.UTF8.GetString(tee.ToArray()),
                    Is.EqualTo "A\rB\rC",
                    "the tee must stay byte-exact"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- ProcessResult text projections decode a byte[] capture with the configured StdoutEncoding,
    //     not a hardcoded UTF-8 (T-165) ---

    [<Test>]
    member _.``OutputBytesAsync's ProcessResult.Combined decodes with a configured non-UTF-8 StdoutEncoding``() : Task =
        task {
            // "café" differs at the byte level between Latin-1 and UTF-8: 'é' is the single byte 0xE9 in
            // Latin-1 but the two-byte sequence 0xC3 0xA9 in UTF-8. Decoding the Latin-1 bytes as UTF-8
            // would surface a replacement character instead of "café" — proving the projection actually
            // uses the configured encoding, not a hardcoded UTF-8 fallback that happens to work.
            let text = "café"
            let latin1Bytes = Encoding.Latin1.GetBytes text
            use stdout = new MemoryStream(latin1Bytes)

            let config =
                (Command.create "test" |> Command.stdoutEncoding Encoding.Latin1).Config

            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            match! running.OutputBytesAsync() with
            | Ok result ->
                Assert.That(
                    result.Combined,
                    Is.EqualTo text,
                    "Combined must decode the byte[] stdout capture with the command's configured StdoutEncoding"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytesAsync's ProcessResult.Combined still decodes UTF-8 by default (no regression)``() : Task =
        task {
            // Contrast case for the fix above: a command with no explicit StdoutEncoding keeps decoding
            // its byte[] capture as UTF-8 (the Command default), so a multi-byte UTF-8 character still
            // round-trips correctly.
            let text = "café"
            let utf8Bytes = Encoding.UTF8.GetBytes text
            use stdout = new MemoryStream(utf8Bytes)

            let config = (Command.create "test").Config
            use running = syntheticProcessOverStreams config (Some(stdout :> Stream)) None

            match! running.OutputBytesAsync() with
            | Ok result -> Assert.That(result.Combined, Is.EqualTo text)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- Tee flush after pump completion (T-086): a buffered tee sink (here a real `BufferedStream`,
    //     which genuinely holds written bytes in its own buffer until `Flush`/a large-enough write/
    //     dispose) must see its last bytes as soon as the pump's read loop ends — not only once the
    //     caller eventually disposes the tee. ProcessKit never disposes a caller-supplied tee, so these
    //     tests deliberately assert BEFORE the `use` bindings dispose anything. ---

    [<Test>]
    member _.``StdoutTee flushes a buffered sink after the pump completes, before the caller disposes it``() : Task =
        task {
            // `underlying` only ever sees what `tee` (the `BufferedStream`) has actually flushed to it —
            // its buffer (64 KiB) is far larger than the payload, so nothing forces an implicit flush on
            // write. If the pump never flushed the tee, `underlying` would still be empty here.
            use underlying = new MemoryStream()
            use tee = new BufferedStream(underlying, 65536)

            let config = (Command.create "test" |> Command.stdoutTee tee).Config
            use running = syntheticStdoutProcess config "line1\nline2\n"

            match! running.OutputStringAsync() with
            | Ok _ ->
                Assert.That(
                    Encoding.UTF8.GetString(underlying.ToArray()),
                    Is.EqualTo "line1\nline2\n",
                    "the buffered stdout tee must be flushed by the pump itself, without the caller disposing it"
                )
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StderrTee flushes a buffered sink after the pump completes, before the caller disposes it``() : Task =
        task {
            use underlying = new MemoryStream()
            use tee = new BufferedStream(underlying, 65536)

            let command = threeLinesToStderr |> Command.stderrTee tee

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputBytesAsync() with
                | Ok result ->
                    // The tee is byte-exact (it may carry the shell's own CRLF line endings on
                    // Windows, plus the trailing line terminator after the last line), while
                    // `result.Stderr` is decoded/line-joined with '\n' and has no trailing separator —
                    // normalize before comparing so this asserts "the tee got flushed with everything
                    // the pump read", not byte-for-byte equality with the decoded text.
                    Assert.That(
                        Encoding.UTF8.GetString(underlying.ToArray()).Replace("\r\n", "\n").TrimEnd '\n',
                        Is.EqualTo result.Stderr,
                        "the buffered stderr tee must be flushed by the pump itself, without the caller disposing it"
                    )
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``RotatingFileSink splits writes at the byte limit and retains newest archives``() : Task =
        task {
            let directory = Directory.CreateTempSubdirectory("pk-rotate-").FullName
            let path = Path.Combine(directory, "worker.log")
            let sink = new RotatingFileSink(path, 4L, 2)

            try
                do! sink.WriteAsync(Encoding.UTF8.GetBytes("abcdefghij").AsMemory(), CancellationToken.None).AsTask()
                do! sink.FlushAsync()
                let activeLength = sink.Length
                sink.Dispose()

                Assert.That(File.ReadAllText path, Is.EqualTo "ij")
                Assert.That(File.ReadAllText(path + ".1"), Is.EqualTo "efgh")
                Assert.That(File.ReadAllText(path + ".2"), Is.EqualTo "abcd")
                Assert.That(activeLength, Is.EqualTo 2L)
            finally
                sink.Dispose()
                Directory.Delete(directory, true)
        }
        :> Task

    [<Test>]
    member _.``RotatingFileSink composes with StdoutTee without changing captured bytes``() : Task =
        task {
            let directory = Directory.CreateTempSubdirectory("pk-rotate-tee-").FullName
            let path = Path.Combine(directory, "worker.log")
            let sink = new RotatingFileSink(path, 4L, 8)

            try
                let command = shell "echo abcdefghij" |> Command.stdoutTee sink

                match! runBytes command with
                | Error error -> Assert.Fail $"{error}"
                | Ok result ->
                    do! sink.FlushAsync()
                    sink.Dispose()

                    let replayed =
                        [ for index in 8..-1..1 do
                              let archive = path + $".{index}"

                              if File.Exists archive then
                                  yield! File.ReadAllBytes archive

                          yield! File.ReadAllBytes path ]
                        |> Array.ofList

                    CollectionAssert.AreEqual(result.Stdout, replayed)
            finally
                sink.Dispose()
                Directory.Delete(directory, true)
        }
        :> Task

    // --- Idle timeout (`Command.IdleTimeout`): kill a run that stops producing output (T-052). The
    //     deadline is reset by each chunk of stdout/stderr (byte granularity, across every verb), and
    //     surfaces — like the total `Timeout` — as `Outcome.TimedOut`. ---

    [<Test>]
    member _.``IdleTimeout kills a run that goes quiet and reports TimedOut``() : Task =
        task {
            let command =
                quietAfterBurst |> Command.idleTimeout (TimeSpan.FromMilliseconds 600.0)

            let stopwatch = Stopwatch.StartNew()

            match! command.OutputStringAsync() with
            | Ok result ->
                stopwatch.Stop()
                Assert.That(result.IsTimedOut, Is.True, "the idle deadline should have fired")
                // It fires shortly after the single burst goes quiet — nowhere near the 8s the child
                // would otherwise stay alive.
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout does not fire while output keeps coming (the deadline is reset)``() : Task =
        task {
            // The drip outlives the 2s idle window but never goes quiet for that long, so a working reset
            // keeps it alive to a clean exit; a broken (fixed-from-start) deadline would fire at ~2s.
            let command = idleDrip |> Command.idleTimeout (TimeSpan.FromSeconds 2.0)

            match! command.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.IsTimedOut, Is.False, "output kept flowing, so the idle deadline must not fire")
                Assert.That(result.IsSuccess, Is.True, "the drip should exit cleanly")
                // The run outlived the idle window, so the "did not fire" result is meaningful (a
                // non-reset deadline would have killed it before this).
                Assert.That(result.Duration, Is.GreaterThan(TimeSpan.FromSeconds 2.0))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout and total Timeout coexist without a double outcome``() : Task =
        task {
            // Both deadlines are armed; the short idle one fires first. `RunAsync` yields exactly one
            // result, so a single `Timeout` here is proof there is no double kill / double report.
            let command =
                quietAfterBurst
                |> Command.timeout (TimeSpan.FromSeconds 8.0)
                |> Command.idleTimeout (TimeSpan.FromMilliseconds 500.0)

            let stopwatch = Stopwatch.StartNew()
            let! result = command.RunAsync()
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected Timeout, got {other}"

            // The idle deadline (500ms) won over the total (8s) — the run ends promptly, not at 8s.
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout honours TimeoutGrace``() : Task =
        task {
            // Idle fires, then the graceful-kill machinery (SIGTERM, then SIGKILL after the grace) runs
            // exactly as it does for the total timeout — still a single, prompt `TimedOut`.
            let command =
                quietAfterBurst
                |> Command.idleTimeout (TimeSpan.FromMilliseconds 400.0)
                |> Command.timeoutGrace (TimeSpan.FromMilliseconds 200.0)

            let stopwatch = Stopwatch.StartNew()

            match! command.OutputStringAsync() with
            | Ok result ->
                stopwatch.Stop()
                Assert.That(result.IsTimedOut, Is.True)
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout fires on the streaming path too``() : Task =
        task {
            // The idle deadline is armed once the streaming session's exit wait begins and reset by the
            // stdout/stderr reads the pumps do — so a streamed run that hangs is killed just like a
            // buffered one, surfacing `TimedOut` through `FinishAsync`.
            let command =
                quietAfterBurst |> Command.idleTimeout (TimeSpan.FromMilliseconds 600.0)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok started ->
                use running = started
                let! _lines = collect (running.StdoutLinesAsync())

                match! running.FinishAsync() with
                | Ok finished -> Assert.That(finished.Outcome.IsTimedOut, Is.True)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IdleTimeout rejects a negative duration at the builder boundary``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> Command.create "x" |> Command.idleTimeout (TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    // ---- T-360: the bounded post-exit output drain ------------------------------------------------
    //
    // Every regression below models the one shape that used to hang indefinitely: the leader's fate is
    // ALREADY settled (the synthetic host's `Wait` answers at once, exactly as a real reap does the
    // moment the child exits), but the parent's read end never reaches EOF, because something that
    // inherited it — a daemonized worker, a `setsid` helper, a shell's background job — still holds the
    // write end. `HeldOpenOutputStream` is that pipe, in both of its real forms: a cancellable
    // pipe/socket read, and a pty master's uninterruptible one.

    /// A handle over a stdout pipe that delivers `payload` and then never ends, with no stderr.
    member private _.HeldOpenStdout(payload: string, respectsCancellation: bool) : RunningProcess =
        let stdout =
            new HeldOpenOutputStream(Encoding.UTF8.GetBytes payload, respectsCancellation)

        syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

    [<Test>]
    member this.``OutputStringAsync bounds a tail an inherited pipe holds open and reports it truncated``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                use running = this.HeldOpenStdout("line-1\nline-2\n", respectsCancellation = true)
                let stopwatch = Stopwatch.StartNew()
                let! result = running.OutputStringAsync()
                stopwatch.Stop()

                match result with
                | Error error -> Assert.Fail $"the bounded drain must still produce an honest capture: {error}"
                | Ok captured ->
                    // The leader's own outcome, unchanged by the bound.
                    Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                    // Everything that DID arrive before the bound is still there...
                    Assert.That(captured.Stdout, Does.Contain "line-1")
                    Assert.That(captured.Stdout, Does.Contain "line-2")
                    // ...and the capture says it is incomplete rather than passing for the whole output.
                    Assert.That(captured.Truncated, Is.True, "a capture cut short by the bound must be truncated")

                Assert.That(running.OutputDrainWasBounded, Is.True, "the drain should have severed the read end")
                Assert.That(running.OutputPumpsWereAbandoned, Is.False, "a cancellable read must end at the sever")

                Assert.That(
                    stopwatch.Elapsed,
                    Is.LessThan(TimeSpan.FromSeconds 30.0),
                    "the verb hung on a pipe the leader no longer owns"
                )
            })
        :> Task

    [<Test>]
    member this.``OutputStringAsync still answers when the held-open read cannot be interrupted``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The pty-master shape: no token wakes the pending read, so the sever cannot end the
                // pump and the drain must abandon it — observed, never awaited again — while the verb
                // still reports the bytes that did arrive.
                use running = this.HeldOpenStdout("line-1\n", respectsCancellation = false)
                let stopwatch = Stopwatch.StartNew()
                let! result = running.OutputStringAsync()
                stopwatch.Stop()

                match result with
                | Error error -> Assert.Fail $"an abandoned pump must not fault the verb: {error}"
                | Ok captured ->
                    Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                    Assert.That(captured.Stdout, Does.Contain "line-1")
                    Assert.That(captured.Truncated, Is.True)

                Assert.That(running.OutputDrainWasBounded, Is.True)
                Assert.That(running.OutputPumpsWereAbandoned, Is.True, "an uninterruptible read must be abandoned")
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 30.0))
            })
        :> Task

    [<Test>]
    member this.``OutputBytesAsync is symmetric with the text verb under the drain bound``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The raw byte path was the last consumer without a bound (the Rust prototype closed it
                // last too), and its read loop discarded its buffer on any non-EOF ending — so it has to
                // be checked for BOTH properties: it returns, and it returns the bytes.
                use running = this.HeldOpenStdout("raw-bytes-tail", respectsCancellation = true)
                let! result = running.OutputBytesAsync()

                match result with
                | Error error -> Assert.Fail $"the bounded drain must still produce an honest capture: {error}"
                | Ok captured ->
                    Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                    Assert.That(Encoding.UTF8.GetString captured.Stdout, Is.EqualTo "raw-bytes-tail")
                    Assert.That(captured.Truncated, Is.True)

                Assert.That(running.OutputDrainWasBounded, Is.True)
            })
        :> Task

    [<Test>]
    member this.``OutputBytesAsync keeps its partial bytes when the held-open read cannot be interrupted``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                use running = this.HeldOpenStdout("raw-bytes-tail", respectsCancellation = false)
                let! result = running.OutputBytesAsync()

                match result with
                | Error error -> Assert.Fail $"an abandoned pump must not fault the byte verb: {error}"
                | Ok captured ->
                    // The bytes live in a sink the VERB owns, so abandoning the pump loses none of them.
                    Assert.That(Encoding.UTF8.GetString captured.Stdout, Is.EqualTo "raw-bytes-tail")
                    Assert.That(captured.Truncated, Is.True)

                Assert.That(running.OutputPumpsWereAbandoned, Is.True)
            })
        :> Task

    [<Test>]
    member this.``WaitAsync and ProfileAsync end on the leader's exit, not on the inherited pipe``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The discard paths have no capture to salvage, so the whole contract is "conclude with
                // the leader's outcome, promptly, without leaking the drain task".
                use waiting = this.HeldOpenStdout("ignored\n", respectsCancellation = true)
                let! waited = waiting.WaitAsync()
                Assert.That(waited, Is.EqualTo(Outcome.Exited 0))
                Assert.That(waiting.OutputDrainWasBounded, Is.True)

                use profiling = this.HeldOpenStdout("ignored\n", respectsCancellation = false)
                let! profile = profiling.ProfileAsync(TimeSpan.FromMilliseconds 20.0)
                Assert.That(profile.Outcome, Is.EqualTo(Outcome.Exited 0))
                Assert.That(profiling.OutputPumpsWereAbandoned, Is.True)
            })
        :> Task

    [<Test>]
    member this.``a streamed stdout consumer and FinishAsync both end at the drain bound``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The streaming session's own contract: the enumerator must reach the END of its stream
                // (the session completes the channel at the bound, even for a pump it had to abandon),
                // and the terminal `FinishAsync` must report the run as truncated.
                use running =
                    this.HeldOpenStdout("streamed-1\nstreamed-2\n", respectsCancellation = true)

                let! lines = collect (running.StdoutLinesAsync())
                Assert.That(lines, Is.EqualTo<string list>([ "streamed-1"; "streamed-2" ]))

                match! running.FinishAsync() with
                | Error error -> Assert.Fail $"FinishAsync must conclude at the bound: {error}"
                | Ok finished ->
                    Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                    Assert.That(finished.Truncated, Is.True, "output cut short by the bound must be reported")

                Assert.That(running.OutputDrainWasBounded, Is.True)
            })
        :> Task

    [<Test>]
    member this.``an event stream ends at the drain bound``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                use running = this.HeldOpenStdout("event-1\n", respectsCancellation = true)
                let! events = collect (running.OutputEventsAsync())

                Assert.That(
                    events |> Seq.map (fun (event: OutputEvent) -> event.Text) |> List.ofSeq,
                    Is.EqualTo<string list>([ "event-1" ]),
                    "the events that did arrive must still be delivered"
                )

                Assert.That(running.OutputDrainWasBounded, Is.True)
            })
        :> Task

    [<Test>]
    member this.``WaitAllAsync resolves on a handle whose inherited pipe is held open``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // `ExitTask`'s FRESH branch — no verb ever claimed the pipes, so it starts its own
                // discard drains and must bound them like every other consumer.
                use running = this.HeldOpenStdout("ignored\n", respectsCancellation = true)
                let! outcomes = RunningProcess.WaitAllAsync [| running |]
                Assert.That(outcomes, Is.EqualTo<Outcome[]>([| Outcome.Exited 0 |]))
                Assert.That(running.OutputDrainWasBounded, Is.True)
            })
        :> Task

    [<Test>]
    member _.``an ordinary short tail is never cut by the drain bound``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The other half of the contract, and the one a too-eager bound would break: a child
                // that closed its pipes on the way out is already at EOF, so the window is never even
                // armed and nothing is reported as truncated.
                let stdout = new MemoryStream(Encoding.UTF8.GetBytes "line-1\nline-2\n")

                use running =
                    syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

                match! running.OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok captured ->
                    Assert.That(captured.Stdout, Does.Contain "line-2")
                    Assert.That(captured.Truncated, Is.False, "an ordinary tail must not be reported truncated")

                Assert.That(running.OutputDrainWasBounded, Is.False, "the bound must not fire on a normal run")
            })
        :> Task

    [<Test>]
    member this.``a checking verb refuses a drain-bounded capture instead of hanging``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // `RunAsync`/`ParseAsync`/`OutputJsonAsync` present their capture AS the whole of stdout,
                // so they refuse ANY truncated one — including one this bound cut short. The composition
                // that matters here is that they now REFUSE, in bounded time, rather than hang: a clipped
                // string must never reach a caller (or a parser) as if it were complete.
                use running = this.HeldOpenStdout("partial\n", respectsCancellation = true)

                // No ceiling configured — the ordinary case for this shape, and the one that used to
                // send the refusal into the shared formatter's EVENT branch.
                match! CaptureVerbs.run None None (fun () -> running.OutputStringAsync()) with
                | Ok text -> Assert.Fail $"a clipped capture must not be presented as whole output: {text}"
                | Error(ProcessError.OutputIncomplete "test" as error) ->
                    // The MESSAGE, not just the case: this refusal must name the drain bound's own cause.
                    // Asserting the case alone is what let "'test' produced too many events (1 events)"
                    // — a line count reported as events, against a ceiling nobody configured — pass for a
                    // plain text capture.
                    Assert.That(
                        error.Message,
                        Is.EqualTo
                            "'test' output was cut short: something that inherited its stdout/stderr outlived the run, so the capture is incomplete"
                    )

                    Assert.That(running.OutputDrainWasBounded, Is.True)
                | Error other -> Assert.Fail $"expected the drain-bound truncation refusal, got {other}"
            })
        :> Task

    [<Test>]
    member _.``a pump fault before the bound is still an error, not a quiet truncation``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The bound must never convert a genuine failure into a partial success: a read fault
                // completes the pump, so the drain sees it settle and re-raises exactly as the
                // unbounded join did.
                use stdout =
                    new ErroringStream([ Encoding.UTF8.GetBytes "line-1\n" ], IOException "disk read error")

                use running =
                    syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

                try
                    let! _ = running.OutputStringAsync()
                    Assert.Fail "a genuine read fault must not be reported as a truncated success"
                with :? ProcessException as pe ->
                    match pe.Error with
                    | ProcessError.Io _ -> ()
                    | other -> Assert.Fail $"expected ProcessError.Io, got {other}"

                Assert.That(running.OutputDrainWasBounded, Is.False, "a settled pump must not trip the bound")
            })
        :> Task

    [<Test>]
    member _.``a severed read that aborts with an I/O error still ends at EOF``() : Task =
        task {
            // The sever's contract at the seam itself: HOW an aborted pending read comes back is the
            // transport's choice — a cancellation on a cancellable one, an I/O abort on a stream over a
            // raw fd — and both are the read WE cut, so both must answer EOF. Asserted directly on
            // `SeverableStream` because no synthetic pipe above can produce the second shape and no real
            // pipe can be relied on to produce it on every platform.
            use severCts = new CancellationTokenSource()

            use inner =
                new AbortOnSeverStream(Encoding.UTF8.GetBytes "tail", IOException "the I/O operation was aborted")

            use severable = new SeverableStream(inner, severCts.Token)
            let buffer = Array.zeroCreate<byte> 32

            // The payload still arrives verbatim before the sever.
            let! first = severable.ReadAsync(Memory<byte> buffer, CancellationToken.None)
            Assert.That(first, Is.EqualTo 4)

            let pending = severable.ReadAsync(Memory<byte> buffer, CancellationToken.None)
            severCts.Cancel()
            let! afterSever = pending

            Assert.That(afterSever, Is.EqualTo 0, "an aborted read the sever caused is this stream's EOF")

            // And every later read stays at EOF rather than touching the pipe again.
            let! next = severable.ReadAsync(Memory<byte> buffer, CancellationToken.None)
            Assert.That(next, Is.EqualTo 0)
        }
        :> Task

    [<Test>]
    member _.``an I/O fault with no sever still propagates through the severable stream``() : Task =
        task {
            // The other half of that rule, and the one that keeps it from becoming a fault-swallower:
            // with nothing severed, a genuine read failure is still a genuine read failure.
            use severCts = new CancellationTokenSource()

            use inner =
                new ErroringStream([ Encoding.UTF8.GetBytes "line-1\n" ], IOException "disk read error")

            use severable = new SeverableStream(inner, severCts.Token)
            let buffer = Array.zeroCreate<byte> 32
            let! first = severable.ReadAsync(Memory<byte> buffer, CancellationToken.None)
            Assert.That(first, Is.GreaterThan 0)

            try
                let! _ = severable.ReadAsync(Memory<byte> buffer, CancellationToken.None)
                Assert.Fail "a read fault nobody severed must not be reported as EOF"
            with :? IOException ->
                // Exactly what the pump has to see to report `ProcessError.Io`.
                ()
        }
        :> Task

    [<Test>]
    member _.``a verb reports a truncated capture when the severed read aborts with an I/O error``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // End to end over the same shape: the bound severs, the transport answers the abort with
                // an `IOException`, and the verb must still deliver the honest partial capture. Before
                // this was handled at the sever, that ending faulted the verb with `ProcessError.Io` —
                // an error where the documented answer is `Truncated`.
                let stdout =
                    new AbortOnSeverStream(
                        Encoding.UTF8.GetBytes "line-1\n",
                        IOException "the I/O operation was aborted"
                    )

                use running =
                    syntheticProcessOverStreams (Command.create "test").Config (Some(stdout :> Stream)) None

                match! running.OutputStringAsync() with
                | Error error -> Assert.Fail $"an aborted severed read must not fault the verb: {error}"
                | Ok captured ->
                    Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                    Assert.That(captured.Stdout, Does.Contain "line-1")
                    Assert.That(captured.Truncated, Is.True)

                Assert.That(running.OutputDrainWasBounded, Is.True)
            })
        :> Task

    // ---- T-360: the same bound, on a REAL child and a REAL OS pipe ---------------------------------
    //
    // Everything above drives a hand-written `Stream`, which can model the two endings the bound has to
    // cope with but cannot prove the one thing only the OS decides: HOW a pending read on an actual
    // pipe unwinds when this handle severs it. A cancellable transport unwinds it as an
    // `OperationCanceledException`, a non-cancellable one as an I/O abort — and reporting the second as
    // a genuine fault would fail these verbs with `ProcessError.Io` instead of the documented truncated
    // capture, on exactly the shape the whole task exists for. So the regressions below spawn a real
    // child that leaves a real descendant holding its real stdout.

    /// A REAL child that hands a long-lived descendant its own stdout and then exits at once: the
    /// leader's fate settles in milliseconds while the parent's read end stays open for as long as the
    /// descendant lives, which is what makes an unbounded pump join hang.
    ///
    /// POSIX: the background job inherits stdout, writes nothing to it, and `$!` publishes its pid, so
    /// the ownership half of the contract can be checked against the OS too. Windows: `start /b` hands
    /// the new process this `cmd`'s own std handles and returns immediately, so `ping` holds (and keeps
    /// writing to) the pipe long after `cmd` has exited.
    member private _.RealDescendantHoldingStdout: Command =
        if isWindows then
            Command.create "cmd.exe"
            |> Command.args [ "/c"; "start /b ping -n 30 127.0.0.1 & echo hi" ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; "sleep 30 & echo $!; echo hi" ]

    /// Is `pid` still a LIVE process? A descendant this run killed but did not father is reaped by init
    /// rather than by us, so a brief zombie (`Z`) is "not alive" — the assertion is about the kill, not
    /// about who collects the corpse. Linux-only (`/proc` is the portable-enough source for it).
    member private _.IsLiveProcess(pid: int) : bool =
        let statPath = $"/proc/{pid}/stat"

        if not (File.Exists statPath) then
            false
        else
            try
                let stat = File.ReadAllText statPath
                // "<pid> (comm) <state> ..." — `comm` may itself contain spaces and parentheses, so the
                // state is the field after the LAST ')'.
                match stat.LastIndexOf ')' with
                | -1 -> false
                | close ->
                    let rest = stat.Substring(close + 1).TrimStart()
                    rest.Length > 0 && rest[0] <> 'Z' && rest[0] <> 'X'
            with :? IOException ->
                // The entry vanished between `Exists` and the read — that is the process being gone.
                false

    [<Test>]
    member this.``a real child whose descendant holds its stdout is bounded, not hung, and not an Io fault``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                match! runner.StartAsync(this.RealDescendantHoldingStdout, CancellationToken.None) with
                | Error error -> Assert.Fail $"the real spawn should succeed: {error}"
                | Ok started ->
                    use running = started
                    let stopwatch = Stopwatch.StartNew()
                    let! result = running.OutputStringAsync()
                    stopwatch.Stop()

                    match result with
                    | Error error ->
                        // The failure mode this test exists to catch: a severed pending read that
                        // unwinds as an `IOException` would arrive here as `ProcessError.Io`.
                        Assert.Fail $"a severed real pipe must end as a truncated capture, not a failure: {error}"
                    | Ok captured ->
                        // The leader's own outcome, decided long before the bound fired.
                        Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                        // What the leader wrote before exiting is still captured in full...
                        Assert.That(captured.Stdout, Does.Contain "hi")
                        // ...and the capture admits it is not the whole of stdout (the descendant still
                        // owns the write end).
                        Assert.That(captured.Truncated, Is.True, "a capture cut short by the bound must be truncated")

                    Assert.That(running.OutputDrainWasBounded, Is.True, "the held-open real pipe must hit the bound")

                    // The descendant lives ~30s; anything near that means the pump join was never bounded.
                    Assert.That(
                        stopwatch.Elapsed,
                        Is.LessThan(TimeSpan.FromSeconds 20.0),
                        "the verb waited on a pipe the exited leader no longer owns"
                    )
            })
        :> Task

    [<Test>]
    member this.``a real drain-bounded run is symmetric for bytes and refused by the checking verb``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The byte path on a real pipe (the last consumer the Rust prototype bounded, and the one
                // whose read loop used to discard its buffer on any non-EOF ending)...
                match! runner.StartAsync(this.RealDescendantHoldingStdout, CancellationToken.None) with
                | Error error -> Assert.Fail $"the real spawn should succeed: {error}"
                | Ok started ->
                    use running = started
                    let! result = running.OutputBytesAsync()

                    match result with
                    | Error error -> Assert.Fail $"the bytes verb must not fault on a severed real pipe: {error}"
                    | Ok captured ->
                        Assert.That(Encoding.UTF8.GetString captured.Stdout, Does.Contain "hi")
                        Assert.That(captured.Truncated, Is.True)

                    Assert.That(running.OutputDrainWasBounded, Is.True)

                // ...and the checking verb over a real run of the same shape, which must refuse the
                // clipped capture — with the error that names the drain bound, not a buffer ceiling
                // nobody configured.
                let stopwatch = Stopwatch.StartNew()

                let! refusal = this.RealDescendantHoldingStdout |> Runner.run runner CancellationToken.None

                stopwatch.Stop()

                match refusal with
                | Ok text -> Assert.Fail $"a clipped capture must not be presented as whole output: {text}"
                | Error(ProcessError.OutputIncomplete program as error) ->
                    Assert.That(program, Is.EqualTo(if isWindows then "cmd.exe" else "/bin/sh"))

                    Assert.That(
                        error.Message,
                        Does.Contain
                            "output was cut short: something that inherited its stdout/stderr outlived the run"
                    )
                | Error other -> Assert.Fail $"expected the drain-bound refusal, got {other}"

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 20.0))
            })
        :> Task

    [<Test>]
    member this.``a real run's private group takes the descendant that held its stdout``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The ownership half, against the OS rather than a backend's counters: the bound exists
                // partly BECAUSE a verb that never returned never reached its teardown, so the private
                // per-run group never reaped what the child left behind. Whether a process is alive is
                // only portably observable through `/proc`, so this half is Linux-gated; the capture
                // halves above cover Windows and macOS.
                if not isLinux then
                    Assert.Ignore "liveness of a foreign pid is observable via /proc on Linux only"

                let mutable descendant = 0

                match! runner.StartAsync(this.RealDescendantHoldingStdout, CancellationToken.None) with
                | Error error -> Assert.Fail $"the real spawn should succeed: {error}"
                | Ok started ->
                    use running = started

                    match! running.OutputStringAsync() with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok captured ->
                        // `sleep 30 & echo $!` — the first line is the descendant's pid.
                        let firstLine =
                            captured.Stdout.Split('\n') |> Array.map (fun l -> l.Trim()) |> Array.head

                        Assert.That(Int32.TryParse firstLine |> fst, Is.True, $"expected a pid, got '{firstLine}'")
                        descendant <- int firstLine
                        Assert.That(captured.Truncated, Is.True)

                    Assert.That(running.OutputDrainWasBounded, Is.True)

                // The verb reached its `reapGuard`, so the private group was released — and the
                // descendant that outlived the leader goes with it. The kill is delivered by us but the
                // corpse is collected by init (it is not our child), so poll briefly for "no longer
                // alive" rather than demanding the entry be gone the instant the verb returned.
                let deadline = Stopwatch.StartNew()

                while this.IsLiveProcess descendant && deadline.Elapsed < TimeSpan.FromSeconds 10.0 do
                    do! Task.Delay 50

                Assert.That(
                    this.IsLiveProcess descendant,
                    Is.False,
                    "the private group must take the descendant that held the run's stdout"
                )
            })
        :> Task
