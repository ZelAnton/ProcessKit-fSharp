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
    let mutable adoptByPidCount = 0

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

    /// How many times a BARE-PID adopt actually reached the backend (a released group, or a pid the
    /// public guard refuses up front, never increments this).
    member _.AdoptByPidCount = lock gate (fun () -> adoptByPidCount)

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
                  ExtraFds = []
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

        member _.AdoptByPid(_pid) =
            // The bare-pid door runs the same lifecycle gate, so it is policed the same way and counted
            // separately: a test can then tell which verb reached the backend, and the pid guards
            // (`pid <= 0`, this process's own pid) are provable by this counter staying at zero.
            requireLive "AdoptByPid"
            lock gate (fun () -> adoptByPidCount <- adoptByPidCount + 1)
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
            Ok()

        member _.GracefulKillTree (_signal) (_grace) =
            requireLive "GracefulKillTree"
            lock gate (fun () -> gracefulKillTreeCount <- gracefulKillTreeCount + 1)
            Task.CompletedTask

        member _.SignalChild(_spawned, _signal) = Ok()

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
            Ok(ProcessGroupStats(lock gate (fun () -> tracked.Count), None, None, None, None))

        member _.MemberStats() =
            requireLive "MemberStats"
            Ok []

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

/// A synthetic backend that models the Windows `ctrlGroups` stale-snapshot delivery race (T-204). Its
/// `Signal` mirrors `JobObjectBackend.Signal(Int/Term)`: it snapshots the set of still-tracked children
/// (the `ctrlGroups.Values` analogue), waits at a barrier that widens the delivery window, then
/// "delivers" a by-number Ctrl event to each snapshot member — recording a WRONG-TARGET delivery for any
/// member a concurrent `Release` has already dropped (exactly where the real backend would
/// `GenerateConsoleCtrlEvent` a pid the OS may have recycled). `Release` — the shared-run per-teardown
/// detach — stops tracking the child (dropping its `ctrlGroups` entry and closing its handle in the real
/// backend). With the fix, `ProcessGroup` runs `Release` under the same `sync` lock `Signal` holds, so
/// the two can never interleave: at delivery time every snapshot member is still tracked and no
/// wrong-target delivery is recorded. `SnapshotTaken` lets the test start the racing `Release` only once
/// `Signal` has taken its snapshot; `proceedToDeliver` lets an off-lock `Release` (the pre-fix behaviour)
/// unblock delivery immediately, so the wrong-target window is exercised deterministically rather than by
/// luck.
type internal CtrlSignalRaceBackend() =
    let gate = obj ()
    let tracked = System.Collections.Generic.HashSet<nativeint>()
    let wrongTargets = System.Collections.Generic.List<nativeint>()
    let snapshotTaken = new ManualResetEventSlim(false)
    let proceedToDeliver = new ManualResetEventSlim(false)
    let mutable nextHandle = 0

    /// Set once `Signal` has snapshotted the tracked children and is about to deliver — the point after
    /// which a racing `Release` must not be able to make the snapshot stale.
    member _.SnapshotTaken = snapshotTaken

    /// Reset the per-iteration barrier state so the backend can be re-raced.
    member _.ResetBarriers() =
        snapshotTaken.Reset()
        proceedToDeliver.Reset()

    /// The snapshot members a delivery hit after they had already left tracking — must stay empty.
    member _.WrongTargetDeliveries = lock gate (fun () -> List.ofSeq wrongTargets)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject

        member _.Spawn(_command) =
            let handle =
                lock gate (fun () ->
                    nextHandle <- nextHandle + 1
                    nativeint nextHandle)

            Ok
                { Native.Common.Spawned.Handle = handle
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  ExtraFds = []
                  WindowsCtrlGroup = true
                  PtyControl = None }

        member _.Track(spawned) =
            lock gate (fun () -> tracked.Add spawned.Handle |> ignore)
            Ok()

        member _.Adopt(_pid) = Ok()
        member _.AdoptByPid(_pid) = Ok()

        member _.Release(spawned) =
            // The shared-run teardown detach: stop tracking this child (drops its ctrlGroups entry and
            // closes its handle in the real backend), then let a delivery blocked at the barrier proceed —
            // so if this ran OFF the lock (the pre-fix behaviour) the delivery would immediately see a
            // now-untracked member and misfire.
            lock gate (fun () -> tracked.Remove spawned.Handle |> ignore)
            proceedToDeliver.Set()

        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(spawned) = Some(int spawned.Handle)
        member _.KillChild(_spawned) = ()
        member _.KillTree() = Ok()
        member _.GracefulKillTree (_signal) (_grace) = Task.CompletedTask
        member _.SignalChild(_spawned, _signal) = Ok()

        member _.Members() =
            lock gate (fun () -> Ok(tracked |> Seq.map int |> List.ofSeq))

        member _.Signal(_signal) =
            // Snapshot the still-tracked children (the `ctrlGroups.Values` analogue), announce the
            // snapshot, then wait briefly at the barrier before delivering — widening the window a racing
            // `Release` would need to make the snapshot stale. A member no longer tracked at delivery is a
            // wrong-target Ctrl event, exactly what the fix must make impossible.
            let snapshot = lock gate (fun () -> List.ofSeq tracked)
            snapshotTaken.Set()
            // Bounded wait: an off-lock `Release` sets this the instant it drops the child (pre-fix). Under
            // the fix `Release` is blocked on `sync` behind this very call, so this simply times out and
            // delivery then finds every member still tracked.
            proceedToDeliver.Wait 250 |> ignore

            for handle in snapshot do
                let stillTracked = lock gate (fun () -> tracked.Contains handle)

                if not stillTracked then
                    lock gate (fun () -> wrongTargets.Add handle)

            Ok()

        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(lock gate (fun () -> tracked.Count), None, None, None, None))

        member _.MemberStats() = Ok []

        member _.UpdateLimits(_limits) = Ok()
        member _.HardRelease() = lock gate (fun () -> tracked.Clear())

