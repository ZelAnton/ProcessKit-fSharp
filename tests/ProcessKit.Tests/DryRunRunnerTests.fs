namespace ProcessKit.Tests

open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// Tests demonstrating `DryRunRunner` as a `--dry-run` seam: construct a `Command`, run it through the
/// runner instead of a real `IProcessRunner`, and inspect the deterministic render (and call history)
/// instead of anything actually being spawned.
[<TestFixture>]
type DryRunRunnerTests() =

    [<Test>]
    member _.``a --dry-run consumer swaps in DryRunRunner and inspects the render instead of a real exit``() : Task =
        task {
            // A consumer wires an IProcessRunner and, under --dry-run, substitutes DryRunRunner for the
            // real one — no subprocess is spawned, but the verb vocabulary (OutputStringAsync here) is
            // unchanged.
            let runner: IProcessRunner = DryRunRunner()

            let command =
                Command.create "git" |> Command.args [ "commit"; "-m"; "release notes" ]

            match! runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.True)
                Assert.That(result.Stdout, Is.EqualTo "git commit -m \"release notes\"")
            | Error error -> Assert.Fail $"OutputStringAsync failed: {error.Message}"
        }

    [<Test>]
    member _.``Render is deterministic and includes the working directory when set``() =
        let command =
            Command.create "tool"
            |> Command.args [ "a"; "b" ]
            |> Command.currentDir "/srv/app"

        let renderedOnce = DryRunRunner.Render command
        let renderedAgain = DryRunRunner.Render command

        Assert.That(renderedAgain, Is.EqualTo renderedOnce)
        Assert.That(renderedOnce, Is.EqualTo "tool a b (cwd: /srv/app)")

    [<Test>]
    member _.``Render quotes an argument containing whitespace``() =
        let command = Command.create "echo" |> Command.arg "hello world"
        Assert.That(DryRunRunner.Render command, Is.EqualTo "echo \"hello world\"")

    [<Test>]
    member _.``History records every command served, in call order``() : Task =
        task {
            let runner = DryRunRunner()
            let first = Command.create "one"
            let second = Command.create "two" |> Command.arg "x"

            let! _ = (runner :> IProcessRunner).CaptureStringAsync(first, CancellationToken.None)
            let! _ = (runner :> IProcessRunner).CaptureStringAsync(second, CancellationToken.None)

            let history = runner.History
            Assert.That(history.Count, Is.EqualTo 2)
            Assert.That(history[0], Is.EqualTo "one")
            Assert.That(history[1], Is.EqualTo "two x")
        }

    [<Test>]
    member _.``CaptureBytesAsync returns the render as UTF-8 bytes``() : Task =
        task {
            let runner: IProcessRunner = DryRunRunner()
            let command = Command.create "svc" |> Command.arg "status"

            match! runner.CaptureBytesAsync(command, CancellationToken.None) with
            | Ok result -> Assert.That(System.Text.Encoding.UTF8.GetString result.Stdout, Is.EqualTo "svc status")
            | Error error -> Assert.Fail $"CaptureBytesAsync failed: {error.Message}"
        }

    [<Test>]
    member _.``SpawnAsync serves a live handle whose OutputStringAsync agrees with CaptureStringAsync``() : Task =
        task {
            // The chosen SpawnAsync/streaming behaviour (documented on the type): served via the same
            // FakeProcess-backed render as the capture verbs, so both paths agree byte-for-byte.
            let runner: IProcessRunner = DryRunRunner()
            let command = Command.create "svc" |> Command.arg "status"

            let! captured = runner.CaptureStringAsync(command, CancellationToken.None)

            match! runner.SpawnAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"SpawnAsync failed: {error.Message}"
            | Ok proc ->
                use proc = proc
                let! streamed = proc.OutputStringAsync()

                match captured, streamed with
                | Ok cap, Ok str -> Assert.That(str.Stdout, Is.EqualTo cap.Stdout)
                | _ -> Assert.Fail "expected both paths to succeed"
        }

    [<Test>]
    member _.``a cancelled call is reported as an error and is not recorded``() : Task =
        task {
            let runner = DryRunRunner()
            use cts = new CancellationTokenSource()
            cts.Cancel()

            match! (runner :> IProcessRunner).CaptureStringAsync(Command.create "x", cts.Token) with
            | Error(ProcessError.Cancelled _) -> ()
            | other -> Assert.Fail $"expected a Cancelled error, got {other}"

            Assert.That(runner.History.Count, Is.EqualTo 0)
        }
