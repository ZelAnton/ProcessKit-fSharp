namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// A `Stream` whose `ReadAsync` parks until the test releases it, and records whether two
/// `ReadAsync` calls on the same instance were ever in flight at once — direct, deterministic
/// evidence of "two readers pumping the same pipe", which a regression in `RunningProcess.ExitTask`
/// (the "buffered verb, then WaitAnyAsync" order) would produce.
type private GatedStream(payload: byte[]) =
    inherit Stream()

    let inner = new MemoryStream(payload)
    let mutable inFlight = 0
    let mutable everConcurrent = false

    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let proceed =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once some `ReadAsync` call has been entered (and is parked, waiting on `Release`).
    member _.Entered: Task = entered.Task :> Task

    /// Whether two `ReadAsync` calls were ever in flight on this stream at the same time.
    member _.EverConcurrent = everConcurrent

    /// Lets every parked (and future) `ReadAsync` call proceed to the real, underlying read.
    member _.Release() = proceed.TrySetResult() |> ignore

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set value = inner.Position <- value

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(buffer, offset, count) = inner.Read(buffer, offset, count)

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        let run =
            task {
                if Interlocked.Increment(&inFlight) > 1 then
                    everConcurrent <- true

                entered.TrySetResult() |> ignore

                try
                    do! proceed.Task
                    return! inner.ReadAsync(buffer, cancellationToken).AsTask()
                finally
                    Interlocked.Decrement(&inFlight) |> ignore
            }

        ValueTask<int>(run)

/// A write-only `Stream` whose every write throws — a stand-in for a `Command.stdoutTee` sink that
/// faults, so the T-066 fault-teardown tests can drive a pump fault through the *tee* path (as
/// distinct from a throwing line handler) and prove it still kills the tree instead of hanging.
type private ThrowingTeeStream() =
    inherit Stream()

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

    override _.Write(_buffer, _offset, _count) =
        raise (InvalidOperationException "tee-boom")

    override _.WriteAsync(_buffer: ReadOnlyMemory<byte>, _cancellationToken: CancellationToken) : ValueTask =
        raise (InvalidOperationException "tee-boom")

/// A single-threaded `SynchronizationContext` standing in for a WPF/WinForms UI thread or classic
/// ASP.NET request context: `Post` merely *queues* the continuation (recording the count) rather than
/// running it, because a real UI thread runs it only when it next pumps its message loop. In the
/// T-123 deadlock test the sole thread that owns this context is blocked inside `TakeStdin` and never
/// pumps, so anything the stdin feed posted here would never run — the deadlock the fix (running the
/// feed on the thread pool via `backgroundTask`) must avoid. `Posted` staying `0` is direct evidence
/// the feed never captured this context.
type private QueueingSyncContext() =
    inherit SynchronizationContext()
    let mutable posted = 0

    /// How many continuations were posted to this context (0 proves nothing captured it).
    member _.Posted = Volatile.Read(&posted)

    override _.Post(_callback, _state) =
        Interlocked.Increment(&posted) |> ignore

    override _.Send(callback, state) = callback.Invoke state

    override this.CreateCopy() = this :> SynchronizationContext

/// A stdout double for the T-197 teardown-race tests: its first `ReadAsync` yields `firstChunk`, its
/// second parks until the stream is disposed, and the parked read then throws `ObjectDisposedException`
/// — exactly as a real parent-side pipe stream does when this handle's own teardown (a concurrent
/// `StopAsync`/`Dispose`) disposes it while a buffered pump is still draining the tail. Whether that
/// dispose comes THROUGH the handle's teardown (which cancels `disposalCts` first — the buffered pump
/// swallows it) or DIRECTLY from the test (leaving `disposalCts` un-cancelled — a genuine fault surfaced
/// as `ProcessError.Io`) is what the two sides of the classification turn on.
type private ParkThenFaultOnDisposeStream(firstChunk: byte[]) =
    inherit Stream()

    let mutable served = false

    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes once the pump has served the first chunk and parked on the tail read.
    member _.ParkedOnTail: Task = entered.Task :> Task

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 firstChunk.Length

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.Write(_buffer, _offset, _count) = raise (NotSupportedException())
    override _.Read(_buffer, _offset, _count) : int = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        let run =
            task {
                if not served then
                    served <- true
                    firstChunk.AsSpan().CopyTo(buffer.Span)
                    return firstChunk.Length
                else
                    // The tail read: park until disposed, then fault exactly like a real disposed pipe.
                    entered.TrySetResult() |> ignore
                    do! released.Task
                    return raise (ObjectDisposedException "Stream")
            }

        ValueTask<int>(run)

    override _.Dispose(disposing: bool) =
        released.TrySetResult() |> ignore
        base.Dispose disposing

