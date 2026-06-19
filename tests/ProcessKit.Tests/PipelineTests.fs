namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
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

    [<Test>]
    member _.``two-stage pipeline wires stdout into the next stage's stdin``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.Run() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``three-stage pipeline chains every stage``() : Task =
        task {
            let pipeline =
                (emit [ "cherry"; "apple"; "banana" ]).Pipe(sortStage).Pipe(sortStage)

            match! pipeline.Run() with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana"; "cherry" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``OutputBytes captures the last stage's raw stdout``() : Task =
        task {
            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.OutputBytes() with
            | Ok result ->
                let text = Encoding.UTF8.GetString result.Stdout
                Assert.That(lines text, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``pipefail fails the pipeline on a checked stage's non-zero exit``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.Run() with
            | Error(ProcessError.Exit(_, 3, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected Exit 3, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OutputString keeps a non-zero pipefail exit as data``() : Task =
        task {
            let pipeline = (shell "exit 3").Pipe sortStage

            match! pipeline.OutputString() with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``UncheckedInPipe lets a stage fail without failing the pipeline``() : Task =
        task {
            let failing = (shell "exit 3").UncheckedInPipe()
            let pipeline = failing.Pipe(shell "echo done")

            match! pipeline.Run() with
            | Ok output -> Assert.That(output, Does.Contain "done")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``an unchecked failing last stage does not fail the pipeline``() : Task =
        task {
            let failingLast = (shell "exit 5").UncheckedInPipe()
            let pipeline = (emit [ "x" ]).Pipe failingLast

            match! pipeline.Run() with
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

                match! pipeline.Run() with
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

            match! pipeline.Run() with
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

            match! pipeline.Run cts.Token with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline module functions compose a pipeline``() : Task =
        task {
            let pipeline =
                emit [ "banana"; "apple" ] |> fun first -> Pipeline.create first sortStage

            match! Pipeline.run pipeline with
            | Ok output -> Assert.That(lines output, Is.EqualTo(box [ "apple"; "banana" ]))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task
