namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

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

[<TestFixture>]
type StreamingTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

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
              StdinError = fun () -> None
              StartKill = ignore
              GracefulKill = fun _ -> Task.CompletedTask
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
              StdinError = fun () -> None
              StartKill = ignore
              GracefulKill = fun _ -> Task.CompletedTask
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
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputEvents merges stdout and stderr``() : Task =
        let script =
            if isWindows then
                "echo out&echo err 1>&2"
            else
                "echo out; echo err 1>&2"

        task {
            match! runner.StartAsync(shell script, CancellationToken.None) with
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
                  StdinError = fun () -> None
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
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
                  StdinError = fun () -> None
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
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
    [<Test>]
    member _.``spawnPosix fails instead of silently inheriting when open(/dev/null) fails``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises Native.Posix.spawnPosix's open(/dev/null) failure path"

            if not isLinux then
                Assert.Ignore "macOS: no /proc/self/fd to pin the exact rlimit deterministically"

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
            // By filling them first, we ensure the "budget of 1 new fd" assumption is exact.
            let gapCount = (maxOpenFd + 1) - usedCount
            let fillerFds = ResizeArray<int>(gapCount)

            for _ in 1..gapCount do
                let fd = DevNullExhaustion.openDevNull ("/dev/null", DevNullExhaustion.O_RDONLY)

                if fd >= 0 then
                    fillerFds.Add fd
                else
                    Assert.Fail $"failed to fill fd gap (errno {Marshal.GetLastWin32Error()})"

            // Now the fd table 0..maxOpenFd is fully occupied, so the next open will request fd `maxOpenFd + 1`.
            // Allow exactly one more fd beyond the process's current high-water mark: enough for
            // the default stdin's own open("/dev/null") — the first fd-creating call spawnPosix
            // makes, since this command sets no stdin source — to succeed, so the very NEXT open,
            // the explicit StdioMode.Null stdout below, is the one that fails with EMFILE.
            let mutable exhausted =
                DevNullExhaustion.RLimit(Current = int64 (maxOpenFd + 2), Max = original.Max)

            try
                Assert.That(
                    DevNullExhaustion.setrlimit (DevNullExhaustion.RLIMIT_NOFILE, &exhausted),
                    Is.EqualTo 0,
                    "setrlimit failed"
                )

                let command = shell "true" |> Command.stdout StdioMode.Null

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
                | Ok _ -> ()
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
            | Ok _ -> ()
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
            | Ok _ -> ()
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
            | Ok _ -> ()
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
                  StdinError = fun () -> None
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
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
            let total = 20
            let capacity = 3

            let config =
                (Command.create "test"
                 |> Command.streamBuffer (StreamBufferPolicy.Bounded(capacity, StreamFullMode.Error)))
                    .Config

            use running = syntheticStdoutProcess config (linesPayload total)
            let! drain = drainWithDeadline (running.StdoutLinesAsync()) 5000
            let! error = processError drain

            match error with
            | Some(ProcessError.OutputTooLarge _) -> Assert.Pass()
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
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
