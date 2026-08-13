namespace ProcessKit.Tests

open System.IO
open System.Text
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
    member _.``Windows raw fragments remain verbatim after quoted ordinary arguments``() =
        let command =
            Command.create "tool"
            |> Command.windowsRawArg "\"raw one\" raw-two"
            |> Command.arg "ordinary value"

        Assert.That(DryRunRunner.Render command, Is.EqualTo "tool \"ordinary value\" \"raw one\" raw-two")

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
    member _.``SpawnAsync models Command.Pty consistently with ScriptedRunner``() : Task =
        task {
            let command = Command.create "tui" |> Command.pty
            let dryRun: IProcessRunner = DryRunRunner()
            let scripted: IProcessRunner = ScriptedRunner().On([ "tui" ], Reply.Ok "tui")

            let observe (runner: IProcessRunner) =
                task {
                    match! runner.SpawnAsync(command, CancellationToken.None) with
                    | Error error -> return $"spawn error: {error.Message}", []
                    | Ok proc ->
                        use proc = proc
                        let! resize = proc.ResizeAsync(120, 40)

                        let resizeOutcome =
                            match resize with
                            | Ok() -> "ok"
                            | Error(ProcessError.Unsupported _) -> "unsupported"
                            | Error error -> $"error: {error.Message}"

                        let events = ResizeArray<string * string>()
                        let enumerator = proc.OutputEventsAsync().GetAsyncEnumerator()
                        let mutable more = true

                        while more do
                            let! has = enumerator.MoveNextAsync()

                            if has then
                                match enumerator.Current with
                                | OutputEvent.Stdout line -> events.Add("stdout", line.Text)
                                | OutputEvent.Stderr line -> events.Add("stderr", line.Text)
                            else
                                more <- false

                        do! enumerator.DisposeAsync()
                        return resizeOutcome, List.ofSeq events
                }

            let! dryRunObservation = observe dryRun
            let! scriptedObservation = observe scripted
            let expected = "ok", [ "stdout", "tui" ]

            let consistencyMessage =
                "DryRunRunner and ScriptedRunner must expose the same PTY behavior"

            let contractMessage =
                "a PTY handle must resize successfully and expose only the merged stdout stream"

            Assert.That(dryRunObservation, Is.EqualTo scriptedObservation, consistencyMessage)
            Assert.That(dryRunObservation, Is.EqualTo expected, contractMessage)
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

    [<Test>]
    member _.``a dry run neither reserves nor consumes a one-shot stdin source (T-342)``() : Task =
        task {
            // A one-shot stdin payload (`FromStream`/`FromLines`/`FromAsyncLines`) belongs to at most one
            // real incarnation: the launch boundary takes it before it spawns, so a second consumer is
            // refused rather than handed an exhausted source. A dry run spawns nothing at all, so it must
            // stay outside that ownership entirely — it validates the configuration (the render, the
            // history, the fact that a stdin source is set) and leaves the payload exactly where it was.
            // Otherwise a `--dry-run` preview would silently spend the input of the real run it previews.
            let runner: IProcessRunner = DryRunRunner()
            use stream = new MemoryStream(Encoding.UTF8.GetBytes "payload")

            let command =
                Command.create "git"
                |> Command.args [ "apply"; "-" ]
                |> Command.stdin (Stdin.FromStream stream)

            // Repeatable as often as the caller likes: three previews of the SAME command, each a
            // success with the identical render.
            for _ in 1..3 do
                match! runner.OutputStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "git apply -")
                | Error error -> Assert.Fail $"a dry run must not be refused a one-shot stdin source: {error.Message}"

            // And the payload itself is untouched — nothing read a byte of the stream, so a real run
            // afterwards would still be handed the caller's original input.
            Assert.That(stream.Position, Is.EqualTo 0L, "a dry run must not read the one-shot payload")
        }
