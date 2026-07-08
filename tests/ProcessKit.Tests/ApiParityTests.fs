namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NUnit.Framework
open ProcessKit
open ProcessKit.Extensions.Hosting
open ProcessKit.Testing

/// Tests for the API completeness & parity additions: CliClient verbs routed through the configured
/// runner, the ProcessGroup runner honouring Command.Timeout, generic ensureSuccess, the
/// OutputBufferPolicy line cap, and readiness-probe cancellation.
[<TestFixture>]
type ApiParityTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A long-running child, portable across cmd.exe and /bin/sh.
    let sleeper () =
        if isWindows then
            shell "ping 127.0.0.1 -n 10 >NUL"
        else
            shell "sleep 8"

    [<Test>]
    member _.``ProcessError accessors read fields across cases without destructuring``() =
        let exit = ProcessError.Exit("git", 2, "out", "err")
        Assert.That(exit.Program, Is.EqualTo(Some "git"))
        Assert.That(exit.Code, Is.EqualTo(Some 2))
        Assert.That(exit.Stdout, Is.EqualTo(Some "out"))
        Assert.That(exit.Stderr, Is.EqualTo(Some "err"))
        Assert.That(exit.Signal, Is.EqualTo(None: int option))
        Assert.That(exit.Combined, Is.EqualTo(Some "out\nerr"))

        let signalled = ProcessError.Signalled("tool", Some 9, "", "boom")
        Assert.That(signalled.Program, Is.EqualTo(Some "tool"))
        Assert.That(signalled.Signal, Is.EqualTo(Some 9))
        Assert.That(signalled.Code, Is.EqualTo(None: int option))
        Assert.That(signalled.Combined, Is.EqualTo(Some "boom")) // stdout empty -> just stderr

        // Cases with no program / no streams read as None rather than forcing a match on every variant.
        let io = ProcessError.Io "disk full"
        Assert.That(io.Program, Is.EqualTo(None: string option))
        Assert.That(io.Stdout, Is.EqualTo(None: string option))
        Assert.That(io.Combined, Is.EqualTo(None: string option))

        let cancelled = ProcessError.Cancelled "svc"
        Assert.That(cancelled.Program, Is.EqualTo(Some "svc"))
        Assert.That(cancelled.Combined, Is.EqualTo(None: string option))

    [<Test>]
    member _.``ProcessResult Combined joins the streams and OutputContainsAny searches both``() =
        let r = ProcessResult.Failure "stdout line" "stderr line" 1
        Assert.That(r.Combined, Is.EqualTo "stdout line\nstderr line")
        Assert.That(r.OutputContainsAny [ "STDOUT" ], Is.True) // case-insensitive, in stdout
        Assert.That(r.OutputContainsAny [ "STDERR" ], Is.True) // in stderr
        Assert.That(r.OutputContainsAny [ "absent" ], Is.False)
        Assert.That(r.OutputContainsAny [], Is.False) // empty needles -> false
        Assert.That(r.OutputContainsAny [ "nope"; "stdout" ], Is.True) // any-of

        // A needle never matches across the stdout/stderr boundary — each stream is searched on its own.
        let split = ProcessResult.Failure "foo" "bar" 1
        Assert.That(split.OutputContainsAny [ "foobar" ], Is.False)

        // A byte[] capture decodes for both the search and the join.
        let bytesResult =
            ProcessResult.Failure (System.Text.Encoding.UTF8.GetBytes "binary MARKER here") "" 1

        Assert.That(bytesResult.OutputContainsAny [ "marker" ], Is.True)
        Assert.That(bytesResult.Combined, Is.EqualTo "binary MARKER here")

        // Null-tolerance: a null needle element (as a non-nullable-unaware C# caller could pass) is
        // skipped, not a crash — other needles still match.
        let nullStr = Unchecked.defaultof<string>
        Assert.That(r.OutputContainsAny [ nullStr; "stdout" ], Is.True)
        Assert.That(r.OutputContainsAny [ nullStr ], Is.False)
        Assert.That(r.OutputContainsAny [ "" ], Is.True) // empty needle matches (String.Contains parity)

        // A null stderr (constructible via the factories) must not crash and reads as empty.
        let nullErr = ProcessResult.Failure "out" nullStr 1
        Assert.That(nullErr.OutputContainsAny [ "absent" ], Is.False)
        Assert.That(nullErr.Combined, Is.EqualTo "out")

        // A non-string/byte[] capture (via the generic factory) uses ToString for the text form.
        let intResult = ProcessResult.Success 42
        Assert.That(intResult.Combined, Is.EqualTo "42")
        Assert.That(intResult.OutputContainsAny [ "42" ], Is.True)

    [<Test>]
    member _.``many concurrent runs all complete (async wait under load)``() : Task =
        task {
            // Exercises the OS-wait path under concurrency (Windows registered-wait / POSIX wait): 40
            // short runs, 16 live at once, must all complete with a real exit code — none hung or lost.
            let runner: IProcessRunner = JobRunner()
            let commands = [ for i in 0..39 -> shell $"exit {i % 3}" ]
            let! results = Exec.outputAll 16 runner commands CancellationToken.None
            Assert.That(results.Length, Is.EqualTo 40)

            let codes =
                results
                |> Array.map (fun r ->
                    match r with
                    | Ok outcome -> outcome.Code
                    | Error _ -> None)

            Assert.That(codes |> Array.forall Option.isSome, Is.True)
        }

    [<Test>]
    member _.``the derived verbs are available on any IProcessRunner``() : Task =
        task {
            // A ScriptedRunner gains ExitCode (a derived verb) for free via the runner extensions.
            let scripted: IProcessRunner = ScriptedRunner().On([ "tool" ], Reply.Fail(2, ""))

            match! scripted.ExitCodeAsync(Command.create "tool") with
            | Ok code -> Assert.That(code, Is.EqualTo 2)
            | Error error -> Assert.Fail $"expected ExitCode 2, got {error.Message}"

            // A ProcessGroup used as a runner gains Run too.
            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            use group = group

            match! (group :> IProcessRunner).RunAsync(shell "echo hi") with
            | Ok out -> Assert.That(out.Trim(), Is.EqualTo "hi")
            | Error error -> Assert.Fail $"group.RunAsync failed: {error.Message}"
        }

    [<Test>]
    member _.``CliClient verbs route through the configured runner, not the default``() : Task =
        task {
            // The program is not a real binary; only the scripted runner can answer it. If a client
            // verb fell back to the default JobRunner the call would fail with a not-found error
            // instead of the scripted exit code — so Ok 3 proves the verb went through config.Runner.
            let scripted: IProcessRunner =
                ScriptedRunner().On([ "pk-fake-tool"; "status" ], Reply.Fail(3, ""))

            let client = CliClient("pk-fake-tool").WithRunner scripted

            match! client.ExitCodeAsync [ "status" ] with
            | Ok code -> Assert.That(code, Is.EqualTo 3)
            | Error error -> Assert.Fail $"expected the scripted exit code, got {error.Message}"
        }

    [<Test>]
    member _.``ProcessGroup as a runner honours the command timeout``() : Task =
        task {
            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            use group = group
            let command = sleeper () |> Command.timeout (TimeSpan.FromMilliseconds 400.0)

            match! (group :> IProcessRunner).OutputStringAsync(command, CancellationToken.None) with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    [<Test>]
    member _.``ProcessGroup runner captures identically to JobRunner (same normalization)``() : Task =
        task {
            // The shared-group runner captures through the same RunningProcess path as JobRunner, so the
            // captured stdout is normalized identically (CRLF -> LF, trailing newline trimmed) rather
            // than raw-decoded — `group.OutputStringAsync cmd` must equal `JobRunner` for the same command.
            let command = shell "echo hi"
            let jobRunner: IProcessRunner = JobRunner()

            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            use group = group

            let! viaJob = jobRunner.OutputStringAsync(command, CancellationToken.None)
            let! viaGroup = (group :> IProcessRunner).OutputStringAsync(command, CancellationToken.None)

            match viaJob, viaGroup with
            | Ok j, Ok g ->
                Assert.That(j.Stdout, Is.EqualTo "hi") // trailing newline trimmed, CRLF collapsed
                Assert.That(g.Stdout, Is.EqualTo j.Stdout) // shared-group runner agrees with JobRunner
            | _ -> Assert.Fail "expected both runners to capture successfully"
        }

    [<Test>]
    member _.``ensureSuccess works on a byte[] result``() : Task =
        task {
            // ensureSuccess is generic over the captured-stdout type: a failing bytes capture maps to
            // an Exit error just like the text capture does.
            match! (shell "exit 7").OutputBytesAsync() with
            | Error error -> Assert.Fail $"OutputBytes errored: {error.Message}"
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Error(ProcessError.Exit(_, code, _, _)) -> Assert.That(code, Is.EqualTo 7)
                | Error other -> Assert.Fail $"expected an Exit error, got {other.Message}"
                | Ok _ -> Assert.Fail "expected exit 7 to fail ensureSuccess"
        }

    [<Test>]
    member _.``OutputBufferPolicy.WithMaxLines sets the line cap and preserves the rest``() =
        let policy =
            OutputBufferPolicy.Unbounded.WithMaxBytes(4096).WithOverflow(OverflowMode.Error).WithMaxLines(7)

        Assert.That(policy.MaxLines, Is.EqualTo(Some 7))
        Assert.That(policy.MaxBytes, Is.EqualTo(Some 4096))
        Assert.That(policy.Overflow, Is.EqualTo OverflowMode.Error)

    [<Test>]
    member _.``an all-unchecked pipeline with a failing last stage still succeeds``() : Task =
        task {
            // Every stage opts out of pipefail, so nothing can fail the pipeline — Run must succeed
            // even though the last stage exits non-zero.
            let pipeline =
                ((shell "echo hi") |> Command.uncheckedInPipe).Pipe((shell "exit 5") |> Command.uncheckedInPipe)

            match! pipeline.RunAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"expected an all-unchecked pipeline to succeed, got {error.Message}"
        }

    [<Test>]
    member _.``WaitForLine tolerates an out-of-range timeout instead of throwing``() : Task =
        task {
            let runner: IProcessRunner = JobRunner()

            match! runner.StartAsync(sleeper (), CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use _ = running
                // A negative timeout must not throw out of the CTS constructor; it resolves to NotReady.
                match! running.WaitForLineAsync((fun _ -> false), TimeSpan.FromSeconds -1.0) with
                | Error(ProcessError.NotReady _) -> ()
                | other -> Assert.Fail $"expected NotReady for a negative timeout, got {other}"
        }

    [<Test>]
    member _.``WaitFor with a cancelled token reports Cancelled, not NotReady``() : Task =
        task {
            let runner: IProcessRunner = JobRunner()

            match! runner.StartAsync(sleeper (), CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use cts = new CancellationTokenSource()
                cts.Cancel()

                let! outcome =
                    running.WaitForAsync((fun () -> Task.FromResult false), TimeSpan.FromSeconds 5.0, cts.Token)

                match outcome with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"expected Cancelled, got {other}"

                do! (running :> IAsyncDisposable).DisposeAsync()
        }

    [<Test>]
    member _.``AddProcessKitHostedProcess registers an IHostedService``() =
        let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
        services.AddProcessKitHostedProcess("svc", shell "echo hosted") |> ignore
        use provider = services.BuildServiceProvider()
        let hosted = provider.GetServices<IHostedService>() |> Seq.toArray
        Assert.That(hosted, Has.Length.EqualTo 1)
