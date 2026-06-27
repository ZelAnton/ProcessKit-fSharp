namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
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
    member _.``the derived verbs are available on any IProcessRunner``() : Task =
        task {
            // A ScriptedRunner gains ExitCode (a derived verb) for free via the runner extensions.
            let scripted: IProcessRunner = ScriptedRunner().On([ "tool" ], Reply.Fail(2, ""))

            match! scripted.ExitCode(Command.create "tool") with
            | Ok code -> Assert.That(code, Is.EqualTo 2)
            | Error error -> Assert.Fail $"expected ExitCode 2, got {error.Message}"

            // A ProcessGroup used as a runner gains Run too.
            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            use group = group

            match! (group :> IProcessRunner).Run(shell "echo hi") with
            | Ok out -> Assert.That(out.Trim(), Is.EqualTo "hi")
            | Error error -> Assert.Fail $"group.Run failed: {error.Message}"
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

            match! client.ExitCode [ "status" ] with
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

            match! (group :> IProcessRunner).OutputString(command, CancellationToken.None) with
            | Ok result -> Assert.That(result.IsTimedOut, Is.True)
            | Error error -> Assert.Fail $"expected a timed-out result, got {error.Message}"
        }

    [<Test>]
    member _.``ensureSuccess works on a byte[] result``() : Task =
        task {
            // ensureSuccess is generic over the captured-stdout type: a failing bytes capture maps to
            // an Exit error just like the text capture does.
            match! (shell "exit 7").OutputBytes() with
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

            match! pipeline.Run() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"expected an all-unchecked pipeline to succeed, got {error.Message}"
        }

    [<Test>]
    member _.``WaitForLine tolerates an out-of-range timeout instead of throwing``() : Task =
        task {
            let runner: IProcessRunner = JobRunner()

            match! runner.Start(sleeper (), CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use _ = running
                // A negative timeout must not throw out of the CTS constructor; it resolves to NotReady.
                match! running.WaitForLine((fun _ -> false), TimeSpan.FromSeconds -1.0) with
                | Error(ProcessError.NotReady _) -> ()
                | other -> Assert.Fail $"expected NotReady for a negative timeout, got {other}"
        }

    [<Test>]
    member _.``WaitFor with a cancelled token reports Cancelled, not NotReady``() : Task =
        task {
            let runner: IProcessRunner = JobRunner()

            match! runner.Start(sleeper (), CancellationToken.None) with
            | Error e -> failwith $"Start failed: {e}"
            | Ok running ->
                use cts = new CancellationTokenSource()
                cts.Cancel()

                let! outcome = running.WaitFor((fun () -> Task.FromResult false), TimeSpan.FromSeconds 5.0, cts.Token)

                match outcome with
                | Error(ProcessError.Cancelled _) -> ()
                | other -> Assert.Fail $"expected Cancelled, got {other}"

                do! (running :> IAsyncDisposable).DisposeAsync()
        }
