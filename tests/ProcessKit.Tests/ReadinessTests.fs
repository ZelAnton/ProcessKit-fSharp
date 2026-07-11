namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Net
open System.Net.Sockets
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

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
    // `StreamingTests.syntheticStdoutProcess`'s comment for the same rationale). `Wait` resolves
    // immediately; nothing here exercises the process's own exit path. Returns the process plus the
    // two writer-side `Stream`s the test uses to play "the child" writing its startup burst.
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
              Wait = fun () -> Task.FromResult(Outcome.Exited 0)
              StdinError = fun () -> None
              StartKill = ignore
              GracefulKill = fun _ -> Task.CompletedTask
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
                    neverConnects
                    "test"
                    endpoint
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
            // handshake) and only ever succeeds — 300ms after invocation, well past the 100ms shared
            // deadline. Regression test for two things at once: (1) the deadline is honored even though
            // the connect itself never observes cancellation — this must not block for the connect's own
            // 300ms, only race it against the deadline, and (2) once the abandoned connect does complete
            // in the background, its stale success is still reported as NotReady, never surfaced as a
            // late `Ok` from this call (which has already returned).
            let mutable lateConnectTask: Task = Unchecked.defaultof<Task>

            let lateConnect (_: IPEndPoint) (_: CancellationToken) : Task =
                let t = task { do! Task.Delay 300 }
                lateConnectTask <- t
                t

            let endpoint = IPEndPoint(IPAddress.Loopback, 1)
            let elapsed = Stopwatch.StartNew()

            match!
                ReadinessProbe.waitForPortUsing
                    lateConnect
                    "test"
                    endpoint
                    (TimeSpan.FromMilliseconds 100.0)
                    CancellationToken.None
            with
            | Error(ProcessError.NotReady _) ->
                // Bounded well below the connect's own 300ms — the fix under test is that a
                // non-cooperative connect is raced against the shared deadline rather than blocked on.
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds 250.0))
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
                    faultsLate
                    "test"
                    endpoint
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
                // from `startLate` itself; it only signals that teardown pre-empted it.
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
