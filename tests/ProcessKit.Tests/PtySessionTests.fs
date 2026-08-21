namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Runtime.InteropServices
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open FsCheck
open FsCheck.FSharp
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

type private ManualTimer(callback: TimerCallback, state: obj | null) =
    let mutable disposed = 0

    member _.Fire() =
        if Volatile.Read(&disposed) = 0 then
            callback.Invoke state

    interface ITimer with
        member _.Change(_dueTime, _period) = Volatile.Read(&disposed) = 0

        member _.Dispose() =
            Interlocked.Exchange(&disposed, 1) |> ignore

        member _.DisposeAsync() =
            Interlocked.Exchange(&disposed, 1) |> ignore
            ValueTask()

type private ManualTimerProvider() =
    inherit TimeProvider()

    let gate = obj ()
    let timers = ResizeArray<ManualTimer>()

    let timerCreated =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    override _.CreateTimer(callback, state, _dueTime, _period) =
        let timer = new ManualTimer(callback, state)
        lock gate (fun () -> timers.Add timer)
        timerCreated.TrySetResult() |> ignore
        timer :> ITimer

    member _.TimerCreated = timerCreated.Task

    member _.FireAll() =
        let snapshot = lock gate (fun () -> timers.ToArray())
        snapshot |> Array.iter (fun timer -> timer.Fire())

