namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// A policy that rewrites every line it is shown, tagging it with the stream it came from — so an
/// assertion can prove both THAT the seam ran and WHICH discriminator it was handed.
type private TaggingPolicy(name: string) =
    interface ICapturePolicy with
        member _.Name = name

        member _.OnCapture(stream, line) =
            let tag =
                match stream with
                | CaptureStream.Stdout -> "out"
                | CaptureStream.Stderr -> "err"

            $"[{tag}]{line}"

/// The motivating policy: scrub a secret out of the retained capture. Deliberately idempotent (running
/// it over its own output changes nothing), which is what makes a cassette round trip safe.
type private RedactingPolicy(secret: string) =
    interface ICapturePolicy with
        member _.Name = "redact-secret"
        member _.OnCapture(_stream, line) = line.Replace(secret, "***")

/// A policy that throws on the line carrying the secret — the fail-closed case. It counts its calls so
/// a test can prove the policy stayed ACTIVE for the lines after the failure rather than being disabled.
///
/// The counter is atomic because one policy instance serves BOTH streams and the two pumps run
/// concurrently — the same thread-safety `ICapturePolicy` asks its implementors for, honoured here so
/// the call-count assertion cannot flake.
type private ThrowingPolicy(trigger: string) =
    let mutable calls = 0

    member _.Calls = Volatile.Read(&calls)

    interface ICapturePolicy with
        member _.Name = "throws-on-secret"

        member _.OnCapture(_stream, line) =
            Interlocked.Increment(&calls) |> ignore

            if line.Contains(trigger, StringComparison.Ordinal) then
                failwith "policy bug"

            line

/// A policy that returns `null` — what a consumer compiled without nullable reference types can hand
/// back through a `string`-returning member, and the second half of the fail-closed rule.
type private NullReturningPolicy() =
    interface ICapturePolicy with
        member _.Name = "returns-null"
        member _.OnCapture(_stream, _line) = Unchecked.defaultof<string>

/// A deterministic inner runner for the cassette recordings below: it answers the string verb from a
/// real `FakeProcess` built over the command, so the recording half of a round trip goes through the
/// very capture path a live run uses (and therefore through the capture seam).
type private FakeBackedRunner(stdout: string, stderr: string) =
    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            task {
                use running =
                    FakeProcess.OfCommand(command).WithStdout(stdout).WithStderr(stderr).Build()

                return! running.OutputStringAsync()
            }

        member _.CaptureBytesAsync(command, _cancellationToken) =
            task {
                use running =
                    FakeProcess.OfCommand(command).WithStdout(stdout).WithStderr(stderr).Build()

                return! running.OutputBytesAsync()
            }

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FakeBackedRunner has no Spawn"))

