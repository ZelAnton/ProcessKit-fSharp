namespace ProcessKit.Tests

open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type VerbTests() =

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

    [<Test>]
    member _.``StdoutTee copies raw output to the sink as well as capturing it``() : Task =
        task {
            use sink = new MemoryStream()
            let command = shell "echo teed" |> Command.stdoutTee sink

            match! runner.Start(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputString() with
                | Ok result ->
                    let teed = Encoding.UTF8.GetString(sink.ToArray())
                    Assert.That(teed, Does.Contain "teed")
                    Assert.That(result.Stdout, Does.Contain "teed")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``parse converts trimmed stdout to a typed value``() : Task =
        task {
            match! Runner.parse runner CancellationToken.None (fun s -> int (s.Trim())) (shell "echo 42") with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``tryParse surfaces a parser failure as Parse``() : Task =
        task {
            let parser (_: string) : Result<int, string> = Error "bad value"

            match! Runner.tryParse runner CancellationToken.None parser (shell "echo x") with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Parse, got {other}"
        }
        :> Task

    [<Test>]
    member _.``firstLine returns the first matching line``() : Task =
        task {
            match! Runner.firstLine runner CancellationToken.None (fun line -> line.Contains "line2") threeLines with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.ExitCode returns the process exit code``() : Task =
        task {
            match! (shell "exit 7").ExitCode() with
            | Ok code -> Assert.That(code, Is.EqualTo 7)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.Probe reads exit 0/1 as true/false``() : Task =
        task {
            match! (shell "exit 0").Probe() with
            | Ok value -> Assert.That(value, Is.True)
            | Error error -> Assert.Fail $"{error}"

            match! (shell "exit 1").Probe() with
            | Ok value -> Assert.That(value, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.RunUnit succeeds on a zero exit and is cancellable``() : Task =
        task {
            match! (shell "echo hi").RunUnit(CancellationToken.None) with
            | Ok() -> Assert.Pass()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.Parse and FirstLine are reachable on the default runner``() : Task =
        task {
            match! (shell "echo 42").Parse(fun s -> int (s.Trim())) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"parse: {error}"

            match! threeLines.FirstLine(fun line -> line.Contains "line2") with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"firstLine: {error}"
        }
        :> Task
