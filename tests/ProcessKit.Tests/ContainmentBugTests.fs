namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// A synthetic `IContainmentBackend` for the lifecycle-guard barrier tests. It owns no real OS handle:
/// `Spawn` hands out a fresh fake handle with no streams, and every verb is a book-keeping no-op. Its
/// job is to police the guard's invariant — it records whether teardown (`HardRelease`) has run and
/// FLAGS any spawn/control/stat op driven after teardown, which is exactly what `ProcessGroup`'s single
/// critical section must make impossible (an op either completes on the live backend or is refused
/// before it reaches the backend). It also records which fake children were reaped, so a double-reap or
/// an un-reaped child left tracked after teardown is observable.
type internal SyntheticBackend() =
    let gate = obj ()
    let tracked = System.Collections.Generic.HashSet<nativeint>()
    let reaped = System.Collections.Generic.List<nativeint>()
    let violations = System.Collections.Generic.List<string>()
    let mutable released = false
    let mutable hardReleaseCount = 0
    let mutable spawnCount = 0
    let mutable nextHandle = 0

    let note (message: string) =
        lock gate (fun () -> violations.Add message)

    // Assert the backend is live at entry AND after a yield that widens the race window. Under a correct
    // guard `released` cannot flip while an op runs — the op and the teardown serialize on ProcessGroup's
    // lifecycle lock — so both reads see `false`; a broken guard would let `HardRelease` slip in between.
    let requireLive (op: string) =
        if lock gate (fun () -> released) then
            note $"{op} entered after HardRelease"

        Thread.Yield() |> ignore

        if lock gate (fun () -> released) then
            note $"{op} completed after HardRelease"

    let freshHandle () =
        lock gate (fun () ->
            nextHandle <- nextHandle + 1
            nativeint nextHandle)

    /// How many times teardown ran — must be exactly one across any mix of Dispose/DisposeAsync/Shutdown.
    member _.HardReleaseCount = lock gate (fun () -> hardReleaseCount)
    /// How many children were actually spawned (a start that lost the race to the release spawns none).
    member _.SpawnCount = lock gate (fun () -> spawnCount)
    /// Children still tracked — must be zero after teardown drains them.
    member _.TrackedCount = lock gate (fun () -> tracked.Count)
    /// Children reaped by teardown, with duplicates preserved so a double-reap is visible.
    member _.ReapedCount = lock gate (fun () -> reaped.Count)

    member _.DistinctReapedCount =
        lock gate (fun () -> reaped |> Seq.distinct |> Seq.length)

    /// Every use-after-teardown the guard let through — must stay empty.
    member _.Violations = lock gate (fun () -> List.ofSeq violations)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(_command) =
            requireLive "Spawn"
            let handle = freshHandle ()
            lock gate (fun () -> spawnCount <- spawnCount + 1)

            Ok
                { Native.Common.Spawned.Handle = handle
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  WindowsCtrlGroup = false }

        member _.Track(spawned) =
            requireLive "Track"
            lock gate (fun () -> tracked.Add spawned.Handle |> ignore)
            Ok()

        member _.Release(spawned) =
            lock gate (fun () -> tracked.Remove spawned.Handle |> ignore)

        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(spawned) = Some(int spawned.Handle)
        member _.KillChild(_spawned) = ()
        member _.KillTree() = requireLive "KillTree"
        member _.GracefulKillTree(_grace) = Task.CompletedTask

        member _.Members() =
            requireLive "Members"
            lock gate (fun () -> Ok(tracked |> Seq.map int |> List.ofSeq))

        member _.Signal(_signal) =
            requireLive "Signal"
            Ok()

        member _.Suspend() =
            requireLive "Suspend"
            Ok()

        member _.Resume() =
            requireLive "Resume"
            Ok()

        member _.Stats() =
            requireLive "Stats"
            Ok(ProcessGroupStats(lock gate (fun () -> tracked.Count), None, None))

        member _.HardRelease() =
            // Drain (reap) every tracked child exactly once and mark released, all under one lock — the
            // atomic teardown the guard must serialize every backend op against.
            lock gate (fun () ->
                reaped.AddRange tracked
                tracked.Clear()
                released <- true
                hardReleaseCount <- hardReleaseCount + 1)

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
            // The mutual-exclusion primitive underlying teardown's per-child ownership: a concurrent
            // Drain (HardRelease's atomic take-and-clear) and Remove (a run's `Release`, or the cgroup
            // `Track` migration-failure reap) for the same tracked item must never both report success —
            // exactly one of them may act on the item, or Drain must win outright (it always empties the
            // whole list). Run it many times under `Task.WhenAll` to shake out any lock-ordering issue.
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
    member _.``StartAsync racing Dispose never builds a run over a released backend and reaps once``() : Task =
        task {
            // The spawn-versus-dispose race at the ProcessGroup level, over a synthetic backend that
            // fails loud if any op touches it after teardown. With the single lifecycle lock the start
            // either wins outright — the child is spawned, tracked, and then reaped exactly once by the
            // teardown — or it loses and spawns nothing, returning the non-transient released error. It
            // is never a RunningProcess built over a backend whose teardown has already begun.
            for _ in 1..300 do
                let backend = SyntheticBackend()
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())
                let command = Command.create "synthetic"

                let mutable startResult = Unchecked.defaultof<Result<RunningProcess, ProcessError>>

                let startTask =
                    Task.Run(fun () -> startResult <- group.StartAsync(command).GetAwaiter().GetResult())

                let disposeTask = Task.Run(fun () -> (group :> IDisposable).Dispose())
                do! Task.WhenAll(startTask, disposeTask)

                Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
                // Dispose owns the one teardown; a shared-group start never releases the group.
                Assert.That(backend.HardReleaseCount, Is.EqualTo 1)
                Assert.That(backend.TrackedCount, Is.EqualTo 0, "a child was left tracked after teardown")
                Assert.That(backend.DistinctReapedCount, Is.EqualTo backend.ReapedCount, "a child was reaped twice")

                match startResult with
                | Ok running ->
                    // Won the race: exactly one spawn, and the group reaped it on Dispose (one reap).
                    Assert.That(backend.SpawnCount, Is.EqualTo 1)
                    Assert.That(backend.ReapedCount, Is.EqualTo 1)
                    GC.KeepAlive running
                | Error err ->
                    // Lost the race: released before the spawn, so nothing was spawned and the error is
                    // the non-transient released condition (a retry must not re-try a dead group).
                    Assert.That(backend.SpawnCount, Is.EqualTo 0)
                    Assert.That(ProcessError.isTransient err, Is.False)
        }

    [<Test>]
    member _.``control and stat verbs racing Dispose run on the live backend or fail Unsupported``() : Task =
        task {
            // Signal/Stats/Members/Suspend racing a concurrent Dispose. The guard makes each verb atomic
            // with the release: it either completes fully on the live backend (Ok) or is refused before
            // touching native (a non-transient Unsupported) — never a half-run against a torn-down
            // backend. The synthetic backend flags any op that reaches it after teardown.
            for _ in 1..300 do
                let backend = SyntheticBackend()
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                match! group.StartAsync(Command.create "synthetic") with
                | Ok running -> GC.KeepAlive running
                | Error e -> failwith $"seed start failed: {e}"

                let results =
                    System.Collections.Concurrent.ConcurrentQueue<Result<unit, ProcessError>>()

                let signalTask = Task.Run(fun () -> results.Enqueue(group.Signal Signal.Term))

                let statsTask =
                    Task.Run(fun () -> results.Enqueue(group.Stats() |> Result.map ignore))

                let membersTask =
                    Task.Run(fun () -> results.Enqueue(group.Members() |> Result.map ignore))

                let suspendTask = Task.Run(fun () -> results.Enqueue(group.Suspend()))
                let disposeTask = Task.Run(fun () -> (group :> IDisposable).Dispose())
                do! Task.WhenAll(signalTask, statsTask, membersTask, suspendTask, disposeTask)

                Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
                Assert.That(backend.HardReleaseCount, Is.EqualTo 1)

                for result in results do
                    match result with
                    | Ok() -> ()
                    | Error err -> Assert.That(ProcessError.isTransient err, Is.False)
        }

    [<Test>]
    member _.``concurrent Dispose, DisposeAsync, and ShutdownAsync tear down exactly once``() : Task =
        task {
            // The once-only teardown under a three-way race. Exactly one of the paths wins the release
            // transition and runs HardRelease; the losers are no-ops. The seeded child is reaped exactly
            // once, and no op is driven against the backend after teardown.
            for _ in 1..300 do
                let backend = SyntheticBackend()
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                match! group.StartAsync(Command.create "synthetic") with
                | Ok running -> GC.KeepAlive running
                | Error e -> failwith $"seed start failed: {e}"

                let disposeTask = Task.Run(fun () -> (group :> IDisposable).Dispose())

                let disposeAsyncTask: Task =
                    Task.Run(fun () -> (group :> IAsyncDisposable).DisposeAsync().AsTask())

                let shutdownTask: Task = Task.Run(fun () -> group.ShutdownAsync TimeSpan.Zero)
                do! Task.WhenAll(disposeTask, disposeAsyncTask, shutdownTask)

                Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
                Assert.That(backend.HardReleaseCount, Is.EqualTo 1, "teardown ran more than once")
                Assert.That(backend.ReapedCount, Is.EqualTo 1, "the seeded child was not reaped exactly once")
                Assert.That(backend.DistinctReapedCount, Is.EqualTo backend.ReapedCount, "a child was reaped twice")
        }
