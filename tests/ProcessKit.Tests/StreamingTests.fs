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

[<TestFixture>]
type StreamingTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
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