/// Regression tests for the correctness & robustness fixes: timeout validation/clamping, the
/// single-consumption guard on `RunningProcess`, pipeline per-stage `OkCodes`, and pipeline wiring
/// of a stage whose stdout was set non-piped.
[<TestFixture>]
type CorrectnessBugTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    // A minimal synthetic `RunningHost` over the given config with no pipes and an immediate clean
    // exit — the T-066 concurrency/fault tests below override just the fields they need (`Stdout`,
    // `Wait`, `StartKill`, `Teardown`) with `{ baseHost cfg with ... }`.
    let baseHost (config: CommandConfig) : RunningHost =
        { Config = config
          Pid = None
          Stdout = None
          Stderr = None
          Stdin = None
          StartTime = DateTime.UtcNow
          StartedTimestamp = Stopwatch.GetTimestamp()
          StartTimeIdentity = None
          Wait = fun () -> Task.FromResult(Outcome.Exited 0)
          StdinError = fun () -> None
          StdinFeedComplete = ignore
          StartKill = ignore
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          Teardown = fun () -> ValueTask() }

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A path in an existing directory (temp) with a random leaf, so `File.OpenRead` fails with a
    // genuine source error (FileNotFound), not a directory-not-found or a permissions quirk.
    let missingStdinPath () =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pk-missing-{Guid.NewGuid():N}.txt")

    // ---- T-198: CreateProcessW must never mutate a managed command-line string -----------------
    //
    // Win32 documents that `CreateProcessW` may modify `lpCommandLine` IN PLACE while probing
    // executable candidates. Marshalling that parameter as a managed `string` (pinned, not copied,
    // under `CharSet.Unicode`) therefore handed the OS a writable pointer into managed string memory —
    // and for a single-token, argument-less command the builder forwards `command.Program` itself,
    // frequently an INTERNED literal shared by every use of that literal in the process. A native write
    // into it is a memory-corruption-class bug visible to arbitrary unrelated readers of the same
    // literal. The fix copies the command line into a private unmanaged buffer, so no managed string is
    // ever exposed to the OS as writable.
    //
    // Provoking the kernel's transient in-place patch deterministically across Windows versions is not
    // portable (and our own argument quoting defeats the classic space-ambiguity trigger), so this test
    // guards the invariant behaviourally: after real spawn attempts driven by an interned literal
    // program name, that literal must stay byte-for-byte identical to an independent, non-interned copy
    // taken before the spawns. It never false-fails with the fix in place, and catches a regression
    // that lets the OS persist a write back into the managed literal.
    [<Test>]
    member _.``spawning does not mutate the interned program-name string (T-198)``() =
        if not isWindows then
            Assert.Ignore "CreateProcessW lpCommandLine aliasing is a Windows-only concern"

        // An interned string literal used verbatim as a single-token, argument-less program — the exact
        // shape whose whole command line IS `command.Program`. It does not resolve on PATH, so every
        // spawn takes the CreateProcess candidate-probing / not-found path without hanging.
        let program = "pk_t198_intern_canary_program"

        // An INDEPENDENT, non-interned copy of the same text, captured before any spawn. Comparing the
        // interned literal against this copy detects a native write into the literal; comparing the
        // literal against its own source literal could not — both would be the same, equally corrupted
        // instance.
        let reference = String(program.ToCharArray())

        Assert.That(
            Object.ReferenceEquals(program, reference),
            Is.False,
            "the reference copy must be a distinct instance"
        )

        for _ in 1..50 do
            match (Command.create program).ExitCodeAsync().GetAwaiter().GetResult() with
            | Error(ProcessError.NotFound _) -> ()
            | Error other -> Assert.Fail $"expected a NotFound error for a missing program, got: {other.Message}"
            | Ok code -> Assert.Fail $"a non-existent program must not spawn (exit {code})"

        Assert.That(
            String.Equals(program, reference, StringComparison.Ordinal),
            Is.True,
            "CreateProcessW must not mutate the managed program-name string"
        )

    [<Test>]
    member _.``a negative command timeout is rejected at configuration time``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.timeout (TimeSpan.FromSeconds -1.0)
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a negative pipeline timeout is rejected at configuration time``() =
        let pipeline = (shell "echo a").Pipe(shell "cat")

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> pipeline.Timeout(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a negative TimeoutGrace is rejected at configuration time``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.timeoutGrace (TimeSpan.FromSeconds -1.0)
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Command.Env rejects an empty key or a key containing '='``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> Command.create "whatever" |> Command.env "" "value" |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> Command.create "whatever" |> Command.env "KEY=X" "value" |> ignore)
        )
        |> ignore

        // A valid key still works.
        Command.create "whatever" |> Command.env "KEY" "value" |> ignore

    [<Test>]
    member _.``Command.Env and EnvRemove reject an embedded NUL in the key, and Env rejects one in the value``() =
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.env (sprintf "KE%cY" '\000') "value"
                |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.env "KEY" (sprintf "val%cue" '\000')
                |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.envRemove (sprintf "KE%cY" '\000')
                |> ignore)
        )
        |> ignore

        // Valid Unicode keys/values still work.
        Command.create "whatever" |> Command.env "KÉY_日本語" "vàlüe-日本語" |> ignore

    [<Test>]
    member _.``a timeout larger than the timer range is treated as no timeout, not a throw``() : Task =
        task {
            // TimeSpan.MaxValue would overflow Task.Delay and throw synchronously, faulting the run and
            // orphaning the pumps; it must instead run as if no timeout were set.
            let cmd = (shell "exit 0") |> Command.timeout TimeSpan.MaxValue

            match! cmd.RunAsync() with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"expected the run to complete, got {err.Message}"
        }

    [<Test>]
    member _.``a second terminal verb on a consumed RunningProcess is refused, not a double-pump``() : Task =
        task {
            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error e -> Assert.Fail $"first OutputString failed: {e.Message}"
                | Ok _ ->
                    // A second buffered verb is refused with a clean error rather than racing a second
                    // reader on the (now torn-down) pipe.
                    match! running.OutputStringAsync() with
                    | Error(ProcessError.Unsupported _) -> ()
                    | Error other -> Assert.Fail $"expected Unsupported, got {other.Message}"
                    | Ok _ -> Assert.Fail "expected the second OutputString to be refused"

                    // A non-Result terminal verb refuses by throwing.
                    Assert.Throws<InvalidOperationException>(Action(fun () -> running.WaitAsync() |> ignore))
                    |> ignore

                do! (running :> IAsyncDisposable).DisposeAsync()
        }

    [<Test>]
    member _.``WaitAnyAsync after a buffered verb reuses its wait, not a second pipe reader``() : Task =
        task {
            let payload = "hello"
            let stdout = new GatedStream(Encoding.UTF8.GetBytes payload)

            let host: RunningHost =
                { Config = (Command.create "test").Config
                  Pid = None
                  Stdout = Some(stdout :> Stream)
                  Stderr = None
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> Task.FromResult(Outcome.Exited 0)
                  StdinError = fun () -> None
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  Teardown = fun () -> ValueTask() }

            use running = new RunningProcess(host)

            // Start the buffered verb; its stdout pump parks mid-`ReadAsync` (the gate is not released
            // yet) — proving it is genuinely in flight, not already finished, once WaitAnyAsync is
            // called next on the very same handle.
            let outputTask = running.OutputStringAsync()
            let! winner = Task.WhenAny(stdout.Entered, Task.Delay 5000)

            Assert.That(obj.ReferenceEquals(winner, stdout.Entered), Is.True, "the stdout pump never started reading")

            // The regression this guards against: WaitAnyAsync on the same handle, called while the
            // buffered verb's own pump is still parked mid-read, used to start a second, independent
            // drain of the very same stdout stream — two concurrent readers on one pipe. It must
            // instead reuse the buffered verb's own in-flight wait.
            let waitAnyTask = RunningProcess.WaitAnyAsync [| running |]

            stdout.Release()

            let! outputResult = outputTask
            let! waitAnyResult = waitAnyTask

            Assert.That(stdout.EverConcurrent, Is.False, "two readers were pumping the same stdout pipe at once")

            match outputResult with
            | Error e -> Assert.Fail $"OutputStringAsync failed: {e.Message}"
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo payload)

            Assert.That(waitAnyResult.Outcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member _.``a fault in the exit wait does not orphan a buffered verb's still-in-flight pump``() : Task =
        task {
            // `backend.Wait` is designed never to fault, but the composed exit wait (`waitWithTimeout`)
            // also runs `onTimeout`'s native kill calls, so it CAN throw. If `WaitAsync` read the exit
            // wait's result before its pumps were drained (the bug this guards against), the pump would
            // be left unobserved/in-flight while `reapGuard`'s teardown races disposing its stream.
            let stdout = new GatedStream(Encoding.UTF8.GetBytes "hello")

            let waitTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let host: RunningHost =
                { Config = (Command.create "test").Config
                  Pid = None
                  Stdout = Some(stdout :> Stream)
                  Stderr = None
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  StartTimeIdentity = None
                  Wait = fun () -> waitTcs.Task
                  StdinError = fun () -> None
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  Teardown = fun () -> ValueTask() }

            use running = new RunningProcess(host)

            let waitAsyncTask = running.WaitAsync()

            // Let the stdout drain genuinely start reading (park mid-`ReadAsync`) before faulting the
            // exit wait, so the pump is provably still in flight when the fault happens.
            let! enteredWinner = Task.WhenAny(stdout.Entered, Task.Delay 5000)

            Assert.That(
                obj.ReferenceEquals(enteredWinner, stdout.Entered),
                Is.True,
                "the stdout drain never started reading"
            )

            waitTcs.SetException(InvalidOperationException "exit wait faulted")

            // The verb must not surface the fault until its still-parked pump is drained — proving the
            // pump is awaited, not left orphaned, before the exception propagates.
            let! settledEarly = Task.WhenAny(waitAsyncTask :> Task, Task.Delay 200)

            Assert.That(
                obj.ReferenceEquals(settledEarly, waitAsyncTask),
                Is.False,
                "WaitAsync surfaced the exit-wait fault before draining its still-in-flight pump"
            )

            stdout.Release()

            try
                let! _ = waitAsyncTask
                Assert.Fail "expected WaitAsync to propagate the exit-wait fault"
            with :? InvalidOperationException as ex ->
                Assert.That(ex.Message, Is.EqualTo "exit wait faulted")
        }
        :> Task

    [<Test>]
    member _.``WaitAnyAsync claiming a fresh handle still refuses a later terminal verb``() : Task =
        task {
            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use running = running

                let! _ = RunningProcess.WaitAnyAsync [| running |]

                // The reverse order still refuses a terminal verb, unchanged: a `Fresh` ExitTask
                // claims the buffered slot itself, so a verb called afterwards races nothing.
                match! running.OutputStringAsync() with
                | Error(ProcessError.Unsupported _) -> ()
                | Error other -> Assert.Fail $"expected Unsupported, got {other.Message}"
                | Ok _ -> Assert.Fail "expected OutputStringAsync to be refused after WaitAnyAsync"
        }
        :> Task

    [<Test>]
    member _.``WaitAnyAsync rejects a null array``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () -> RunningProcess.WaitAnyAsync(Unchecked.defaultof<RunningProcess[]>) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``WaitAnyAsync rejects an empty array``() =
        Assert.Throws<ArgumentException>(Action(fun () -> RunningProcess.WaitAnyAsync [||] |> ignore))
        |> ignore

    [<Test>]
    member _.``WaitAnyAsync rejects an array with a null element``() : Task =
        task {
            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use running = running

                Assert.Throws<ArgumentException>(
                    Action(fun () ->
                        RunningProcess.WaitAnyAsync [| running; Unchecked.defaultof<RunningProcess> |]
                        |> ignore)
                )
                |> ignore
        }
        :> Task

    [<Test>]
    member _.``WaitAllAsync rejects a null array``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () -> RunningProcess.WaitAllAsync(Unchecked.defaultof<RunningProcess[]>) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``WaitAllAsync rejects an empty array``() =
        Assert.Throws<ArgumentException>(Action(fun () -> RunningProcess.WaitAllAsync [||] |> ignore))
        |> ignore

    [<Test>]
    member _.``WaitAllAsync rejects an array with a null element``() : Task =
        task {
            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use running = running

                Assert.Throws<ArgumentException>(
                    Action(fun () ->
                        RunningProcess.WaitAllAsync [| running; Unchecked.defaultof<RunningProcess> |]
                        |> ignore)
                )
                |> ignore
        }
        :> Task

    [<Test>]
    member _.``a pipeline honours the last stage's accepted exit codes``() : Task =
        task {
            // The last stage exits 3, but 3 is one of its accepted codes — pipefail must treat that as
            // success, which means the pipeline result must carry that stage's OkCodes (not a hardcoded
            // {0}).
            let pipeline = (shell "echo hi").Pipe((shell "exit 3") |> Command.okCodes [ 0; 3 ])

            match! pipeline.OutputStringAsync() with
            | Error e -> Assert.Fail $"pipeline errored: {e.Message}"
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Ok _ -> ()
                | Error e -> Assert.Fail $"expected the accepted exit code to pass, got {e.Message}"
        }

    [<Test>]
    member _.``a pipeline still fails on an unaccepted exit code``() : Task =
        task {
            let pipeline = (shell "echo hi").Pipe(shell "exit 4")

            match! pipeline.RunAsync() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected the pipeline to fail on the unaccepted exit 4"
        }

    [<Test>]
    member _.``a pipeline stage with a non-piped stdout does not deadlock``() : Task =
        task {
            // The producing stage's stdout is set to Inherit; the pipeline must still wire it to the
            // next stage's stdin (forcing Piped) instead of leaving `cat` blocked on an unfed stdin.
            // The timeout is a safety net — a regression would surface as TimedOut, not a hung suite.
            // `sort` is a cross-platform passthrough for single-line input (exists on Windows and
            // Unix); it emits once it sees EOF on stdin, so it doubles as a check that the pipeline
            // closes the wired-up stdin.
            let pipeline =
                ((shell "echo hello") |> Command.stdout StdioMode.Inherit).Pipe(shell "sort")
                |> Pipeline.timeout (TimeSpan.FromSeconds 10.0)

            match! pipeline.OutputStringAsync() with
            | Error e -> Assert.Fail $"pipeline errored: {e.Message}"
            | Ok result ->
                match result.Outcome with
                | Outcome.TimedOut -> Assert.Fail "pipeline deadlocked (timed out) — intermediate stdout was not wired"
                | _ -> Assert.That(result.Stdout.Trim(), Is.EqualTo "hello")
        }

    [<Test>]
    member _.``a missing FromFile stdin source surfaces as ProcessError.Stdin on a successful run``() : Task =
        task {
            // The source can't be opened, so the child gets empty stdin and still exits 0. That silent
            // failure must surface as `ProcessError.Stdin` rather than a spurious `Ok` — otherwise a
            // consumer never learns its input was dropped.
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a missing stdin source to surface as ProcessError.Stdin"
        }

    [<Test>]
    member _.``a louder non-zero exit wins over a stdin-source failure``() : Task =
        task {
            // The stdin source is missing (a genuine feed failure) but the process exits non-zero. The
            // "realer" failure wins: the outcome passes through as data, not `ProcessError.Stdin`.
            let cmd = (shell "exit 7") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))

            match! cmd.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 7 -> ()
                | other -> Assert.Fail $"expected exit 7 to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a non-zero exit must win over the stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"
        }

    [<Test>]
    member _.``a readable stdin source on a successful run never surfaces a stdin error``() : Task =
        task {
            // A valid source feeding a child that may close stdin early (a broken pipe) must never be
            // misreported as `ProcessError.Stdin` — only a genuine source-acquisition failure is.
            let cmd =
                (shell "exit 0")
                |> Command.stdin (Stdin.FromString "payload the child may ignore")

            match! cmd.OutputStringAsync() with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"a readable stdin source must not error, got {err.Message}"
        }

    [<Test>]
    member _.``a FromLines source that throws mid-iteration surfaces as ProcessError.Stdin``() : Task =
        task {
            // The generator raises an arbitrary exception (not one of the old FileNotFoundException /
            // DirectoryNotFoundException / UnauthorizedAccessException allow-list) partway through
            // iteration. `sort` keeps reading until EOF, so the first line is genuinely written before
            // the generator faults — this must surface as `ProcessError.Stdin`, not truncate the
            // child's input and pass through as a silent success.
            let source =
                seq {
                    yield "first line"
                    failwith "boom mid-iteration"
                }

            let cmd = (shell "sort") |> Command.stdin (Stdin.FromLines source)

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a mid-iteration source fault to surface as ProcessError.Stdin"
        }

    [<Test>]
    member _.``a pipeline surfaces a missing first-stage stdin source as ProcessError.Stdin``() : Task =
        task {
            // The first stage's stdin source can't be read; the pipeline otherwise succeeds (both stages
            // exit 0), so it must surface `ProcessError.Stdin` — uniformly with a single command — rather
            // than silently feeding stage 0 empty input.
            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))).Pipe(shell "sort")

            match! pipeline.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a missing first-stage stdin source to surface as ProcessError.Stdin"
        }

    [<Test>]
    member _.``a pipefail failure wins over a first-stage stdin-source failure``() : Task =
        task {
            // The first stage's stdin source is missing, but the pipeline fails pipefail (last stage exits
            // 4, unaccepted). That louder failure wins: the outcome passes through as data, not Stdin.
            let pipeline =
                ((shell "exit 0") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))).Pipe(shell "exit 4")

            match! pipeline.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 4 -> ()
                | other -> Assert.Fail $"expected pipefail exit 4 to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a pipefail failure must win over the stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"
        }

    // --- T-066: atomic claim/transition state machine, and fault-aware terminal teardown ---

    [<Test>]
    member _.``concurrent buffered verbs on one handle resolve to a single winner``() : Task =
        task {
            // A synchronous barrier releases many threads onto the SAME handle at once; the atomic
            // `claimBuffered` must let exactly one win and refuse every other with `Unsupported`,
            // rather than several observing `Fresh` and double-pumping the one stdout pipe.
            let attempts = 24
            let config = (Command.create "test").Config
            let stdout = new MemoryStream(Encoding.UTF8.GetBytes "hello\n") :> Stream

            let host =
                { baseHost config with
                    Stdout = Some stdout }

            use running = new RunningProcess(host)
            let results = Array.zeroCreate<int> attempts
            use ready = new CountdownEvent(attempts)
            use gate = new ManualResetEventSlim(false)

            let threads =
                [| for i in 0 .. attempts - 1 do
                       let t =
                           Thread(
                               ThreadStart(fun () ->
                                   ready.Signal() |> ignore
                                   gate.Wait()

                                   results[i] <-
                                       match running.OutputStringAsync().GetAwaiter().GetResult() with
                                       | Ok _ -> 1
                                       | Error(ProcessError.Unsupported _) -> 0
                                       | Error _ -> -1)
                           )

                       t.IsBackground <- true
                       t.Start()
                       yield t |]

            ready.Wait()
            gate.Set()

            for t in threads do
                t.Join()

            Assert.That(results |> Array.contains -1, Is.False, "a verb failed with an unexpected error")

            Assert.That(
                results |> Array.filter (fun r -> r = 1) |> Array.length,
                Is.EqualTo 1,
                "exactly one buffered verb must win the claim"
            )

            Assert.That(
                results |> Array.filter (fun r -> r = 0) |> Array.length,
                Is.EqualTo(attempts - 1),
                "every loser must be refused with Unsupported"
            )
        }
        :> Task

    [<Test>]
    member _.``concurrent TakeStdin hands out the interactive stdin at most once``() : Task =
        task {
            // Two concurrent `TakeStdin` calls must not both observe `not stdinTaken` and hand out the
            // same stdin stream twice — the atomic guard admits exactly one.
            let attempts = 24
            let config = (Command.create "test" |> Command.keepStdinOpen).Config
            let stdin = new MemoryStream() :> Stream

            let host =
                { baseHost config with
                    Stdin = Some stdin }

            use running = new RunningProcess(host)
            let granted = Array.zeroCreate<bool> attempts
            use ready = new CountdownEvent(attempts)
            use gate = new ManualResetEventSlim(false)

            let threads =
                [| for i in 0 .. attempts - 1 do
                       let t =
                           Thread(
                               ThreadStart(fun () ->
                                   ready.Signal() |> ignore
                                   gate.Wait()
                                   granted[i] <- (running.TakeStdin()).IsSome)
                           )

                       t.IsBackground <- true
                       t.Start()
                       yield t |]

            ready.Wait()
            gate.Set()

            for t in threads do
                t.Join()

            Assert.That(
                granted |> Array.filter id |> Array.length,
                Is.EqualTo 1,
                "TakeStdin must hand out the stdin stream to exactly one concurrent caller"
            )
        }
        :> Task

    [<Test>]
    member _.``TakeStdin waits for the source feeder before handing over a KeepStdinOpen pipe``() : Task =
        task {
            // T-123: with `Stdin(source)` + `KeepStdinOpen`, `TakeStdin` must not hand the caller the pipe
            // until the background feeder has finished draining the source — otherwise the feeder and the
            // caller would both write the same pipe. Deterministic ORDER proof (no timing guesswork): the
            // gated source parks BEFORE writing, so `TakeStdin` must block until we release it and the feed
            // completes. Then (a) the feed is provably complete the instant `TakeStdin` returns, and (b) the
            // caller's bytes land strictly AFTER the source's on the shared pipe.
            let gated = GatedStdinAsyncLines "SRC"

            // A source + `KeepStdinOpen` config. The `Stdin` metadata only marks the run as source-fed (this
            // synthetic host never spawns, so it is never enumerated); the real feed below is what
            // `StdinFeedComplete` waits on.
            let config =
                (Command.create "test"
                 |> Command.stdin (Stdin.FromString "x")
                 |> Command.keepStdinOpen)
                    .Config

            let pipe = new MemoryStream()

            let feeder =
                Pump.feedStdinSource (Some(pipe :> Stream)) (Some(Stdin.FromAsyncLines gated)) true

            let host =
                { baseHost config with
                    Stdin = Some(pipe :> Stream)
                    // Exactly how `ProcessGroup` wires it: block on the feed task (which never faults).
                    StdinFeedComplete = fun () -> feeder.Task.GetAwaiter().GetResult() |> ignore }

            use running = new RunningProcess(host)

            let mutable takenIsSome = false
            let mutable feedCompleteAtReturn = false

            let taker =
                Thread(
                    ThreadStart(fun () ->
                        match running.TakeStdin() with
                        | Some stdin ->
                            takenIsSome <- true
                            // Recorded the instant TakeStdin returns: for the wait to hold, the feed MUST be
                            // complete here.
                            feedCompleteAtReturn <- feeder.Task.IsCompleted
                            (stdin.WriteAsync(Encoding.UTF8.GetBytes "CALLER")).GetAwaiter().GetResult()
                        | None -> ())
                )

            taker.IsBackground <- true
            taker.Start()

            // Wait — on the source's own `Parked` signal, not a delay — until the feed is parked mid-source.
            // `TakeStdin` is now blocked in `StdinFeedComplete`, and the feed is provably NOT complete.
            let! parked = Task.WhenAny(gated.Parked, Task.Delay 5000)
            Assert.That(parked, Is.SameAs gated.Parked, "the feed never parked in the source")
            Assert.That(feeder.Task.IsCompleted, Is.False, "the feed must not complete while the source is parked")

            // Release the source: the feed writes "SRC\n" and completes -> TakeStdin unblocks and returns.
            gated.Release()
            taker.Join()

            Assert.That(
                takenIsSome,
                Is.True,
                "TakeStdin must hand out the interactive pipe for a Stdin(source) + KeepStdinOpen run"
            )

            Assert.That(
                feedCompleteAtReturn,
                Is.True,
                "TakeStdin must not return until the source feed has finished (single writer at a time)"
            )

            Assert.That(
                Encoding.UTF8.GetString(pipe.ToArray()),
                Is.EqualTo "SRC\nCALLER",
                "the source's bytes must precede the caller's on the shared pipe"
            )
        }
        :> Task

    [<Test>]
    member _.``TakeStdin does not deadlock when called from a single-threaded SynchronizationContext``() : Task =
        task {
            // T-123 / R-01: `TakeStdin` blocks the caller on the background stdin feed
            // (`StdinFeedComplete` -> `feeder.Task.GetAwaiter().GetResult()`). That feed MUST run on the
            // thread pool (`Pump.feedStdin` is a `backgroundTask`) and never capture the caller's
            // `SynchronizationContext`. Otherwise a consumer on a single-threaded UI context (WPF/WinForms/
            // classic ASP.NET) that calls `TakeStdin` from that same thread — while a still-feeding source
            // is parked — deadlocks: the feed's post-`await` continuation would be posted to the one thread
            // already blocked in `GetResult`, and could never run.
            //
            // Deterministic order proof (no timing guesswork): park the source, block `TakeStdin` on the UI
            // thread, then release the source from a POOL thread. The feed must complete on the pool and
            // `TakeStdin` must return; on the regression it would sit forever. A bounded wait converts that
            // hang into a clean failure rather than stalling the whole suite.
            let gated = GatedStdinAsyncLines "SRC"
            let syncContext = QueueingSyncContext()
            let pipe = new MemoryStream()

            let config =
                (Command.create "test"
                 |> Command.stdin (Stdin.FromString "x")
                 |> Command.keepStdinOpen)
                    .Config

            use returned = new ManualResetEventSlim(false)
            let mutable takenIsSome = false
            let mutable startupError: exn option = None
            let mutable running: RunningProcess = Unchecked.defaultof<_>

            // The "UI thread": it installs the single-threaded context, starts the feed UNDER that context
            // (so a regression would capture it), builds the run, and blocks in `TakeStdin` from the SAME
            // thread. It never pumps the context (a blocked UI thread cannot), so any continuation the feed
            // posted there would never run.
            let uiThread =
                Thread(
                    ThreadStart(fun () ->
                        try
                            SynchronizationContext.SetSynchronizationContext syncContext

                            let feeder =
                                Pump.feedStdinSource (Some(pipe :> Stream)) (Some(Stdin.FromAsyncLines gated)) true

                            let host =
                                { baseHost config with
                                    Stdin = Some(pipe :> Stream)
                                    StdinFeedComplete = fun () -> feeder.Task.GetAwaiter().GetResult() |> ignore }

                            let rp = new RunningProcess(host)
                            running <- rp
                            takenIsSome <- (rp.TakeStdin()).IsSome
                        with ex ->
                            startupError <- Some ex

                        returned.Set())
                )

            uiThread.IsBackground <- true
            uiThread.Start()

            // Wait — on the source's own `Parked` signal, not a delay — until the feed is parked mid-source.
            // `TakeStdin` is now blocked on the UI thread; the feed (fixed) is running on the pool.
            let! parked = Task.WhenAny(gated.Parked, Task.Delay 5000)
            Assert.That(parked, Is.SameAs gated.Parked, "the feed never parked in the source")

            // Release from THIS (pool) thread: the fixed feed completes on the pool and `TakeStdin` returns.
            gated.Release()

            let signalled = returned.Wait(TimeSpan.FromSeconds 15.0)
            uiThread.Join(TimeSpan.FromSeconds 5.0) |> ignore

            // Dispose off the (test-blocked) UI thread, from the pool — `RunningProcess` is `IAsyncDisposable`.
            if not (obj.ReferenceEquals(running, null)) then
                do! (running :> IAsyncDisposable).DisposeAsync().AsTask()

            match startupError with
            | Some ex -> raise ex
            | None -> ()

            Assert.That(
                signalled,
                Is.True,
                "TakeStdin deadlocked: the stdin feed captured the caller's single-threaded SynchronizationContext instead of running on the thread pool"
            )

            Assert.That(
                takenIsSome,
                Is.True,
                "TakeStdin must hand out the interactive pipe once the source feed completes"
            )

            Assert.That(
                syncContext.Posted,
                Is.EqualTo 0,
                "the stdin feed must not post any continuation to the caller's SynchronizationContext"
            )
        }
        :> Task

    [<Test>]
    member _.``concurrent ExitTask access builds the exit wait exactly once``() : Task =
        task {
            // Many threads race `ExitTask` on a fresh handle behind a barrier. The memoization must be
            // atomic: every caller receives the one same task object, and only one drain ever reads the
            // pipe (a `GatedStream` proves no two readers were ever in flight at once).
            let attempts = 24
            let config = (Command.create "test").Config
            let stdout = new GatedStream(Encoding.UTF8.GetBytes "hello")

            let host =
                { baseHost config with
                    Stdout = Some(stdout :> Stream) }

            use running = new RunningProcess(host)
            let results = Array.zeroCreate<Task<Outcome>> attempts
            use ready = new CountdownEvent(attempts)
            use gate = new ManualResetEventSlim(false)

            let threads =
                [| for i in 0 .. attempts - 1 do
                       let t =
                           Thread(
                               ThreadStart(fun () ->
                                   ready.Signal() |> ignore
                                   gate.Wait()
                                   results[i] <- running.ExitTask)
                           )

                       t.IsBackground <- true
                       t.Start()
                       yield t |]

            ready.Wait()
            gate.Set()

            for t in threads do
                t.Join()

            Assert.That(
                results |> Array.forall (fun t -> obj.ReferenceEquals(t, results[0])),
                Is.True,
                "every concurrent ExitTask must return the one memoized task"
            )

            // Let the single drain finish so nothing is left pending, then confirm only one reader ran.
            stdout.Release()
            let! _ = results[0]
            Assert.That(stdout.EverConcurrent, Is.False, "two readers pumped the same stdout pipe")
        }
        :> Task

    [<Test>]
    member _.``a faulting output pump kills the tree instead of hanging on a wedged exit wait``() : Task =
        task {
            // The child never exits on its own — only a kill completes `Wait`, exactly like a verbose
            // child wedged writing to a full pipe once its faulted pump stopped draining. A throwing
            // OnStdoutLine handler must therefore kill the tree so the exit wait concludes and the run
            // is reaped in bounded time WITHOUT any configured timeout — surfacing the ORIGINAL fault.
            let mutable teardowns = 0

            let killTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let stdout =
                new MemoryStream(Encoding.UTF8.GetBytes "line1\nline2\nline3\n") :> Stream

            let config =
                (Command.create "test"
                 |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom")))
                    .Config

            let host =
                { baseHost config with
                    Stdout = Some stdout
                    Wait = fun () -> killTcs.Task
                    StartKill = fun () -> killTcs.TrySetResult(Outcome.Exited 137) |> ignore
                    Teardown =
                        fun () ->
                            teardowns <- teardowns + 1
                            ValueTask() }

            use running = new RunningProcess(host)
            let verb = running.OutputStringAsync() :> Task
            let! winner = Task.WhenAny(verb, Task.Delay 10000)

            Assert.That(
                obj.ReferenceEquals(winner, verb),
                Is.True,
                "the verb hung — a faulting pump did not kill the wedged exit wait"
            )

            let mutable caught = None

            try
                do! verb
            with ex ->
                caught <- Some ex.Message

            Assert.That(
                caught,
                Is.EqualTo(Some "boom"),
                "the original handler fault must surface, not a secondary closed-pipe error"
            )

            Assert.That(teardowns, Is.GreaterThanOrEqualTo 1, "the faulted verb must still reap the tree")
        }
        :> Task

    [<Test>]
    member _.``a faulting stdout tee kills the tree on the streaming path instead of hanging``() : Task =
        task {
            // The same wedge, on the streaming terminal path (`FinishAsync`) and via a faulting tee
            // rather than a line handler: the tee fault must kill the tree so `streamOutcome`'s exit
            // wait concludes and `FinishAsync` surfaces the original tee fault in bounded time.
            let mutable teardowns = 0

            let killTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let stdout = new MemoryStream(Encoding.UTF8.GetBytes "line1\nline2\n") :> Stream

            let config =
                (Command.create "test" |> Command.stdoutTee (new ThrowingTeeStream() :> Stream)).Config

            let host =
                { baseHost config with
                    Stdout = Some stdout
                    Wait = fun () -> killTcs.Task
                    StartKill = fun () -> killTcs.TrySetResult(Outcome.Exited 137) |> ignore
                    Teardown =
                        fun () ->
                            teardowns <- teardowns + 1
                            ValueTask() }

            use running = new RunningProcess(host)
            let finish = running.FinishAsync() :> Task
            let! winner = Task.WhenAny(finish, Task.Delay 10000)

            Assert.That(
                obj.ReferenceEquals(winner, finish),
                Is.True,
                "FinishAsync hung — a faulting tee did not kill the wedged exit wait"
            )

            let mutable caught = None

            try
                do! finish
            with ex ->
                caught <- Some ex.Message

            Assert.That(
                caught,
                Is.EqualTo(Some "tee-boom"),
                "the original tee fault must surface, not a secondary closed-pipe error"
            )

            Assert.That(teardowns, Is.GreaterThanOrEqualTo 1, "the faulted streaming verb must still reap the tree")
        }
        :> Task

    // --- T-082: an unreadable cgroup.procs must not look like an empty (drained) group ---

    [<Test>]
    member _.``an unreadable cgroup.procs is a read failure, not an empty (drained) group``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "cgroup.procs fail-safe reads are Linux-only"

            // A throwaway directory standing in for a cgroup, with its cgroup.procs made unreadable
            // (chmod 000) — reproduces an EACCES read failure without needing a real cgroup v2 mount.
            let dir =
                Path.Combine(Path.GetTempPath(), $"processkit-cgroup-failsafe-{Guid.NewGuid():N}")

            Directory.CreateDirectory dir |> ignore
            let procsPath = Path.Combine(dir, "cgroup.procs")
            File.WriteAllText(procsPath, "")
            File.SetUnixFileMode(procsPath, UnixFileMode.None)

            try
                match Native.Cgroup.cgroupMembers dir with
                | Ok _ ->
                    // Running with a privilege that reads past chmod 000 (e.g. root) — the fail-safe
                    // path under test is not reachable in this environment.
                    Assert.Ignore
                        "this environment can read past chmod 000 (likely running as root) — the fail-safe path under test is not reachable here"
                | Error _ ->
                    // The read genuinely failed — proceed to exercise every fail-safe decision built on it.
                    ()

                // The graceful-teardown "alive" (not-yet-drained) check must treat the unreadable member
                // list as unknown, not empty — this is exactly what keeps `GracefulKillTree`'s poll loop
                // escalating instead of reporting the tree already gone.
                Assert.That(
                    Native.Cgroup.cgroupAlive dir,
                    Is.True,
                    "an unreadable cgroup.procs must not be treated as an empty (drained) group"
                )

                // `CgroupBackend.Members`/`Stats` must propagate the read failure as an honest `Error`,
                // never a fabricated empty member list / zero-active-process stats snapshot.
                let backend: IContainmentBackend = CgroupBackend dir

                match backend.Members() with
                | Error(ProcessError.Io _) -> ()
                | Error other -> Assert.Fail $"expected ProcessError.Io from Members, got {other}"
                | Ok members -> Assert.Fail $"expected Members to surface the read failure, got Ok {members}"

                match backend.Stats() with
                | Error(ProcessError.Io _) -> ()
                | Error other -> Assert.Fail $"expected ProcessError.Io from Stats, got {other}"
                | Ok stats ->
                    Assert.Fail $"expected Stats to surface the read failure, got Ok active={stats.ActiveProcessCount}"

                // Block new file creation in `dir` too, so `killCgroup`'s `cgroup.kill` write (which would
                // otherwise trivially succeed against a writable temp directory) fails and it falls
                // through to the legacy per-pid SIGKILL sweep — the bounded retry loop this fix keeps
                // running to its full iteration budget (50 * 2ms) instead of exiting on the first failed
                // read.
                File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

                try
                    let stopwatch = Stopwatch.StartNew()
                    Native.Cgroup.killCgroup dir
                    stopwatch.Stop()

                    Assert.That(
                        stopwatch.Elapsed,
                        Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds 90.0),
                        "killCgroup's bounded sweep must not exit early on a persistent cgroup.procs read failure"
                    )
                finally
                    // Restore write access before the outer cleanup deletes the directory.
                    File.SetUnixFileMode(
                        dir,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )
            finally
                (try
                    File.SetUnixFileMode(procsPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                 with _ ->
                     // Best-effort restore before delete; a failure here is not actionable in test cleanup.
                     ())

                (try
                    Directory.Delete(dir, true)
                 with _ ->
                     // Best-effort cleanup; a leftover temp dir does not fail the test.
                     ())
        }
        :> Task

    [<Test>]
    member _.``cgroup suspend and resume surface freeze write failures``() =
        // A missing directory makes the cgroup.freeze write fail without requiring a writable cgroup v2
        // hierarchy. The backend must expose that native failure rather than report a false success.
        let missingCgroup =
            Path.Combine(Path.GetTempPath(), $"processkit-missing-freeze-{Guid.NewGuid():N}")

        let backend: IContainmentBackend = CgroupBackend missingCgroup

        let assertIo operation result =
            match result with
            | Error(ProcessError.Io _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Io from {operation}, got {other}"
            | Ok() -> Assert.Fail $"expected {operation} to surface the cgroup.freeze write failure"

        assertIo "Suspend" (backend.Suspend())
        assertIo "Resume" (backend.Resume())

    // --- T-197: a concurrent StopAsync teardown during an in-flight buffered verb must not fault the
    // verb with a false ProcessError.Io (the supervision path drives exactly this: `monitorLiveness`
    // fires `running.StopAsync grace` while `captureIncarnation`'s `OutputStringAsync` is in flight), yet
    // a GENUINE mid-run read fault (no teardown) must still surface as ProcessError.Io — T-087. ---

    // Build a synthetic handle whose `Wait` resolves only when `waitTcs` is set and whose `Teardown`
    // disposes `stdout` (the pipe close a real reap performs), plus the stream itself. Used by the three
    // teardown-race tests below; the stream is disposed through `Teardown`, so `disposalCts` is cancelled
    // first and the buffered pump reads that as this handle's own teardown.
    member private _.RaceHost(stdout: ParkThenFaultOnDisposeStream, waitTcs: TaskCompletionSource<Outcome>) =
        { baseHost (Command.create "test").Config with
            Stdout = Some(stdout :> Stream)
            Wait = fun () -> waitTcs.Task
            GracefulKill = fun _ -> Task.CompletedTask
            Teardown =
                fun () ->
                    (stdout :> IDisposable).Dispose()
                    ValueTask() }

    [<Test>]
    member this.``a concurrent StopAsync during OutputStringAsync does not fault with a false ProcessError.Io``
        ()
        : Task =
        task {
            let stdout =
                new ParkThenFaultOnDisposeStream(Encoding.UTF8.GetBytes "captured-tail\n")

            let waitTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = new RunningProcess(this.RaceHost(stdout, waitTcs))

            // The buffered verb's stdout pump serves the first line, then parks mid-read on the tail —
            // provably still in flight when StopAsync fires next on the very same handle.
            let outputTask = running.OutputStringAsync()
            let! parked = Task.WhenAny(stdout.ParkedOnTail, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(parked, stdout.ParkedOnTail), Is.True, "the stdout pump never parked")

            // StopAsync reuses the shared exit wait; once it resolves, StopAsync's reapGuard tears down —
            // disposing the pipe out from under the still-reading pump. That dispose used to be
            // reclassified as a genuine ProcessError.Io, faulting the verb.
            let stopTask = running.StopAsync TimeSpan.Zero
            waitTcs.SetResult(Outcome.Exited 0)

            let! outputResult = outputTask
            let! stopOutcome = stopTask

            match outputResult with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "captured-tail", "the captured output was lost")
            | Error err -> Assert.Fail $"expected an honest capture, got a false fault: {err.Message}"

            Assert.That(stopOutcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member this.``a concurrent StopAsync during WaitAsync does not fault with a false ProcessError.Io``() : Task =
        task {
            let stdout = new ParkThenFaultOnDisposeStream(Encoding.UTF8.GetBytes "tail\n")

            let waitTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = new RunningProcess(this.RaceHost(stdout, waitTcs))

            let waitTask = running.WaitAsync()
            let! parked = Task.WhenAny(stdout.ParkedOnTail, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(parked, stdout.ParkedOnTail), Is.True, "the stdout drain never parked")

            let stopTask = running.StopAsync TimeSpan.Zero
            waitTcs.SetResult(Outcome.Exited 0)

            // WaitAsync throws on a genuine pump fault; a clean Outcome proves the race was not misreported.
            let! waitOutcome = waitTask
            let! stopOutcome = stopTask
            Assert.That(waitOutcome, Is.EqualTo(Outcome.Exited 0))
            Assert.That(stopOutcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member this.``a concurrent StopAsync during OutputBytesAsync does not fault``() : Task =
        task {
            let stdout = new ParkThenFaultOnDisposeStream(Encoding.UTF8.GetBytes "tail-bytes")

            let waitTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            use running = new RunningProcess(this.RaceHost(stdout, waitTcs))

            let outputTask = running.OutputBytesAsync()
            let! parked = Task.WhenAny(stdout.ParkedOnTail, Task.Delay 5000)

            Assert.That(
                obj.ReferenceEquals(parked, stdout.ParkedOnTail),
                Is.True,
                "the raw stdout capture never parked"
            )

            let stopTask = running.StopAsync TimeSpan.Zero
            waitTcs.SetResult(Outcome.Exited 0)

            let! outputResult = outputTask
            let! stopOutcome = stopTask

            match outputResult with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"expected an honest capture, got a false fault: {err.Message}"

            Assert.That(stopOutcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member _.``a genuine mid-run read fault (no teardown) still surfaces as ProcessError.Io``() : Task =
        task {
            // The other side of the classification (T-087): the SAME stream fault, but the stream is
            // disposed DIRECTLY (not through the handle's teardown), so `disposalCts` stays un-cancelled
            // and the buffered pump must report the read failure honestly rather than swallow it.
            let stdout = new ParkThenFaultOnDisposeStream(Encoding.UTF8.GetBytes "line1\n")

            let waitTcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let host =
                { baseHost (Command.create "test").Config with
                    Stdout = Some(stdout :> Stream)
                    Wait = fun () -> waitTcs.Task
                    // Teardown does NOT dispose the stream here — the test triggers the fault itself below,
                    // outside teardown, so it is a genuine external read fault, not this handle's own race.
                    Teardown = fun () -> ValueTask() }

            use running = new RunningProcess(host)

            let outputTask = running.OutputStringAsync()
            let! parked = Task.WhenAny(stdout.ParkedOnTail, Task.Delay 5000)
            Assert.That(obj.ReferenceEquals(parked, stdout.ParkedOnTail), Is.True, "the stdout pump never parked")

            // Resolve the exit wait so the verb reaches the pump await, then fault the read OUTSIDE any
            // teardown (disposalCts un-cancelled) — a genuine mid-run read failure.
            waitTcs.SetResult(Outcome.Exited 0)
            (stdout :> IDisposable).Dispose()

            try
                let! _ = outputTask
                Assert.Fail "expected the genuine read fault to surface"
            with :? ProcessException as pe ->
                match pe.Error with
                | ProcessError.Io _ -> ()
                | other -> Assert.Fail $"expected ProcessError.Io, got {other}"
        }
        :> Task
