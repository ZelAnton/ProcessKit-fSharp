namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
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

/// A stdin transport double for the T-354 end-of-input tests: an in-memory pipe that records every byte
/// AND signals the moment its end of input was delivered, so the assertions never poll or guess at a
/// delivery that a terminal verb performs off its own thread. Which signal a transport uses differs — a
/// plain pipe is CLOSED, a ConPTY session's host-input pipe receives a written gesture over a pipe that
/// deliberately stays open — so both are exposed, alongside the close count that makes "exactly one owner
/// of the pipe" directly checkable.
type private EndOfInputPipe() =
    inherit MemoryStream()

    let closed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let written =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closes = 0

    /// Completes the first time this pipe is CLOSED — how a plain stdin pipe delivers end of input.
    member _.Closed: Task = closed.Task

    /// Completes the first time anything is WRITTEN to this pipe. The ConPTY writer sends its whole
    /// end-of-input gesture in a single write, so for that transport the first write IS the delivery.
    member _.FirstWrite: Task = written.Task

    /// How many times this pipe has actually been closed.
    member _.Closes = Volatile.Read(&closes)

    override _.Write(buffer: byte[], offset: int, count: int) =
        base.Write(buffer, offset, count)
        written.TrySetResult() |> ignore

    override _.Write(buffer: ReadOnlySpan<byte>) =
        base.Write buffer
        written.TrySetResult() |> ignore

    override _.Dispose(disposing) =
        Interlocked.Increment(&closes) |> ignore
        closed.TrySetResult() |> ignore
        base.Dispose disposing

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

/// A decorator of exactly the kind `DelegatingProcessRunner`'s own doc names — "a wrapper: logging,
/// **retry**, metrics, fault injection" — that drives its inner runner TWICE with the very `Command` it
/// was handed. Under a retry budget that command carries the verb layer's run-level hold on a one-shot
/// stdin payload, so the second call is a second launch bearing a hold it did not take: it must be
/// refused before it can create a child, not waved through onto a payload the first child drained.
/// `concurrent` picks whether the two calls race or follow one another — both must end with exactly one
/// child, so the guard cannot be beaten by check-then-act timing either.
type private DoubleCallingRunner(inner: IProcessRunner, concurrent: bool) =
    inherit DelegatingProcessRunner(inner)

    let outcomes = ResizeArray<Result<ProcessResult<string>, ProcessError>>()

    /// Both calls' outcomes (completion order for the concurrent variant, call order otherwise).
    member _.Outcomes = List.ofSeq outcomes

    override this.CaptureStringAsync(command, cancellationToken) =
        task {
            if concurrent then
                let first = this.Inner.CaptureStringAsync(command, cancellationToken)
                let second = this.Inner.CaptureStringAsync(command, cancellationToken)
                let! both = Task.WhenAll [| first; second |]
                outcomes.AddRange both
            else
                let! first = this.Inner.CaptureStringAsync(command, cancellationToken)
                outcomes.Add first
                let! second = this.Inner.CaptureStringAsync(command, cancellationToken)
                outcomes.Add second

            // Surface the LAST outcome as the decorated verb's result, as a retry wrapper would.
            return outcomes[outcomes.Count - 1]
        }

/// A runner hook that KEEPS the `Command` it is handed and spawns nothing — the seam every user double
/// occupies (`ScriptedRunner.When(Func<Command,bool>, …)`, a hand-written `IProcessRunner`), here over a
/// `DryRunRunner` preview so the run it stands in for really does start no child. Under a retry budget
/// the kept value carries that run's hold on a one-shot payload, so it is a stamped command that
/// outlives the run which stamped it — starting it later must be checked like any other launch.
type private CommandKeepingRunner(inner: IProcessRunner) =
    inherit DelegatingProcessRunner(inner)

    let mutable seen: Command option = None

    /// The command this hook was handed, stamp and all.
    member _.Seen = seen

    override this.CaptureStringAsync(command, cancellationToken) =
        seen <- Some command
        this.Inner.CaptureStringAsync(command, cancellationToken)