/// `Command.CapturePolicy` — the redaction-at-capture seam: that it shapes the retained capture backlog,
/// that it shapes NOTHING else (handlers, tees, the streaming verbs, a raw byte capture), that a failing
/// policy fails closed, and that the three test doubles agree with a live capture on all of it.
[<TestFixture>]
type CapturePolicyTests() =

    let secretLine = "token=s3cr3t-value"

    /// Drain a streaming line enumerator to a list.
    let collectLines (running: RunningProcess) : Task<string list> =
        task {
            let acc = ResizeArray<string>()
            let enumerator = running.StdoutLinesAsync().GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then acc.Add enumerator.Current else more <- false

            do! enumerator.DisposeAsync()
            return List.ofSeq acc
        }

    let outputOf (running: RunningProcess) : Task<ProcessResult<string>> =
        task {
            match! running.OutputStringAsync() with
            | Ok result -> return result
            | Error error ->
                Assert.Fail $"OutputStringAsync failed: {error.Message}"
                return failwith "unreachable"
        }

    let tempCassette () =
        Path.Combine(Path.GetTempPath(), $"pk-capture-policy-{Guid.NewGuid():N}.json")

    // ---- (1) the transform runs on the way into the backlog ---------------------------------------

    [<Test>]
    member _.``The policy shapes the retained stdout and stderr captures``() : Task =
        task {
            let command =
                Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            use running =
                FakeProcess
                    .OfCommand(command)
                    .WithStdout($"before\n{secretLine}\nafter")
                    .WithStderr($"stderr {secretLine}")
                    .Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "before\ntoken=***\nafter")
            Assert.That(result.Stderr, Is.EqualTo "stderr token=***")
            // The whole point: the raw secret is nowhere in what was retained.
            Assert.That(result.Stdout.Contains("s3cr3t-value", StringComparison.Ordinal), Is.False)
            Assert.That(result.Stderr.Contains("s3cr3t-value", StringComparison.Ordinal), Is.False)
        }

    [<Test>]
    member _.``The policy is told which stream each line came from``() : Task =
        task {
            let command = Command.create "svc" |> Command.capturePolicy (TaggingPolicy "tagger")

            use running =
                FakeProcess.OfCommand(command).WithStdout("a\nb").WithStderr("c").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "[out]a\n[out]b")
            Assert.That(result.Stderr, Is.EqualTo "[err]c")
        }

    [<Test>]
    member _.``No configured policy retains exactly what was framed``() : Task =
        task {
            use running = FakeProcess.Create("svc").WithStdout($"before\n{secretLine}").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo $"before\n{secretLine}")
        }

    [<Test>]
    member _.``The configured policy name is introspectable and the last call wins``() =
        let command =
            Command.create "svc"
            |> Command.capturePolicy (TaggingPolicy "first")
            |> Command.capturePolicy (TaggingPolicy "second")

        Assert.That((Command.create "svc").ConfiguredCapturePolicyName, Is.EqualTo None)
        Assert.That(command.ConfiguredCapturePolicyName, Is.EqualTo(Some "second"))

    [<Test>]
    member _.``A null policy is rejected at the builder boundary``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () ->
                (Command.create "svc").CapturePolicy(Unchecked.defaultof<ICapturePolicy>)
                |> ignore)
        )
        |> ignore

    // ---- (2) input the decoder could not represent -------------------------------------------------

    [<Test>]
    member _.``Undecodable stdout bytes still reach the policy and are still shaped``() : Task =
        task {
            // A lone 0x80 continuation byte is not valid UTF-8; the decoder replaces it with U+FFFD.
            // The line must still be shown to the policy (no bypass on a decode failure) and the
            // capture must carry what the policy returned.
            let payload =
                Array.concat
                    [ Encoding.UTF8.GetBytes "keep\n"
                      [| 0x80uy |]
                      Encoding.UTF8.GetBytes secretLine ]

            let command =
                Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            use running = FakeProcess.OfCommand(command).WithStdoutBytes(payload).Build()

            let! result = outputOf running

            let lines = result.Stdout.Split '\n'
            Assert.That(lines.Length, Is.EqualTo 2)
            Assert.That(lines[0], Is.EqualTo "keep")
            // The undecodable byte survives as the replacement character, INSIDE a line the policy
            // shaped — so a decode failure neither drops the line nor lets it past the seam unshaped.
            Assert.That(lines[1], Is.EqualTo "�token=***")
            Assert.That(result.Stdout.Contains("s3cr3t-value", StringComparison.Ordinal), Is.False)
        }

    [<Test>]
    member _.``An empty line is shown to the policy like any other``() : Task =
        task {
            let command = Command.create "svc" |> Command.capturePolicy (TaggingPolicy "tagger")

            use running = FakeProcess.OfCommand(command).WithStdout("a\n\nb").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "[out]a\n[out]\n[out]b")
        }

    // ---- (3) fail closed --------------------------------------------------------------------------

    [<Test>]
    member _.``A throwing policy retains the line empty rather than raw and stays active``() : Task =
        task {
            let policy = ThrowingPolicy "s3cr3t-value"

            let command = Command.create "svc" |> Command.capturePolicy policy

            use running =
                FakeProcess.OfCommand(command).WithStdout($"before\n{secretLine}\nafter").WithStderr(secretLine).Build()

            let! result = outputOf running

            // The offending line is blank — never the raw line the policy failed to scrub — and the
            // lines around it are untouched, so the policy was not disabled by its own failure.
            Assert.That(result.Stdout, Is.EqualTo "before\n\nafter")
            Assert.That(result.Stderr, Is.EqualTo "")
            Assert.That(result.Stdout.Contains("s3cr3t", StringComparison.Ordinal), Is.False)
            Assert.That(result.Stderr.Contains("s3cr3t", StringComparison.Ordinal), Is.False)
            // Four calls: three stdout lines plus the stderr one — the throw on line two did not stop
            // line three (or stderr) from being offered to the same policy.
            Assert.That(policy.Calls, Is.EqualTo 4)
        }

    [<Test>]
    member _.``A throwing policy does not fail the run``() : Task =
        task {
            let command =
                Command.create "svc" |> Command.capturePolicy (ThrowingPolicy "s3cr3t-value")

            use running =
                FakeProcess.OfCommand(command).WithStdout(secretLine).WithExit(0).Build()

            match! running.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Code, Is.EqualTo(Some 0))
                Assert.That(result.Stdout, Is.EqualTo "")
            | Error error -> Assert.Fail $"a failing capture policy must not fail the run: {error.Message}"
        }

    [<Test>]
    member _.``A policy returning null retains the line empty rather than raw``() : Task =
        task {
            let command = Command.create "svc" |> Command.capturePolicy (NullReturningPolicy())

            use running = FakeProcess.OfCommand(command).WithStdout($"a\n{secretLine}").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "\n")
            Assert.That(result.Stdout.Contains("s3cr3t", StringComparison.Ordinal), Is.False)
        }

    // ---- the boundary: what the seam deliberately does NOT shape -----------------------------------

    [<Test>]
    member _.``Handlers and tees see the unshaped line``() : Task =
        task {
            let handled = ResizeArray<string>()
            use tee = new MemoryStream()

            let command =
                Command.create "svc"
                |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")
                |> Command.onStdoutLine handled.Add
                |> Command.stdoutTee tee

            use running = FakeProcess.OfCommand(command).WithStdout(secretLine).Build()

            let! result = outputOf running

            // The observation seams are what they always were: they see the real output.
            Assert.That(List.ofSeq handled, Is.EqualTo<string list>([ secretLine ]))
            Assert.That(Encoding.UTF8.GetString(tee.ToArray()), Is.EqualTo secretLine)
            // The retained capture is the shaped one.
            Assert.That(result.Stdout, Is.EqualTo "token=***")
        }

    [<Test>]
    member _.``The stdout line stream sees the unshaped line while the retained stderr is shaped``() : Task =
        task {
            let command =
                Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            use running =
                FakeProcess.OfCommand(command).WithStdout(secretLine).WithStderr($"err {secretLine}").Build()

            let! lines = collectLines running

            // Streaming hands each line to a live consumer instead of retaining it — the same category
            // as a per-line handler, and documented as outside this seam.
            Assert.That(lines, Is.EqualTo<string list>([ secretLine ]))

            match! running.FinishAsync() with
            | Error error -> Assert.Fail $"FinishAsync failed: {error.Message}"
            // `Finished.Stderr` IS retained backlog, so it is shaped.
            | Ok finished -> Assert.That(finished.Stderr, Is.EqualTo "err token=***")
        }

    [<Test>]
    member _.``A bytes capture keeps stdout byte-exact and shapes its line-pumped stderr``() : Task =
        task {
            let command =
                Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            use running =
                FakeProcess.OfCommand(command).WithStdout(secretLine).WithStderr($"err {secretLine}").Build()

            match! running.OutputBytesAsync() with
            | Error error -> Assert.Fail $"OutputBytesAsync failed: {error.Message}"
            | Ok result ->
                // Raw bytes have no decoded line for a line-oriented seam to shape.
                Assert.That(Encoding.UTF8.GetString result.Stdout, Is.EqualTo secretLine)
                Assert.That(result.Stderr, Is.EqualTo "err token=***")
        }

    // ---- composition with the buffer policy --------------------------------------------------------

    [<Test>]
    member _.``Retention bookkeeping is computed from the shaped text``() : Task =
        task {
            // Each shaped line is `[out]x` — 6 UTF-8 bytes plus the separator byte the line buffer
            // charges, so two of them are 14 bytes and a 14-byte ceiling retains exactly two. Had the
            // accounting been taken on the 1-byte raw lines instead, all three would have fitted.
            let command =
                Command.create "svc"
                |> Command.capturePolicy (TaggingPolicy "tagger")
                |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes 14)

            use running = FakeProcess.OfCommand(command).WithStdout("a\nb\nc").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "[out]b\n[out]c")
            Assert.That(result.Truncated, Is.True)
        }

    [<Test>]
    member _.``Line and raw-byte counters report what the child produced, not what the policy returned``() : Task =
        task {
            // Every line is blanked, so the capture retains nothing — but the counters, which are taken
            // before this seam (the raw bytes at the read, the lines at the framing), still report the
            // three lines and nine-plus-two bytes the child actually wrote.
            let command = Command.create "svc" |> Command.capturePolicy (NullReturningPolicy())

            use running = FakeProcess.OfCommand(command).WithStdout("aaa\nbbb\nccc").Build()

            let! result = outputOf running

            Assert.That(result.Stdout, Is.EqualTo "\n\n")
            Assert.That(running.StdoutLineCount, Is.EqualTo 3)
            Assert.That(running.StdoutBytesSeen, Is.EqualTo 11L)
        }

    [<Test>]
    member _.``A pipeline stage's policy does not fire, because a pipeline captures raw bytes``() : Task =
        task {
            // Pins the documented boundary rather than assuming it: a pipeline's final stdout (and every
            // stage's stderr) is a RAW byte capture with no line framing, so a line-oriented transform
            // has nothing to shape — exactly why that stage's `OnStdoutLine`/tees do not fire either.
            // A change that silently started (or stopped) applying it here must update
            // `docs/hardening.md` and `Pipeline`'s own contract, which this test is the guard for.
            let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

            let shell (script: string) =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; script ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; script ]

            // `sort` reads stdin on both platforms — a portable, shell-free last stage.
            let lastStage =
                Command.create "sort" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            let pipeline = (shell $"echo {secretLine}").Pipe lastStage

            match! pipeline.OutputStringAsync() with
            | Error error -> Assert.Fail $"pipeline failed: {error.Message}"
            | Ok result ->
                Assert.That(result.Stdout.Contains("s3cr3t-value", StringComparison.Ordinal), Is.True)
                Assert.That(result.Stdout.Contains("***", StringComparison.Ordinal), Is.False)
        }

    // ---- (4) parity across the three test doubles ---------------------------------------------------

    [<Test>]
    member _.``ScriptedRunner routes its retained capture through the same seam``() : Task =
        task {
            let scripted =
                ScriptedRunner().Fallback(Reply.Ok($"before\n{secretLine}").WithStderr(secretLine))

            let command =
                Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

            match! (scripted :> IProcessRunner).OutputStringAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"scripted capture failed: {error.Message}"
            | Ok result ->
                Assert.That(result.Stdout, Is.EqualTo "before\ntoken=***")
                Assert.That(result.Stderr, Is.EqualTo "token=***")
        }

    [<Test>]
    member _.``A scripted double fails closed exactly like a live capture``() : Task =
        task {
            let scripted = ScriptedRunner().Fallback(Reply.Ok($"before\n{secretLine}\nafter"))

            let command =
                Command.create "svc" |> Command.capturePolicy (ThrowingPolicy "s3cr3t-value")

            match! (scripted :> IProcessRunner).OutputStringAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"scripted capture failed: {error.Message}"
            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "before\n\nafter")
        }

    [<Test>]
    member _.``A cassette records the shaped capture, so the secret never reaches the file``() : Task =
        task {
            let path = tempCassette ()

            try
                let command =
                    Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FakeBackedRunner($"before\n{secretLine}", secretLine))

                        let! _ = (recorder :> IProcessRunner).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error.Message}"
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk.Contains("s3cr3t-value", StringComparison.Ordinal), Is.False)
                Assert.That(onDisk.Contains("token=***", StringComparison.Ordinal), Is.True)
            finally
                if File.Exists path then
                    File.Delete path
        }

    [<Test>]
    member _.``A cassette hit shapes its retained capture through the same seam``() : Task =
        task {
            let path = tempCassette ()

            try
                // Record WITHOUT a policy, so the entry holds the raw line: the replaying command's own
                // policy is then the only thing that can keep the secret out of the replayed result.
                let recordedCommand = Command.create "svc"

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FakeBackedRunner($"before\n{secretLine}", secretLine))

                        let! _ = (recorder :> IProcessRunner).OutputStringAsync(recordedCommand, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error.Message}"
                    }

                let replayCommand =
                    Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error.Message}"
                | Ok replayer ->
                    match! (replayer :> IProcessRunner).OutputStringAsync(replayCommand, CancellationToken.None) with
                    | Error error -> Assert.Fail $"replay: {error.Message}"
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "before\ntoken=***")
                        Assert.That(result.Stderr, Is.EqualTo "token=***")
            finally
                if File.Exists path then
                    File.Delete path
        }

    [<Test>]
    member _.``A cassette capture verb and its spawned replay agree on the shaped capture``() : Task =
        task {
            let path = tempCassette ()

            try
                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FakeBackedRunner($"before\n{secretLine}", secretLine))

                        let! _ =
                            (recorder :> IProcessRunner).OutputStringAsync(Command.create "svc", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error.Message}"
                    }

                let replayCommand =
                    Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error.Message}"
                | Ok replayer ->
                    let runner = replayer :> IProcessRunner
                    let! viaVerb = runner.OutputStringAsync(replayCommand, CancellationToken.None)

                    match! runner.SpawnAsync(replayCommand, CancellationToken.None) with
                    | Error error -> Assert.Fail $"spawn replay: {error.Message}"
                    | Ok spawned ->
                        use spawned = spawned
                        let! viaSpawn = spawned.OutputStringAsync()

                        match viaVerb, viaSpawn with
                        | Ok fromVerb, Ok fromSpawn ->
                            // K-144's capture boundary, extended to CONTENT: the two replay shapes must
                            // not disagree about what the retained capture holds.
                            Assert.That(fromVerb.Stdout, Is.EqualTo "before\ntoken=***")
                            Assert.That(fromSpawn.Stdout, Is.EqualTo fromVerb.Stdout)
                            Assert.That(fromSpawn.Stderr, Is.EqualTo fromVerb.Stderr)
                        | _ -> Assert.Fail "one of the two replay shapes failed"
            finally
                if File.Exists path then
                    File.Delete path
        }

    [<Test>]
    member _.``A cassette bytes replay keeps stdout byte-exact and shapes its stderr``() : Task =
        task {
            let path = tempCassette ()

            try
                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FakeBackedRunner(secretLine, $"err {secretLine}"))

                        let! _ =
                            (recorder :> IProcessRunner).OutputBytesAsync(Command.create "svc", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error.Message}"
                    }

                let replayCommand =
                    Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error.Message}"
                | Ok replayer ->
                    match! (replayer :> IProcessRunner).OutputBytesAsync(replayCommand, CancellationToken.None) with
                    | Error error -> Assert.Fail $"replay: {error.Message}"
                    | Ok result ->
                        Assert.That(Encoding.UTF8.GetString result.Stdout, Is.EqualTo secretLine)
                        Assert.That(result.Stderr, Is.EqualTo "err token=***")
            finally
                if File.Exists path then
                    File.Delete path
        }

    [<Test>]
    member _.``A cassette round trip under one idempotent policy is stable``() : Task =
        task {
            let path = tempCassette ()

            try
                // The policy is configured on BOTH halves, so it runs at record time and again on the
                // replayed entry. An idempotent policy — which redaction is — round-trips unchanged.
                let command =
                    Command.create "svc" |> Command.capturePolicy (RedactingPolicy "s3cr3t-value")

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FakeBackedRunner($"before\n{secretLine}", secretLine))

                        let! _ = (recorder :> IProcessRunner).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error.Message}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error.Message}"
                | Ok replayer ->
                    match! (replayer :> IProcessRunner).OutputStringAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"replay: {error.Message}"
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "before\ntoken=***")
                        Assert.That(result.Stderr, Is.EqualTo "token=***")
            finally
                if File.Exists path then
                    File.Delete path
        }
