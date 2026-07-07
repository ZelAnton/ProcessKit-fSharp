namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Regression tests for containment-integrity fixes: spawning into a released group, pipeline
/// mid-chain spawn failures, inherited stdio, and teardown reaping (no zombie leaders).
[<TestFixture>]
type ContainmentBugTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    [<Test>]
    member _.``spawning into a released group fails fast and is not transient``() : Task =
        task {
            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            (group :> IDisposable).Dispose() // release the group

            // Spawning through the released group must fail rather than leak an uncontained child,
            // and the failure must NOT be classified transient (a retry must not re-try a dead group).
            match! Runner.outputString group CancellationToken.None (shell "exit 0") with
            | Error err -> Assert.That(ProcessError.isTransient err, Is.False)
            | Ok _ -> Assert.Fail "expected an error when spawning into a released group"
        }

    [<Test>]
    member _.``Start on a released group fails``() : Task =
        task {
            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            (group :> IDisposable).Dispose()

            match! group.StartAsync(shell "exit 0") with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected an error starting into a released group"
        }

    [<Test>]
    member _.``a pipeline with a non-existent stage errors without hanging``() : Task =
        task {
            // The first stage spawns; the second fails to spawn — the error branch must reap the
            // started stage and return promptly rather than hang or leak.
            let pipeline =
                (shell "echo hello").Pipe(Command.create "pk-definitely-not-a-program-xyz")

            match! pipeline.RunAsync() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected an error from the missing pipeline stage"
        }

    [<Test>]
    member _.``a child with inherited stdout runs successfully``() : Task =
        task {
            // With StdioMode.Inherit the child writes to this process's own stdout; it must still
            // run cleanly (on macOS the spawn must keep fd 1 open under CLOEXEC_DEFAULT).
            let cmd = (shell "echo inherited-ok") |> Command.stdout StdioMode.Inherit

            match! cmd.RunAsync() with
            | Ok _ -> ()
            | Error err -> Assert.Fail $"inherited-stdout run failed: {err.Message}"
        }

    [<Test>]
    member _.``disposing a group reaps its unawaited children instead of leaving zombies``() : Task =
        task {
            // Zombie (defunct) state is only observable portably via /proc, which is Linux-only.
            if not isLinux then
                Assert.Ignore "zombie state is observable via /proc on Linux only"

            let group =
                match ProcessGroup.Create() with
                | Ok g -> g
                | Error e -> failwith $"ProcessGroup.Create failed: {e}"

            // Start a child but deliberately never consume the RunningProcess, so the group's teardown
            // is the only thing that can reap the leader.
            let! started = group.StartAsync(shell "sleep 30")

            let _running =
                match started with
                | Ok r -> r
                | Error e -> failwith $"Start failed: {e}"

            let pids =
                match group.Members() with
                | Ok m -> m
                | Error e -> failwith $"Members failed: {e}"

            Assert.That(Seq.isEmpty pids, Is.False, "expected the started child to be tracked")

            // Teardown must SIGKILL *and* waitpid the leaders. After Dispose each pid must be fully
            // reaped (gone from /proc) — not lingering as a zombie (state 'Z').
            (group :> IDisposable).Dispose()
            GC.KeepAlive _running

            for pid in pids do
                let statPath = $"/proc/{pid}/stat"

                let isZombie =
                    if not (File.Exists statPath) then
                        false // reaped: no /proc entry
                    else
                        try
                            let stat = File.ReadAllText statPath
                            // "/proc/<pid>/stat" is "pid (comm) state ...": comm may hold spaces and
                            // parens, so locate the state field just past the final ')'.
                            let closeParen = stat.LastIndexOf ')'
                            closeParen >= 0 && closeParen + 2 < stat.Length && stat.[closeParen + 2] = 'Z'
                        with :? IOException ->
                            // the entry vanished between the existence check and the read — reaped.
                            false

                Assert.That(isZombie, Is.False, $"child pid {pid} was left as a zombie after dispose")
        }

    [<Test>]
    member _.``TrackedChildren.Drain and Remove of the same item never both win the race``() : Task =
        task {
            // The mutual-exclusion primitive underlying the HardRelease/ReapEscapee fix: a concurrent
            // Drain (HardRelease's atomic take-and-clear) and Remove (ReapEscapee's guard) for the same
            // tracked item must never both report success — exactly one of them may act on the item, or
            // Drain must win outright (it always empties the whole list). Run it many times under
            // `Task.WhenAll` to shake out any lock-ordering issue.
            for _ in 1..500 do
                let children = TrackedChildren<int>()
                children.Add 42

                let mutable drained: int list = []
                let mutable removed = false

                let drainTask = Task.Run(fun () -> drained <- children.Drain())
                let removeTask = Task.Run(fun () -> removed <- children.Remove 42)
                do! Task.WhenAll(drainTask, removeTask)

                let bothClaimedIt = List.contains 42 drained && removed

                Assert.That(
                    bothClaimedIt,
                    Is.False,
                    "both Drain and Remove claimed the same tracked item — the double-reap race is open"
                )

                // Regardless of who won, nothing tracked is left behind.
                Assert.That(children.Snapshot(), Is.Empty)
        }

    [<Test>]
    member _.``ProcessGroupBackend: HardRelease racing ReapEscapee never leaves the pgid tracked twice``() : Task =
        task {
            // The full-stack shape of the fix: HardRelease (teardown) and ReapEscapee (racing-spawn
            // cleanup) contend for the SAME tracked pgid. Before this fix HardRelease used Snapshot (no
            // clear) and ReapEscapee unconditionally re-reaped, so both could killpg/waitpid the same
            // leader — after the first waitpid the OS may reuse that pid for an unrelated process
            // group, so the second killpg would land on the wrong target. Actually observing a
            // wrong-target kill needs a pid-reuse window this test cannot force deterministically; what
            // IS deterministic and asserted here is that only one side ever "wins" the pgid (Members()
            // is empty afterwards either way, and no exception/hang occurs from a double reap racing).
            if isWindows then
                Assert.Ignore "ProcessGroupBackend is the POSIX (non-cgroup) mechanism only"

            for _ in 1..20 do
                let backend = ProcessGroupBackend() :> IContainmentBackend

                let spawned =
                    match backend.Spawn(shell "sleep 5") with
                    | Ok s -> s
                    | Error e -> failwith $"spawn failed: {e}"

                match backend.Track spawned with
                | Ok() -> ()
                | Error e -> failwith $"track failed: {e}"

                let hardReleaseTask = Task.Run(fun () -> backend.HardRelease())
                let reapEscapeeTask = Task.Run(fun () -> backend.ReapEscapee spawned)
                do! Task.WhenAll(hardReleaseTask, reapEscapeeTask)

                match backend.Members() with
                | Ok members -> Assert.That(members, Is.Empty, "the pgid must not remain tracked after both race")
                | Error e -> failwith $"Members failed: {e}"
        }