/// Regression tests for the correctness & robustness fixes: timeout validation/clamping, the
/// single-consumption guard on `RunningProcess`, pipeline per-stage `OkCodes`, and pipeline wiring
/// of a stage whose stdout was set non-piped.
[<TestFixture>]
type CorrectnessBugTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let withSyntheticCgroup (freezeState: string option) (writeHook: string -> string -> unit) action =
        // The cgroup path is exercised through plain file I/O with an empty cgroup.procs, so no Linux
        // syscall is reached. This keeps the fault-injection contract executable on every test runner.
        let dir =
            Path.Combine(Path.GetTempPath(), $"processkit-cgroup-kill-{Guid.NewGuid():N}")

        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, "cgroup.procs"), "")

        freezeState
        |> Option.iter (fun state -> File.WriteAllText(Path.Combine(dir, "cgroup.freeze"), state))

        let originalHook = Native.Cgroup.killCgroupWriteTestHook
        Native.Cgroup.killCgroupWriteTestHook <- Some writeHook

        try
            action dir
        finally
            Native.Cgroup.killCgroupWriteTestHook <- originalHook

            try
                Directory.Delete(dir, true)
            with
            | :? DirectoryNotFoundException
            | :? IOException
            | :? UnauthorizedAccessException ->
                // Best-effort cleanup; the synthetic cgroup is not a production resource.
                ()

    /// A stand-in cgroup directory with NO files in it (T-363), plus a `killCgroupWriteTestHook` that
    /// refuses every cgroup control write so nothing teardown does can create one — which is what makes
    /// the post-kill directory reclaim observable on an ordinary filesystem, where (unlike cgroupfs) a
    /// directory is not removed together with the files in it. `budget` shortens the bounded drain wait
    /// through its own seam. Both process-wide seams are restored afterwards; like every other user of
    /// them, these tests run sequentially.
    let withReclaimableCgroup (budget: TimeSpan) (body: string -> unit) =
        let dir =
            Path.Combine(Path.GetTempPath(), $"processkit-cgroup-reclaim-{Guid.NewGuid():N}")

        Directory.CreateDirectory dir |> ignore
        let originalHook = Native.Cgroup.killCgroupWriteTestHook
        let originalBudget = Native.Cgroup.drainBudgetOverrideForTests

        Native.Cgroup.killCgroupWriteTestHook <-
            Some(fun _file _content -> raise (IOException "this stand-in cgroup takes no control writes"))

        Native.Cgroup.drainBudgetOverrideForTests <- Some budget

        try
            body dir
        finally
            Native.Cgroup.killCgroupWriteTestHook <- originalHook
            Native.Cgroup.drainBudgetOverrideForTests <- originalBudget

            try
                Directory.Delete(dir, true)
            with
            | :? DirectoryNotFoundException
            | :? IOException
            | :? UnauthorizedAccessException ->
                // Best-effort cleanup; a leftover stand-in directory must not fail the test.
                ()

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
          StdinError = RunningHost.NoStdinError
          StdinFeedComplete = ignore
          StartKill = ignore
          Signal = fun _ -> Ok()
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          TreeStats = None
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
        let pipeline = (shell "echo a").Pipe(shell (if isWindows then "more" else "cat"))

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
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
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
                  StdinError = RunningHost.NoStdinError
                  StdinFeedComplete = ignore
                  StartKill = ignore
                  Signal = fun _ -> Ok()
                  GracefulKill = fun _ -> Task.CompletedTask
                  ResizePty = None
                  TreeStats = None
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
    member _.``a pipeline stage with a non-piped stdout is rejected before it can deadlock``() =
        // A pipeline owns every stage's stdout for wiring/capture. Refusing an incompatible explicit
        // destination at the builder boundary is safer than silently overwriting it or risking an
        // unfed downstream stdin.
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                ((shell "echo hello") |> Command.stdout StdioMode.Inherit).Pipe(shell "sort")
                |> ignore)
        )
        |> ignore

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

    // --- T-322: Stdin.FromBytes must take a defensive copy at the API boundary --------------------

    [<Test>]
    member _.``mutating the source array after Stdin.FromBytes does not change what the child receives``() : Task =
        task {
            let bytes = [| 65uy; 66uy; 67uy |]

            let cmd =
                (shell (if isWindows then "more" else "cat"))
                |> Command.stdin (Stdin.FromBytes bytes)

            // Mutate the caller's array AFTER the command was built (and before it runs) — the already
            // built `Command`/`Stdin` must not alias it, so the child must still see the original bytes.
            bytes[0] <- 90uy

            match! cmd.OutputBytesAsync() with
            | Error e -> Assert.Fail $"expected the run to complete, got {e.Message}"
            | Ok result ->
                // Windows `more` may append a trailing CRLF to its output, so compare only the leading
                // bytes the child actually echoed back from the fed content.
                Assert.That(
                    result.Stdout |> Array.truncate 3,
                    Is.EqualTo<byte>([| 65uy; 66uy; 67uy |]),
                    "the child observed the caller's post-construction mutation of the source array"
                )
        }
        :> Task

    [<Test>]
    member _.``two sequential Stdin.FromBytes attempts read the identical unmutated byte snapshot``() : Task =
        task {
            let original = [| 1uy; 2uy; 3uy; 4uy |]
            let stdin = Stdin.FromBytes original

            // Mutate the caller's array after building `stdin`, then run it twice (simulating two retry
            // attempts sharing the same `Stdin` value) — both attempts must read the same, original
            // snapshot taken at `FromBytes`, not the caller's mutated array and not each other's copy.
            original[0] <- 99uy

            let cmd = (shell (if isWindows then "more" else "cat")) |> Command.stdin stdin

            match! cmd.OutputBytesAsync() with
            | Error e -> Assert.Fail $"expected attempt 1 to complete, got {e.Message}"
            | Ok first ->
                match! cmd.OutputBytesAsync() with
                | Error e -> Assert.Fail $"expected attempt 2 to complete, got {e.Message}"
                | Ok second ->
                    // Windows `more` may append a trailing CRLF to its output, so compare only the
                    // leading bytes the child actually echoed back from the fed content.
                    Assert.That(first.Stdout |> Array.truncate 4, Is.EqualTo<byte>([| 1uy; 2uy; 3uy; 4uy |]))
                    Assert.That(second.Stdout |> Array.truncate 4, Is.EqualTo<byte>([| 1uy; 2uy; 3uy; 4uy |]))
                    Assert.That(second.Stdout, Is.EqualTo<byte>(first.Stdout))
        }
        :> Task

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

    // ---- T-354: a terminal verb ends a KeepStdinOpen pipe the caller never took -------------------
    //
    // Past a terminal/consuming verb nobody can write this run's stdin any more, so a `KeepStdinOpen`
    // writer the caller never took must have its input ended — otherwise a child reading to EOF waits
    // forever and the verb hangs with it. The claim is the SAME once-only claim `TakeStdin` makes, so the
    // two resolve to exactly one owner; the delivery itself uses the transport's own end-of-input path.

    [<Test>]
    member _.``a terminal verb ends a KeepStdinOpen pipe the caller never took (T-354)``() : Task =
        task {
            let config = (Command.create "test" |> Command.keepStdinOpen).Config
            let pipe = new EndOfInputPipe()

            let host =
                { baseHost config with
                    Stdin = Some(pipe :> Stream) }

            use running = new RunningProcess(host)

            let! outcome = running.WaitAsync()
            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

            let! ended = Task.WhenAny(pipe.Closed, Task.Delay 5000)

            Assert.That(
                ended,
                Is.SameAs pipe.Closed,
                "a completion verb must end the input of a kept-open stdin the caller never took"
            )

            Assert.That(pipe.Closes, Is.EqualTo 1, "the kept-open pipe must be ended exactly once")

            Assert.That(
                running.TakeStdin(),
                Is.EqualTo(None: ProcessStdin option),
                "the verb now owns the pipe, so a later TakeStdin must not hand out a stream it has ended"
            )
        }
        :> Task

    [<Test>]
    member _.``a terminal verb leaves an already-taken KeepStdinOpen writer to its owner (T-354)``() : Task =
        task {
            let config = (Command.create "test" |> Command.keepStdinOpen).Config
            let pipe = new EndOfInputPipe()

            let host =
                { baseHost config with
                    Stdin = Some(pipe :> Stream) }

            use running = new RunningProcess(host)

            match running.TakeStdin() with
            | None -> Assert.Fail "expected an interactive stdin handle for a KeepStdinOpen run"
            | Some stdin ->
                let! outcome = running.WaitAsync()
                Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

                // The claim was already spent by `TakeStdin`, so the verb never even started a delivery —
                // there is nothing racing this assertion. The owner's writes still land, and its own
                // `FinishAsync` is what ends the input.
                Assert.That(pipe.Closes, Is.Zero, "a verb must not close a stdin handle the caller took")

                do! stdin.WriteAsync(Encoding.UTF8.GetBytes "OWNER")
                do! stdin.FinishAsync()

                Assert.That(pipe.Closes, Is.EqualTo 1, "the owner's own FinishAsync ends the input")

                Assert.That(
                    Encoding.UTF8.GetString(pipe.ToArray()),
                    Is.EqualTo "OWNER",
                    "the owner's bytes must reach the child, so the verb cannot have ended the pipe first"
                )
        }
        :> Task

    [<Test>]
    member _.``TakeStdin racing a terminal verb leaves exactly one owner of the stdin pipe (T-354)``() : Task =
        task {
            // Both paths claim through the SAME once-only guard, so the race has exactly two outcomes and no
            // third: either the `TakeStdin` caller owns the writer (and the verb ends nothing), or the verb
            // owns it (and ends the input exactly once). Never both — that is the double close — and never
            // neither, which would abandon the kept-open pipe with the child waiting on an EOF.
            //
            // The INVARIANT is what is asserted, so this can never flake on which side happens to win; the
            // two orders are each pinned down deterministically by the two tests above (a verb that claimed
            // first makes a later `TakeStdin` answer `None`; a caller that took it first leaves the pipe
            // untouched by the verb). Each round races a fresh handle head-on, one thread per path.
            let rounds = 24
            let mutable takenWins = 0
            let mutable verbWins = 0

            for _ in 1..rounds do
                let config = (Command.create "test" |> Command.keepStdinOpen).Config
                let pipe = new EndOfInputPipe()

                let host =
                    { baseHost config with
                        Stdin = Some(pipe :> Stream) }

                use running = new RunningProcess(host)
                // A `ref` cell, not a `let mutable`: the racing thread bodies below are closures, which F#
                // does not let capture a mutable local.
                let taken = ref false
                use ready = new CountdownEvent(2)
                use gate = new ManualResetEventSlim(false)

                let start (body: unit -> unit) =
                    let t =
                        Thread(
                            ThreadStart(fun () ->
                                ready.Signal() |> ignore
                                gate.Wait()
                                body ())
                        )

                    t.IsBackground <- true
                    t.Start()
                    t

                let taker = start (fun () -> taken.Value <- (running.TakeStdin()).IsSome)
                let verb = start (fun () -> running.WaitAsync().GetAwaiter().GetResult() |> ignore)

                ready.Wait()
                gate.Set()
                taker.Join()
                verb.Join()

                let owned = if taken.Value then 1 else 0

                if not taken.Value then
                    // The verb won the claim, and its delivery runs off the verb's own thread — wait for it
                    // rather than sampling a race.
                    let! ended = Task.WhenAny(pipe.Closed, Task.Delay 5000)

                    Assert.That(
                        ended,
                        Is.SameAs pipe.Closed,
                        "with no TakeStdin winner the verb owns the pipe and must end its input"
                    )

                    verbWins <- verbWins + 1
                else
                    takenWins <- takenWins + 1

                Assert.That(
                    pipe.Closes + owned,
                    Is.EqualTo 1,
                    "exactly one owner: the pipe is either handed to a caller or ended by the verb, never both"
                )

            Assert.That(takenWins + verbWins, Is.EqualTo rounds, "every round must resolve to exactly one owner")
        }
        :> Task

    [<Test>]
    member _.``a terminal verb delivers the whole stdin source before ending the input (T-354)``() : Task =
        task {
            // `Stdin(source)` + `KeepStdinOpen`: the background feeder is still the pipe's writer, so the
            // verb's end of input must wait for it exactly as `TakeStdin` does — otherwise it would race a
            // second writer on one pipe and cut the child's input short. Deterministic ORDER proof, with no
            // timing guesswork: the gated source parks BEFORE writing, so while it is parked the input
            // provably has not been ended; releasing it lets the feed complete, and only then does the end
            // of input follow the source's bytes.
            let gated = GatedStdinAsyncLines "SRC"

            let config =
                (Command.create "test"
                 |> Command.stdin (Stdin.FromString "x")
                 |> Command.keepStdinOpen)
                    .Config

            let pipe = new EndOfInputPipe()

            // Exactly how `ProcessGroup` wires a `KeepStdinOpen` source feed: the feeder leaves the pipe
            // open when it is done, so the only close that can happen here is the verb's own.
            let feeder =
                Pump.feedStdinSource (Some(pipe :> Stream)) (Some(Stdin.FromAsyncLines gated)) true

            let host =
                { baseHost config with
                    Stdin = Some(pipe :> Stream)
                    StdinFeedComplete = fun () -> feeder.Task.GetAwaiter().GetResult() |> ignore }

            use running = new RunningProcess(host)

            let waiting = running.WaitAsync()

            // Wait — on the source's own `Parked` signal, not a delay — until the feed is parked mid-source.
            let! parked = Task.WhenAny(gated.Parked, Task.Delay 5000)
            Assert.That(parked, Is.SameAs gated.Parked, "the feed never parked in the source")
            Assert.That(feeder.Task.IsCompleted, Is.False, "the feed must not complete while the source is parked")

            Assert.That(
                pipe.Closes,
                Is.Zero,
                "the end of input must wait for the source feed: ending it here would truncate the child's input"
            )

            // Release the source: the feed writes "SRC\n" and completes, and only then may the input end.
            gated.Release()
            let! outcome = waiting
            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

            let! ended = Task.WhenAny(pipe.Closed, Task.Delay 5000)
            Assert.That(ended, Is.SameAs pipe.Closed, "the input must be ended once the source feed has finished")

            Assert.That(
                Encoding.UTF8.GetString(pipe.ToArray()),
                Is.EqualTo "SRC\n",
                "the whole source must reach the child before its end of input"
            )

            Assert.That(pipe.Closes, Is.EqualTo 1, "the kept-open pipe must be ended exactly once")
        }
        :> Task

    [<Test>]
    member _.``a terminal verb ends a POSIX PTY stdin with the terminal's own end-of-input character (T-354)``
        ()
        : Task =
        task {
            // The parent side of a POSIX PTY run's stdin is `Native.Posix.PtyStdinStream`, a NON-owning view
            // over the shared pty master: closing it releases nothing and takes no terminal with it, so a
            // plain `Dispose` here would leave the child waiting on an EOF that can never arrive. The verb
            // must therefore go through the same `IStdinFinisher` delivery `ProcessStdin.FinishAsync` uses —
            // the terminal's own `termios.c_cc[VEOF]` character, twice. Driven over the native write seam, so
            // it is exercised on every platform.
            let written = ResizeArray<byte>()

            let delivered =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let deliveredTask: Task = delivered.Task

            Native.Posix.ptyWriteForTests <-
                Some(fun _ ptr count ->
                    let buffer = Array.zeroCreate<byte> (int count)
                    Marshal.Copy(ptr, buffer, 0, buffer.Length)

                    let total =
                        lock written (fun () ->
                            written.AddRange buffer
                            written.Count)

                    if total >= 2 then
                        delivered.TrySetResult() |> ignore

                    nativeint buffer.Length)

            try
                let config = (Command.create "test" |> Command.keepStdinOpen).Config

                use stdin =
                    new Native.Posix.PtyStdinStream(new SafeFileHandle(IntPtr.Zero, ownsHandle = false), 4uy)

                let host =
                    { baseHost config with
                        Stdin = Some(stdin :> Stream) }

                use running = new RunningProcess(host)

                let! outcome = running.WaitAsync()
                Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

                let! got = Task.WhenAny(deliveredTask, Task.Delay 5000)

                Assert.That(
                    got,
                    Is.SameAs deliveredTask,
                    "a completion verb must deliver the pty's own end-of-input character, not close the master view"
                )

                Assert.That(lock written (fun () -> written.ToArray()), Is.EqualTo<byte[]>([| 4uy; 4uy |]))
            finally
                Native.Posix.ptyWriteForTests <- None
        }
        :> Task

    [<Test>]
    member _.``a terminal verb ends a ConPTY stdin with Ctrl-Z + Enter and leaves the session pipe open (T-354)``
        ()
        : Task =
        task {
            // The Windows counterpart: a ConPTY run's stdin writer is a non-owning view over the SESSION's
            // host-input pipe, so ending the input is the console's own Ctrl-Z + Enter gesture — closing that
            // pipe would end the child's console instead of its input. Managed code over a `Stream`, so this
            // runs on every platform.
            let pipe = new EndOfInputPipe()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe :> Stream)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)
            let config = (Command.create "test" |> Command.keepStdinOpen).Config

            let host =
                { baseHost config with
                    Stdin = Some(stdin :> Stream) }

            use running = new RunningProcess(host)

            let! outcome = running.WaitAsync()
            Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))

            let! delivered = Task.WhenAny(pipe.FirstWrite, Task.Delay 5000)

            Assert.That(
                delivered,
                Is.SameAs pipe.FirstWrite,
                "a completion verb must deliver the console's end-of-input gesture to an untaken ConPTY stdin"
            )

            Assert.That(pipe.ToArray(), Is.EqualTo<byte[]>([| 0x1Auy; 0x0Duy |]))
            Assert.That(stdin.IsFinished, Is.True)

            Assert.That(
                pipe.Closes,
                Is.Zero,
                "ending stdin must not close the session's host-input pipe, which belongs to the run"
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
                    Native.Cgroup.killCgroup dir |> ignore
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

    [<Test>]
    member _.``atomic cgroup kill thaws and verifies the reusable group before succeeding``() =
        let writes = ResizeArray<string * string>()

        let writeHook (file: string) (content: string) =
            let control =
                if file.EndsWith("cgroup.kill", StringComparison.Ordinal) then
                    "cgroup.kill"
                elif file.EndsWith("cgroup.freeze", StringComparison.Ordinal) then
                    "cgroup.freeze"
                else
                    file

            writes.Add(control, content)

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            match Native.Cgroup.killCgroup dir with
            | Error detail -> Assert.Fail $"expected atomic kill followed by thaw, got {detail}"
            | Ok() -> ()

            Assert.That(writes.Count, Is.EqualTo 2, "atomic kill and thaw must perform exactly two control writes")
            Assert.That(fst writes[0], Is.EqualTo "cgroup.kill")
            Assert.That(snd writes[0], Is.EqualTo "1")
            Assert.That(fst writes[1], Is.EqualTo "cgroup.freeze")
            Assert.That(snd writes[1], Is.EqualTo "0", "a successful atomic kill must be followed by an explicit thaw")

            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "0"))

    [<Test>]
    member _.``atomic cgroup thaw failure reaches Signal Kill and KillAll, then a later KillAll can recover``() =
        let mutable allowThaw = false

        let writeHook (file: string) (content: string) =
            if
                file.EndsWith("cgroup.freeze", StringComparison.Ordinal)
                && content = "0"
                && not allowThaw
            then
                raise (UnauthorizedAccessException "thaw remains refused")

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            let backend: IContainmentBackend = CgroupBackend dir

            let assertKillFailure operation result =
                match result with
                | Error(ProcessError.Io message) ->
                    Assert.That(message, Does.Contain "freeze", $"{operation} must identify the thaw failure")
                | Error other -> Assert.Fail $"expected ProcessError.Io from {operation}, got {other}"
                | Ok() -> Assert.Fail $"{operation} falsely reported success for a frozen reusable cgroup"

            assertKillFailure "backend KillTree" (backend.KillTree())
            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "1")

            let group = ProcessGroup.FromBackend(CgroupBackend dir, ProcessGroupOptions())

            try
                assertKillFailure "ProcessGroup.Signal Kill" (group.Signal Signal.Kill)
                assertKillFailure "ProcessGroup.KillAll" (group.KillAll())

                allowThaw <- true

                match group.KillAll() with
                | Error error -> Assert.Fail $"expected a later KillAll to recover, got {error}"
                | Ok() -> ()

                Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "0")
            finally
                (group :> IDisposable).Dispose())

    [<Test>]
    member _.``HardRelease accepts a cgroup removed during atomic post-kill thaw and remains idempotent``() =
        let writeHook (file: string) (content: string) =
            if file.EndsWith("cgroup.freeze", StringComparison.Ordinal) && content = "0" then
                Directory.Delete(Path.GetFullPath(Path.Combine(file, "..")), true)
                raise (DirectoryNotFoundException "the killed cgroup was removed before thaw verification")

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            let backend = CgroupBackend dir
            let teardown = backend :> IContainmentBackend

            teardown.HardRelease()

            Assert.That(Directory.Exists dir, Is.False, "the synthetic group must disappear after atomic kill")

            Assert.That(
                backend.RetainedCgroupDetail,
                Is.EqualTo None,
                "a removed cgroup is not a retained cleanup failure"
            )

            Assert.That(backend.DirectoryReclaims, Is.EqualTo 1)

            teardown.HardRelease()

            Assert.That(
                backend.RetainedCgroupDetail,
                Is.EqualTo None,
                "repeat cleanup must keep the removed-group success"
            )

            Assert.That(backend.DirectoryReclaims, Is.EqualTo 1, "repeat cleanup must not reclaim the path twice"))

    [<Test>]
    member _.``legacy cgroup kill retries a refused first thaw and verifies the resulting state``() =
        let mutable thawAttempts = 0

        let writeHook (file: string) (content: string) =
            if file.EndsWith("cgroup.kill", StringComparison.Ordinal) then
                raise (IOException "cgroup.kill is unavailable")
            elif content = "0" then
                thawAttempts <- thawAttempts + 1

                if thawAttempts = 1 then
                    raise (UnauthorizedAccessException "first thaw is refused")

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            match Native.Cgroup.killCgroup dir with
            | Error detail -> Assert.Fail $"expected the retry to thaw the cgroup, got {detail}"
            | Ok() -> ()

            Assert.That(thawAttempts, Is.EqualTo 2, "a refused first thaw must be retried")
            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "0"))

    [<Test>]
    member _.``legacy cgroup kill failure reaches Signal Kill and KillAll, then a later KillAll can recover``() =
        let mutable allowThaw = false

        let writeHook (file: string) (content: string) =
            if file.EndsWith("cgroup.kill", StringComparison.Ordinal) then
                raise (IOException "cgroup.kill is unavailable")
            elif content = "0" && not allowThaw then
                raise (UnauthorizedAccessException "thaw remains refused")

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            let backend: IContainmentBackend = CgroupBackend dir

            let assertKillFailure operation result =
                match result with
                | Error(ProcessError.Io message) ->
                    Assert.That(message, Does.Contain "freeze", $"{operation} must identify the thaw failure")
                | Error other -> Assert.Fail $"expected ProcessError.Io from {operation}, got {other}"
                | Ok() -> Assert.Fail $"{operation} falsely reported success for a frozen reusable cgroup"

            assertKillFailure "backend KillTree" (backend.KillTree())
            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "1")

            let group = ProcessGroup.FromBackend(CgroupBackend dir, ProcessGroupOptions())

            try
                assertKillFailure "ProcessGroup.Signal Kill" (group.Signal Signal.Kill)
                assertKillFailure "ProcessGroup.KillAll" (group.KillAll())

                allowThaw <- true

                match group.KillAll() with
                | Error error -> Assert.Fail $"expected a later KillAll to recover, got {error}"
                | Ok() -> ()

                Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "0")
            finally
                (group :> IDisposable).Dispose())

    [<Test>]
    member _.``legacy cgroup kill does not fail for a fully write-restricted already-unfrozen cgroup``() =
        let writeHook _file _content =
            raise (UnauthorizedAccessException "all cgroup writes are refused")

        withSyntheticCgroup (Some "0\n") writeHook (fun dir ->
            let group = ProcessGroup.FromBackend(CgroupBackend dir, ProcessGroupOptions())

            try
                match group.KillAll() with
                | Error error -> Assert.Fail $"an already-unfrozen cgroup should remain reusable, got {error}"
                | Ok() -> ()

                match group.Signal Signal.Kill with
                | Error error -> Assert.Fail $"Signal Kill should preserve best-effort success, got {error}"
                | Ok() -> ()
            finally
                (group :> IDisposable).Dispose())

    [<Test>]
    member _.``legacy cgroup kill treats a freezer that disappears during thaw as successful``() =
        let writeHook (file: string) (content: string) =
            if file.EndsWith("cgroup.kill", StringComparison.Ordinal) then
                raise (IOException "cgroup.kill is unavailable")
            elif content = "0" then
                File.Delete file
                raise (IOException "freezer disappeared during thaw")

        withSyntheticCgroup (Some "1\n") writeHook (fun dir ->
            match Native.Cgroup.killCgroup dir with
            | Error detail -> Assert.Fail $"a removed freezer cannot leave a reusable group frozen: {detail}"
            | Ok() -> ()

            Assert.That(File.Exists(Path.Combine(dir, "cgroup.freeze")), Is.False))

    // --- T-363: teardown must give a hard-killed cgroup a BOUNDED window to actually empty before it
    // removes the directory, and must reclaim that directory exactly once. A stand-in directory on an
    // ordinary filesystem is not a cgroup, so the cgroup control writes are refused through the existing
    // write hook — that way teardown's kill leaves no interface file behind and the reclaim is observable
    // here (a real cgroupfs directory is removed together with its kernel-generated files; a plain one is
    // not). The drain budget is shortened through its test seam so neither test pays the real wait. ---

    [<Test>]
    member _.``cgroup teardown reclaims the directory, and a repeat teardown never removes it again``() =
        withReclaimableCgroup (TimeSpan.FromMilliseconds 20.0) (fun dir ->
            let backend = CgroupBackend dir
            let teardown = backend :> IContainmentBackend
            teardown.HardRelease()

            Assert.That(
                Directory.Exists dir,
                Is.False,
                "teardown left the drained cgroup directory behind — the leak this fix closes"
            )

            match backend.RetainedCgroupDetail with
            | None -> ()
            | Some detail -> Assert.Fail $"a reclaimed cgroup must leave no teardown complaint behind: {detail}"

            // A second teardown — a `Dispose` racing a `ShutdownAsync`, or a test driving the backend
            // directly — must not remove this path again. The cgroup name carries THIS process's pid, which
            // another process inherits once we exit, so by then the directory can belong to a new cgroup.
            Directory.CreateDirectory dir |> ignore
            teardown.HardRelease()

            Assert.That(
                Directory.Exists dir,
                Is.True,
                "a repeat teardown removed a cgroup directory it no longer owns"
            )

            Assert.That(
                backend.DirectoryReclaims,
                Is.EqualTo 1,
                "the cgroup directory reclaim must run exactly once per backend, however teardown is driven"
            ))

    [<Test>]
    member _.``a cgroup that will not drain is reported, and reclaimed once even when teardown races itself``() : Task =
        task {
            // The stand-in directory holds an ordinary file here, so the removal is refused exactly as a
            // populated cgroup's would be — the case a single best-effort `rmdir` used to swallow whole.
            let dir =
                Path.Combine(Path.GetTempPath(), $"processkit-cgroup-retained-{Guid.NewGuid():N}")

            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, "cgroup.procs"), "")
            let originalBudget = Native.Cgroup.drainBudgetOverrideForTests
            Native.Cgroup.drainBudgetOverrideForTests <- Some(TimeSpan.FromMilliseconds 20.0)

            try
                let backend = CgroupBackend dir
                let teardown = backend :> IContainmentBackend
                let recordedBefore = Native.Cgroup.retainedCgroupCount ()

                // Two teardowns at once: exactly one of them may run the reclaim, and neither may hang.
                let first: Task = Task.Run(fun () -> teardown.HardRelease())
                let second: Task = Task.Run(fun () -> teardown.HardRelease())
                do! Task.WhenAll(first, second)

                Assert.That(
                    backend.DirectoryReclaims,
                    Is.EqualTo 1,
                    "concurrent teardowns must reclaim the cgroup directory exactly once between them"
                )

                match backend.RetainedCgroupDetail with
                | Some detail ->
                    Assert.That(
                        detail,
                        Does.Contain "refused to remove",
                        "the verdict kept for diagnosis must say what stopped the reclaim"
                    )
                | None -> Assert.Fail "a cgroup teardown could not reclaim must be diagnosable, not silently dropped"

                // The same verdict also reaches the process-wide teardown diagnostic, which is what makes
                // an accumulating hierarchy visible at all. Asserted as a floor, never an exact delta: any
                // other group's teardown — including a finalizer-driven one — shares that counter (K-148).
                Assert.That(
                    Native.Cgroup.retainedCgroupCount (),
                    Is.GreaterThanOrEqualTo(recordedBefore + 1),
                    "an unreclaimed cgroup directory must be counted, not silently dropped"
                )

                Assert.That(
                    Directory.Exists dir,
                    Is.True,
                    "a cgroup that would not drain keeps its directory rather than losing one still in use"
                )
            finally
                Native.Cgroup.drainBudgetOverrideForTests <- originalBudget

                try
                    Directory.Delete(dir, true)
                with
                | :? DirectoryNotFoundException
                | :? IOException
                | :? UnauthorizedAccessException ->
                    // Best-effort cleanup; a leftover stand-in directory must not fail the test.
                    ()
        }
        :> Task

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

    // --- T-342: a one-shot stdin payload belongs to at most ONE incarnation -----------------------
    //
    // `Stdin.FromStream`/`FromLines`/`FromAsyncLines` wrap a payload that can be read exactly once, but
    // every spawn used to hand that same payload to a fresh feeder of its own. A second run therefore
    // created a live child and only then read the stream from wherever the first one left it (usually
    // EOF), and two concurrent runs both started children that split one stream between them — silent
    // wrong input either way. The boundary that actually creates a child — `ProcessGroup.BuildHost` for
    // every verb, runner and streaming start, and the pipeline's own stage-0 spawn — now takes the
    // payload BEFORE it spawns and commits it the instant the child exists, so a second consumer is
    // refused with a typed error while it still has no child of its own, and a launch that produced no
    // child hands the payload back intact.

    /// A fresh, empty directory for a marker file. The (possibly space-carrying) temp path travels as
    /// the child's working directory, never inside a shell script, so the recorder below needs no
    /// quoting on either platform.
    member private _.MarkerDir() : string =
        let dir = Path.Combine(Path.GetTempPath(), $"pk-t342-{Guid.NewGuid():N}")
        Directory.CreateDirectory dir |> ignore
        dir

    /// A child that records its own existence (one `ran` line) and then appends whatever stdin it was
    /// handed, both into `marker.log` in `dir`. So the file counts the children that actually started,
    /// and shows which of them received the payload.
    member private _.Recorder(dir: string) : Command =
        (if isWindows then
             shell "echo ran>>marker.log&sort>>marker.log"
         else
             shell "echo ran >> marker.log; sort >> marker.log")
        |> Command.currentDir dir

    /// The marker file's non-empty, trimmed lines — `[]` when no child ever created it. Opened
    /// share-compatible so reading can never trip a Windows sharing violation against a child's still
    /// open write handle.
    member private _.MarkerLines(dir: string) : string list =
        let path = Path.Combine(dir, "marker.log")

        if not (File.Exists path) then
            []
        else
            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            use reader = new StreamReader(fs)

            reader.ReadToEnd().Split('\n')
            |> Array.map (fun line -> line.Trim())
            |> Array.filter (fun line -> line <> "")
            |> Array.toList

    member private _.DeleteDirQuietly(dir: string) =
        try
            Directory.Delete(dir, true)
        with _ ->
            // Best-effort test cleanup: a leftover temp directory is harmless and must never fail a
            // test (a child's handle can still be closing on Windows when this runs).
            ()

    [<Test>]
    member this.``a second run of a one-shot stdin command is refused before any second child exists``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                let command = this.Recorder dir |> Command.stdin (Stdin.FromStream stream)

                match! command.OutputStringAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"expected the first run to succeed, got {error.Message}"

                let afterFirst = this.MarkerLines dir
                Assert.That(afterFirst |> List.filter ((=) "ran") |> List.length, Is.EqualTo 1)
                Assert.That(afterFirst, Does.Contain "alpha", "the first child must have been fed the payload")

                match! command.OutputStringAsync() with
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
                | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
                | Ok _ -> Assert.Fail "a second run must not silently re-run over an exhausted one-shot source"

                // The refusal precedes the spawn: no second child was created, so the marker file is
                // exactly as the first run left it (before the fix a second child ran and read nothing).
                Assert.That(
                    this.MarkerLines dir,
                    Is.EqualTo(box afterFirst),
                    "the refused run must not have started a child"
                )
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member this.``two concurrent runs over one one-shot stdin source produce exactly one child``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                let command = this.Recorder dir |> Command.stdin (Stdin.FromStream stream)

                // Both runs are in flight before either can finish; whichever reserves the payload first
                // owns it, and the other must be refused rather than started alongside it.
                let first = command.OutputStringAsync()
                let second = command.OutputStringAsync()
                let! results = Task.WhenAll [| first; second |]

                let succeeded =
                    results
                    |> Array.filter (fun result ->
                        match result with
                        | Ok _ -> true
                        | Error _ -> false)
                    |> Array.length

                let refused =
                    results
                    |> Array.filter (fun result ->
                        match result with
                        | Error(ProcessError.Unsupported message) -> message.Contains "one-shot stdin source"
                        | _ -> false)
                    |> Array.length

                Assert.That(succeeded, Is.EqualTo 1, $"exactly one run may own the payload, got {results}")
                Assert.That(refused, Is.EqualTo 1, "the losing run must be refused loudly, not handed a shared source")

                let recorded = this.MarkerLines dir
                Assert.That(recorded |> List.filter ((=) "ran") |> List.length, Is.EqualTo 1, "exactly one child")

                Assert.That(
                    recorded |> List.filter ((=) "alpha") |> List.length,
                    Is.EqualTo 1,
                    "the payload must have reached exactly one child, whole"
                )
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member _.``a pre-spawn failure hands the one-shot payload to the next run``() : Task =
        task {
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            // The program does not exist, so the launch fails before any child: the reservation it took
            // is rolled back rather than stranding the payload for the life of the stream.
            let missing =
                Command.create "pk_t342_no_such_program_canary"
                |> Command.stdin (Stdin.FromStream stream)

            match! missing.OutputStringAsync() with
            | Error(ProcessError.NotFound _) -> ()
            | Error other -> Assert.Fail $"expected NotFound for a missing program, got {other.Message}"
            | Ok _ -> Assert.Fail "a non-existent program must not spawn"

            // A DIFFERENT `Stdin` wrapper over the SAME stream — the claim is keyed on the payload
            // object, not on the wrapper or the command — is handed the whole, untouched payload.
            let sorted = Command.create "sort" |> Command.stdin (Stdin.FromStream stream)

            match! sorted.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "alpha")
            | Error error -> Assert.Fail $"expected the returned payload to be usable, got {error.Message}"
        }
        :> Task

    [<Test>]
    member _.``a stdin feed that fails after the child launched keeps the payload spent``() : Task =
        task {
            // The generator yields one line and then faults, so the child was launched (and had already
            // been fed part of the payload) before the feed failed. The payload is spent all the same —
            // a child read it — so the failure must not hand it back for a replay of the remains.
            let source =
                seq {
                    yield "first line"
                    failwith "boom mid-iteration"
                }

            let command = (shell "sort") |> Command.stdin (Stdin.FromLines source)

            match! command.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected the mid-iteration source fault to surface"

            match! command.OutputStringAsync() with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
            | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
            | Ok _ -> Assert.Fail "a stdin failure after the launch must not restore the payload"
        }
        :> Task

    [<Test>]
    member _.``a dry-run preview leaves the one-shot payload for the real run that follows``() : Task =
        task {
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            // The `--dry-run` flow in full: preview the command, then actually run it. The retry budget
            // is what makes the preview take a run-level hold on the payload before it knows that its
            // runner spawns nothing at all; that hold is a loan, returned when no attempt launched a
            // child, so the real run below still finds the caller's whole input. (Held for good, it
            // refused every later run of this payload with `Unsupported` — a source no child had read.)
            let command =
                (shell "sort")
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let preview: IProcessRunner = ProcessKit.Testing.DryRunRunner()

            match! preview.OutputStringAsync(command, CancellationToken.None) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"a preview must never be refused a one-shot source: {error.Message}"

            match! command.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "alpha", "the real run lost the previewed payload")
            | Error error -> Assert.Fail $"the real run must be handed the previewed payload: {error.Message}"

            // And the run that DID feed a child keeps the payload spent, preview or no preview.
            match! command.OutputStringAsync() with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
            | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
            | Ok _ -> Assert.Fail "a run after the payload was fed to a child must be refused"
        }
        :> Task

    [<Test>]
    member _.``a repeatable stdin source still feeds every run``() : Task =
        task {
            // The other half of the contract: only a ONE-SHOT payload is owned. A repeatable source has
            // nothing to exhaust, so the same command runs as often as the caller likes, unchanged.
            let command = (shell "sort") |> Command.stdin (Stdin.FromString "bravo\nalpha\n")

            for attempt in 1..3 do
                match! command.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout, Does.Contain "alpha", $"run {attempt} lost the payload")
                | Error error -> Assert.Fail $"run {attempt} must not be refused a repeatable source: {error.Message}"
        }
        :> Task

    [<Test>]
    member this.``a pipeline's stage-0 one-shot stdin source feeds exactly one chain``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                // A chain spawns its stages itself rather than through `ProcessGroup.BuildHost`, so it
                // used to be the one path that could drain a payload with nothing recording it. The
                // recorder is the LAST stage, so the marker file shows whether the chain ran at all and
                // what stage 0 forwarded to it.
                let stage0 = Command.create "sort" |> Command.stdin (Stdin.FromStream stream)
                let pipeline = stage0.Pipe(this.Recorder dir)

                match! pipeline.OutputStringAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"expected the first chain to run, got {error.Message}"

                let afterFirst = this.MarkerLines dir
                Assert.That(afterFirst |> List.filter ((=) "ran") |> List.length, Is.EqualTo 1)
                Assert.That(afterFirst, Does.Contain "alpha", "stage 0 must have been fed the payload")

                match! pipeline.OutputStringAsync() with
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
                | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
                | Ok _ -> Assert.Fail "a pipeline must not re-run stage 0 over an exhausted one-shot source"

                // Refused at stage 0's launch boundary, so not one stage of the second chain started.
                Assert.That(this.MarkerLines dir, Is.EqualTo(box afterFirst), "no stage of the refused chain may start")
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member _.``a streaming start spends the one-shot payload like a captured run does``() : Task =
        task {
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")
            let command = (shell "sort") |> Command.stdin (Stdin.FromStream stream)

            match! command.StartAsync() with
            | Error error -> Assert.Fail $"expected the first start to succeed, got {error.Message}"
            | Ok running ->
                use started = running

                match! started.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout, Does.Contain "alpha")
                | Error error -> Assert.Fail $"expected the streamed child to be fed the payload, got {error.Message}"

            // `StartAsync` was entirely outside the old guard — its child drained the payload without
            // anything recording it, so a later run was handed the exhausted remains. It commits now.
            match! command.StartAsync() with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
            | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
            | Ok running ->
                use _ = running
                Assert.Fail "a second streaming start must not be handed an exhausted one-shot source"
        }
        :> Task

    [<Test>]
    member this.``a streaming pipeline start spends stage 0's one-shot payload``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                // The streaming staging loop is a second, independent copy of the buffered one, so it
                // gets the same guard — and the recorder proves the second chain started no stage at all.
                let stage0 = Command.create "sort" |> Command.stdin (Stdin.FromStream stream)
                let pipeline = stage0.Pipe(this.Recorder dir)

                match! pipeline.StartAsync() with
                | Error error -> Assert.Fail $"expected the first chain to start, got {error.Message}"
                | Ok session ->
                    use started = session

                    match! started.FinishAsync() with
                    | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                    | Error error -> Assert.Fail $"expected the streamed chain to finish, got {error.Message}"

                let afterFirst = this.MarkerLines dir
                Assert.That(afterFirst |> List.filter ((=) "ran") |> List.length, Is.EqualTo 1)
                Assert.That(afterFirst, Does.Contain "alpha", "stage 0 must have been fed the payload")

                match! pipeline.StartAsync() with
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
                | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
                | Ok session ->
                    use _ = session
                    Assert.Fail "a second streaming chain must not be handed an exhausted one-shot source"

                Assert.That(
                    this.MarkerLines dir,
                    Is.EqualTo(box afterFirst),
                    "no stage of the refused streaming chain may start"
                )
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    // --- T-342 / R-03: a run's hold buys no launch a free pass -----------------------------------
    //
    // The run-level hold a retrying run takes rides on the `Command` it drives (`WithStdinReservation`),
    // and that command is an ordinary value the library hands to whatever `IProcessRunner` drives it. So
    // "the payload is already reserved BY THIS RUN" cannot be a question answered from the stamp alone:
    // the launch boundary used to wave any launch carrying the stamp straight through, with no check of
    // the claim's state, which handed a second child the drained remains — through shipped public API,
    // and only when a retry policy was set, so having one WEAKENED the guarantee. A launch now takes the
    // hold's loan atomically instead, and only while the payload is intact and the run still holds it.

    /// The one-child assertions shared by the decorator variants: the double call ends with exactly one
    /// success and one typed refusal, and the marker file proves only one child ever existed and that it
    /// got the whole payload.
    member private this.AssertSingleChildAcrossDoubleCall
        (dir: string, outcomes: Result<ProcessResult<string>, ProcessError> list)
        =
        let succeeded =
            outcomes
            |> List.filter (fun outcome ->
                match outcome with
                | Ok _ -> true
                | Error _ -> false)
            |> List.length

        let refused =
            outcomes
            |> List.filter (fun outcome ->
                match outcome with
                | Error(ProcessError.Unsupported message) -> message.Contains "one-shot stdin source"
                | _ -> false)
            |> List.length

        Assert.That(outcomes |> List.length, Is.EqualTo 2, "the decorator must have called the runner twice")
        Assert.That(succeeded, Is.EqualTo 1, "exactly one call may own the payload")
        Assert.That(refused, Is.EqualTo 1, "the other call must be refused loudly, not handed the same payload")

        let recorded = this.MarkerLines dir

        Assert.That(
            recorded |> List.filter ((=) "ran") |> List.length,
            Is.EqualTo 1,
            "the refused call must not have started a child of its own"
        )

        Assert.That(
            recorded |> List.filter ((=) "alpha") |> List.length,
            Is.EqualTo 1,
            "the payload must have reached exactly one child, whole"
        )

    [<Test>]
    member this.``a decorator that calls its inner runner twice with a retrying run's command gets one child``
        ()
        : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                // The retry budget is what makes the verb layer stamp its run-level hold on the command
                // handed to the runner; the decorator then drives that stamped command twice.
                let command =
                    this.Recorder dir
                    |> Command.stdin (Stdin.FromStream stream)
                    |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

                let decorator = DoubleCallingRunner(runner, concurrent = false)
                let driver: IProcessRunner = decorator

                match! driver.OutputStringAsync(command, CancellationToken.None) with
                | Ok _ -> Assert.Fail "the second call must be refused, so the run's last outcome is that refusal"
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
                | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"

                this.AssertSingleChildAcrossDoubleCall(dir, decorator.Outcomes)
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member this.``two concurrent calls under one retrying run's hold still produce exactly one child``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                // The same decorator, but with both calls in flight at once: the hold is lent to one
                // launch at a time, so the loser cannot slip past between "is this our run's payload?"
                // and the spawn that answers it.
                let command =
                    this.Recorder dir
                    |> Command.stdin (Stdin.FromStream stream)
                    |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

                let decorator = DoubleCallingRunner(runner, concurrent = true)
                let driver: IProcessRunner = decorator

                let! _ = driver.OutputStringAsync(command, CancellationToken.None)
                this.AssertSingleChildAcrossDoubleCall(dir, decorator.Outcomes)
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member _.``a retrying run gets its own hold back between attempts``() : Task =
        task {
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            // The other side of the same rule: the loan is exclusive, so it has to come BACK to the run
            // whenever an attempt creates no child, or the run's second attempt would be refused as a
            // second consumer of the payload it holds itself. Every attempt here fails at the launch
            // boundary before a child exists, so the run must end with that honest `NotFound` — a
            // one-shot refusal in its place would mean an attempt was locked out by its own run.
            let mutable classified = 0

            let missing =
                Command.create "pk_t342_r03_no_such_program_canary"
                |> Command.stdin (Stdin.FromStream stream)
                |> Command.retry 3 TimeSpan.Zero (fun _ ->
                    classified <- classified + 1
                    true)

            match! missing.OutputStringAsync() with
            | Error(ProcessError.NotFound _) -> ()
            | Error other -> Assert.Fail $"expected NotFound for a missing program, got {other.Message}"
            | Ok _ -> Assert.Fail "a non-existent program must not spawn"

            Assert.That(classified, Is.EqualTo 2, "each attempt after the first must have been allowed to run")

            // And no attempt read a byte of it, so the payload is still whole for the next run.
            let sorted = Command.create "sort" |> Command.stdin (Stdin.FromStream stream)

            match! sorted.OutputStringAsync() with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "alpha")
            | Error error -> Assert.Fail $"expected the untouched payload to be usable, got {error.Message}"
        }
        :> Task

    [<Test>]
    member this.``a command kept from a runner hook cannot start a child on a spent payload``() : Task =
        task {
            let dir = this.MarkerDir()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "bravo\nalpha\n")

            try
                let recorder = this.Recorder dir |> Command.stdin (Stdin.FromStream stream)

                let previewed = recorder |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

                // A preview run: the verb layer takes the run-level hold and stamps it on the command the
                // hook is handed, the hook keeps that command, and the run — having spawned nothing —
                // gives the payload back untouched.
                let hook = CommandKeepingRunner(ProcessKit.Testing.DryRunRunner())
                let driver: IProcessRunner = hook

                match! driver.OutputStringAsync(previewed, CancellationToken.None) with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"a preview must never be refused a one-shot source: {error.Message}"

                let leaked =
                    match hook.Seen with
                    | Some seen -> seen
                    | None -> failwith "the hook was never handed the command"

                // A DIFFERENT run then feeds the payload to a real child, so it is spent for good.
                match! recorder.OutputStringAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"the returned payload must be usable by the next run: {error.Message}"

                let afterFirst = this.MarkerLines dir
                Assert.That(afterFirst |> List.filter ((=) "ran") |> List.length, Is.EqualTo 1)
                Assert.That(afterFirst, Does.Contain "alpha", "the first child must have been fed the payload")

                // The kept command still carries the stamp of a hold whose run is long over. Started
                // through the streaming path — which applies no retry, so nothing re-reserves anything on
                // its behalf — it used to be recognized as "the owning run's own launch" and started a
                // child over the exhausted payload. It is a second consumer like any other.
                match! leaked.StartAsync() with
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "one-shot stdin source")
                | Error other -> Assert.Fail $"expected the one-shot refusal, got {other.Message}"
                | Ok running ->
                    use _ = running
                    Assert.Fail "a command kept from a hook must not start a child over a spent payload"

                Assert.That(
                    this.MarkerLines dir,
                    Is.EqualTo(box afterFirst),
                    "the refused start must not have created a child"
                )
            finally
                this.DeleteDirQuietly dir
        }
        :> Task

    // ---- T-351: a killed tree that cannot be reaped must not hold a completion path --------------
    //
    // A delivered SIGKILL/`TerminateProcess` is not a promise that the child is reapable now: a child
    // wedged in uninterruptible (`D`-state) sleep defers even SIGKILL until its I/O unblocks. Every
    // path that kills and then waits is therefore bounded, and hands the single remaining right to
    // wait/reap the tree to the `PostKillReap` ledger rather than blocking on it or dropping it.

    /// Run `action` with a short post-kill reap budget, so these regressions do not pay the production
    /// five seconds. Restored afterwards (tests in this assembly run sequentially).
    member private _.WithShortPostKillBudget(action: unit -> Task<unit>) : Task =
        task {
            let previous = PostKillReap.budgetOverrideForTests
            PostKillReap.budgetOverrideForTests <- Some(TimeSpan.FromMilliseconds 250.0)

            try
                do! action ()
            finally
                PostKillReap.budgetOverrideForTests <- previous
        }
        :> Task

    [<Test>]
    member this.``a cancelled run whose tree never becomes reapable still reports Cancelled (T-351)``() : Task =
        this.WithShortPostKillBudget(fun () ->
            task {
                // The injected never-completing post-kill wait: the cancellation's `Kill()` IS delivered,
                // the tree simply never becomes reapable afterwards.
                let waitTcs =
                    TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                let kills = ref 0
                let command = Command.create "wedged-child"

                let host =
                    { baseHost command.Config with
                        Wait = fun () -> waitTcs.Task
                        StartKill = fun () -> Interlocked.Increment(&kills.contents) |> ignore }

                let running = new RunningProcess(host)
                use cts = new CancellationTokenSource()
                let stopwatch = Stopwatch.StartNew()

                let run =
                    CaptureVerbs.runToCompletion
                        command
                        cts.Token
                        (fun () -> Task.FromResult(Ok running))
                        (fun handle -> handle.OutputStringAsync())

                // Cancel once the run is genuinely parked on the exit wait, so the token registration's
                // kill is what starts the post-kill window (not the pre-start cancellation short-circuit).
                do! Task.Delay 50
                cts.Cancel()

                match! run with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"expected Cancelled, got {other}"

                stopwatch.Stop()

                Assert.That(
                    Volatile.Read(&kills.contents),
                    Is.GreaterThanOrEqualTo 1,
                    "cancellation must still deliver the kill"
                )

                Assert.That(
                    stopwatch.Elapsed,
                    Is.LessThan(TimeSpan.FromSeconds 5.0),
                    "the never-completing post-kill wait held the cancellation past its bounded budget"
                )

                waitTcs.TrySetResult(Outcome.Exited 0) |> ignore
                do! (running :> IAsyncDisposable).DisposeAsync()
            })

    [<Test>]
    member this.``Kill then WaitAsync reports an honest Unobserved when the reap never lands (T-351)``() : Task =
        this.WithShortPostKillBudget(fun () ->
            task {
                let waitTcs =
                    TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                let host =
                    { baseHost (Command.create "wedged-child").Config with
                        Wait = fun () -> waitTcs.Task }

                let running = new RunningProcess(host)
                let stopwatch = Stopwatch.StartNew()

                running.Kill()
                let! outcome = running.WaitAsync()
                stopwatch.Stop()

                match outcome with
                | Outcome.Unobserved reason ->
                    Assert.That(reason, Does.Contain "post-kill", "the detail must say why the status is unknown")
                | other -> Assert.Fail $"expected an honest Unobserved, got {other}"

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))

                Assert.That(
                    PostKillReap.adoptedWaitCountFor waitTcs.Task,
                    Is.EqualTo 1,
                    "the native wait must be adopted exactly once, never abandoned and never duplicated"
                )

                // The late conclusion still arrives on the adopted wait, observed by the ledger.
                waitTcs.TrySetResult(Outcome.Signalled(Some 9)) |> ignore
                do! (running :> IAsyncDisposable).DisposeAsync()
            })

    [<Test>]
    member this.``a fast child is still reaped synchronously and reports its real outcome (T-351)``() : Task =
        this.WithShortPostKillBudget(fun () ->
            task {
                // The ordinary path: the kill lands, the child is reaped at once, and the REAL outcome is
                // reported — no budget is paid, and no ownership changes hands.
                let waitTask = Task.FromResult(Outcome.Signalled(Some 9))

                let host =
                    { baseHost (Command.create "prompt-child").Config with
                        Wait = fun () -> waitTask }

                let running = new RunningProcess(host)

                running.Kill()
                let! outcome = running.WaitAsync()

                Assert.That(
                    outcome,
                    Is.EqualTo(Outcome.Signalled(Some 9)),
                    "a normal kill must report the real signal"
                )

                Assert.That(
                    PostKillReap.adoptedWaitCountFor waitTask,
                    Is.Zero,
                    "a synchronously reaped child must not hand ownership to the ledger"
                )

                do! (running :> IAsyncDisposable).DisposeAsync()
            })

    [<Test>]
    member this.``an exit wait started after the post-kill window still reports the real outcome (T-351)``() : Task =
        this.WithShortPostKillBudget(fun () ->
            task {
                // The window belongs to the WAIT, not to the handle: any work between `Kill()` and the
                // first verb (here, a delay strictly longer than the budget) must not spend the window a
                // wait that has not even started yet is entitled to. The child here is an ordinary one —
                // killed, reaped, its status sitting there for the asking — so the verb must report the
                // REAL signal, and nothing may change hands.
                let waitTcs =
                    TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                let host =
                    { baseHost (Command.create "late-observer").Config with
                        // Deliberately NOT an already-completed task: a real `host.Wait()` (a pidfd/kqueue/
                        // SIGCHLD registration, `WaitForExitAsync` on Windows) hands back a pending task and
                        // resolves it a moment later, so an already-elapsed budget must not beat it to the answer.
                        Wait = fun () -> waitTcs.Task }

                let running = new RunningProcess(host)

                running.Kill()
                do! Task.Delay 400 // strictly longer than this fixture's 250ms budget

                let waiting = running.WaitAsync()
                do! Task.Delay 20
                waitTcs.TrySetResult(Outcome.Signalled(Some 9)) |> ignore
                let! outcome = waiting

                Assert.That(
                    outcome,
                    Is.EqualTo(Outcome.Signalled(Some 9)),
                    "a wait that starts after the budget elapsed must still read the real status, not fabricate Unobserved"
                )

                Assert.That(
                    PostKillReap.adoptedWaitCountFor waitTcs.Task,
                    Is.Zero,
                    "nothing was handed over: the wait concluded inside its own window"
                )

                do! (running :> IAsyncDisposable).DisposeAsync()
            })

    [<Test>]
    member this.``concurrent Stop, Kill and Dispose on one handle keep a single wait owner (T-351)``() : Task =
        this.WithShortPostKillBudget(fun () ->
            task {
                let waitTcs =
                    TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                let waitCalls = ref 0

                let host =
                    { baseHost (Command.create "wedged-child").Config with
                        Wait =
                            fun () ->
                                Interlocked.Increment(&waitCalls.contents) |> ignore
                                waitTcs.Task }

                let running = new RunningProcess(host)

                // Three teardown paths racing on one handle: a terminal wait, a graceful stop, and the
                // fire-and-forget kill — plus the dispose that follows them.
                let waiting = running.WaitAsync()
                let stopping = running.StopAsync(TimeSpan.FromMilliseconds 50.0)
                running.Kill()

                let! waited = waiting
                let! stopped = stopping
                do! (running :> IAsyncDisposable).DisposeAsync()

                Assert.That(
                    Volatile.Read(&waitCalls.contents),
                    Is.EqualTo 1,
                    "the racing paths must share ONE native wait; a second owner would race the reap"
                )

                Assert.That(
                    waited.IsUnobserved,
                    Is.True,
                    "the bounded wait must report honestly, not fabricate an exit"
                )

                Assert.That(stopped.IsUnobserved, Is.True, "the bounded stop must report honestly too")

                waitTcs.TrySetResult(Outcome.Exited 0) |> ignore
            })
