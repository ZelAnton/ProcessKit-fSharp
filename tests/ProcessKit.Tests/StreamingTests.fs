namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
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

    [<Test>]
    member _.``start then OutputString captures stdout``() : Task =
        task {
            match! runner.Start(threeLines, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputString() with
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "line1")
                    Assert.That(result.Stdout, Does.Contain "line3")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``StdoutLines streams each line, then Finish reaps``() : Task =
        task {
            match! runner.Start(threeLines, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! lines = collect (running.StdoutLines())
                let! finished = running.Finish()
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
            match! runner.Start(shell script, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! events = collect (running.OutputEvents())

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

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputString() with
                | Ok result -> Assert.That(result.Stdout.Trim(), Is.EqualTo "hello")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``interactive stdin via TakeStdin feeds the child``() : Task =
        task {
            let command = shell "sort" |> Command.keepStdinOpen

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.TakeStdin() with
                | None -> Assert.Fail "expected an interactive stdin handle"
                | Some stdin ->
                    do! stdin.WriteLine "banana"
                    do! stdin.WriteLine "apple"
                    do! stdin.Finish() // close stdin -> sort emits and exits

                    match! running.OutputString() with
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain "apple")
                        Assert.That(result.Stdout, Does.Contain "banana")
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``concurrent stdin runs do not cross-inherit pipes and deadlock``() : Task =
        task {
            let runOne (i: int) =
                task {
                    let command = shell "sort" |> Command.stdin (Stdin.FromString $"value{i}\n")

                    match! runner.OutputString(command, CancellationToken.None) with
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

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! _ = running.OutputString()
                Assert.That(captured, Does.Contain "line1")
        }
        :> Task

    [<Test>]
    member _.``a throwing OnStdoutLine handler surfaces on StdoutLines instead of hanging``() : Task =
        task {
            let command =
                threeLines
                |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom"))

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let! drain = drainWithDeadline (running.StdoutLines()) 10000
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

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                let! drain = drainWithDeadline (running.OutputEvents()) 10000
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

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                use running = running
                // Finish awaits streamOutcome, which the stdout pump faults via the re-raise — so the
                // error must propagate here (not be swallowed into an Ok result).
                let finish = running.Finish() :> Task
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
    member _.``a faulted terminal verb still reaps the tree``() : Task =
        task {
            // An instrumented host whose stdout yields one line (firing a handler that throws) and
            // whose Teardown records that it ran. A verb that faults mid-flight must still reap.
            let mutable teardowns = 0

            let makeHost () : RunningHost =
                let config =
                    (Command.create "test"
                     |> Command.onStdoutLine (fun _ -> raise (InvalidOperationException "boom")))
                        .Config

                { Config = config
                  Pid = None
                  Stdout = Some(new MemoryStream(Encoding.UTF8.GetBytes "line1\n") :> Stream)
                  Stderr = None
                  Stdin = None
                  StartTime = DateTime.UtcNow
                  StartedTimestamp = Stopwatch.GetTimestamp()
                  Wait = fun () -> Task.FromResult(Outcome.Exited 0)
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
                  Teardown =
                    fun () ->
                        teardowns <- teardowns + 1
                        ValueTask() }

            let faultsAndReaps (verb: RunningProcess -> Task) =
                task {
                    teardowns <- 0
                    let running = new RunningProcess(makeHost ())
                    let mutable faulted = false

                    try
                        do! verb running
                    with :? InvalidOperationException ->
                        faulted <- true

                    Assert.That(faulted, Is.True, "the verb should fault on a throwing handler")
                    Assert.That(teardowns, Is.GreaterThanOrEqualTo 1, "the faulted verb must still reap the tree")
                }

            // The capture verbs (OnStdoutLine fires through the buffer pump) and Finish (through the
            // stream-channel pump) all fault on the throwing handler — each must still tear down.
            do! faultsAndReaps (fun p -> p.OutputString() :> Task)
            do! faultsAndReaps (fun p -> p.Finish() :> Task)
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
                  StartKill = ignore
                  GracefulKill = fun _ -> Task.CompletedTask
                  Teardown = fun () -> ValueTask() }

            let profile =
                (new RunningProcess(host)).Profile(TimeSpan.FromMilliseconds 5.0) :> Task

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

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputString() with
                | Ok result -> Assert.That(result.Stdout, Is.Empty)
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a fail-loud ceiling errors when output exceeds the cap``() : Task =
        task {
            let command = threeLines |> Command.outputBuffer (OutputBufferPolicy.FailLoud 1)

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputString() with
                | Error(ProcessError.OutputTooLarge _) -> Assert.Pass()
                | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task