/// A synthetic backend whose spawned "children" hand back a parent-side stdout that a DESCENDANT is
/// still holding open (T-360): the leader's own wait answers at once — its fate is settled — while the
/// pipe never reaches EOF, exactly as it does when the leader spawned something that inherited its
/// stdout and outlived it.
///
/// It exists to pin down the OWNERSHIP half of the bounded post-exit output drain, which is decided by
/// `ProcessGroup.BuildHost`'s `ownsGroup` branch and is invisible from a `RunningProcess`-only test:
/// a PRIVATE per-run group must be released (reaping whatever the leader left behind) once the bound
/// lets the verb reach its teardown, while a SHARED group must not be — its members belong to the
/// group, not to one run. The counters below are exactly those two questions.
type internal InheritedPipeBackend() =
    let gate = obj ()
    let tracked = System.Collections.Generic.HashSet<nativeint>()
    let mutable hardReleaseCount = 0
    let mutable killTreeCount = 0
    let mutable killChildCount = 0
    let mutable gracefulKillTreeCount = 0
    let mutable nextHandle = 0

    /// How many times the whole container was torn down — the private group's reap of the remainder.
    member _.HardReleaseCount = lock gate (fun () -> hardReleaseCount)
    /// Native whole-tree kills that reached the backend.
    member _.KillTreeCount = lock gate (fun () -> killTreeCount)
    /// Native single-child kills that reached the backend.
    member _.KillChildCount = lock gate (fun () -> killChildCount)
    /// Native graceful tree kills that reached the backend.
    member _.GracefulKillTreeCount = lock gate (fun () -> gracefulKillTreeCount)
    /// Children the container still owns — a shared group keeps its other runs after one is detached.
    member _.TrackedCount = lock gate (fun () -> tracked.Count)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(_command) =
            let handle =
                lock gate (fun () ->
                    nextHandle <- nextHandle + 1
                    nativeint nextHandle)

            // A cancellable held-open read — what a real piped run gets on every supported platform
            // (a Windows overlapped named pipe, a POSIX socketpair).
            let stdout =
                new HeldOpenOutputStream(
                    System.Text.Encoding.UTF8.GetBytes "from-the-leader\n",
                    respectsCancellation = true
                )

            Ok
                { Native.Common.Spawned.Handle = handle
                  Stdout = Some(stdout :> Stream)
                  Stderr = None
                  Stdin = None
                  ExtraFds = []
                  WindowsCtrlGroup = false
                  PtyControl = None }

        member _.Track(spawned) =
            lock gate (fun () -> tracked.Add spawned.Handle |> ignore)
            Ok()

        member _.Adopt(_pid) = Ok()
        member _.AdoptByPid(_pid) = Ok()

        member _.Release(spawned) =
            lock gate (fun () -> tracked.Remove spawned.Handle |> ignore)

        // The leader's fate is settled the moment anything asks — the whole point of this fixture is
        // what happens to the OUTPUT pipe afterwards.
        member _.Wait(_handle) = task { return Outcome.Exited 0 }
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(_spawned) =
            lock gate (fun () -> killChildCount <- killChildCount + 1)

        member _.KillTree() =
            lock gate (fun () -> killTreeCount <- killTreeCount + 1)
            Ok()

        member _.GracefulKillTree (_signal) (_grace) =
            lock gate (fun () -> gracefulKillTreeCount <- gracefulKillTreeCount + 1)
            Task.CompletedTask

        member _.SignalChild(_spawned, _signal) = Ok()

        member _.Members() =
            lock gate (fun () -> Ok(tracked |> Seq.map int |> List.ofSeq))

        member _.Signal(_signal) = Ok()
        member _.Suspend() = Ok()
        member _.Resume() = Ok()

        member _.Stats() =
            Ok(ProcessGroupStats(lock gate (fun () -> tracked.Count), None, None, None, None))

        member _.MemberStats() = Ok []
        member _.UpdateLimits(_limits) = Ok()

        member _.HardRelease() =
            lock gate (fun () ->
                tracked.Clear()
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

    // POSIX errno numbers the legacy cgroup sweep's injected pidfd seam returns below (T-330).
    let ESRCH = 3
    let ENOSYS = 38
    let EPERM = 1

    /// A synthetic cgroup directory standing in for the LEGACY teardown path — a kernel < 5.14 with no
    /// `cgroup.kill`. `cgroup.procs` carries the membership the sweep snapshots and re-reads, and
    /// `cgroup.freeze` starts frozen exactly as the fallback's own freeze leaves it. `cgroup.kill` is
    /// created as a DIRECTORY so writing that control file fails just as it does where the kernel does not
    /// expose it, which is what routes `killCgroupUsing` into the fallback — no process-wide test hook, so
    /// this stays valid on any OS and cannot race another fixture.
    let withLegacyCgroup (members: int list) (body: string -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), $"pk-legacy-cgroup-{Guid.NewGuid():N}")

        Directory.CreateDirectory dir |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "cgroup.kill")) |> ignore

        File.WriteAllText(Path.Combine(dir, "cgroup.procs"), members |> List.map string |> String.concat "\n")

        File.WriteAllText(Path.Combine(dir, "cgroup.freeze"), "1\n")

        try
            body dir
        finally
            try
                Directory.Delete(dir, true)
            with
            | :? DirectoryNotFoundException
            | :? IOException
            | :? UnauthorizedAccessException ->
                // Best-effort cleanup of a temp directory; a leftover must not fail the test.
                ()

    /// Drive the bounded post-kill drain wait (`releaseCgroupUsing`, T-363) over scripted answers on a
    /// FAKE clock: `probes` and `removals` are consumed in order and their LAST entry repeats, so a
    /// "persistently populated" cgroup is one entry rather than a guess at the poll count. The clock moves
    /// only when the loop itself sleeps, which makes "the budget ran out" a deterministic consequence of
    /// the loop's own bounded arithmetic instead of wall-clock luck — and nothing here sleeps for real.
    /// Returns the verdict together with what the loop did: how many emptiness probes it took, how many
    /// removals it attempted, and how long it waited in total.
    let scriptedRelease (budget: TimeSpan) (probes: Native.Cgroup.Drain list) (removals: Native.Cgroup.Removal list) =
        let mutable probeCount = 0
        let mutable removalCount = 0
        let mutable now = TimeSpan.Zero

        let probe () =
            let answer = probes[min probeCount (probes.Length - 1)]
            probeCount <- probeCount + 1
            answer

        let remove () =
            let answer = removals[min removalCount (removals.Length - 1)]
            removalCount <- removalCount + 1
            answer

        let verdict =
            Native.Cgroup.releaseCgroupUsing probe remove (fun () -> now) (fun duration -> now <- now + duration) budget

        {| Verdict = verdict
           Probes = probeCount
           Removals = removalCount
           Waited = now |}

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
    member _.``AdoptByPid refuses pid 0, a negative pid and this process's own pid before any backend``() =
        // The two numbers that are never adoptable, refused at the public boundary so the guarantee holds
        // on EVERY mechanism rather than three times over. Typed, not thrown: a pid usually arrives from
        // outside the program (a pidfile, a registry, an IPC message), so a bad one is data to report.
        // The backend counter staying at zero is what proves the refusal happens BEFORE any native adopt —
        // a `kill(0, ...)`/`setpgid(0, ...)` would address the caller's own process group, and adopting
        // our own pid would enlist this process in the teardown of the group it owns.
        let backend = SyntheticBackend()
        use group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        for rejected in [ 0; -1; Environment.ProcessId ] do
            match group.AdoptByPid rejected with
            | Error(ProcessError.Adopt(pid, detail)) ->
                Assert.That(pid, Is.EqualTo rejected)
                Assert.That(String.IsNullOrWhiteSpace detail, Is.False, "a refusal must say why")
            | other -> Assert.Fail $"expected a typed Adopt refusal for pid {rejected}, got {other}"

        Assert.That(backend.AdoptByPidCount, Is.EqualTo 0, "a refused pid must never reach the backend")
        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``AdoptByPid after the group is released is non-transient and never touches the backend``() =
        // The same lifecycle gate every other control verb runs: refused BEFORE the backend, so no
        // AssignProcessToJobObject / cgroup.procs write / ledger entry can land on a torn-down container.
        let backend = SyntheticBackend()
        let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        (group :> IDisposable).Dispose()

        // A pid that is neither zero nor ours, so it passes the argument guards and is stopped only by
        // the lifecycle gate. Nothing native is attempted, so the number itself is inert here.
        match group.AdoptByPid 2_000_000_501 with
        | Error err -> Assert.That(ProcessError.isTransient err, Is.False)
        | Ok() -> Assert.Fail "AdoptByPid into a released group must fail, not silently succeed"

        Assert.That(backend.AdoptByPidCount, Is.EqualTo 0, "a bare-pid adopt reached the backend after release")
        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

    [<Test>]
    member _.``AdoptByPid of a live pid on a live group reaches the backend exactly once``() =
        // The happy path through the gate, and the proof that `Adopt` and `AdoptByPid` are two distinct
        // doors rather than one dispatching to the other: only the bare-pid counter moves.
        let backend = SyntheticBackend()
        use group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

        match group.AdoptByPid 2_000_000_502 with
        | Ok() ->
            Assert.That(backend.AdoptByPidCount, Is.EqualTo 1)
            Assert.That(backend.AdoptCount, Is.EqualTo 0, "the Process overload must not be involved")
        | Error err -> Assert.Fail $"a live bare-pid adopt on the synthetic backend should succeed, got {err}"

        Assert.That(backend.Violations, Is.Empty, String.Join("; ", backend.Violations))

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
                | Ok(h, _) -> h
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
                | Ok(h, _) -> h
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

    [<Test>]
    member _.``Signal racing a shared run's teardown never delivers a Ctrl event to a released group (T-204)``
        ()
        : Task =
        task {
            // The Windows `ctrlGroups` stale-snapshot wrong-target race. `Signal(Int/Term)` snapshots the
            // console-group ids and delivers a CTRL+BREAK to each while holding the group's `sync` lock; a
            // shared run's teardown detaches that run by calling `backend.Release`, which drops the child's
            // `ctrlGroups` entry and closes its process handle (freeing its pid for OS reuse). Running that
            // `Release` OFF the lock let it strike between the snapshot and a delivery, so a CTRL+BREAK could
            // land on a pid the OS had already recycled onto an unrelated console group. The fix serializes
            // the shared-run `Release` under the same `sync` lock `Signal` holds, so at delivery time every
            // snapshot member is still tracked. The synthetic backend flags any delivery to a member that
            // left tracking; with the fix there is none. The barrier makes the (pre-fix) wrong-target window
            // deterministic rather than timing-dependent.
            for _ in 1..5 do
                let backend = CtrlSignalRaceBackend()
                backend.ResetBarriers()
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                let! started = group.StartAsync(Command.create "synthetic")

                let running =
                    match started with
                    | Ok r -> r
                    | Error e -> failwith $"shared start failed: {e}"

                // Deliver the signal on one thread; it snapshots, announces, then waits at the barrier.
                let signalTask = Task.Run(fun () -> group.Signal Signal.Int |> ignore)

                // Only start the racing teardown once `Signal` has taken its snapshot — the exact instant a
                // stale-snapshot delivery could misfire if `Release` were not serialized against it.
                backend.SnapshotTaken.Wait()

                let disposeTask: Task =
                    Task.Run(fun () -> (running :> IAsyncDisposable).DisposeAsync().AsTask())

                do! Task.WhenAll(signalTask, disposeTask)

                Assert.That(
                    backend.WrongTargetDeliveries,
                    Is.Empty,
                    "a CTRL+BREAK was delivered to a child whose Release had already dropped it — the "
                    + "wrong-target race is open"
                )

                GC.KeepAlive running
                (group :> IDisposable).Dispose()
        }

    // --- T-330: the legacy (kernel < 5.14, no `cgroup.kill`) teardown sweep is identity-safe against pid
    // recycling. It used to SIGKILL raw pid numbers snapshotted from `cgroup.procs`, so a member that
    // exited in the snapshot->syscall window could have its number recycled by a process OUTSIDE the
    // cgroup and be killed in its place — the freeze stops members forking, not exiting. Every SIGKILL now
    // goes through the same pin -> reconfirm-membership -> send choke `signalCgroup` uses. These tests
    // drive `killCgroupUsing` with injected pidfd closures, so each race is deterministic and no real
    // kernel, pidfd, or cgroup mount is involved. ---

    [<Test>]
    member _.``the legacy cgroup sweep SIGKILLs a confirmed member through its pinned handle (T-330)``() =
        let memberPid = 4_242
        let signalled = ResizeArray<int>()
        let mutable signalNumber = 0
        let mutable pins = 0
        let mutable closes = 0

        withLegacyCgroup [ memberPid ] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")

            // The fake pin handle IS the pid, so `send` records exactly which task the signal reached.
            let openPin (pid: int) : Result<int, int> =
                pins <- pins + 1
                Ok pid

            let send (handle: int) (signalNum: int) : Result<unit, int> =
                signalled.Add handle
                signalNumber <- signalNum
                // The kernel drops a SIGKILLed member from cgroup.procs; model that so the group drains.
                File.WriteAllText(procs, "")
                Ok()

            let closePin (_handle: int) = closes <- closes + 1

            match Native.Cgroup.killCgroupUsing openPin send closePin dir with
            | Ok() -> ()
            | Error detail -> Assert.Fail $"the identity-safe sweep should have torn the cgroup down: {detail}"

            Assert.That(signalled.Count, Is.EqualTo 1, "a confirmed member must be signalled exactly once")

            Assert.That(signalled[0], Is.EqualTo memberPid, "the SIGKILL must reach the pinned member itself")

            Assert.That(
                signalNumber,
                Is.EqualTo Native.Posix.SIGKILL,
                "the fallback still delivers SIGKILL, only through a pinned handle"
            )

            Assert.That(pins, Is.EqualTo closes, "every pin the sweep opens must be released")
            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.freeze")).Trim(), Is.EqualTo "0"))

    [<Test>]
    member _.``the legacy cgroup sweep never SIGKILLs a pid recycled between snapshot and delivery (T-330)``() =
        let memberPid = 4_243
        let signalled = ResizeArray<int>()
        let mutable pins = 0
        let mutable closes = 0

        withLegacyCgroup [ memberPid ] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")

            let openPin (pid: int) : Result<int, int> =
                pins <- pins + 1
                // Between the `cgroup.procs` snapshot and this pin the member exited and a process OUTSIDE
                // the cgroup recycled its number, so the pin lands on that stranger — and the membership
                // reconfirm, which re-reads cgroup.procs AFTER the pin, no longer lists the pid.
                File.WriteAllText(procs, "")
                Ok pid

            let send (handle: int) (_signalNum: int) : Result<unit, int> =
                signalled.Add handle
                Ok()

            let closePin (_handle: int) = closes <- closes + 1

            match Native.Cgroup.killCgroupUsing openPin send closePin dir with
            | Ok() -> ()
            | Error detail -> Assert.Fail $"a skipped recycled pid is not a teardown failure: {detail}"

            Assert.That(
                signalled,
                Is.Empty,
                "a pid recycled by a process outside the cgroup received the sweep's SIGKILL — the "
                + "wrong-target kill window is open"
            )

            Assert.That(pins, Is.EqualTo 1, "the snapshotted member must be pinned before anything is decided")
            Assert.That(closes, Is.EqualTo pins, "every pin the sweep opens must be released"))

    [<Test>]
    member _.``the legacy cgroup sweep treats a member gone before its pin as a benign no-op (T-330)``() =
        let memberPid = 4_244
        let signalled = ResizeArray<int>()
        let mutable closes = 0

        withLegacyCgroup [ memberPid ] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")

            let openPin (_pid: int) : Result<int, int> =
                // The member exited before it could be pinned: the intended end state (gone) already holds,
                // and nothing has taken its number yet.
                File.WriteAllText(procs, "")
                Error ESRCH

            let send (handle: int) (_signalNum: int) : Result<unit, int> =
                signalled.Add handle
                Ok()

            let closePin (_handle: int) = closes <- closes + 1

            match Native.Cgroup.killCgroupUsing openPin send closePin dir with
            | Ok() -> ()
            | Error detail -> Assert.Fail $"a member that exited before its pin is a benign race: {detail}"

            Assert.That(signalled, Is.Empty, "nothing may be signalled once the pin reports the target gone")
            Assert.That(closes, Is.EqualTo 0, "a pin that never opened must not be released"))

    [<Test>]
    member _.``the legacy cgroup sweep fails honestly on a kernel without pidfd (T-330)``() =
        let memberPid = 4_245
        let signalled = ResizeArray<int>()
        let mutable pins = 0

        withLegacyCgroup [ memberPid ] (fun dir ->
            let openPin (_pid: int) : Result<int, int> =
                pins <- pins + 1
                Error ENOSYS

            let send (handle: int) (_signalNum: int) : Result<unit, int> =
                signalled.Add handle
                Ok()

            match Native.Cgroup.killCgroupUsing openPin send ignore dir with
            | Ok() -> Assert.Fail "a kernel that cannot pin a member must not report a successful teardown"
            | Error detail ->
                Assert.That(detail, Does.Contain "pidfd", "the error must name the missing kernel primitive")

            Assert.That(signalled, Is.Empty, "a kernel without pidfd must never be downgraded to a raw kill")

            Assert.That(
                pins,
                Is.EqualTo 1,
                "a missing syscall cannot be retried into existence, so the sweep must stop on the first one"
            )

            Assert.That(File.ReadAllText(Path.Combine(dir, "cgroup.procs")).Trim(), Is.EqualTo(string memberPid)))

    [<Test>]
    member _.``a refused pinned SIGKILL is reported when the legacy sweep leaves the cgroup populated (T-330)``() =
        let memberPid = 4_246
        let mutable attempts = 0

        withLegacyCgroup [ memberPid ] (fun dir ->
            let openPin (pid: int) : Result<int, int> = Ok pid

            let send (_handle: int) (_signalNum: int) : Result<unit, int> =
                attempts <- attempts + 1
                Error EPERM

            match Native.Cgroup.killCgroupUsing openPin send ignore dir with
            | Ok() -> Assert.Fail "a member that refused the SIGKILL and is still in the cgroup is not a success"
            | Error detail ->
                Assert.That(detail, Does.Contain "populated", "the error must say the cgroup was not cleared")
                Assert.That(detail, Does.Contain $"errno {EPERM}", "the error must carry the refusing errno")

            Assert.That(attempts, Is.GreaterThan 1, "one refusal must not cut the bounded sweep short"))

    [<Test>]
    member _.``an unreadable membership is not a legacy sweep delivery failure (T-330)``() =
        // `cgroup.procs` is a directory here, so reading the membership throws on any OS. The sweep then
        // has nothing safe to target and attempts no delivery at all — so there is no pin/send failure to
        // report, and the teardown stays the bounded best-effort it has always been rather than turning a
        // concurrently removed or briefly unreadable cgroup into a `KillTree` error. Only a delivery that
        // actually failed is escalated, and only while the group is still populated.
        let mutable pins = 0

        withLegacyCgroup [] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")
            File.Delete procs
            Directory.CreateDirectory procs |> ignore

            let openPin (_pid: int) : Result<int, int> =
                pins <- pins + 1
                Error ENOSYS

            let send (_handle: int) (_signalNum: int) : Result<unit, int> = Ok()

            match Native.Cgroup.killCgroupUsing openPin send ignore dir with
            | Ok() -> ()
            | Error detail -> Assert.Fail $"an unreadable membership is not a delivery failure: {detail}"

            Assert.That(pins, Is.EqualTo 0, "no pid can be pinned honestly while the membership is unknown"))

    [<Test>]
    member _.``a legacy sweep delivery failure whose cgroup drained anyway stays a success (T-330)``() =
        // The member refused its pinned SIGKILL (EPERM) but exited on its own immediately after, so the
        // drain check — the authority on "the tree is gone" — finds nothing left. A recorded failure must
        // not turn that into a teardown error.
        let memberPid = 4_247

        withLegacyCgroup [ memberPid ] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")
            let openPin (pid: int) : Result<int, int> = Ok pid

            let send (_handle: int) (_signalNum: int) : Result<unit, int> =
                File.WriteAllText(procs, "")
                Error EPERM

            match Native.Cgroup.killCgroupUsing openPin send ignore dir with
            | Ok() -> ()
            | Error detail -> Assert.Fail $"an empty cgroup is a completed teardown: {detail}")

    // --- T-363: `cgroup.kill` is ASYNCHRONOUS. The kernel SIGKILLs the subtree, but a member only leaves
    // `cgroup.procs` when it EXITS, which can land well after the kill write returns — so removing the
    // cgroup directory right then answers `EBUSY`, and that error used to be swallowed whole by a single
    // best-effort `rmdir`, leaving an empty-but-permanent cgroup behind on teardown after teardown.
    // Teardown now waits — BOUNDED — for the cgroup to actually empty and retries the removal inside that
    // same budget. The tests below drive the wait through scripted probes/removals on a fake clock (no
    // kernel, no real sleeping), and through the real `cgroup.events`/`cgroup.procs` probe over a
    // synthetic cgroup directory. ---

    [<Test>]
    member _.``the bounded drain wait removes the cgroup directory once its tree has left (T-363)``() =
        // The kill has landed but members are still on their way out; the third probe finds the cgroup
        // empty, and only then is the directory reclaimed.
        let result =
            scriptedRelease
                (TimeSpan.FromMilliseconds 100.0)
                [ Native.Cgroup.Drain.Populated
                  Native.Cgroup.Drain.Populated
                  Native.Cgroup.Drain.Empty ]
                [ Native.Cgroup.Removal.Removed ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed -> ()
        | Native.Cgroup.Release.Retained detail ->
            Assert.Fail $"a cgroup that drained inside the budget must be reclaimed: {detail}"

        Assert.That(result.Probes, Is.EqualTo 3, "the wait must keep probing until the cgroup actually empties")

        Assert.That(
            result.Removals,
            Is.EqualTo 1,
            "the directory must not be removed while the cgroup is still populated"
        )

        Assert.That(
            result.Waited,
            Is.LessThan(TimeSpan.FromMilliseconds 100.0),
            "a cgroup that drains early must not spend the whole budget"
        )

    [<Test>]
    member _.``a transient EBUSY on the cgroup rmdir is retried inside the same budget (T-363)``() =
        // The cgroup reads empty, but the kernel is not done letting go of it yet: the first two removals
        // are refused with EBUSY. That refusal is the kernel's own statement that the cgroup is not drained
        // after all, so it must re-enter the wait rather than be swallowed after one attempt.
        let result =
            scriptedRelease
                (TimeSpan.FromMilliseconds 100.0)
                [ Native.Cgroup.Drain.Empty ]
                [ Native.Cgroup.Removal.Busy "Device or resource busy"
                  Native.Cgroup.Removal.Busy "Device or resource busy"
                  Native.Cgroup.Removal.Removed ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed -> ()
        | Native.Cgroup.Release.Retained detail ->
            Assert.Fail $"a transient EBUSY must be retried, not treated as final: {detail}"

        Assert.That(result.Removals, Is.EqualTo 3, "a refused removal must be retried inside the budget")

        Assert.That(
            result.Waited,
            Is.LessThan(TimeSpan.FromMilliseconds 100.0),
            "the retries must fit inside the same bounded budget"
        )

    [<Test>]
    member _.``a cgroup that never drains is reported, not silently left behind (T-363)``() =
        // A fork bomb still out-spawning, or a task wedged in uninterruptible sleep: the cgroup stays
        // populated for the whole budget. The wait must END (bounded), leave the directory in place, and
        // say so — the swallowed `EBUSY` this fix removes is exactly what made such a leak invisible.
        let budget = TimeSpan.FromMilliseconds 100.0

        let result =
            scriptedRelease
                budget
                [ Native.Cgroup.Drain.Populated ]
                [ Native.Cgroup.Removal.Busy "Device or resource busy" ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed -> Assert.Fail "a cgroup that never drained must not be reported as reclaimed"
        | Native.Cgroup.Release.Retained detail ->
            Assert.That(detail, Does.Contain "populated", "the verdict must say why the directory was kept")

            Assert.That(
                detail,
                Does.Contain "refused to remove",
                "the kernel's own refusal must be reported alongside the state that caused it"
            )

        Assert.That(result.Waited, Is.EqualTo budget, "the wait must end exactly at its bounded budget")

        Assert.That(
            result.Removals,
            Is.EqualTo 1,
            "a populated cgroup is only worth one final removal attempt, once the budget is spent"
        )

    [<Test>]
    member _.``a persistent membership read failure is never reported as a drained cgroup (T-363)``() =
        // An unreadable `cgroup.procs` (EACCES/EIO) is UNKNOWN membership, not an empty group. It must not
        // cut the wait short, and it must not be dressed up as a completed teardown when the kernel refuses
        // the removal too.
        let budget = TimeSpan.FromMilliseconds 100.0

        let result =
            scriptedRelease
                budget
                [ Native.Cgroup.Drain.Unknown "Permission denied" ]
                [ Native.Cgroup.Removal.Busy "Device or resource busy" ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed ->
            Assert.Fail "a cgroup whose emptiness was never confirmed must not be reported as reclaimed"
        | Native.Cgroup.Release.Retained detail ->
            Assert.That(
                detail,
                Does.Contain "never confirmed drained",
                "an unreadable membership must be told apart from a confirmed empty cgroup"
            )

            Assert.That(detail, Does.Contain "Permission denied", "the verdict must carry the read failure itself")

        Assert.That(result.Probes, Is.GreaterThan 1, "a failed read must not end the bounded wait on the spot")
        Assert.That(result.Waited, Is.EqualTo budget, "the wait must still end at its bounded budget")

    [<Test>]
    member _.``an unreadable cgroup is reclaimed only on the kernel's own confirmation (T-363)``() =
        // The membership never reads, so this loop never claims the cgroup drained. It still attempts the
        // removal once the budget is spent — cgroupfs refuses to remove a cgroup that holds members, so
        // that attempt can reclaim a directory whose emptiness could not be READ without ever taking away
        // one still in use. A `Removed` here is the kernel's answer, never this loop's guess.
        let budget = TimeSpan.FromMilliseconds 100.0

        let result =
            scriptedRelease
                budget
                [ Native.Cgroup.Drain.Unknown "Input/output error" ]
                [ Native.Cgroup.Removal.Removed ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed -> ()
        | Native.Cgroup.Release.Retained detail ->
            Assert.Fail $"a directory the kernel itself removed must be reported reclaimed: {detail}"

        Assert.That(
            result.Removals,
            Is.EqualTo 1,
            "an unconfirmed cgroup earns exactly one removal attempt, after the wait"
        )

        Assert.That(result.Waited, Is.EqualTo budget, "the removal must not be attempted before the wait is over")

    [<Test>]
    member _.``an already drained cgroup is removed without paying any of the drain budget (T-363)``() =
        // The ordinary teardown: by the time the directory is reclaimed the tree is long gone. The bounded
        // wait must not become a fixed latency on that path.
        let result =
            scriptedRelease
                (TimeSpan.FromMilliseconds 100.0)
                [ Native.Cgroup.Drain.Empty ]
                [ Native.Cgroup.Removal.Removed ]

        match result.Verdict with
        | Native.Cgroup.Release.Removed -> ()
        | Native.Cgroup.Release.Retained detail -> Assert.Fail $"an empty cgroup must be reclaimed at once: {detail}"

        Assert.That(result.Probes, Is.EqualTo 1, "an empty cgroup needs exactly one probe")
        Assert.That(result.Removals, Is.EqualTo 1, "an empty cgroup needs exactly one removal")
        Assert.That(result.Waited, Is.EqualTo TimeSpan.Zero, "an already drained cgroup must not wait at all")

    [<Test>]
    member _.``an adopted member is waited out through cgroup membership, with no reap (T-363)``() =
        // An ADOPTED process is not our child: nothing may `waitpid` it, so the wait cannot be built on a
        // reap — and it does not have to be. A process leaves `cgroup.procs` when it EXITS, before anyone
        // reaps it, which is exactly what this drives: the REAL membership probe over a real cgroup.procs,
        // with the adopted pid disappearing from the file two polls in, and a removal that models what
        // cgroupfs does about it (refused while the cgroup holds anyone, granted once it does not).
        let adoptedPid = 4_248
        let mutable polls = 0
        let mutable removals = 0
        let mutable now = TimeSpan.Zero

        withLegacyCgroup [ adoptedPid ] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")

            let sleep (duration: TimeSpan) =
                now <- now + duration
                polls <- polls + 1

                if polls = 2 then
                    // The adopted process exits. Nobody reaps it — it is not our child — but the kernel
                    // takes it out of the cgroup all the same, which is what the wait is watching for.
                    File.WriteAllText(procs, "")

            let remove () =
                removals <- removals + 1

                match Native.Cgroup.cgroupMembers dir with
                | Ok [] -> Native.Cgroup.Removal.Removed
                | _ -> Native.Cgroup.Removal.Busy "Device or resource busy"

            let verdict =
                Native.Cgroup.releaseCgroupUsing
                    (fun () -> Native.Cgroup.cgroupDrainState dir)
                    remove
                    (fun () -> now)
                    sleep
                    (TimeSpan.FromMilliseconds 100.0)

            match verdict with
            | Native.Cgroup.Release.Removed -> ()
            | Native.Cgroup.Release.Retained detail ->
                Assert.Fail $"the adopted member left, so the cgroup must be reclaimed: {detail}"

            Assert.That(polls, Is.EqualTo 2, "the wait must poll the membership until the adopted member leaves")

            Assert.That(
                removals,
                Is.EqualTo 1,
                "the directory must not be removed while the adopted member is still in the cgroup"
            ))

    [<Test>]
    member _.``the drain probe trusts the kernel's populated flag over the membership file (T-363)``() =
        // `cgroup.events`' `populated` is the kernel's own aggregate — it also counts a DESCENDANT cgroup's
        // members, which `cgroup.procs` does not, and which `rmdir` refuses on just the same.
        withLegacyCgroup [] (fun dir ->
            let events = Path.Combine(dir, "cgroup.events")
            File.WriteAllText(events, "populated 1\nfrozen 0\n")

            match Native.Cgroup.cgroupDrainState dir with
            | Native.Cgroup.Drain.Populated -> ()
            | other -> Assert.Fail $"the kernel's populated flag must decide while cgroup.procs reads empty: {other}"

            File.WriteAllText(events, "populated 0\nfrozen 0\n")

            match Native.Cgroup.cgroupDrainState dir with
            | Native.Cgroup.Drain.Empty -> ()
            | other -> Assert.Fail $"populated 0 is the kernel confirming the cgroup drained: {other}")

    [<Test>]
    member _.``an unreadable membership is unknown to the drain probe, never empty (T-363)``() =
        // `cgroup.procs` is a directory here, so the membership read throws on any OS. The probe must
        // report that as UNKNOWN — the distinction the whole bounded wait rests on.
        withLegacyCgroup [] (fun dir ->
            let procs = Path.Combine(dir, "cgroup.procs")
            File.Delete procs
            Directory.CreateDirectory procs |> ignore

            match Native.Cgroup.cgroupDrainState dir with
            | Native.Cgroup.Drain.Unknown _ -> ()
            | other -> Assert.Fail $"a failed membership read must never be reported as a drained cgroup: {other}")

    [<Test>]
    member _.``releaseCgroup does not wait for a cgroup directory that is already gone (T-363)``() =
        // Nothing to drain and nothing to remove: teardown of a group whose cgroup never existed (or was
        // already reclaimed) must not spend a millisecond of the budget on it.
        let missing = Path.Combine(Path.GetTempPath(), $"pk-gone-cgroup-{Guid.NewGuid():N}")

        let originalBudget = Native.Cgroup.drainBudgetOverrideForTests
        Native.Cgroup.drainBudgetOverrideForTests <- Some(TimeSpan.FromSeconds 5.0)

        try
            let stopwatch = Stopwatch.StartNew()
            let verdict = Native.Cgroup.releaseCgroup missing
            stopwatch.Stop()

            match verdict with
            | Native.Cgroup.Release.Removed -> ()
            | Native.Cgroup.Release.Retained detail ->
                Assert.Fail $"a cgroup directory that is not there needs no reclaiming: {detail}"

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 1.0),
                "an absent cgroup directory must not be waited on at all"
            )
        finally
            Native.Cgroup.drainBudgetOverrideForTests <- originalBudget

    [<Test>]
    member _.``releaseCgroup reports a cgroup directory it could not reclaim (T-363)``() =
        // End to end on a real filesystem: a still-populated stand-in cgroup. The directory stays (it is
        // still containing something), and the verdict says so instead of hiding the failure.
        let originalBudget = Native.Cgroup.drainBudgetOverrideForTests
        Native.Cgroup.drainBudgetOverrideForTests <- Some(TimeSpan.FromMilliseconds 10.0)

        try
            withLegacyCgroup [ 4_249 ] (fun dir ->
                match Native.Cgroup.releaseCgroup dir with
                | Native.Cgroup.Release.Removed -> Assert.Fail "a populated cgroup must not be reported as reclaimed"
                | Native.Cgroup.Release.Retained detail ->
                    Assert.That(
                        detail,
                        Does.Contain "populated",
                        "the verdict must name the state that kept the directory"
                    )

                Assert.That(Directory.Exists dir, Is.True, "a cgroup that still holds members keeps its directory"))
        finally
            Native.Cgroup.drainBudgetOverrideForTests <- originalBudget

    // ---- T-360: who owns the remainder once the post-exit drain bound fires ----------------------

    [<Test>]
    member _.``a private group reaps the remainder once the post-exit drain bound releases the verb``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The bug this closes was not only the hang: because the verb never returned, it never
                // reached its `reapGuard`, so the PRIVATE per-run group that owns the leftover tree was
                // never released and the descendant that inherited the pipe outlived the run for good.
                // Bounding the drain is what puts the teardown back on the path.
                let backend = InheritedPipeBackend()
                let group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                match group.StartInternal(shell "irrelevant-the-backend-is-synthetic") with
                | Error error -> Assert.Fail $"the synthetic spawn should succeed: {error}"
                | Ok(host, extraFds) ->
                    use running = new RunningProcess(host, extraFds)

                    match! running.OutputStringAsync() with
                    | Error error -> Assert.Fail $"the bounded drain must still produce a capture: {error}"
                    | Ok captured ->
                        Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                        Assert.That(captured.Stdout, Does.Contain "from-the-leader")
                        Assert.That(captured.Truncated, Is.True)

                    Assert.That(running.OutputDrainWasBounded, Is.True, "the held-open pipe should hit the bound")

                    Assert.That(
                        backend.HardReleaseCount,
                        Is.EqualTo 1,
                        "a private group must be torn down once the bound lets the verb reach its reapGuard"
                    )

                    Assert.That(backend.TrackedCount, Is.EqualTo 0, "the private group's tree must not survive it")
            })
        :> Task

    [<Test>]
    member _.``a shared group keeps its other runs when one run's drain bound fires``() : Task =
        PostExitDrainBudget.withBudget PostExitDrainBudget.Short (fun () ->
            task {
                // The other side of the ownership rule: severing is a per-HANDLE act, so a run whose
                // inherited pipe is held open detaches only its own I/O. Nothing may kill the remainder
                // here — the descendants belong to the shared container, alongside every other run in
                // it, and this library does not reach into a group one handle does not own.
                let backend = InheritedPipeBackend()
                use group = ProcessGroup.FromBackend(backend, ProcessGroupOptions())

                match! group.StartAsync(shell "the-neighbour") with
                | Error error -> Assert.Fail $"the synthetic spawn should succeed: {error}"
                | Ok neighbour ->
                    use neighbour = neighbour

                    match! group.StartAsync(shell "the-leader") with
                    | Error error -> Assert.Fail $"the synthetic spawn should succeed: {error}"
                    | Ok running ->
                        use running = running

                        match! running.OutputStringAsync() with
                        | Error error -> Assert.Fail $"the bounded drain must still produce a capture: {error}"
                        | Ok captured ->
                            Assert.That(captured.Stdout, Does.Contain "from-the-leader")
                            Assert.That(captured.Truncated, Is.True)

                        Assert.That(running.OutputDrainWasBounded, Is.True)

                        Assert.That(
                            backend.HardReleaseCount,
                            Is.EqualTo 0,
                            "one run's teardown must not release a group it shares"
                        )

                        Assert.That(
                            backend.KillTreeCount + backend.KillChildCount + backend.GracefulKillTreeCount,
                            Is.EqualTo 0,
                            "a shared group's survivors are not this run's to kill"
                        )

                        Assert.That(
                            backend.TrackedCount,
                            Is.EqualTo 1,
                            "the neighbour must still be owned by the group after the bounded run detached"
                        )

                        // And the neighbour is untouched: its own streams were never severed, so its own
                        // capture still runs — and hits the same bound on its own terms.
                        match! neighbour.OutputStringAsync() with
                        | Error error -> Assert.Fail $"the neighbouring run must still be usable: {error}"
                        | Ok captured ->
                            Assert.That(captured.Outcome, Is.EqualTo(Outcome.Exited 0))
                            Assert.That(captured.Stdout, Does.Contain "from-the-leader")
            })
        :> Task
