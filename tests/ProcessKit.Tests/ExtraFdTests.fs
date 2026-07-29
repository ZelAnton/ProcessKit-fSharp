namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

[<TestFixture>]
type ExtraFdTests() =

    [<Test>]
    member _.``extra fd rejects reserved and duplicate targets``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command("tool").ExtraFd(2) |> ignore))
        |> ignore

        let command = Command("tool").ExtraFd(3)

        Assert.Throws<ArgumentException>(Action(fun () -> command.ExtraFd(3) |> ignore))
        |> ignore

    [<TestCase(3)>]
    [<TestCase(8)>]
    member _.``extra fd is a full-duplex POSIX channel claimed exactly once``(targetFd: int) : Task =
        task {
            let command =
                Command("/bin/sh")
                    .Args(
                        [ "-c"
                          $"printf 'READY\\n' >&{targetFd}; IFS= read -r value <&{targetFd}; printf 'ECHO:%%s\\n' \"$value\" >&{targetFd}" ]
                    )
                    .ExtraFd(targetFd)

            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                match! command.StartAsync() with
                | Error error -> Assert.That(error.IsUnsupported, Is.True, error.Message)
                | Ok running ->
                    do! (running :> IAsyncDisposable).DisposeAsync()
                    Assert.Fail "Windows unexpectedly accepted a POSIX extra file descriptor"
            else
                match! command.StartAsync() with
                | Error error -> Assert.Fail error.Message
                | Ok running ->
                    use running = running

                    use channel =
                        running.TakeExtraFd(targetFd)
                        |> Option.defaultWith (fun () -> failwith $"missing fd {targetFd}")

                    Assert.That(running.TakeExtraFd(targetFd) |> Option.isNone, Is.True)

                    use reader = new StreamReader(channel, leaveOpen = true)
                    use writer = new StreamWriter(channel, leaveOpen = true)
                    writer.AutoFlush <- true

                    let! ready = reader.ReadLineAsync()
                    Assert.That(ready, Is.EqualTo "READY")
                    do! writer.WriteLineAsync("payload")
                    let! echoed = reader.ReadLineAsync()
                    Assert.That(echoed, Is.EqualTo "ECHO:payload")

                    let! outcome = running.WaitAsync()
                    Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))
        }
        :> Task

    [<Test>]
    member _.``pipeline detached and in-memory runners reject extra fd honestly``() : Task =
        task {
            let command = Command("tool").ExtraFd(3)

            match command.LaunchDetached() with
            | Error error -> Assert.That(error.IsUnsupported, Is.True, error.Message)
            | Ok _ -> Assert.Fail "detached launch unexpectedly accepted an extra fd"

            Assert.Throws<ArgumentException>(Action(fun () -> command.Pipe(Command("sink")) |> ignore))
            |> ignore

            let scripted: IProcessRunner = ScriptedRunner().Fallback(Reply.Ok "")
            let dryRun: IProcessRunner = DryRunRunner()

            for runner in [ scripted; dryRun ] do
                match! runner.StartAsync(command, CancellationToken.None) with
                | Error error -> Assert.That(error.IsUnsupported, Is.True, error.Message)
                | Ok running ->
                    do! (running :> IAsyncDisposable).DisposeAsync()
                    Assert.Fail "in-memory runner unexpectedly accepted an extra fd"
        }
        :> Task

    [<Test>]
    member _.``extra fd is not inherited by a concurrent ordinary child``() : Task =
        task {
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                Assert.Ignore "POSIX-only: verifies close-on-exec descriptor hygiene"

            let holder = Command("/bin/sh").Args([ "-c"; "sleep 1" ]).ExtraFd(3)

            match! holder.StartAsync() with
            | Error error -> Assert.Fail error.Message
            | Ok running ->
                use running = running

                use _channel =
                    running.TakeExtraFd(3) |> Option.defaultWith (fun () -> failwith "missing fd 3")

                match! Command("/bin/sh").Args([ "-c"; "(printf leak >&3) 2>/dev/null; test $? -ne 0" ]).RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"ordinary child inherited fd 3: {error.Message}"

                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``repeated low extra fd spawns do not leak reservation descriptors``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Linux) then
                Assert.Ignore "Linux-only: /proc exposes the process fd count"

            let runOnce () =
                task {
                    match! Command("/bin/sh").Args([ "-c"; "exit 0" ]).ExtraFd(3).StartAsync() with
                    | Error error -> Assert.Fail error.Message
                    | Ok running ->
                        use running = running

                        use _channel =
                            running.TakeExtraFd(3) |> Option.defaultWith (fun () -> failwith "missing fd 3")

                        let! outcome = running.WaitAsync()
                        Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))
                }

            do! runOnce ()
            let baseline = Directory.GetFileSystemEntries("/proc/self/fd").Length

            for _ in 1..30 do
                do! runOnce ()

            let after = Directory.GetFileSystemEntries("/proc/self/fd").Length
            Assert.That(after, Is.LessThan(baseline + 10), $"fd count grew from {baseline} to {after}")
        }
        :> Task
