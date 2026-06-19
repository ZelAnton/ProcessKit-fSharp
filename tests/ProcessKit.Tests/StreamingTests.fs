namespace ProcessKit.Tests

open System.Collections.Generic
open System.Runtime.InteropServices
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
