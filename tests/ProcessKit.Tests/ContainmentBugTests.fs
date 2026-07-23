namespace ProcessKit.Tests

open System
open System.Diagnostics
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
    let mutable killChildCount = 0
    let mutable killTreeCount = 0
    let mutable gracefulKillTreeCount = 0
    let mutable updateLimitsCount = 0
    let mutable adoptCount = 0

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

    /// How many times a native single-child hard kill actually reached the backend (a gated kill that
    /// no-ops on a released/torn-down run never increments this).
    member _.KillChildCount = lock gate (fun () -> killChildCount)
    /// How many times a native whole-tree hard kill actually reached the backend.
    member _.KillTreeCount = lock gate (fun () -> killTreeCount)
    /// How many times a native graceful tree kill actually reached the backend.
    member _.GracefulKillTreeCount = lock gate (fun () -> gracefulKillTreeCount)
    /// How many times a live limit re-apply actually reached the backend (a gated update that is
    /// refused on a released group before touching native never increments this).
    member _.UpdateLimitsCount = lock gate (fun () -> updateLimitsCount)
    /// How many times an adopt actually reached the backend (a gated adopt refused on a released group
    /// before touching native never increments this).
    member _.AdoptCount = lock gate (fun () -> adoptCount)

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
                  WindowsCtrlGroup = false
                  PtyControl = None }

        member _.Track(spawned) =
            requireLive "Track"
            lock gate (fun () -> tracked.Add spawned.Handle |> ignore)
            Ok()

        member _.Adopt(_pid) =
            // An adopt must never reach the backend after teardown; count the ones that do so a live adopt
            // is still observable and a post-release one is a flagged violation. Adopted processes are not
            // our children, so — like the real backends — nothing is added to `tracked`/`reaped`.
            requireLive "Adopt"
            lock gate (fun () -> adoptCount <- adoptCount + 1)
            Ok()

        member _.Release(spawned) =
            lock gate (fun () -> tracked.Remove spawned.Handle |> ignore)

        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(_spawned) =
            // A native kill must never reach the backend after teardown; count the ones that do so a live
            // kill is still observable and a post-release/post-teardown one is a flagged violation.
            requireLive "KillChild"
            lock gate (fun () -> killChildCount <- killChildCount + 1)

        member _.KillTree() =
            requireLive "KillTree"
            lock gate (fun () -> killTreeCount <- killTreeCount + 1)

        member _.GracefulKillTree(_grace) =
            requireLive "GracefulKillTree"
            lock gate (fun () -> gracefulKillTreeCount <- gracefulKillTreeCount + 1)
            Task.CompletedTask

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

        member _.UpdateLimits(_limits) =
            // A live limit re-apply must never reach the backend after teardown; count the ones that do
            // so a live update is still observable and a post-release one is a flagged violation.
            requireLive "UpdateLimits"
            lock gate (fun () -> updateLimitsCount <- updateLimitsCount + 1)
            Ok()

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
    member _.``UpdateLimits after the group is released is non-transient and never touches the backend``() =
        // A released group must refuse a live limit update the same way every other control verb does:
        // the lifecycle gate returns a non-transient error BEFORE the backend is reached, so no
        // SetInformationJobObject / cgroup write can ever land on a closed/recycled native handle.
        let backend = SyntheticBackend()
        let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        (group :> IDisposable).Dispose()

        match group.UpdateLimits(ResourceLimits.None.WithMemoryMax(64L * 1024L * 1024L)) with
        | Error err -> Assert.That(ProcessError.isTransient err, Is.False)
        | Ok() -> Assert.Fail "UpdateLimits on a released group must fail, not silently succeed"

        Assert.That(backend.UpdateLimitsCount, Is.EqualTo 0, "a limit update reached the backend after release")
        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``UpdateLimits on a live group reaches the backend and refreshes the Options snapshot``() =
        // The happy path through the lifecycle gate: the update reaches the live backend exactly once and
        // the `Options` snapshot a consumer reads back is swapped to the new set (only on success).
        let backend = SyntheticBackend()
        let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        let newLimits =
            ResourceLimits.None.WithMemoryMax(200L * 1024L * 1024L).WithCpuQuota(1.5)

        match group.UpdateLimits newLimits with
        | Ok() ->
            Assert.That(backend.UpdateLimitsCount, Is.EqualTo 1)
            Assert.That(group.Options.Limits.MemoryMax, Is.EqualTo(Some(200L * 1024L * 1024L)))
            Assert.That(group.Options.Limits.CpuQuota, Is.EqualTo(Some 1.5))
        | Error err -> Assert.Fail $"a live update on the synthetic backend should succeed, got {err}"

        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``Adopt after the group is released is non-transient and never touches the backend``() =
        // Adopting into a released group must be refused the same way every other control verb is: the
        // lifecycle gate returns a non-transient error BEFORE the backend is reached, so no
        // AssignProcessToJobObject / cgroup.procs write can ever land on a closed/removed native container.
        // Uses the current process as a convenient live external process — the guard bails before any
        // native adopt is even attempted, so nothing actually happens to it.
        let backend = SyntheticBackend()
        let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        (group :> IDisposable).Dispose()

        use self = Process.GetCurrentProcess()

        match group.Adopt self with
        | Error err -> Assert.That(ProcessError.isTransient err, Is.False)
        | Ok() -> Assert.Fail "Adopt into a released group must fail, not silently succeed"

        Assert.That(backend.AdoptCount, Is.EqualTo 0, "an adopt reached the backend after release")
        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``Adopt of a live process on a live group reaches the backend exactly once``() =
        // The happy path through the lifecycle gate: a live external process (the test process itself)
        // reaches the live backend's adopt exactly once. The synthetic backend records nothing beyond the
        // count — an adopted process is not our child, so it is deliberately not tracked/reaped.
        let backend = SyntheticBackend()
        let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())
        use self = Process.GetCurrentProcess()

        match group.Adopt self with
        | Ok() -> Assert.That(backend.AdoptCount, Is.EqualTo 1)
        | Error err -> Assert.Fail $"a live adopt on the synthetic backend should succeed, got {err}"

        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``Adopt of a null process throws ArgumentNullException eagerly``() =
        // A null argument is a programming error, surfaced eagerly as an exception (like the other eager
        // argument guards on the type), not folded into the Result channel.
        let backend = SyntheticBackend()
        use group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        Assert.Throws<ArgumentNullException>(Action(fun () -> group.Adopt(Unchecked.defaultof<Process>) |> ignore))
        |> ignore

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

    [<Test>]
    member _.``Kill and StopAsync after the group is released never touch the backend (shared path)``() : Task =
        task {
            // A shared-group handle whose GROUP is released out from under it (an external
            // `ProcessGroup.Dispose()`), then `Kill()`/`StopAsync()` are still called on the live handle.
            // The kill closures route through the lifecycle gate, so neither reaches the backend — no
            // `KillChild` on a Job handle/pid the teardown already closed/recycled (use-after-close /
            // wrong-target kill). The synthetic backend flags any native kill that slips through.
            let backend = SyntheticBackend()
            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())
            let! started = group.StartAsync(Command.create "synthetic")

            let running =
                match started with
                | Ok r -> r
                | Error e -> failwith $"shared start failed: {e}"

            (group :> IDisposable).Dispose() // release the shared group under the live handle

            running.Kill() // fire-and-forget hard kill — must observe the released group and no-op
            let! _ = running.StopAsync() // graceful stop — likewise a no-op on the released group

            Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

            Assert.That(
                backend.KillChildCount,
                Is.EqualTo 0,
                "a native child kill reached the backend after the group was released"
            )

            Assert.That(backend.GracefulKillTreeCount, Is.EqualTo 0)
            Assert.That(backend.HardReleaseCount, Is.EqualTo 1)
            GC.KeepAlive running
        }

    [<Test>]
    member _.``Kill and StopAsync after the owned handle is disposed never touch the backend (owned path)``() : Task =
        task {
            // The owned path (a private per-run group, as `JobRunner` builds): disposing the handle reaps
            // and releases the group. A `Kill()`/`StopAsync()` afterward must not `KillTree`/graceful-kill a
            // container whose handle teardown already closed — the gate no-ops both, and teardown still ran
            // exactly once.
            let backend = SyntheticBackend()
            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

            let host =
                match group.StartInternal(Command.create "synthetic") with
                | Ok h -> h
                | Error e -> failwith $"owned start failed: {e}"

            let running = RunningProcess host
            do! (running :> IAsyncDisposable).DisposeAsync() // owned teardown reaps + releases the group

            running.Kill() // StartKill = KillTree — must no-op on the released group
            let! _ = running.StopAsync() // GracefulKill = GracefulKillTree — must no-op too

            Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
            Assert.That(backend.KillTreeCount, Is.EqualTo 0, "a native tree kill reached a released owned group")
            Assert.That(backend.GracefulKillTreeCount, Is.EqualTo 0)
            Assert.That(backend.HardReleaseCount, Is.EqualTo 1, "owned teardown must run exactly once")
        }

    [<Test>]
    member _.``a live shared Kill reaches the backend but a torn-down run's Kill does not``() : Task =
        task {
            // Two halves of the same guarantee. (1) No over-gating: a `Kill()` on a LIVE shared group must
            // still reach the backend — the timeout/pump-fault kills reuse the same closure and must keep
            // working. (2) The per-run flag: once THIS handle's own teardown has detached it (a shared group
            // stays live for other runs), its `Kill()` must no longer touch native — the recycled-pid /
            // closed-handle window after its own `Release`.
            let backend = SyntheticBackend()
            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())
            let! firstStart = group.StartAsync(Command.create "synthetic")

            let liveRun =
                match firstStart with
                | Ok r -> r
                | Error e -> failwith $"first shared start failed: {e}"

            liveRun.Kill() // live group + live run: the native child kill must land
            Assert.That(backend.KillChildCount, Is.EqualTo 1, "a live shared Kill must reach the backend")

            let! secondStart = group.StartAsync(Command.create "synthetic")

            let tornDownRun =
                match secondStart with
                | Ok r -> r
                | Error e -> failwith $"second shared start failed: {e}"

            do! (tornDownRun :> IAsyncDisposable).DisposeAsync() // detach THIS run; the group stays live
            tornDownRun.Kill() // its own teardown ran — must no-op even though the group is live

            Assert.That(
                backend.KillChildCount,
                Is.EqualTo 1,
                "a torn-down run's Kill reached the backend while the group was still live"
            )

            Assert.That(backend.HardReleaseCount, Is.EqualTo 0, "the shared group must still be live here")
            Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
            GC.KeepAlive liveRun
            (group :> IDisposable).Dispose()
        }

    [<Test>]
    member _.``StopAsync on a live owned group graceful-kills the tree (no regression)``() : Task =
        task {
            // The timeout/StopAsync graceful path must keep working on a live group: the gate lets it
            // through, and it reaches the backend's graceful tree kill exactly once, then reaps.
            let backend = SyntheticBackend()
            let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

            let host =
                match group.StartInternal(Command.create "synthetic") with
                | Ok h -> h
                | Error e -> failwith $"owned start failed: {e}"

            let running = RunningProcess host
            let! _ = running.StopAsync(TimeSpan.Zero)

            Assert.That(
                backend.GracefulKillTreeCount,
                Is.EqualTo 1,
                "a live-group StopAsync must graceful-kill the tree"
            )

            Assert.That(backend.HardReleaseCount, Is.EqualTo 1, "StopAsync must reap the owned group once")
            Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))
        }
