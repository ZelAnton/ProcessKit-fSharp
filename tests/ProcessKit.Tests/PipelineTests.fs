namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

[<TestFixture>]
type PipelineTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // `sort` exists on both Windows (System32) and Unix; with no file argument it reads stdin,
    // sorts the lines, and writes them to stdout — a portable, shell-free pipeline stage.
    let sortStage = Command.create "sort"

    // Emit `lines` in the given order (unsorted) so a downstream `sort` has work to do.
    let emit (lines: string list) =
        if isWindows then
            shell (lines |> List.map (sprintf "echo %s") |> String.concat "&")
        else
            shell (lines |> List.map (sprintf "echo %s") |> String.concat "; ")

    // Split captured output into trimmed, non-empty lines (newline + CR agnostic).
    let lines (text: string) =
        text.Split('\n')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.toList

    // Run a two-stage `emit | sort` pipeline, optionally capping the LAST stage's OutputBuffer, and
    // capture its stdout as raw bytes. The sorted output is deterministic across runs, so an uncapped
    // run is a stable oracle for the capped tail/head. Used by the last-stage byte-cap tests (T-011).
    let pipelineBytes (lastPolicy: OutputBufferPolicy option) =
        let last =
            match lastPolicy with
            | Some policy -> sortStage |> Command.outputBuffer policy
            | None -> sortStage

        ((emit [ "banana"; "apple" ]).Pipe last).OutputBytesAsync()

    [<Test>]
    member _.``two-stage pipeline wires stdout into the next stage's stdin``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``three-stage pipeline chains every stage``() : Task =
        task {
            let pipeline =
                (emit [ "cherry"; "apple"; "banana" ]).Pipe(sortStage).Pipe(sortStage)

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana"; "cherry" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes captures the last stage's raw stdout``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                let text = Encoding.UTF8.GetString result.Stdout
                Assert.That(lines text, Is.EqualTo(box [ "apple"; "banana" ]))
                Assert.That(result.Truncated, Is.False) // no cap on the last stage -> nothing truncated
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- The last stage's OutputBuffer byte cap bounds the captured pipeline stdout (T-011) ---

    [<Test>]
    member _.``pipeline last-stage DropOldest keeps the tail and flags truncation``() : Task =
        task {
            let cap = 4
            let! full = pipelineBytes None
            let! result = pipelineBytes (Some(OutputBufferPolicy.Unbounded.WithMaxBytes cap))

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the sorted output must exceed the cap")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[full.Stdout.Length - cap ..], result.Stdout) // the tail
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline last-stage DropNewest keeps the head and flags truncation``() : Task =
        task {
            let cap = 4
            let! full = pipelineBytes None

            let! result =
                pipelineBytes (
                    Some((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.DropNewest)
                )

            match full, result with
            | Ok full, Ok result ->
                Assert.That(full.Stdout.Length, Is.GreaterThan cap, "the sorted output must exceed the cap")
                Assert.That(result.Truncated, Is.True)
                Assert.That(result.Stdout.Length, Is.EqualTo cap)
                CollectionAssert.AreEqual(full.Stdout[.. cap - 1], result.Stdout) // the head
            | other -> Assert.Fail $"expected both captures to succeed, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline last-stage Error trips OutputTooLarge once the byte cap is exceeded``() : Task =
        task {
            match!
                pipelineBytes (Some((OutputBufferPolicy.Unbounded.WithMaxBytes 3).WithOverflow OverflowMode.Error))
            with
            | Error(ProcessError.OutputTooLarge(_, _, byteLimit, _, totalBytes)) ->
                Assert.That(byteLimit, Is.EqualTo(Some 3))
                Assert.That(totalBytes, Is.GreaterThan 3)
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipeline OutputString also errors OutputTooLarge under the last-stage byte cap``() : Task =
        task {
            // The string verb decodes the raw byte capture, so the same byte cap Error trips it too.
            let last =
                sortStage
                |> Command.outputBuffer ((OutputBufferPolicy.Unbounded.WithMaxBytes 3).WithOverflow OverflowMode.Error)

            match! ((emit [ "banana"; "apple" ]).Pipe last).OutputStringAsync() with
            | Error(ProcessError.OutputTooLarge _) -> Assert.Pass()
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``pipefail fails the pipeline on a checked stage's non-zero exit``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.RunAsync() with
            | Error(ProcessError.Exit(_, 3, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected Exit 3, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputString keeps a non-zero pipefail exit as data``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.OutputStringAsync() with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``UncheckedInPipe lets a stage fail without failing the pipeline``() : Task =
        task {
            let failing = (shell "exit 3").UncheckedInPipe()
            let pipeline = failing.Pipe(shell "echo done")

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(output, Does.Contain "done")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``an unchecked failing last stage does not fail the pipeline``() : Task =
        task {
            let failingLast = (shell "exit 5").UncheckedInPipe()
            let pipeline = (emit [ "x" ]).Pipe failingLast

            match! pipeline.RunAsync() with
            | Ok _ -> Assert.Pass()
            | Error error -> Assert.Fail $"expected success (last stage unchecked), got {error}"
        }
        :> Task

    [<Test>]
    member _.``an early-exiting consumer does not hang the producer``() : Task =
        task {
            if isWindows then
                // Windows pipes have no SIGPIPE: a producer that ignores write errors cannot be
                // unblocked by closing the read end, so this guarantee is POSIX-only.
                Assert.Ignore "POSIX-only (no SIGPIPE on Windows)"
            else
                // `yes` writes forever; `head -n 1` reads one line and exits. Closing the read end
                // must SIGPIPE `yes` so the pipeline completes instead of blocking on a full pipe.
                let yes = (Command.create "yes").UncheckedInPipe()
                let head = Command.create "head" |> Command.args [ "-n"; "1" ]
                let pipeline = (yes.Pipe head).Timeout(TimeSpan.FromSeconds 15.0)

                match! pipeline.RunAsync() with
                | Ok output -> Assert.That(output.Trim(), Is.EqualTo "y")
                | Error error -> Assert.Fail $"expected the pipeline to complete, got {error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout reports TimedOut and fails Run``() : Task =
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                (emit [ "hi" ]).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a cancelled token cancels the whole pipeline``() : Task =
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline = (emit [ "hi" ]).Pipe sleeper
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync cts.Token with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline module builders compose a pipeline``() : Task =
        task {
            // Builders pipe (module); the terminal verb is an instance method.
            let pipeline =
                Pipeline.create (emit [ "banana"; "apple" ]) sortStage
                |> Pipeline.timeout (TimeSpan.FromSeconds 30.0)

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline parse converts the trimmed pipefail output to a typed value``() : Task =
        task {
            let pipeline = (emit [ "42" ]).Pipe sortStage

            match! pipeline.ParseAsync(fun s -> int (s.Trim())) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline tryParse uses the TryParser delegate and maps a thrown parser to Parse``() : Task =
        task {
            let pipeline = (emit [ "42" ]).Pipe sortStage

            // Success: the C#-friendly delegate parses the trimmed pipefail output.
            let tryInt =
                TryParser(fun (s: string) (v: byref<int>) -> Int32.TryParse(s.Trim(), &v))

            match! pipeline.TryParseAsync tryInt with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"

            // A parser that throws is surfaced as ProcessError.Parse, not a faulted task.
            let tryThrow = TryParser(fun (_: string) (_: byref<int>) -> failwith "boom")

            match! pipeline.TryParseAsync tryThrow with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    // --- Fail-fast: per-stage config a pipeline cannot honour is rejected when the stage is piped
    //     (T-012). A pipeline spawns stages directly and rewires stdin, so these settings never took
    //     effect; rejecting at build time replaces the previous silent drop. ---

    [<Test>]
    member _.``a Stdin source on a stage after the first is rejected when piped``() =
        let withStdin () =
            sortStage |> Command.stdin (Stdin.FromString "y\n")

        // The two-argument Pipe: the appended (second) stage — index 1 — carries a source.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withStdin ()) |> ignore))
        |> ignore

        // A later stage appended through Pipeline.Pipe — index 2 — is caught the same way.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(sortStage).Pipe(withStdin ()) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage Timeout is rejected on any stage when piped``() =
        let withTimeout cmd =
            cmd |> Command.timeout (TimeSpan.FromSeconds 5.0)

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withTimeout (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withTimeout sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``a per-stage Retry is rejected when piped``() =
        let withRetry cmd =
            cmd |> Command.retry 3 (TimeSpan.FromMilliseconds 10.0) (fun _ -> true)

        // On the first stage...
        Assert.Throws<ArgumentException>(Action(fun () -> (withRetry (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage.
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withRetry sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``a Stdin source on stage 0 stays allowed and feeds the whole chain``() : Task =
        task {
            // Regression: only stages AFTER the first reject a source; stage 0 feeds the chain.
            let pipeline =
                (sortStage |> Command.stdin (Stdin.FromString "banana\napple\n")).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task
