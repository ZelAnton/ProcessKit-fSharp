namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

[<TestFixture>]
type ShutdownTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let createGroup () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    let tempMarker (prefix: string) =
        let id = Guid.NewGuid().ToString("N")
        Path.Combine(Path.GetTempPath(), $"pk-{prefix}-{id}.marker")

    [<Test>]
    member _.``Shutdown reaps a running process promptly``() : Task =
        task {
            use group = createGroup ()

            let sleeper =
                if isWindows then
                    shell "ping 127.0.0.1 -n 30"
                else
                    shell "sleep 30"

            let stopwatch = Stopwatch.StartNew()
            // Start the run but do not await it; Shutdown must kill the still-running child.
            let capture =
                (group :> IProcessRunner).OutputStringAsync(sleeper, CancellationToken.None)

            do! Task.Delay 300
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            let! result = capture
            stopwatch.Stop()

            match result with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"capture failed: {error}"

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 15.0))
        }
        :> Task

    [<Test>]
    member _.``Shutdown is idempotent with itself and Dispose``() : Task =
        task {
            let group = createGroup ()
            // The second Shutdown and the Dispose must be no-ops, not signal a released handle/pgid.
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
            (group :> IDisposable).Dispose()
            Assert.Pass()
        }
        :> Task

    [<Test>]
    member _.``Shutdown reaps a lingering grandchild``() : Task =
        if isWindows then
            Assert.Ignore "Deep-tree reaping uses POSIX shell job control; exercised on Unix."

        // The parent backgrounds a grandchild (detached stdio, so the parent's pipe still EOFs)
        // that writes a marker after a delay. Containment + Shutdown must reap it first.
        task {
            let marker = tempMarker "reap"

            try
                use group = createGroup ()
                let script = $"( sleep 3 && touch {marker} ) </dev/null >/dev/null 2>&1 &"

                match! (group :> IProcessRunner).OutputStringAsync(shell script, CancellationToken.None) with
                | Error error -> Assert.Fail $"spawn failed: {error}"
                | Ok _ -> ()

                do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
                do! Task.Delay 4000
                Assert.That(File.Exists marker, Is.False, "grandchild escaped the group and wrote its marker")
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task

    [<Test>]
    member _.``a setsid child escapes the group (documented weakness)``() : Task =
        if not isLinux then
            Assert.Ignore "The setsid-escape demonstration needs the Linux `setsid` binary."

        // A POSIX process group is escapable: a child that calls setsid() starts a new session
        // and is no longer reached by killpg. This is the documented weakness cgroup v2 closes.
        task {
            let marker = tempMarker "setsid"

            try
                use group = createGroup ()

                let script =
                    $"( setsid sh -c 'sleep 3 && touch {marker}' ) </dev/null >/dev/null 2>&1 &"

                match! (group :> IProcessRunner).OutputStringAsync(shell script, CancellationToken.None) with
                | Error error -> Assert.Fail $"spawn failed: {error}"
                | Ok _ -> ()

                // Let setsid finish establishing the new session before teardown, so we test an
                // *established* escapee rather than racing the detach.
                do! Task.Delay 1000
                do! group.ShutdownAsync(TimeSpan.FromSeconds 1.0)
                do! Task.Delay 4000

                Assert.That(
                    File.Exists marker,
                    Is.True,
                    "a setsid child should escape killpg — the documented pgroup weakness"
                )
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        :> Task
