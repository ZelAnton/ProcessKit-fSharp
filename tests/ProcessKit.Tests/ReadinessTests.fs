namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Net
open System.Net.Sockets
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