/// Tests for the expect-style interaction layer (`PtySession`, T-226): waiting for a pattern in the
/// child's RAW merged terminal output — a prompt such as `Password: ` carries no line terminator, so it
/// is exactly what the line-framed `WaitForLineAsync` cannot see — sending input back through the
/// existing interactive stdin, and the optional session transcript.
///
/// The double-backed tests run everywhere (`FakeProcess.WithPty` models the merged-stream shape, and
/// `WithStdinOpen` gives the session a stdin to record). The genuine expect/send round trip is
/// Linux-gated exactly like the rest of `PtyTests`, because it drives `/bin/sh` conversations through
/// the POSIX ctty helper (util-linux `setsid --ctty`, absent on macOS/BSD). It is no longer gated on the
/// launcher's console: since T-338 a ConPTY child's stdio is bound to the pseudoconsole from a
/// console-attached and a headless launcher alike (see the T-338 tests in `PtyTests`).
[<TestFixture>]
type PtySessionTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let hostForStdinFeeder (command: Command) (stdin: Stream) (feedComplete: unit -> unit) : RunningHost =
        { Config = command.Config
          Pid = None
          Stdout = Some(new MemoryStream() :> Stream)
          Stderr = None
          Stdin = Some stdin
          StartTime = DateTime.UtcNow
          StartedTimestamp = Stopwatch.GetTimestamp()
          StartTimeIdentity = None
          Wait = fun () -> Task.FromResult(Outcome.Exited 0)
          StdinError = fun () -> Task.FromResult None
          StdinFeedComplete = feedComplete
          StartKill = ignore
          Signal = fun _ -> Ok()
          GracefulKill = fun _ -> Task.CompletedTask
          ResizePty = None
          TreeStats = None
          Teardown = fun () -> ValueTask() }

    // A child that stays alive for a few seconds and prints nothing at all, on either platform — so a
    // pattern wait against it can only ever end at its own deadline, never at an early end-of-output.
    let silentSleeper () =
        let command =
            if isWindows then
                // `ping` (not `timeout`, which needs a console) with its output discarded.
                Command.create "cmd.exe" |> Command.args [ "/c"; "ping -n 6 127.0.0.1 >NUL" ]
            else
                Command.create "/bin/sh" |> Command.args [ "-c"; "sleep 5" ]

        command |> Command.timeout (TimeSpan.FromSeconds 60.0)

    // A child that stays quiet long enough for a pattern wait to park, then prints `text` and exits at
    // once — so its last chunk and the end of its output reach the session back to back, which is the
    // interleaving a final prompt/answer of a real conversation actually has.
    let lastWordThenExit (text: string) =
        let command =
            if isWindows then
                Command.create "cmd.exe"
                |> Command.args [ "/c"; $"ping -n 2 127.0.0.1 >NUL & echo {text}" ]
            else
                Command.create "/bin/sh" |> Command.args [ "-c"; $"sleep 0.3; printf '{text}'" ]

        command |> Command.timeout (TimeSpan.FromSeconds 60.0)

    // The matcher shape `ExpectAsync(string, ...)` builds: an ordinal substring search over the raw,
    // unframed window.
    let literal (pattern: string) (text: string) =
        match text.IndexOf(pattern, StringComparison.Ordinal) with
        | -1 -> None
        | index -> Some(index, pattern.Length)

    let filterAnsi (text: string) (chunkSizes: int list) =
        let filter = AnsiEscapeFilter()
        let output = StringBuilder()
        let sizes = List.toArray chunkSizes
        let mutable offset = 0
        let mutable chunk = 0

        while offset < text.Length do
            let count = min sizes[chunk % sizes.Length] (text.Length - offset)
            filter.Append(text.AsSpan(offset, count), output)
            offset <- offset + count
            chunk <- chunk + 1

        output.ToString()

    // ----------------------------------------------------------------------------------
    // Pattern waiting + sending, against the PTY double
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``expect matches a prompt with no line terminator, and send answers it``() : Task =
        task {
            // The scripted conversation: a banner, then a prompt that does NOT end its line (the shape a
            // line-framed wait cannot deliver), then the child's confirmation.
            let fake =
                FakeProcess
                    .Create("fake-installer")
                    .WithPty()
                    .WithStdinOpen()
                    .WithStdout("Welcome to the installer\r\nPassword: LEN=6\r\n")

            use running = fake.Build()
            let session = PtySession running

            match! session.ExpectAsync("Password: ", TimeSpan.FromSeconds 10.0) with
            | Ok matched ->
                Assert.That(matched.Text, Is.EqualTo "Password: ")
                Assert.That(matched.Before, Does.Contain "Welcome to the installer")
            | Error error -> Assert.Fail $"the unterminated prompt should have matched: {error}"

            match! session.SendLineAsync "secret" with
            | Ok() -> ()
            | Error error -> Assert.Fail $"sending the answer failed: {error}"

            // Consumed up to and including the prompt, so the follow-up is matched from what came after.
            match! session.ExpectAsync("LEN=6", TimeSpan.FromSeconds 10.0) with
            | Ok matched -> Assert.That(matched.Text, Is.EqualTo "LEN=6")
            | Error error -> Assert.Fail $"the confirmation should have matched: {error}"

            // The answer went through the ordinary interactive stdin, terminated with the carriage
            // return a terminal sends for Enter (PtyLineEnding.Auto on a pty-backed run).
            Assert.That(Encoding.UTF8.GetString fake.StdinBytes, Is.EqualTo "secret\r")
        }

    [<Test>]
    member _.``expect accepts a Regex and consumes only up to the match``() : Task =
        task {
            let fake =
                FakeProcess
                    .Create("fake-repl")
                    .WithPty()
                    .WithStdinOpen()
                    .WithStdout("banner\r\nrepl v2.7.0> answer=42\r\n")

            use running = fake.Build()
            let session = PtySession running

            match! session.ExpectAsync(Regex @"repl v\d+\.\d+\.\d+> ", TimeSpan.FromSeconds 10.0) with
            | Ok matched ->
                Assert.That(matched.Text, Is.EqualTo "repl v2.7.0> ")
                Assert.That(matched.Before, Does.Contain "banner")
            | Error error -> Assert.Fail $"the regex prompt should have matched: {error}"

            // Everything after the match is still pending for the next pattern.
            match! session.ExpectAsync("answer=42", TimeSpan.FromSeconds 10.0) with
            | Ok matched -> Assert.That(matched.Text, Is.EqualTo "answer=42")
            | Error error -> Assert.Fail $"the text after the regex match should still be pending: {error}"
        }

    [<Test>]
    member _.``zero-width string and Regex matches share a typed error and leave the window unchanged``() : Task =
        task {
            let fake =
                FakeProcess.Create("zero-width-regex").WithPty().WithStdout("primedready")

            use running = fake.Build()
            let session = PtySession running

            match! session.ExpectAsync(Regex "primed", TimeSpan.FromSeconds 10.0) with
            | Ok matched -> Assert.That(matched.Text, Is.EqualTo "primed")
            | Error error -> Assert.Fail $"the non-zero regex should have primed the pending window: {error}"

            let! emptyString = session.ExpectAsync("", TimeSpan.FromSeconds 10.0)

            let expectedMessage =
                match emptyString with
                | Error(ProcessError.Unsupported message) -> message
                | Error error ->
                    Assert.Fail $"an empty string pattern should return a typed Unsupported error, got {error}"
                    ""
                | Ok matched ->
                    Assert.Fail $"an empty string pattern must not match, got '{matched.Text}'"
                    ""

            for pattern in [ Regex ""; Regex "^"; Regex "$"; Regex "(?=ready)" ] do
                match! session.ExpectAsync(pattern, TimeSpan.FromSeconds 10.0) with
                | Error(ProcessError.Unsupported message) -> Assert.That(message, Is.EqualTo expectedMessage)
                | Error error -> Assert.Fail $"a zero-width regex should be rejected, got {error}"
                | Ok matched -> Assert.Fail $"a zero-width regex must not match, got '{matched.Text}'"

                Assert.That(session.Pending, Is.EqualTo "ready")
        }

    [<Test>]
    member _.``ANSI filtering makes styled prompts matchable and cleans the transcript``() : Task =
        task {
            let fake =
                FakeProcess
                    .Create("fake-colour-tool")
                    .WithPty()
                    .WithStdout("\u001b]0;installer\u0007\u001b[33mPassword:\u001b[0m ready\u001b7!\u001b8")

            use running = fake.Build()
            let session = PtySession.WithAnsiFiltering running

            match! session.ExpectAsync("Password: ready!", TimeSpan.FromSeconds 10.0) with
            | Ok matched -> Assert.That(matched.Text, Is.EqualTo "Password: ready!")
            | Error error -> Assert.Fail $"the styled prompt should match without its controls: {error}"

            let! _ = session.WaitForExitAsync()
            Assert.That(session.Transcript, Is.EqualTo "Password: ready!")
            Assert.That(session.Transcript.IndexOf('\u001b'), Is.EqualTo -1)
        }

    [<Test>]
    member _.``the ordinary session keeps ANSI controls raw by default``() : Task =
        task {
            let raw = "\u001b[32mready> \u001b[0m"
            let fake = FakeProcess.Create("fake-colour-tool").WithPty().WithStdout(raw)
            use running = fake.Build()
            let session = PtySession running
            let! _ = session.WaitForExitAsync()
            Assert.That(session.Transcript, Is.EqualTo raw)
        }

    [<Test>]
    member _.``ANSI filtering is invariant under arbitrary chunk boundaries``() =
        let input =
            "head \u001b[1;31mred\u001b[0m "
            + "\u001b]0;BEL title\u0007after "
            + "\u001b]8;;https://example.test\u001b\\link\u001b]8;;\u001b\\ "
            + "\u001b7saved\u001b8 done"

        let expected = "head red after link saved done"
        let chunks = Gen.nonEmptyListOf (Gen.choose (1, 12))

        let property =
            Prop.forAll (Arb.fromGen chunks) (fun chunkSizes -> filterAnsi input chunkSizes = expected)

        Check.QuickThrowOnFailure property

    [<Test>]
    member _.``a send on a run with no interactive stdin is a typed Unsupported``() : Task =
        task {
            // No `WithStdinOpen`, so the built handle has no stdin pipe at all — the send verbs must say
            // so, never silently drop the bytes.
            let fake = FakeProcess.Create("fake-tool").WithPty().WithStdout("ready> ")
            use running = fake.Build()
            let session = PtySession running

            match! session.SendLineAsync "hello" with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "KeepStdinOpen")
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
            | Ok() -> Assert.Fail "sending without an interactive stdin must not report success"

            match! session.CloseStdinAsync() with
            | Error(ProcessError.Unsupported _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
            | Ok() -> Assert.Fail "closing an absent stdin must not report success"

            Assert.That(fake.StdinBytes, Is.Empty)
        }

    [<Test>]
    member _.``PtySession construction does not wait for a blocked feeder, and sends after its source``() : Task =
        task {
            let feeder =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let sourceWritten =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let stdin = new MemoryStream()
            let sourceBytes = Encoding.UTF8.GetBytes "source bytes"

            let command =
                Command.create "non-reading-child"
                |> Command.stdin (Stdin.FromString "source bytes")
                |> Command.keepStdinOpen

            use running =
                new RunningProcess(
                    hostForStdinFeeder command (stdin :> Stream) (fun () ->
                        // A real non-reading child can leave the source feeder parked in its pipe write.
                        // This gate models that completion without relying on platform pipe sizes.
                        stdin.Write(sourceBytes, 0, sourceBytes.Length)
                        sourceWritten.TrySetResult() |> ignore
                        feeder.Task.GetAwaiter().GetResult())
                )

            try
                let construction = Task.Run(fun () -> PtySession running)
                let! completed = Task.WhenAny(construction :> Task, Task.Delay 2000)

                Assert.That(
                    obj.ReferenceEquals(completed, construction),
                    Is.True,
                    "PtySession construction must not wait for a source feeder blocked by a non-reading child"
                )

                let! session = construction
                use cancellation = new CancellationTokenSource()
                let sending = session.SendAsync("answer", cancellation.Token)
                let! sourceObserved = Task.WhenAny(sourceWritten.Task :> Task, Task.Delay 2000)

                Assert.That(
                    obj.ReferenceEquals(sourceObserved, sourceWritten.Task),
                    Is.True,
                    "the fake feeder must write its source bytes before it reports completion"
                )

                let! stillWaiting = Task.WhenAny(sending :> Task, Task.Delay 200)

                Assert.That(
                    obj.ReferenceEquals(stillWaiting, sending),
                    Is.False,
                    "SendAsync must wait for the source feeder before writing interactive stdin"
                )

                cancellation.Cancel()

                match! sending with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"expected feeder-wait cancellation, got {other}"

                feeder.TrySetResult() |> ignore

                match! session.SendAsync "answer" with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the send should complete after the feeder, got {error}"

                Assert.That(Encoding.UTF8.GetString(stdin.ToArray()), Is.EqualTo "source bytesanswer")
            finally
                feeder.TrySetResult() |> ignore
        }

    [<Test>]
    member _.``PtySession close waits for a source feeder before closing interactive stdin``() : Task =
        task {
            let feeder =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let sourceWritten =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let stdin = new MemoryStream()
            let sourceBytes = Encoding.UTF8.GetBytes "source bytes"

            let command =
                Command.create "non-reading-child"
                |> Command.stdin (Stdin.FromString "source bytes")
                |> Command.keepStdinOpen

            use running =
                new RunningProcess(
                    hostForStdinFeeder command (stdin :> Stream) (fun () ->
                        stdin.Write(sourceBytes, 0, sourceBytes.Length)
                        sourceWritten.TrySetResult() |> ignore
                        feeder.Task.GetAwaiter().GetResult())
                )

            try
                let session = PtySession running
                let closing = session.CloseStdinAsync()
                let! sourceObserved = Task.WhenAny(sourceWritten.Task :> Task, Task.Delay 2000)

                Assert.That(
                    obj.ReferenceEquals(sourceObserved, sourceWritten.Task),
                    Is.True,
                    "the fake feeder must write its source bytes before it reports completion"
                )

                Assert.That(
                    Encoding.UTF8.GetString(stdin.ToArray()),
                    Is.EqualTo "source bytes",
                    "source bytes must be preserved before interactive stdin is closed"
                )

                let! stillWaiting = Task.WhenAny(closing :> Task, Task.Delay 200)

                Assert.That(
                    obj.ReferenceEquals(stillWaiting, closing),
                    Is.False,
                    "CloseStdinAsync must wait for the source feeder before closing the pipe"
                )

                Assert.That(stdin.CanWrite, Is.True, "close must not close stdin while the source feeder is active")

                feeder.TrySetResult() |> ignore

                match! closing with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the close should complete after the feeder, got {error}"

                Assert.That(stdin.CanWrite, Is.False, "close must finish the interactive stdin after the feeder")
                Assert.That(Encoding.UTF8.GetString(stdin.ToArray()), Is.EqualTo "source bytes")
            finally
                feeder.TrySetResult() |> ignore
        }

    // ----------------------------------------------------------------------------------
    // The per-pattern deadline is the session's own, separate from the run's
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``an expect deadline is per pattern, reported as NotReady, and leaves the run alive``() : Task =
        task {
            // A live child that prints nothing: the only way this wait can end is its OWN deadline —
            // there is no early end-of-output to short-circuit it, and the run-wide `Command.Timeout`
            // (60s, above) is two orders of magnitude away.
            match! runner.StartAsync(silentSleeper (), CancellationToken.None) with
            | Error error -> Assert.Fail $"spawn failed: {error}"
            | Ok running ->
                use running = running
                let session = PtySession running
                let waited = TimeSpan.FromMilliseconds 400.0
                let started = Stopwatch.GetTimestamp()

                match! session.ExpectAsync("this-never-arrives", waited) with
                | Error(ProcessError.NotReady(_, reported)) ->
                    // The reported budget is the per-pattern one, not the run's 60-second timeout.
                    Assert.That(reported, Is.EqualTo waited)
                | Error other -> Assert.Fail $"expected ProcessError.NotReady, got {other}"
                | Ok matched -> Assert.Fail $"nothing should have matched, got {matched.Text}"

                // It genuinely waited for its own deadline rather than returning early (a generous lower
                // bound: this asserts "not instant", not a tight timing threshold).
                let elapsed = Stopwatch.GetElapsedTime started

                Assert.That(
                    elapsed,
                    Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds 250.0),
                    "the pattern wait must burn its own deadline, not return immediately"
                )

                // The run itself was untouched by that deadline: the session is still usable, and a
                // second wait is budgeted independently of the first.
                match! session.ExpectAsync("still-never-arrives", TimeSpan.FromMilliseconds 200.0) with
                | Error(ProcessError.NotReady(_, reported)) ->
                    Assert.That(reported, Is.EqualTo(TimeSpan.FromMilliseconds 200.0))
                | Error other -> Assert.Fail $"expected a second independent NotReady, got {other}"
                | Ok _ -> Assert.Fail "nothing should have matched the second pattern either"
        }

    [<Test>]
    member _.``an expect deadline uses the command TimeProvider``() : Task =
        task {
            let provider = ManualTimerProvider()

            let command = silentSleeper () |> Command.timeProvider provider

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"spawn failed: {error}"
            | Ok running ->
                use running = running
                Assert.That(running.Config.TimeProvider, Is.SameAs provider)
                let session = PtySession running
                let budget = TimeSpan.FromHours 1.0
                let pending = session.ExpectAsync("this-never-arrives", budget)

                Assert.That(pending.IsCompleted, Is.False)
                do! provider.TimerCreated.WaitAsync(TimeSpan.FromSeconds 2.0)
                provider.FireAll()

                match! pending.WaitAsync(TimeSpan.FromSeconds 2.0) with
                | Error(ProcessError.NotReady(_, reported)) -> Assert.That(reported, Is.EqualTo budget)
                | Error other -> Assert.Fail $"expected ProcessError.NotReady, got {other}"
                | Ok matched -> Assert.Fail $"nothing should have matched, got {matched.Text}"
        }

    [<Test>]
    member _.``an expect whose caller token fires reports Cancelled, not NotReady``() : Task =
        task {
            match! runner.StartAsync(silentSleeper (), CancellationToken.None) with
            | Error error -> Assert.Fail $"spawn failed: {error}"
            | Ok running ->
                use running = running
                let session = PtySession running
                use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 200.0)

                // The caller's token fires long before the pattern's own (generous) deadline: a cancelled
                // wait is an error, a timed-out one is only "not ready yet".
                match! session.ExpectAsync("never", TimeSpan.FromSeconds 30.0, cancellation.Token) with
                | Error(ProcessError.Cancelled _) -> ()
                | Error other -> Assert.Fail $"expected ProcessError.Cancelled, got {other}"
                | Ok _ -> Assert.Fail "nothing should have matched"
        }

    [<Test>]
    member _.``an expect ends promptly with NotReady once the child's output has ended``() : Task =
        task {
            // The scripted output is exhausted immediately, so a pattern that is not in it must be
            // refused at once rather than waiting out the (very long) deadline.
            let fake = FakeProcess.Create("fake-tool").WithPty().WithStdout("all there is")
            use running = fake.Build()
            let session = PtySession running
            let started = Stopwatch.GetTimestamp()

            match! session.ExpectAsync("absent", TimeSpan.FromSeconds 60.0) with
            | Error(ProcessError.NotReady _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.NotReady, got {other}"
            | Ok _ -> Assert.Fail "nothing should have matched"

            let elapsed = Stopwatch.GetElapsedTime started

            Assert.That(
                elapsed,
                Is.LessThan(TimeSpan.FromSeconds 30.0),
                "end-of-output must end the wait, not the 60-second deadline"
            )
        }

    // ----------------------------------------------------------------------------------
    // A match arriving WITH the end of output beats the end (the R-01 ordering)
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``a match that landed with the end of output is reported as a match, not as the end``() =
        // The ordering a two-question waiter ("did it match?", then "has it ended?") could be preempted
        // in the middle of: the last chunk carrying the pattern AND the readers finishing both land
        // before the waiter looks. One step must give one verdict — a match sitting in the window always
        // outranks the end that arrived with it.
        let window = ExpectWindow(1024, Some 1024)
        window.Append "answer=42"
        window.Complete None

        match window.TryConsume(literal "answer=42") with
        | ExpectStep.Matched(before, text) ->
            Assert.That(text, Is.EqualTo "answer=42")
            Assert.That(before, Is.Empty)
        | other -> Assert.Fail $"a match that arrived with the end of output must win over it, got {other}"

        // Only once the pattern is genuinely absent does the ended window say so — promptly, so a wait
        // does not burn its whole deadline on output that can no longer come.
        match window.TryConsume(literal "answer=42") with
        | ExpectStep.Ended None -> ()
        | other -> Assert.Fail $"an ended window with nothing left to match must report Ended, got {other}"

    [<Test>]
    member _.``a zero-length window match never consumes or repeats``() =
        let window = ExpectWindow(1024, None)
        let zeroWidth _ = Some(0, 0)
        window.Append "ready"

        match window.TryConsume zeroWidth with
        | ExpectStep.InvalidPattern -> ()
        | other -> Assert.Fail $"a zero-length match on a live window must be rejected, got {other}"

        Assert.That(window.Pending, Is.EqualTo "ready")
        window.Complete None

        match window.TryConsume zeroWidth with
        | ExpectStep.InvalidPattern -> ()
        | other -> Assert.Fail $"a zero-length match after completion must be rejected, got {other}"

        Assert.That(window.Pending, Is.EqualTo "ready")

    [<Test>]
    member _.``a match that landed with a read fault is reported first, and the fault after it``() =
        // Output the child genuinely produced stays honest output even when reading the rest of it
        // failed; the fault is what the NEXT unmatched look reports.
        let fault = IOException "the terminal went away"
        let window = ExpectWindow(1024, None)
        window.Append "LEN=7"
        window.Complete(Some fault)

        match window.TryConsume(literal "LEN=7") with
        | ExpectStep.Matched(_, text) -> Assert.That(text, Is.EqualTo "LEN=7")
        | other -> Assert.Fail $"the delivered output must be matchable before the fault is reported, got {other}"

        match window.TryConsume(literal "LEN=7") with
        | ExpectStep.Ended(Some reported) -> Assert.That(reported, Is.SameAs fault)
        | other -> Assert.Fail $"the fault that ended the output must reach the waiter, got {other}"

    [<Test>]
    member _.``a half-arrived prompt on a live window tells the waiter to keep waiting``() =
        let window = ExpectWindow(1024, None)
        window.Append "Passwo"

        match window.TryConsume(literal "Password: ") with
        | ExpectStep.Waiting -> ()
        | other -> Assert.Fail $"a partially arrived prompt is neither a match nor an end, got {other}"

    [<Test>]
    member _.``a pattern printed just before the child exits is matched, never a false NotReady``() : Task =
        task {
            // End to end through the real loop: the wait is parked well before the child speaks, and what
            // wakes it is the final chunk of the conversation immediately followed by end-of-output —
            // exactly the shape of a script's last prompt/answer. A `NotReady` here would be a flaky
            // expect script in the wild.
            match! runner.StartAsync(lastWordThenExit "Done.", CancellationToken.None) with
            | Error error -> Assert.Fail $"spawn failed: {error}"
            | Ok running ->
                use running = running
                let session = PtySession running

                match! session.ExpectAsync("Done.", TimeSpan.FromSeconds 30.0) with
                | Ok matched -> Assert.That(matched.Text, Is.EqualTo "Done.")
                | Error error -> Assert.Fail $"the child's final output should have matched: {error}"

                match! session.WaitForExitAsync() with
                | Outcome.Exited 0 -> ()
                | other -> Assert.Fail $"expected a clean exit after the final pattern, got {other}"
        }

    // ----------------------------------------------------------------------------------
    // Transcript
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``the transcript keeps the whole session's output after a series of expect and send``() : Task =
        task {
            let fake =
                FakeProcess
                    .Create("fake-installer")
                    .WithPty()
                    .WithStdinOpen()
                    .WithStdout("step one\r\nContinue? step two\r\nDone.\r\n")

            use running = fake.Build()
            let session = PtySession running

            match! session.ExpectAsync("Continue? ", TimeSpan.FromSeconds 10.0) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"the prompt should have matched: {error}"

            match! session.SendLineAsync "y" with
            | Ok() -> ()
            | Error error -> Assert.Fail $"sending the answer failed: {error}"

            match! session.ExpectAsync("Done.", TimeSpan.FromSeconds 10.0) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"the final marker should have matched: {error}"

            // Consuming a match removes it from the WINDOW, never from the transcript: everything the
            // child emitted this session is still there, in order.
            let transcript = session.Transcript
            Assert.That(transcript, Does.Contain "step one")
            Assert.That(transcript, Does.Contain "Continue? ")
            Assert.That(transcript, Does.Contain "step two")
            Assert.That(transcript, Does.Contain "Done.")
            Assert.That(session.TranscriptTruncated, Is.False)
            // Input is the caller's, not the child's output: it is never recorded here.
            Assert.That(transcript, Does.Not.Contain "y\r")
        }

    [<Test>]
    member _.``a session with the transcript off still matches but records nothing``() : Task =
        task {
            let fake =
                FakeProcess.Create("fake-tool").WithPty().WithStdout("secret-banner ready> ")

            use running = fake.Build()

            let session =
                PtySession(
                    running,
                    { PtySessionOptions.Default with
                        CaptureTranscript = false }
                )

            match! session.ExpectAsync("ready> ", TimeSpan.FromSeconds 10.0) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"the prompt should still match with no transcript: {error}"

            Assert.That(session.Transcript, Is.Empty)
            Assert.That(session.TranscriptTruncated, Is.False)
        }

    [<Test>]
    member _.``the match window slides, dropping the oldest output and saying so``() : Task =
        task {
            let filler = String('x', 200)

            let fake = FakeProcess.Create("fake-chatty").WithPty().WithStdout(filler + "TAIL")

            use running = fake.Build()

            let session =
                PtySession(
                    running,
                    { PtySessionOptions.Default with
                        WindowChars = 16 }
                )

            // The tail is still matchable; the evicted head is not, and the session reports the eviction
            // rather than silently pretending the output never existed.
            match! session.ExpectAsync("TAIL", TimeSpan.FromSeconds 10.0) with
            | Ok matched -> Assert.That(matched.Text, Is.EqualTo "TAIL")
            | Error error -> Assert.Fail $"the tail of the window should still match: {error}"

            Assert.That(session.WindowTruncated, Is.True)
            // The transcript is bounded separately and much larger, so it still has the whole output.
            Assert.That(session.Transcript, Does.StartWith "xxx")
            Assert.That(session.Transcript.Length, Is.EqualTo(filler.Length + 4))
        }

    // ----------------------------------------------------------------------------------
    // Claim gate + options validation
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``a session owns the pipes: a second session and a streaming verb are both refused``() : Task =
        task {
            let fake = FakeProcess.Create("fake-tool").WithPty().WithStdout("ready> ")
            use running = fake.Build()
            let _session = PtySession running

            // Two matchers over one window would silently consume each other's output.
            Assert.Throws<InvalidOperationException>(Action(fun () -> PtySession running |> ignore))
            |> ignore

            // And the ordinary streaming verbs see the handle as already consumed, exactly as they do
            // after `OutputEventsAsync`.
            Assert.Throws<InvalidOperationException>(Action(fun () -> running.StdoutLinesAsync() |> ignore))
            |> ignore

            match! running.OutputStringAsync() with
            | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "consumed")
            | Error other -> Assert.Fail $"expected the already-consumed error, got {other}"
            | Ok _ -> Assert.Fail "a capture verb must not run alongside an interactive session"
        }

    [<Test>]
    member _.``a session over an already-consumed handle throws rather than half-attaching``() : Task =
        task {
            let fake = FakeProcess.Create("fake-tool").WithStdout("line one\n")
            use running = fake.Build()
            let _ = running.StdoutLinesAsync()

            Assert.Throws<InvalidOperationException>(Action(fun () -> PtySession running |> ignore))
            |> ignore
        }

    [<Test>]
    member _.``session options are validated at construction``() =
        let fake = FakeProcess.Create("fake-tool").WithPty()

        let build (options: PtySessionOptions) =
            task {
                use running = fake.Build()
                PtySession(running, options) |> ignore
            }
            |> fun t -> t.GetAwaiter().GetResult()

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                build
                    { PtySessionOptions.Default with
                        WindowChars = 0 })
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                build
                    { PtySessionOptions.Default with
                        TranscriptChars = 0 })
        )
        |> ignore

    [<Test>]
    member _.``session options reject null at public entry points``() : Task =
        task {
            let fake = FakeProcess.Create("fake-tool").WithPty()

            use running = fake.Build()
            let options = Unchecked.defaultof<PtySessionOptions>

            Assert.Throws<ArgumentNullException>(Action(fun () -> PtySession(running, options) |> ignore))
            |> ignore

            Assert.Throws<ArgumentNullException>(
                Action(fun () -> PtySession.WithAnsiFiltering(running, options) |> ignore)
            )
            |> ignore
        }

    [<Test>]
    member _.``a session started after a readiness probe still reports the real exit (K-016)``() : Task =
        task {
            // The probe starts the handle's ONE shared exit wait while deliberately leaving the pipes
            // unclaimed, and a session claims them right afterwards. On POSIX that wait reaps the child,
            // so a session that started its own second wait would see the pid already gone and fabricate
            // an `Unobserved` outcome instead of the real exit code.
            let command =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "exit 7" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "exit 7" ]

            match!
                runner.StartAsync(command |> Command.timeout (TimeSpan.FromSeconds 60.0), CancellationToken.None)
            with
            | Error error -> Assert.Fail $"spawn failed: {error}"
            | Ok running ->
                use running = running
                // Port 1 on loopback is never listening; the child exits at once, so the probe's own
                // early-exit race ends it well before its deadline.
                let endpoint = IPEndPoint(IPAddress.Loopback, 1)

                match! running.WaitForPortAsync(endpoint, TimeSpan.FromSeconds 10.0) with
                | Error(ProcessError.NotReady _) -> ()
                | Error other -> Assert.Fail $"expected the probe to report NotReady, got {other}"
                | Ok() -> Assert.Fail "nothing should be listening on loopback port 1"

                let session = PtySession running

                match! session.WaitForExitAsync() with
                | Outcome.Exited 7 -> ()
                | other -> Assert.Fail $"expected the child's real exit code after the probe, got {other}"
        }

    [<Test>]
    member _.``WaitForExitAsync reports the child's outcome without reaping it``() : Task =
        task {
            let fake = FakeProcess.Create("fake-tool").WithPty().WithStdout("bye").WithExit 3

            use running = fake.Build()
            let session = PtySession running

            match! session.ExpectAsync("bye", TimeSpan.FromSeconds 10.0) with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"the scripted output should have matched: {error}"

            match! session.WaitForExitAsync() with
            | Outcome.Exited 3 -> ()
            | other -> Assert.Fail $"expected the scripted exit code, got {other}"
        }

    // ----------------------------------------------------------------------------------
    // The real thing: a POSIX pty conversation end to end
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``a real PTY conversation expects a prompt, answers it, and keeps the secret out``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the POSIX ctty helper is util-linux setsid --ctty"
            else
                // `printf 'Password: '` leaves the prompt UNTERMINATED — the child then blocks in `read`,
                // so no newline can ever arrive to flush a line-framed wait. Only a raw pattern wait can
                // see it. The child reports the answer's LENGTH, so the secret itself is never printed.
                let secret = "hunter2"

                let script = "printf 'Password: '; IFS= read -r pw; printf 'LEN=%s\\n' \"${#pw}\""

                let command =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; script ])
                        .Pty({ PtyConfig.Default with Echo = false })
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 60.0)

                match! runner.StartAsync(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported message) -> Assert.Ignore $"host lacks a PTY: {message}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use running = running
                    let session = PtySession running

                    match! session.ExpectAsync("Password: ", TimeSpan.FromSeconds 30.0) with
                    | Ok matched -> Assert.That(matched.Text, Is.EqualTo "Password: ")
                    | Error error -> Assert.Fail $"the live pty prompt should have matched: {error}"

                    match! session.SendLineAsync secret with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"sending the credential failed: {error}"

                    match! session.ExpectAsync("LEN=7", TimeSpan.FromSeconds 30.0) with
                    | Ok matched -> Assert.That(matched.Text, Is.EqualTo "LEN=7")
                    | Error error -> Assert.Fail $"the child should have read the whole answer: {error}"

                    // Echo=false plus "input is never transcribed" means the credential appears nowhere in
                    // the session's own record of the conversation.
                    Assert.That(session.Transcript, Does.Contain "Password: ")
                    Assert.That(session.Transcript, Does.Not.Contain secret)

                    match! session.WaitForExitAsync() with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child, got {other}"
        }

    [<Test>]
    member _.``a real PTY session drives a multi-step conversation through one handle``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // Two prompts in a row: the second is only printed after the first answer arrives, so the
                // test can only pass if expect and send genuinely interleave against a live child.
                let script =
                    "printf 'name> '; IFS= read -r name; printf 'city> '; IFS= read -r city; "
                    + "printf 'HELLO %s OF %s\\n' \"$name\" \"$city\""

                let command =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; script ])
                        .Pty({ PtyConfig.Default with Echo = false })
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 60.0)

                match! runner.StartAsync(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported message) -> Assert.Ignore $"host lacks a PTY: {message}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use running = running
                    let session = PtySession running

                    let step (prompt: string) (answer: string) =
                        task {
                            match! session.ExpectAsync(prompt, TimeSpan.FromSeconds 30.0) with
                            | Ok _ -> ()
                            | Error error -> Assert.Fail $"prompt '{prompt}' never arrived: {error}"

                            match! session.SendLineAsync answer with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"answering '{prompt}' failed: {error}"
                        }

                    do! step "name> " "ada"
                    do! step "city> " "london"

                    match! session.ExpectAsync("HELLO ada OF london", TimeSpan.FromSeconds 30.0) with
                    | Ok _ -> ()
                    | Error error -> Assert.Fail $"the child should have combined both answers: {error}"

                    match! session.WaitForExitAsync() with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child, got {other}"
        }

    [<Test>]
    member _.``CloseStdinAsync ends a PTY child reading to EOF after an unterminated line (T-332)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `cat` reads until EOF and the sent line is deliberately UNTERMINATED, so the child can
                // only finish if `CloseStdinAsync` delivers a real terminal end of input — its stdin is a
                // view over the same terminal the output comes from, with no pipe of its own to close.
                // Echo=false keeps the terminal's own echo out of it, so the text can only come back from
                // `cat` having actually received it.
                let payload = "unterminated-line"

                let command =
                    (Command.create "/bin/cat").Pty({ PtyConfig.Default with Echo = false })
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 60.0)

                match! runner.StartAsync(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported message) -> Assert.Ignore $"host lacks a PTY: {message}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use running = running
                    let session = PtySession running

                    match! session.SendAsync payload with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"sending the unterminated line failed: {error}"

                    match! session.CloseStdinAsync() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"closing the pty stdin failed: {error}"

                    match! session.ExpectAsync(payload, TimeSpan.FromSeconds 30.0) with
                    | Ok _ -> ()
                    | Error error -> Assert.Fail $"the child should have received the unterminated line: {error}"

                    // The child read to EOF and exited on its own — not killed by the run's timeout, which
                    // is what a stdin close that delivered nothing would have left it waiting for.
                    match! session.WaitForExitAsync() with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"the child should have exited at EOF, got {other}"

                    // Closing again is a no-op, not a second end of input or a failure.
                    match! session.CloseStdinAsync() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"a repeated CloseStdinAsync must stay Ok: {error}"
        }
