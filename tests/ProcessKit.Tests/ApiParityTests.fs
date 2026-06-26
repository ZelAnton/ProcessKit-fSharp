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
