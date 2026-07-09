namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open System.Runtime.InteropServices
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

/// A capturing `ILogger`, scoped to this file's pipeline observability tests (mirrors
/// `LoggingTests.CapturingLogger` — a `private` type there is not reachable from this file, and
/// `PipelineTests.fs` compiles before `LoggingTests.fs` regardless).
type private PipelineCapturingLogger() =
    let records = ConcurrentQueue<string>()
    member _.Text = String.Join("\n", records)

    interface ILogger with
        member _.Log(_logLevel, _eventId, state, error, formatter) =
            records.Enqueue(formatter.Invoke(state, error))

        member _.IsEnabled(_logLevel) = true

        member _.BeginScope(_state) =
            { new IDisposable with
                member _.Dispose() = () }

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

    // A stage that writes `lineCount` lines to stderr (never stdout), then exits non-zero — used to
    // prove a chatty stage's stderr is bounded by that stage's own `OutputBuffer` byte cap (T-034).
    // Optionally capped via `cap`; `None` keeps the previous unbounded behaviour.
    let noisyFailingStage (lineCount: int) (cap: int option) =
        let line = String.replicate 32 "x"
        let echoErr = sprintf "echo %s 1>&2" line
        // A space-padded separator on Windows keeps cmd.exe from misparsing the trailing `1>&2`
        // redirection against an immediately-following `&` command separator.
        let separator = if isWindows then " & " else "; "

        let script =
            (List.replicate lineCount echoErr |> String.concat separator)
            + separator
            + "exit 3"

        let stage = shell script

        match cap with
        | Some maxBytes ->
            stage
            |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes maxBytes)
        | None -> stage

    // A stage that writes `errLineCount` 32-char lines to stderr (never stdout) then exits 0, capturing
    // its stderr under `policy` — drives a stage's OWN stderr past a fail-loud (`Error`) byte cap so the
    // pipeline must surface it (T-062). Exiting 0 keeps pipefail silent, so an `OutputTooLarge` can only
    // come from the stderr overflow, not the exit code.
    let stderrStage (errLineCount: int) (policy: OutputBufferPolicy) =
        let line = String.replicate 32 "x"
        // Space-padded on Windows so cmd.exe does not misparse a trailing `1>&2` against the next `&`.
        let sep = if isWindows then " & " else "; "

        let script =
            List.replicate errLineCount (sprintf "echo %s 1>&2" line) |> String.concat sep

        shell script |> Command.outputBuffer policy

    // A stage that writes `stdoutLines` to stdout AND `errLineCount` 32-char lines to stderr, then exits
    // 0, under `policy` — the collision fixture (T-062): its stderr can overflow while it still feeds the
    // downstream stage enough stdout for the LAST stage's own stdout cap to overflow too, so both a
    // stage's stderr and the final stdout trip at once.
    let dualStreamStage (stdoutLines: string list) (errLineCount: int) (policy: OutputBufferPolicy) =
        let errLine = String.replicate 32 "x"
        let sep = if isWindows then " & " else "; "
        let stdoutCmds = stdoutLines |> List.map (sprintf "echo %s")
        let errCmds = List.replicate errLineCount (sprintf "echo %s 1>&2" errLine)
        let script = (stdoutCmds @ errCmds) |> String.concat sep
        shell script |> Command.outputBuffer policy

    // A silent producer that never writes (and would run a long time) — the stage whose empty,
    // still-open stdout blocks the relay's read, so proactive teardown, not a broken pipe, is what
    // must end it once a downstream stage fails.
    let slowSilentStage =
        if isWindows then
            shell "ping -n 30 127.0.0.1 >nul"
        else
            shell "sleep 30"

    // Race a pipeline run against a generous deadline: assert it finished by teardown (won the race)
    // rather than by outliving the slow stage (which would let the delay win). 15s is far below the
    // 30s slow stage, so a hang is unmistakable, yet far above the sub-second proactive teardown.
    let assertFinishesPromptly (run: Task<'T>) : Task =
        task {
            let! finished = Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds 15.0))

            Assert.That(
                finished,
                Is.SameAs run,
                "the pipeline must tear the chain down proactively, not wait for the slow/silent stage"
            )
        }
        :> Task

    // Listen to every `int64` measurement on ProcessKit's meter for the pipeline observability
    // tests below — mirrors `LoggingTests.listenToRunMetrics` (not reachable from this file).
    let listenToRunMetrics () =
        let activeDeltas = ConcurrentQueue<int64>()
        let mutable startedCount = 0L
        let mutable completedCount = 0L

        let listener = new MeterListener()

        listener.InstrumentPublished <-
            (fun instrument l ->
                if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                    l.EnableMeasurementEvents instrument)

        listener.SetMeasurementEventCallback<int64>(
            MeasurementCallback<int64>(fun instrument value _tags _state ->
                match instrument.Name with
                | "processkit.runs.active" -> activeDeltas.Enqueue value
                | "processkit.runs.started" -> Interlocked.Add(&startedCount, value) |> ignore
                | "processkit.runs.completed" -> Interlocked.Add(&completedCount, value) |> ignore
                | _ -> ())
        )

        listener.Start()
        listener, activeDeltas, (fun () -> startedCount), (fun () -> completedCount)

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

    // --- Every stage's stderr is bounded by that stage's own OutputBuffer byte cap (T-034) ---

    [<Test>]
    member _.``a chatty stage's stderr is bounded by its own OutputBuffer byte cap``() : Task =
        task {
            let cap = 64
            let noisy = noisyFailingStage 50 (Some cap)
            let pipeline = noisy.Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                Assert.That(
                    result.Outcome,
                    Is.EqualTo(Outcome.Exited 3),
                    "the noisy failing stage must be the pipefail representative carrying the capped stderr"
                )

                let retainedBytes = Encoding.UTF8.GetByteCount result.Stderr
                Assert.That(retainedBytes, Is.LessThanOrEqualTo cap, "retained stderr must never exceed its byte cap")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a stage without an OutputBuffer cap keeps its stderr unbounded, as before``() : Task =
        task {
            let noisy = noisyFailingStage 50 None
            let pipeline = noisy.Pipe sortStage

            match! pipeline.OutputBytesAsync() with
            | Ok result ->
                Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 3))

                let retainedLines =
                    result.Stderr.Split('\n')
                    |> Array.filter (fun l -> l.Trim().Length > 0)
                    |> Array.length

                Assert.That(retainedLines, Is.EqualTo 50, "an uncapped stage's stderr must retain every line")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // --- A fail-loud (`OverflowMode.Error`) stderr overflow on ANY stage surfaces OutputTooLarge (T-062) ---

    [<Test>]
    member _.``an intermediate stage's fail-loud stderr overflow surfaces OutputTooLarge naming that stage``() : Task =
        task {
            // Stage 0's stderr overflows its own fail-loud byte cap; the last stage (sort) is uncapped and
            // exits 0, so nothing else can fail the run — the surfaced error must be stage 0's stderr.
            let cap = 16

            let noisy =
                stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.Error)

            match! (noisy.Pipe sortStage).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, _, byteLimit, totalLines, totalBytes)) ->
                Assert.That(program, Is.EqualTo noisy.Program, "the error must name the overflowing stage")
                Assert.That(byteLimit, Is.EqualTo(Some cap), "the limit must be the offending stage's own cap")
                Assert.That(totalLines, Is.EqualTo 0, "a raw stderr byte capture has no line structure")
                Assert.That(totalBytes, Is.GreaterThan cap, "the totals must reflect the overflow past the cap")
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``the last stage's fail-loud stderr overflow surfaces OutputTooLarge naming the last stage``() : Task =
        task {
            // The last stage overflows its OWN stderr cap (its stdout stays empty, so the final-stdout path
            // is not what trips); the run must still fail loud, naming the last stage's own cap.
            let cap = 16

            let noisyLast =
                stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes cap).WithOverflow OverflowMode.Error)

            match! ((emit [ "banana"; "apple" ]).Pipe noisyLast).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, _, byteLimit, _, totalBytes)) ->
                Assert.That(program, Is.EqualTo noisyLast.Program)
                Assert.That(byteLimit, Is.EqualTo(Some cap), "the last stage's own stderr cap must be reported")
                Assert.That(totalBytes, Is.GreaterThan cap)
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``an earlier stage's stderr overflow outranks a simultaneous final-stdout overflow``() : Task =
        task {
            // first-offending-stage-in-pipeline-order: stage 0's stderr AND the last stage's stdout both
            // trip their fail-loud caps at once; the leftmost stage (stage 0's stderr) must win, reported
            // with ITS cap (16) and program — never the last stage's stdout cap (3).
            let stderrCap = 16
            let stdoutCap = 3

            let stage0 =
                dualStreamStage
                    [ "banana"; "apple" ]
                    50
                    ((OutputBufferPolicy.Unbounded.WithMaxBytes stderrCap).WithOverflow OverflowMode.Error)

            let last =
                sortStage
                |> Command.outputBuffer (
                    (OutputBufferPolicy.Unbounded.WithMaxBytes stdoutCap).WithOverflow OverflowMode.Error
                )

            match! (stage0.Pipe last).OutputBytesAsync() with
            | Error(ProcessError.OutputTooLarge(program, _, byteLimit, _, _)) ->
                Assert.That(program, Is.EqualTo stage0.Program, "the leftmost offending stage must be blamed")

                Assert.That(
                    byteLimit,
                    Is.EqualTo(Some stderrCap),
                    "the leftmost stage's stderr cap must be reported, not the final stdout's"
                )
            | other -> Assert.Fail $"expected OutputTooLarge, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a DropOldest/DropNewest stderr overflow on a stage stays lossy, never an error``() : Task =
        task {
            let mutable okCount = 0

            // The same stderr flood that fails loud under Error stays lossy-but-Ok under a drop mode —
            // no new Error path for the bounded drop modes.
            for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest ] do
                let noisy =
                    stderrStage 50 ((OutputBufferPolicy.Unbounded.WithMaxBytes 16).WithOverflow overflow)

                match! (noisy.Pipe sortStage).OutputBytesAsync() with
                | Ok _ -> okCount <- okCount + 1
                | Error error -> Assert.Fail $"a dropping-mode stderr overflow must not error, got {error}"

            Assert.That(okCount, Is.EqualTo 2, "both drop modes must succeed without a new Error path")
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
    member _.``a failed downstream stage tears down a silent upstream instead of waiting for pipe EOF``() : Task =
        task {
            // Stage 0 never writes and would run ~30s; the relay copying its (empty) stdout blocks
            // indefinitely, and a producer that never writes never dies of a broken pipe. Stage 1 fails
            // fast (checked). Before proactive teardown the chain hung on stage 0's natural exit; now the
            // checked failure kills the whole chain at once — the pipefail representative stays stage 1.
            let pipeline = slowSilentStage.Pipe(shell "exit 7")
            let run = pipeline.OutputStringAsync()
            do! assertFinishesPromptly run

            match! run with
            | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 7))
            | Error error -> Assert.Fail $"expected the failing checked stage as data, got {error}"
        }
        :> Task

    [<Test>]
    member _.``an upstream checked failure tears down a slow downstream but still blames the upstream``() : Task =
        task {
            // Stage 0 fails fast (checked exit 3). Stage 1 ignores its stdin and would run ~30s, so a
            // teardown victim's signal-kill lands to the RIGHT of the real failure. The pipefail result
            // must still be the upstream's exit 3, proving the torn-down downstream never steals blame.
            let pipeline = (shell "exit 3").Pipe slowSilentStage
            let run = pipeline.OutputStringAsync()
            do! assertFinishesPromptly run

            match! run with
            | Ok result ->
                Assert.That(
                    result.Outcome,
                    Is.EqualTo(Outcome.Exited 3),
                    "pipefail blames the upstream's real failure, not the torn-down downstream victim"
                )
            | Error error -> Assert.Fail $"expected exit 3 as data, got {error}"
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
    member _.``cancellation between stage spawns starts no later stage and reaps promptly``() : Task =
        // Regression (T-061): the staging loop registered ONE `linkedCts` callback that KillTree's the
        // stages then running, but never re-checked the token between spawns. `KillTree` leaves the group
        // usable, so a cancellation landing in the window between two spawns killed the running stages,
        // then the loop spawned the NEXT stage right afterwards — a stage the one-shot sweep never
        // targeted, which therefore outlived the pipeline (the caller either lost track of it or blocked
        // on its natural exit). The seam fires cancellation in exactly that window; the fix must start no
        // later stage and return a prompt `Cancelled`, never wait on the long-running escapee.
        task {
            let longStage =
                if isWindows then
                    shell "ping -n 30 127.0.0.1 >nul"
                else
                    shell "sleep 30"

            // Two long stages: without the fix, stage 1 (spawned right after the sweep) escapes and the
            // run blocks ~30s on its natural exit; with the fix it never starts and the run is prompt.
            let pipeline = longStage.Pipe longStage
            use cts = new CancellationTokenSource()

            // Fire cancellation the instant stage 0 has spawned — i.e. between the two spawns, the exact
            // race window. `Cancel` runs the linked KillTree callback inline before the loop reaches
            // stage 1, reproducing "the sweep fired, now the loop wants to start the next stage".
            PipelineRunner.stageSpawnedTestHook <-
                Some(fun index ->
                    if index = 0 then
                        cts.Cancel())

            try
                let run = pipeline.RunAsync cts.Token
                // Won the race against a 15s deadline (far below the 30s stage): a stage started after the
                // sweep would have escaped it and blocked the run on its ~30s natural exit.
                do! assertFinishesPromptly run

                match! run with
                | Error(ProcessError.Cancelled _) -> Assert.Pass()
                | other -> Assert.Fail $"expected a prompt Cancelled once staging was cancelled, got {other}"
            finally
                PipelineRunner.stageSpawnedTestHook <- None
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
    member _.``a per-stage IdleTimeout is rejected on any stage when piped``() =
        // A pipeline captures only the last stage's output and does not monitor per-stage output
        // activity, so a stage's own idle deadline could never fire — reject it at build time (like the
        // per-stage Timeout) rather than silently dropping it.
        let withIdle cmd =
            cmd |> Command.idleTimeout (TimeSpan.FromSeconds 2.0)

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withIdle (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withIdle sortStage) |> ignore))
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
    member _.``a per-stage CancelOn is rejected on any stage when piped``() =
        use cts = new CancellationTokenSource()

        let withCancelOn cmd = cmd |> Command.cancelOn cts.Token

        // On the first stage (index 0)...
        Assert.Throws<ArgumentException>(Action(fun () -> (withCancelOn (emit [ "x" ])).Pipe sortStage |> ignore))
        |> ignore

        // ...and on a later stage (index 1).
        Assert.Throws<ArgumentException>(Action(fun () -> (emit [ "x" ]).Pipe(withCancelOn sortStage) |> ignore))
        |> ignore

    [<Test>]
    member _.``chain-level Pipeline.CancelOn cancels the whole pipeline``() : Task =
        // The chain-level builder is a distinct, un-guarded method (unlike a per-stage Command.CancelOn),
        // so it must keep cancelling the whole chain exactly as before.
        task {
            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)
            let pipeline = ((emit [ "hi" ]).Pipe sleeper).CancelOn cts.Token

            match! pipeline.RunAsync() with
            | Error(ProcessError.Cancelled _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Cancelled, got {other}"
        }
        :> Task

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

    // --- Observability: a pipeline run is whole-chain, not per-stage (T-013) ---

    [<Test>]
    member _.``a successful pipeline run logs spawn and exit under one shared run id``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let pipeline =
                ((emit [ "banana"; "apple" ]) |> Command.logger logger).Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok _ ->
                Assert.That(logger.Text, Does.Contain "spawned")
                Assert.That(logger.Text, Does.Contain "finished")

                let runIdOf (m: string) =
                    Regex.Match(m, @"run ([0-9a-f]+)").Groups[1].Value

                let spawnLine =
                    logger.Text.Split('\n') |> Array.find (fun l -> l.Contains "spawned")

                let exitLine =
                    logger.Text.Split('\n') |> Array.find (fun l -> l.Contains "finished")

                let spawnRunId = runIdOf spawnLine
                Assert.That(spawnRunId, Is.Not.Empty, "spawn carries a run id")
                Assert.That(runIdOf exitLine, Is.EqualTo spawnRunId, "spawn and exit share the run id")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a successful pipeline run's program label composes every stage's name``() : Task =
        task {
            let logger = PipelineCapturingLogger()
            let stage0 = (emit [ "banana"; "apple" ]) |> Command.logger logger
            let pipeline = stage0.Pipe sortStage

            match! pipeline.RunAsync() with
            | Ok _ -> Assert.That(logger.Text, Does.Contain(stage0.Program + " | " + sortStage.Program))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout is logged and reports TimedOut``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                ((emit [ "hi" ]) |> Command.logger logger).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) -> Assert.That(logger.Text, Does.Contain "timed out")
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``argv is never logged for a pipeline run``() : Task =
        task {
            let logger = PipelineCapturingLogger()

            let secretStage =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args [ (if isWindows then "/c" else "-c"); "echo ok"; "--token=SUPERSECRET" ]
                |> Command.logger logger

            let pipeline = secretStage.Pipe sortStage

            let! _ = pipeline.RunAsync()
            Assert.That(logger.Text, Does.Not.Contain "SUPERSECRET")
            Assert.That(logger.Text, Does.Contain "spawned")
        }
        :> Task

    [<Test>]
    member _.``a successful pipeline run emits one runs.started/completed pair and settles active at zero``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            let pipeline = (emit [ "banana"; "apple" ]).Pipe sortStage

            match! pipeline.RunAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok _ ->
                Assert.That(started (), Is.EqualTo 1L, "expected exactly one runs.started for the whole chain")
                Assert.That(completed (), Is.EqualTo 1L, "expected exactly one runs.completed for the whole chain")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero")
        }
        :> Task

    [<Test>]
    member _.``a timed-out pipeline still settles runs.active at zero without counting extra completions``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            let sleeper =
                if isWindows then
                    shell "ping -n 6 127.0.0.1 >nul"
                else
                    shell "sleep 5"

            let pipeline =
                (emit [ "hi" ]).Pipe(sleeper).Timeout(TimeSpan.FromMilliseconds 300.0)

            match! pipeline.RunAsync() with
            | Error(ProcessError.Timeout _) ->
                Assert.That(started (), Is.EqualTo 1L)
                Assert.That(completed (), Is.EqualTo 1L, "a timed-out run still reaches a terminal state")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must return to zero on timeout")
            | other -> Assert.Fail $"expected Timeout, got {other}"
        }
        :> Task

    [<Test>]
    member _.``a partial spawn failure past stage 0 clears runs.active without counting as completed``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Stage 0 spawns fine; the second stage's program does not exist, so the chain fails to
            // spawn fully. `runs.started`/`runs.active` were already armed by stage 0 — they must be
            // closed, but this must never count as `runs.completed` (a spawn failure, not a run that
            // reached a terminal verb).
            let pipeline =
                (shell "echo hello").Pipe(Command.create "pk-definitely-not-a-program-xyz")

            match! pipeline.RunAsync() with
            | Error _ ->
                Assert.That(started (), Is.EqualTo 1L, "stage 0 did spawn, so the chain counts as started")
                Assert.That(completed (), Is.EqualTo 0L, "a spawn failure must not count as completed")
                Assert.That(activeDeltas |> Seq.sum, Is.EqualTo 0L, "runs.active must not leak on a partial spawn")
            | Ok _ -> Assert.Fail "expected an error from the missing pipeline stage"
        }
        :> Task

    [<Test>]
    member _.``a stage-0 spawn failure emits no run metrics at all``() : Task =
        task {
            let listener, activeDeltas, started, completed = listenToRunMetrics ()
            use _listener = listener

            // Stage 0 itself never spawns, so — mirroring a single command's own spawn failure never
            // counting as a run — no `runs.started`/`runs.active` mark is ever armed for this pipeline.
            let pipeline =
                (Command.create "pk-definitely-not-a-program-xyz").Pipe(shell "echo hello")

            match! pipeline.RunAsync() with
            | Error _ ->
                Assert.That(started (), Is.EqualTo 0L, "stage 0 never spawned, so the run never started")
                Assert.That(completed (), Is.EqualTo 0L)
                Assert.That(activeDeltas |> Seq.isEmpty, Is.True, "no runs.active mark was ever armed")
            | Ok _ -> Assert.Fail "expected an error from the missing stage-0 program"
        }
        :> Task
