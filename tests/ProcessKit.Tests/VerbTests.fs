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

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
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
    member _.``Command.ExitCodeAsync returns the process exit code``() : Task =
        task {
            match! (shell "exit 7").ExitCodeAsync() with
            | Ok code -> Assert.That(code, Is.EqualTo 7)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.ProbeAsync reads exit 0/1 as true/false``() : Task =
        task {
            match! (shell "exit 0").ProbeAsync() with
            | Ok value -> Assert.That(value, Is.True)
            | Error error -> Assert.Fail $"{error}"

            match! (shell "exit 1").ProbeAsync() with
            | Ok value -> Assert.That(value, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.RunUnitAsync succeeds on a zero exit and is cancellable``() : Task =
        task {
            match! (shell "echo hi").RunUnitAsync(CancellationToken.None) with
            | Ok() -> Assert.Pass()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.Parse/TryParse/FirstLine are reachable on the default runner (both overloads)``() : Task =
        task {
            // Parse — no-token and cancellation-token overloads.
            match! (shell "echo 42").ParseAsync(fun s -> int (s.Trim())) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"parse: {error}"

            match! (shell "echo 42").ParseAsync((fun s -> int (s.Trim())), CancellationToken.None) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"parse(ct): {error}"

            // TryParse — the C#-friendly TryParser delegate (BCL try-parse shape), no-token and ct overloads.
            let tryInt =
                TryParser(fun (s: string) (v: byref<int>) -> System.Int32.TryParse(s.Trim(), &v))

            match! (shell "echo 42").TryParseAsync tryInt with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"tryParse: {error}"

            match! (shell "echo 42").TryParseAsync(tryInt, CancellationToken.None) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"tryParse(ct): {error}"

            // A parser that rejects the output becomes ProcessError.Parse.
            let tryFail = TryParser(fun (_: string) (_: byref<int>) -> false)

            match! (shell "echo 42").TryParseAsync tryFail with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error, got {other}"

            // A parser that *throws* (rather than returning false) is also surfaced as ProcessError.Parse.
            let tryThrow = TryParser(fun (_: string) (_: byref<int>) -> failwith "boom")

            match! (shell "echo 42").TryParseAsync tryThrow with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error from a throwing parser, got {other}"

            // FirstLine — no-token and cancellation-token overloads.
            match! threeLines.FirstLineAsync(fun line -> line.Contains "line2") with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"firstLine: {error}"

            match! threeLines.FirstLineAsync((fun line -> line.Contains "line2"), CancellationToken.None) with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"firstLine(ct): {error}"
        }
        :> Task
