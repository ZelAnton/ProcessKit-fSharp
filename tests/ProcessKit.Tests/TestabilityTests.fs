namespace ProcessKit.Tests

open System.Threading
open System.Threading.Tasks
open System.Text
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit
open ProcessKit.Testing

/// A decorator over `DelegatingProcessRunner` that counts and forwards the `CaptureString` seam.
type private CountingRunner(inner: IProcessRunner) =
    inherit DelegatingProcessRunner(inner)
    let mutable count = 0
    member _.Count = count

    override this.CaptureStringAsync(command, cancellationToken) =
        count <- count + 1
        base.CaptureStringAsync(command, cancellationToken)

/// Tests for the additive testability seams: FakeProcess, failable/timed-out Reply, ScriptedRunner.Start,
/// and DelegatingProcessRunner.
[<TestFixture>]
type TestabilityTests() =

    /// Drain a merged stdout+stderr event stream to a list (for the PTY merged-stream assertions).
    let collectEvents (proc: RunningProcess) : Task<OutputEvent list> =
        task {
            let acc = ResizeArray<OutputEvent>()
            let enumerator = proc.OutputEventsAsync().GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then acc.Add enumerator.Current else more <- false

            do! enumerator.DisposeAsync()
            return List.ofSeq acc
        }

    let isStdout (event: OutputEvent) =
        match event with
        | OutputEvent.Stdout _ -> true
        | OutputEvent.Stderr _ -> false

    [<Test>]
    member _.``FakeProcess builds a RunningProcess the buffered verbs can consume``() : Task =
        task {
            use proc = FakeProcess.Create("svc").WithStdout("hello\nworld").WithExit(3).Build()

            match! proc.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "hello\nworld")
                Assert.That(result.Code, Is.EqualTo(Some 3))
            | Error error -> Assert.Fail $"FakeProcess OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``FakeProcess streams stdout lines``() : Task =
        task {
            use proc = FakeProcess.Create().WithStdoutLines([ "a"; "b"; "c" ]).Build()
            let collected = ResizeArray<string>()
            let enumerator = proc.StdoutLinesAsync().GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then
                    collected.Add enumerator.Current
                else
                    more <- false

            do! enumerator.DisposeAsync()
            CollectionAssert.AreEqual([| "a"; "b"; "c" |], collected.ToArray())
        }

    [<Test>]
    member _.``FakeProcess exposes a recorded interactive stdin only for KeepStdinOpen``() : Task =
        task {
            let command = Command.create "svc" |> Command.keepStdinOpen
            let fake = FakeProcess.OfCommand(command)
            use proc = fake.Build()

            match proc.TakeStdin() with
            | Some stdin ->
                do! stdin.WriteLineAsync "interactive payload"
                do! stdin.FinishAsync()
            | None -> Assert.Fail "a KeepStdinOpen fake must provide interactive stdin"

            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes "interactive payload\n", fake.StdinBytes)

            Assert.That(
                proc.TakeStdin(),
                Is.EqualTo(None: ProcessStdin option),
                "TakeStdin must hand a KeepStdinOpen fake's sink out only once"
            )

            use noStdin = FakeProcess.OfCommand(Command.create "svc").Build()

            Assert.That(
                noStdin.TakeStdin(),
                Is.EqualTo(None: ProcessStdin option),
                "a fake without KeepStdinOpen must not expose stdin"
            )
        }

    [<Test>]
    member _.``ScriptedRunner.StartAsync serves a fake streaming process``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "svc" ], Reply.Ok "ready\ngo")

            match! runner.StartAsync(Command.create "svc", CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc ->
                use proc = proc

                match! proc.OutputStringAsync() with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ready\ngo")
                | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a runner-level error``() : Task =
        task {
            let runner: IProcessRunner =
                ScriptedRunner().On([ "x" ], Reply.Error(ProcessError.NotFound("x", None)))

            match! runner.OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Error(ProcessError.NotFound _) -> ()
            | other -> Assert.Fail $"expected a NotFound error, got {other}"
        }

    [<Test>]
    member _.``ScriptedRunner can script a timeout``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.TimedOut)

            match! runner.OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    [<Test>]
    member _.``DelegatingProcessRunner forwards and lets a decorator intercept``() : Task =
        task {
            let scripted: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.Ok "hi")
            let counter = CountingRunner scripted

            match! (counter :> IProcessRunner).OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "hi")
                Assert.That(counter.Count, Is.EqualTo 1)
            | Error error -> Assert.Fail $"forwarded OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner capture and Start agree byte-for-byte``() : Task =
        task {
            // Both seam paths route through the same FakeProcess, so a reply with internal *and*
            // trailing newlines is captured identically whether read via OutputString or via a
            // started RunningProcess — guarding against the capture path drifting from Start.
            let runner: IProcessRunner =
                ScriptedRunner().On([ "svc" ], Reply.Ok "first\nsecond\n")

            let command = Command.create "svc"

            let! captured = runner.OutputStringAsync(command, CancellationToken.None)

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc ->
                use proc = proc
                let! streamed = proc.OutputStringAsync()

                match captured, streamed with
                | Ok cap, Ok str ->
                    Assert.That(str.Stdout, Is.EqualTo cap.Stdout)
                    Assert.That(str.Code, Is.EqualTo cap.Code)
                | _ -> Assert.Fail "expected both capture paths to succeed"
        }

    [<Test>]
    member _.``ScriptedRunner.OutputBytesAsync captures the scripted output``() : Task =
        task {
            let runner: IProcessRunner = ScriptedRunner().On([ "x" ], Reply.Ok "payload-bytes")

            match! runner.OutputBytesAsync(Command.create "x", CancellationToken.None) with
            | Ok result ->
                // ASCII payload, so the decoded bytes are encoding-agnostic.
                Assert.That(System.Text.Encoding.UTF8.GetString result.Stdout, Is.EqualTo "payload-bytes")
            | Error error -> Assert.Fail $"OutputBytes failed: {error.Message}"
        }

    [<Test>]
    member _.``ScriptedRunner honours the command's OkCodes``() : Task =
        task {
            // A non-zero exit the command accepts must surface as success through the scripted seam.
            let runner: IProcessRunner = ScriptedRunner().On([ "svc" ], Reply.Exit 3)
            let command = Command.create "svc" |> Command.okCodes [ 0; 3 ]

            match! runner.OutputStringAsync(command, CancellationToken.None) with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.True)
                Assert.That(result.Code, Is.EqualTo(Some 3))
                CollectionAssert.AreEqual([| 0; 3 |], result.AcceptedCodes)
            | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``FakeProcess.OfCommand carries the command's OkCodes into IsSuccess``() : Task =
        task {
            let command = Command.create "svc" |> Command.okCodes [ 0; 3 ]
            use proc = FakeProcess.OfCommand(command).WithExit(3).Build()

            match! proc.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.True)
                CollectionAssert.AreEqual([| 0; 3 |], result.AcceptedCodes)
            | Error error -> Assert.Fail $"OutputString failed: {error.Message}"
        }

    [<Test>]
    member _.``ProcessResult.Success builds an accepted result``() =
        let result = ProcessResult.Success "hello"
        Assert.That(result.Stdout, Is.EqualTo "hello")
        Assert.That(result.Code, Is.EqualTo(Some 0))
        Assert.That(result.IsSuccess, Is.True)

        match ProcessResult.ensureSuccess result with
        | Ok ok -> Assert.That(ok.Stdout, Is.EqualTo "hello")
        | Error error -> Assert.Fail $"expected success, got {error}"

    [<Test>]
    member _.``ProcessResult.Success works for a byte[] capture``() =
        let result = ProcessResult.Success [| 1uy; 2uy; 3uy |]
        CollectionAssert.AreEqual([| 1uy; 2uy; 3uy |], result.Stdout)
        Assert.That(result.IsSuccess, Is.True)

    [<Test>]
    member _.``ProcessResult.Failure rejects a zero exit code``() =
        Assert.Throws<System.ArgumentOutOfRangeException>(
            System.Action(fun () -> ProcessResult.Failure "" "" 0 |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``ProcessResult.Failure builds a non-zero exit that EnsureSuccess rejects``() =
        let result = ProcessResult.Failure "out" "boom" 2
        Assert.That(result.Code, Is.EqualTo(Some 2))
        Assert.That(result.Stderr, Is.EqualTo "boom")
        Assert.That(result.IsSuccess, Is.False)

        match ProcessResult.ensureSuccess result with
        | Error(ProcessError.Exit(_, code, _, stderr)) ->
            Assert.That(code, Is.EqualTo 2)
            Assert.That(stderr, Is.EqualTo "boom")
        | other -> Assert.Fail $"expected an Exit error, got {other}"

    [<Test>]
    member _.``ProcessResult.Create uses elapsed duration when a timeout's configured cause is unknown``() =
        let elapsed = System.TimeSpan.FromSeconds 5.0
        let result = ProcessResult.Create "" "" Outcome.TimedOut elapsed

        Assert.That(result.IsTimedOut, Is.True)
        Assert.That(result.IsSuccess, Is.False)
        Assert.That(result.Duration, Is.EqualTo elapsed)

        match ProcessResult.ensureSuccess result with
        | Error(ProcessError.Timeout(_, timeout, _, _)) ->
            Assert.That(timeout, Is.EqualTo elapsed, "configured duration unknown — using actual elapsed")
        | other -> Assert.Fail $"expected a Timeout error, got {other}"

    [<Test>]
    member _.``an Unobserved outcome never counts as success and maps to ProcessError.Unobserved``() =
        let result =
            ProcessResult.Create "" "" (Outcome.Unobserved "native failure") System.TimeSpan.Zero

        Assert.That(result.Code, Is.EqualTo None, "an unobserved outcome carries no exit code")
        Assert.That(result.Signal, Is.EqualTo None, "an unobserved outcome carries no signal")
        Assert.That(result.IsSuccess, Is.False, "an unobserved outcome must never count as success")

        match ProcessResult.ensureSuccess result with
        | Error(ProcessError.Unobserved(_, detail)) -> Assert.That(detail, Is.EqualTo "native failure")
        | other -> Assert.Fail $"expected an Unobserved error, got {other}"

    [<Test>]
    member _.``exitCode and probe error on an Unobserved outcome instead of inventing a code``() =
        let result =
            ProcessResult.Create "" "" (Outcome.Unobserved "native failure") System.TimeSpan.Zero

        match ProcessResult.exitCode result with
        | Error(ProcessError.Unobserved _) -> ()
        | other -> Assert.Fail $"expected an Unobserved error from exitCode, got {other}"

        match ProcessResult.probe result with
        | Error(ProcessError.Unobserved _) -> ()
        | other -> Assert.Fail $"expected an Unobserved error from probe, got {other}"

    [<Test>]
    member _.``ScriptedRunner can script an unobserved outcome``() : Task =
        task {
            let runner: IProcessRunner =
                ScriptedRunner().On([ "x" ], Reply.Unobserved "native failure")

            match! runner.OutputStringAsync(Command.create "x", CancellationToken.None) with
            | Ok result ->
                Assert.That(result.Outcome, Is.EqualTo(Outcome.Unobserved "native failure"))
                Assert.That(result.IsSuccess, Is.False, "an unobserved outcome must never count as success")

                match ProcessResult.ensureSuccess result with
                | Error(ProcessError.Unobserved(_, detail)) -> Assert.That(detail, Is.EqualTo "native failure")
                | other -> Assert.Fail $"expected an Unobserved error, got {other}"
            | Error error -> Assert.Fail $"expected a completed (non-success) result, got {error.Message}"
        }

    // --- PTY test doubles (Stage 5 — D3 merged stream / D10 recorded-no-op resize) -----------------

    [<Test>]
    member _.``a PTY FakeProcess emits only OutputEvent.Stdout on the merged stream (D3)``() : Task =
        task {
            use proc = FakeProcess.Create("tui").WithPty().WithStdout("line1\nline2").Build()

            let! events = collectEvents proc

            Assert.That(events |> List.forall isStdout, Is.True, "a PTY fake must never emit OutputEvent.Stderr")
            CollectionAssert.AreEqual([| "line1"; "line2" |], events |> List.map (fun e -> e.Text) |> List.toArray)
        }

    [<Test>]
    member _.``a PTY FakeProcess folds scripted stderr into the merged stream, never a Stderr event``() : Task =
        task {
            // Explicitly setting a separate stderr on a PTY fake must NOT surface an OutputEvent.Stderr:
            // under a real PTY (D3) there is one physical stream, so the stderr text folds into stdout.
            use proc =
                FakeProcess.Create("tui").WithPty().WithStdout("out").WithStderr("err").Build()

            let! events = collectEvents proc

            Assert.That(events |> List.forall isStdout, Is.True, "stderr must fold into the merged stdout stream")
            let texts = events |> List.map (fun e -> e.Text)
            Assert.That(texts, Does.Contain "out")
            Assert.That(texts, Does.Contain "err")
        }

    [<Test>]
    member _.``a PTY FakeProcess reports an empty ProcessResult.Stderr (no separate stderr channel)``() : Task =
        task {
            use proc =
                FakeProcess.Create("tui").WithPty().WithStdout("hello").WithStderr("diag").Build()

            match! proc.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stderr, Is.EqualTo "", "a PTY fake has no separate stderr")
                Assert.That(result.Stdout, Does.Contain "hello")
                Assert.That(result.Stdout, Does.Contain "diag", "scripted stderr folds into the merged stdout")
            | Error error -> Assert.Fail $"{error.Message}"
        }

    [<Test>]
    member _.``ResizeAsync on a PTY FakeProcess is a recorded no-op success with the last geometry (D10)``() : Task =
        task {
            let fake = FakeProcess.Create("tui").WithPty().WithStdout("x")
            use proc = fake.Build()

            Assert.That(fake.LastResize, Is.EqualTo None, "no resize requested yet")

            match! proc.ResizeAsync(120, 40) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"a PTY fake's ResizeAsync must succeed, not Unsupported: {error.Message}"

            Assert.That(fake.LastResize, Is.EqualTo(Some(120, 40)))

            // A later resize updates the recorded last-requested geometry.
            match! proc.ResizeAsync(80, 24) with
            | Ok() -> ()
            | Error error -> Assert.Fail $"{error.Message}"

            Assert.That(fake.LastResize, Is.EqualTo(Some(80, 24)))
        }

    [<Test>]
    member _.``ResizeAsync on a non-PTY FakeProcess stays a typed Unsupported (regression invariant)``() : Task =
        task {
            let fake = FakeProcess.Create("svc").WithStdout("x")
            use proc = fake.Build()

            match! proc.ResizeAsync(80, 24) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"a non-PTY fake's ResizeAsync must be Unsupported, got {other}"

            Assert.That(fake.LastResize, Is.EqualTo None, "a non-PTY fake records no resize")
        }

    [<Test>]
    member _.``ScriptedRunner serves a Command.Pty script as a merged-stream PTY fake``() : Task =
        task {
            let runner: IProcessRunner =
                ScriptedRunner().On([ "tui" ], (Reply.Ok "frame1\nframe2").WithStderr "diag")

            let command = Command.create "tui" |> Command.pty

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error.Message}"
            | Ok proc ->
                use proc = proc
                let! events = collectEvents proc

                Assert.That(
                    events |> List.forall isStdout,
                    Is.True,
                    "a Command.Pty script must replay as a merged stream (only OutputEvent.Stdout)"
                )

                let texts = events |> List.map (fun e -> e.Text)
                Assert.That(texts, Does.Contain "frame1")
                Assert.That(texts, Does.Contain "frame2")
                Assert.That(texts, Does.Contain "diag", "scripted stderr folds into the merged stream")
        }

    // --- Structural invocation journal (ScriptedRunner.Received / CountReceived) --------------------

    [<Test>]
    member _.``ScriptedRunner.Received records the secret-free shape and verb of each call in order``() : Task =
        task {
            let scripted = ScriptedRunner().On([ "git" ], Reply.Ok "ok").Fallback(Reply.Ok "")

            let runner = scripted :> IProcessRunner

            let gitCommit =
                Command.create "git"
                |> Command.args [ "commit"; "-m"; "msg" ]
                |> Command.currentDir "/repo"
                |> Command.env "GIT_AUTHOR_NAME" "Ada"

            // A text capture, then a bytes capture, then a spawn of a PTY command — one of each verb.
            let! _ = runner.OutputStringAsync(gitCommit, CancellationToken.None)
            let! _ = runner.OutputBytesAsync(Command.create "ls", CancellationToken.None)

            match! runner.StartAsync(Command.create "top" |> Command.pty, CancellationToken.None) with
            | Error error -> Assert.Fail $"Start failed: {error.Message}"
            | Ok proc -> do! (proc :> System.IAsyncDisposable).DisposeAsync().AsTask()

            let received = scripted.Received
            Assert.That(received.Count, Is.EqualTo 3, "one entry per verb call, in order")

            let first = received[0]
            Assert.That(first.Program, Is.EqualTo "git")
            CollectionAssert.AreEqual([| "commit"; "-m"; "msg" |], first.Args |> Seq.toArray)
            Assert.That(first.Cwd, Is.EqualTo(Some "/repo"))
            Assert.That(first.EnvNames, Does.Contain "GIT_AUTHOR_NAME")
            Assert.That(first.HasStdin, Is.False)
            Assert.That(first.Pty, Is.False)
            Assert.That(first.Verb, Is.EqualTo RunnerVerb.CaptureString)

            let second = received[1]
            Assert.That(second.Program, Is.EqualTo "ls")
            Assert.That(second.Verb, Is.EqualTo RunnerVerb.CaptureBytes)

            let third = received[2]
            Assert.That(third.Program, Is.EqualTo "top")
            Assert.That(third.Pty, Is.True, "a Command.Pty run is recorded as such")
            Assert.That(third.Verb, Is.EqualTo RunnerVerb.Spawn)
        }

    [<Test>]
    member _.``ScriptedRunner.CountReceived counts matching invocations without a decorator``() : Task =
        task {
            let scripted = ScriptedRunner().Fallback(Reply.Ok "")
            let runner = scripted :> IProcessRunner

            let commit () =
                Command.create "git" |> Command.args [ "commit"; "-m"; "x" ]

            let status () =
                Command.create "git" |> Command.args [ "status" ]

            let! _ = runner.OutputStringAsync(commit (), CancellationToken.None)
            let! _ = runner.OutputStringAsync(status (), CancellationToken.None)
            let! _ = runner.OutputStringAsync(commit (), CancellationToken.None)

            // Token matcher (mirrors On's semantics): exactly two `git commit` calls, one `git status`.
            Assert.That(scripted.CountReceived(fun inv -> inv.Matches [ "git"; "commit" ]), Is.EqualTo 2)
            Assert.That(scripted.CountReceived(fun inv -> inv.Matches [ "git"; "status" ]), Is.EqualTo 1)
            Assert.That(scripted.CountReceived(fun inv -> inv.Matches [ "push" ]), Is.EqualTo 0)
            // A plain structural predicate works too.
            Assert.That(scripted.CountReceived(fun inv -> inv.Program = "git"), Is.EqualTo 3)
        }

    [<Test>]
    member _.``ScriptedRunner.Received never captures env values or stdin content (secret invariant)``() : Task =
        task {
            let secretEnv = "s3cr3t-token-value"
            let secretStdin = "hunter2-secret-input"

            let scripted = ScriptedRunner().On([ "deploy" ], Reply.Ok "done")
            let runner = scripted :> IProcessRunner

            let command =
                Command.create "deploy"
                |> Command.args [ "--to"; "prod" ]
                |> Command.env "API_TOKEN" secretEnv
                |> Command.stdin (Stdin.FromString secretStdin)

            let! _ = runner.OutputStringAsync(command, CancellationToken.None)

            let inv = scripted.Received |> Seq.exactlyOne

            // Traverse EVERY field of the record; none may carry the secret env value or stdin content.
            let everyField =
                [ yield inv.Program
                  yield! inv.Args
                  yield inv.Cwd |> Option.defaultValue "<none>"
                  yield! inv.EnvNames
                  yield string inv.HasStdin
                  yield string inv.Pty
                  yield string inv.Verb ]

            for field in everyField do
                Assert.That(field, Does.Not.Contain secretEnv, "an env VALUE must never reach the journal")
                Assert.That(field, Does.Not.Contain secretStdin, "stdin CONTENT must never reach the journal")

            // The env NAME and the stdin PRESENCE flag are recorded — only the payloads are withheld.
            Assert.That(inv.EnvNames, Does.Contain "API_TOKEN")
            Assert.That(inv.HasStdin, Is.True, "the fact of stdin is recorded, its content is not")
        }

    [<Test>]
    member _.``ScriptedRunner builder methods yield a runner with its own empty journal``() : Task =
        task {
            let baseRunner = ScriptedRunner().On([ "x" ], Reply.Ok "a")
            let derived = baseRunner.Fallback(Reply.Ok "b")

            // Run only through the derived runner.
            let! _ = (derived :> IProcessRunner).OutputStringAsync(Command.create "x", CancellationToken.None)

            Assert.That(derived.Received.Count, Is.EqualTo 1)

            Assert.That(
                baseRunner.Received.Count,
                Is.EqualTo 0,
                "a builder method returns a runner with its own journal"
            )
        }
