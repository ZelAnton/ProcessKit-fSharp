namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit

[<TestFixture>]
type ErgonomicsTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shellProgram = if isWindows then "cmd.exe" else "/bin/sh"
    let shellFlag = if isWindows then "/c" else "-c"

    let shell (script: string) =
        Command.create shellProgram |> Command.args [ shellFlag; script ]

    // ----- ok_codes -----

    [<Test>]
    member _.``OkCodes accepts a configured non-zero exit``() : Task =
        task {
            match! (shell "exit 2" |> Command.okCodes [ 0; 2 ]).Run() with
            | Ok _ -> Assert.Pass()
            | Error error -> Assert.Fail $"expected success (2 is accepted), got {error}"
        }
        :> Task

    [<Test>]
    member _.``a non-zero exit outside OkCodes still fails Run``() : Task =
        task {
            match! (shell "exit 2").Run() with
            | Error(ProcessError.Exit(_, 2, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected Exit 2, got {other}"
        }
        :> Task

    [<Test>]
    member _.``OkCodes that excludes 0 makes a zero exit a failure``() : Task =
        task {
            let command = shell "exit 0" |> Command.okCodes [ 1 ]

            match! command.OutputString() with
            | Ok result ->
                Assert.That(result.IsSuccess, Is.False)
                CollectionAssert.AreEqual([| 1 |], result.AcceptedCodes)
            | Error error -> Assert.Fail $"{error}"

            match! command.Run() with
            | Error(ProcessError.Exit(_, 0, _, _)) -> Assert.Pass()
            | other -> Assert.Fail $"expected Exit 0, got {other}"
        }
        :> Task

    [<Test>]
    member _.``CreateNoWindow does not break a normal run``() : Task =
        task {
            match! (shell "echo nowindow" |> Command.createNoWindow).Run() with
            | Ok output -> Assert.That(output, Does.Contain "nowindow")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``EnvClear clears the inherited environment``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX env-echo; the clearing logic is the shared EnvClear path."
            else
                Environment.SetEnvironmentVariable("PK_INHERIT_PROBE", "leaked")

                let command =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "echo [${PK_INHERIT_PROBE}]" ]
                    |> Command.envClear

                match! command.Run() with
                | Ok output -> Assert.That(output.Trim(), Is.EqualTo "[]")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // ----- CliClient -----

    [<Test>]
    member _.``CliClient applies its default timeout to built commands``() =
        let client = CliClient(shellProgram).DefaultTimeout(TimeSpan.FromSeconds 7.0)
        let command = client.Command [ shellFlag; "echo hi" ]
        Assert.That(command.ConfiguredTimeout, Is.EqualTo(Some(TimeSpan.FromSeconds 7.0)))

    [<Test>]
    member _.``CliClient runs commands through its runner``() : Task =
        task {
            let client = CliClient.create shellProgram

            match! client.Run [ shellFlag; "echo client" ] with
            | Ok output -> Assert.That(output, Does.Contain "client")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``CliClient default env reaches the child``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX env-echo."
            else
                let client = CliClient("/bin/sh").DefaultEnv("PK_CLIENT_VAR", "configured")

                match! client.Run [ "-c"; "echo env-$PK_CLIENT_VAR" ] with
                | Ok output -> Assert.That(output, Does.Contain "env-configured")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    // ----- top-level Exec -----

    [<Test>]
    member _.``Exec.run runs a program by name``() : Task =
        task {
            match! Exec.run shellProgram [ shellFlag; "echo execrun" ] CancellationToken.None with
            | Ok output -> Assert.That(output, Does.Contain "execrun")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Exec.outputAll collects every result in input order``() : Task =
        task {
            let runner: IProcessRunner = JobRunner()

            let commands =
                [ shell "echo one"; shell "echo two"; shell "echo three"; shell "exit 4" ]

            let! results = Exec.outputAll 2 runner commands CancellationToken.None
            Assert.That(results.Length, Is.EqualTo 4)

            let stdoutOf i =
                match results[i] with
                | Ok result -> result.Stdout
                | Error error -> failwith $"unexpected error at {i}: {error}"

            Assert.That(stdoutOf 0, Does.Contain "one")
            Assert.That(stdoutOf 1, Does.Contain "two")
            Assert.That(stdoutOf 2, Does.Contain "three")

            // The batch never short-circuits: the failing command is still an Ok(ProcessResult)
            // whose non-zero code is data.
            match results[3] with
            | Ok result -> Assert.That(result.Code, Is.EqualTo(Some 4))
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task
