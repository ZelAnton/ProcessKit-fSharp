namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Regression tests for the correctness & robustness fixes: timeout validation/clamping, the
/// single-consumption guard on `RunningProcess`, pipeline per-stage `OkCodes`, and pipeline wiring
/// of a stage whose stdout was set non-piped.
[<TestFixture>]
type CorrectnessBugTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A path in an existing directory (temp) with a random leaf, so `File.OpenRead` fails with a
    // genuine source error (FileNotFound), not a directory-not-found or a permissions quirk.
    let missingStdinPath () =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pk-missing-{Guid.NewGuid():N}.txt")

    [<Test>]
    member _.``a negative command timeout is rejected at configuration time``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () ->
                Command.create "whatever"
                |> Command.timeout (TimeSpan.FromSeconds -1.0)
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a negative pipeline timeout is rejected at configuration time``() =
        let pipeline = (shell "echo a").Pipe(shell "cat")

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> pipeline.Timeout(TimeSpan.FromSeconds -1.0) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a timeout larger than the timer range is treated as no timeout, not a throw``() : Task =
        task {
            // TimeSpan.MaxValue would overflow Task.Delay and throw synchronously, faulting the run and
            // orphaning the pumps; it must instead run as if no timeout were set.
            let cmd = (shell "exit 0") |> Command.timeout TimeSpan.MaxValue

            match! cmd.RunAsync() with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"expected the run to complete, got {err.Message}"
        }

    [<Test>]
    member _.``a second terminal verb on a consumed RunningProcess is refused, not a double-pump``() : Task =
        task {
            match! runner.StartAsync(shell "echo hi", CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Error e -> Assert.Fail $"first OutputString failed: {e.Message}"
                | Ok _ ->
                    // A second buffered verb is refused with a clean error rather than racing a second
                    // reader on the (now torn-down) pipe.
                    match! running.OutputStringAsync() with
                    | Error(ProcessError.Unsupported _) -> ()
                    | Error other -> Assert.Fail $"expected Unsupported, got {other.Message}"
                    | Ok _ -> Assert.Fail "expected the second OutputString to be refused"

                    // A non-Result terminal verb refuses by throwing.
                    Assert.Throws<InvalidOperationException>(Action(fun () -> running.WaitAsync() |> ignore))
                    |> ignore

                do! (running :> IAsyncDisposable).DisposeAsync()
        }

    [<Test>]
    member _.``a pipeline honours the last stage's accepted exit codes``() : Task =
        task {
            // The last stage exits 3, but 3 is one of its accepted codes — pipefail must treat that as
            // success, which means the pipeline result must carry that stage's OkCodes (not a hardcoded
            // {0}).
            let pipeline = (shell "echo hi").Pipe((shell "exit 3") |> Command.okCodes [ 0; 3 ])

            match! pipeline.OutputStringAsync() with
            | Error e -> Assert.Fail $"pipeline errored: {e.Message}"
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Ok _ -> ()
                | Error e -> Assert.Fail $"expected the accepted exit code to pass, got {e.Message}"
        }

    [<Test>]
    member _.``a pipeline still fails on an unaccepted exit code``() : Task =
        task {
            let pipeline = (shell "echo hi").Pipe(shell "exit 4")

            match! pipeline.RunAsync() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected the pipeline to fail on the unaccepted exit 4"
        }

    [<Test>]
    member _.``a pipeline stage with a non-piped stdout does not deadlock``() : Task =
        task {
            // The producing stage's stdout is set to Inherit; the pipeline must still wire it to the
            // next stage's stdin (forcing Piped) instead of leaving `cat` blocked on an unfed stdin.
            // The timeout is a safety net — a regression would surface as TimedOut, not a hung suite.
            // `sort` is a cross-platform passthrough for single-line input (exists on Windows and
            // Unix); it emits once it sees EOF on stdin, so it doubles as a check that the pipeline
            // closes the wired-up stdin.
            let pipeline =
                ((shell "echo hello") |> Command.stdout StdioMode.Inherit).Pipe(shell "sort")
                |> Pipeline.timeout (TimeSpan.FromSeconds 10.0)

            match! pipeline.OutputStringAsync() with
            | Error e -> Assert.Fail $"pipeline errored: {e.Message}"
            | Ok result ->
                match result.Outcome with
                | Outcome.TimedOut -> Assert.Fail "pipeline deadlocked (timed out) — intermediate stdout was not wired"
                | _ -> Assert.That(result.Stdout.Trim(), Is.EqualTo "hello")
        }

    [<Test>]
    member _.``a missing FromFile stdin source surfaces as ProcessError.Stdin on a successful run``() : Task =
        task {
            // The source can't be opened, so the child gets empty stdin and still exits 0. That silent
            // failure must surface as `ProcessError.Stdin` rather than a spurious `Ok` — otherwise a
            // consumer never learns its input was dropped.
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a missing stdin source to surface as ProcessError.Stdin"
        }

    [<Test>]
    member _.``a louder non-zero exit wins over a stdin-source failure``() : Task =
        task {
            // The stdin source is missing (a genuine feed failure) but the process exits non-zero. The
            // "realer" failure wins: the outcome passes through as data, not `ProcessError.Stdin`.
            let cmd = (shell "exit 7") |> Command.stdin (Stdin.FromFile(missingStdinPath ()))

            match! cmd.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 7 -> ()
                | other -> Assert.Fail $"expected exit 7 to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a non-zero exit must win over the stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"
        }

    [<Test>]
    member _.``a readable stdin source on a successful run never surfaces a stdin error``() : Task =
        task {
            // A valid source feeding a child that may close stdin early (a broken pipe) must never be
            // misreported as `ProcessError.Stdin` — only a genuine source-acquisition failure is.
            let cmd =
                (shell "exit 0")
                |> Command.stdin (Stdin.FromString "payload the child may ignore")

            match! cmd.OutputStringAsync() with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"a readable stdin source must not error, got {err.Message}"
        }
