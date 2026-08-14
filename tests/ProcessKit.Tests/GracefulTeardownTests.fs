namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// The injected native gates behind one POSIX leader's teardown (`PosixReap.leaderUsing`), so the
/// bounded-reap and handoff decisions can be exercised without a real child wedged in `D`-state.
/// `aliveBefore`/`aliveAfterReap` model the liveness+identity choke's verdict on either side of the
/// bounded reap: a leader that becomes reapable answers `false` afterwards, one that stays wedged
/// answers `true`.
type private LeaderProbe(aliveBefore: bool, aliveAfterReap: bool, owned: bool) =
    let kills = List<int>()
    let reaps = List<int>()
    let adopts = List<int * uint64 option>()
    let mutable reaped = false

    member _.Kills = List.ofSeq kills
    member _.Reaps = List.ofSeq reaps
    member _.Adopts = List.ofSeq adopts

    member _.Run(reapNow: bool, pid: int, identity: uint64 option) =
        PosixReap.leaderUsing
            (fun _ _ -> if reaped then aliveAfterReap else aliveBefore)
            (fun _ _ -> owned)
            (fun id -> kills.Add id)
            (fun id ->
                reaps.Add id
                reaped <- true)
            (fun id token -> adopts.Add(id, token))
            reapNow
            pid
            identity

type private FakeTeardownClock() =
    let delays = List<TimeSpan>()
    let mutable elapsed = TimeSpan.Zero

    member _.Elapsed = elapsed
    member _.Delays = List.ofSeq delays
    member _.Start() = fun () -> elapsed

    member _.Delay(duration: TimeSpan) =
        delays.Add duration
        elapsed <- elapsed + duration
        Task.CompletedTask

[<TestFixture>]
type GracefulTeardownTests() =
    let run (clock: FakeTeardownClock) grace alive forceKill =
        GracefulTeardown.pollUsing clock.Start clock.Delay ignore alive forceKill grace

    [<Test>]
    member _.``zero grace escalates immediately without delaying``() : Task =
        task {
            let clock = FakeTeardownClock()
            let mutable forceKillCount = 0

            do! run clock TimeSpan.Zero (fun () -> true) (fun () -> forceKillCount <- forceKillCount + 1)

            Assert.That(clock.Delays, Is.Empty)
            Assert.That(clock.Elapsed, Is.EqualTo TimeSpan.Zero)
            Assert.That(forceKillCount, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``grace shorter than one poll interval delays only to its deadline``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 1.0
            let mutable forceKillAt = None

            do! run clock grace (fun () -> true) (fun () -> forceKillAt <- Some clock.Elapsed)

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ grace ]))
            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillAt, Is.EqualTo(Some grace))
        }
        :> Task

    [<Test>]
    member _.``intermediate poll delay is bounded by the remaining grace budget``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 120.0
            let mutable forceKillAt = None

            do! run clock grace (fun () -> true) (fun () -> forceKillAt <- Some clock.Elapsed)

            Assert.That(
                clock.Delays,
                Is.EqualTo<TimeSpan list>
                    [ TimeSpan.FromMilliseconds 50.0
                      TimeSpan.FromMilliseconds 50.0
                      TimeSpan.FromMilliseconds 20.0 ]
            )

            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillAt, Is.EqualTo(Some grace))
        }
        :> Task

    [<Test>]
    member _.``process exiting at the grace boundary is not force killed``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 50.0
            let mutable forceKillCount = 0

            do! run clock grace (fun () -> clock.Elapsed < grace) (fun () -> forceKillCount <- forceKillCount + 1)

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ grace ]))
            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``very large grace keeps the armed delay in range``() : Task =
        task {
            let clock = FakeTeardownClock()
            let mutable aliveChecks = 0

            let alive () =
                aliveChecks <- aliveChecks + 1
                aliveChecks = 1

            do! run clock TimeSpan.MaxValue alive (fun () -> Assert.Fail "process was force killed")

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ TimeSpan.FromMilliseconds 50.0 ]))
            Assert.That(clock.Delays |> List.forall Timeouts.isArmable, Is.True)
        }
        :> Task

    // ---- T-351: bounded post-kill reap on the POSIX teardown path --------------------------------
    //
    // `killpg` SIGKILLs the group but does not reap our own child, and a child wedged in
    // uninterruptible (`D`-state) sleep defers even SIGKILL until its I/O unblocks. Teardown's
    // synchronous reap is therefore bounded — per leader AND for the drain as a whole — and a leader
    // still alive when its window ends hands the single remaining right to wait/reap it to the
    // `PostKillReap` ledger, instead of being abandoned to the OS or re-killed by the next teardown.

    [<Test>]
    member _.``a leader still wedged after its bounded reap is handed to the reaper (T-351)``() =
        let probe = LeaderProbe(aliveBefore = true, aliveAfterReap = true, owned = false)
        probe.Run(reapNow = true, pid = 4242, identity = Some 77UL)

        Assert.That(probe.Kills, Is.EqualTo<int list> [ 4242 ], "the kill must still be delivered")
        Assert.That(probe.Reaps, Is.EqualTo<int list> [ 4242 ], "the bounded synchronous reap must still be attempted")

        Assert.That(
            probe.Adopts,
            Is.EqualTo<(int * uint64 option) list> [ 4242, Some 77UL ],
            "a leader that outlived the bounded reap must be adopted WITH its captured identity token"
        )

    [<Test>]
    member _.``a leader reaped inside its bounded window is not handed off (T-351)``() =
        let probe = LeaderProbe(aliveBefore = true, aliveAfterReap = false, owned = false)
        probe.Run(reapNow = true, pid = 4243, identity = Some 78UL)

        Assert.That(probe.Kills, Is.EqualTo<int list> [ 4243 ])
        Assert.That(probe.Reaps, Is.EqualTo<int list> [ 4243 ])

        Assert.That(
            probe.Adopts,
            Is.Empty,
            "the ordinary just-killed child is reaped synchronously; nothing is handed to the reaper"
        )

    [<Test>]
    member _.``a leader the reaper already owns is neither re-killed nor re-waited (T-351)``() =
        let probe = LeaderProbe(aliveBefore = true, aliveAfterReap = true, owned = true)
        probe.Run(reapNow = true, pid = 4244, identity = Some 79UL)

        // This is the group Shutdown/Dispose case: ownership of this leader was already transferred, so
        // a second killpg (on a number the OS may have recycled) and a second waiter are both refused.
        Assert.That(probe.Kills, Is.Empty, "a leader owned by the reaper must not be killed again")
        Assert.That(probe.Reaps, Is.Empty, "a leader owned by the reaper must not be waited on again")
        Assert.That(probe.Adopts, Is.Empty, "a leader owned by the reaper must not be adopted twice")

    [<Test>]
    member _.``a recycled pgid is neither killed nor adopted (T-351)``() =
        let probe = LeaderProbe(aliveBefore = false, aliveAfterReap = false, owned = false)
        probe.Run(reapNow = true, pid = 4245, identity = Some 80UL)

        Assert.That(probe.Kills, Is.Empty, "the identity choke must keep a recycled pgid from being SIGKILLed")

        Assert.That(
            probe.Reaps,
            Is.EqualTo<int list> [ 4245 ],
            "the waitpid gate is unchanged: it only ever reaps our own child (a stranger is a harmless ECHILD)"
        )

        Assert.That(probe.Adopts, Is.Empty, "a stranger's pgid must never become this ledger's responsibility")

    [<Test>]
    member _.``a leader past the drain budget is killed and handed off without a synchronous reap (T-351)``() =
        let probe = LeaderProbe(aliveBefore = true, aliveAfterReap = true, owned = false)
        probe.Run(reapNow = false, pid = 4246, identity = None)

        Assert.That(probe.Kills, Is.EqualTo<int list> [ 4246 ], "the kill is never skipped, only the wait for it")
        Assert.That(probe.Reaps, Is.Empty, "the synchronous reap is what the spent budget skips")
        Assert.That(probe.Adopts, Is.EqualTo<(int * uint64 option) list> [ 4246, None ])

    [<Test>]
    member _.``the teardown drain stops paying per-leader reaps once its budget is spent (T-351)``() =
        // A group holding many leaders would otherwise add every per-leader window up on the disposing
        // thread. The drain shares ONE budget: each synchronous reap here costs its bounded window, and
        // once the budget is spent the remaining leaders are still torn down — killed and handed off —
        // without waiting on any of them.
        let elapsed = ref TimeSpan.Zero
        let observed = List<bool * int>()

        let reapOne (reapNow: bool) (pid: int) (_identity: uint64 option) =
            observed.Add(reapNow, pid)

            if reapNow then
                elapsed.Value <- elapsed.Value + TimeSpan.FromMilliseconds 200.0

        let leaders = [ for pid in 1..10 -> pid, Some(uint64 pid) ]

        PosixReap.drainUsing (fun () -> fun () -> elapsed.Value) reapOne (TimeSpan.FromMilliseconds 500.0) leaders

        Assert.That(
            observed |> Seq.map snd |> List.ofSeq,
            Is.EqualTo<int list> [ 1..10 ],
            "every tracked leader must still be torn down, budget or not"
        )

        Assert.That(
            observed |> Seq.filter fst |> Seq.length,
            Is.EqualTo 3,
            "only the leaders that fit inside the 500ms budget may be waited on synchronously"
        )

    [<Test>]
    member _.``the reaper adopts a leader exactly once and releases it when the wait concludes (T-351)``() : Task =
        task {
            let pid = 999_001
            let identity = Some 4242UL

            let wait =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let starts = ref 0

            let beginWait (_: int) =
                Interlocked.Increment(&starts.contents) |> ignore
                wait.Task

            let adoptedBefore = PostKillReap.adoptedLeaderCount ()

            try
                Assert.That(PostKillReap.ownsLeader pid identity, Is.False)
                Assert.That(PostKillReap.adoptLeader pid identity beginWait, Is.True)
                Assert.That(PostKillReap.ownsLeader pid identity, Is.True)

                // A racing teardown adopting the same leader must not create a SECOND waiter on it.
                Assert.That(PostKillReap.adoptLeader pid identity beginWait, Is.False)
                Assert.That(Volatile.Read(&starts.contents), Is.EqualTo 1, "a second wait was started for one leader")

                Assert.That(
                    PostKillReap.adoptedLeaderCount () - adoptedBefore,
                    Is.EqualTo 1,
                    "the ledger must record exactly one adoption for one leader"
                )

                // A different known identity is positive proof of a recycled number: the ledger's entry
                // says nothing about that stranger, so a teardown that owns it is not waved off.
                Assert.That(PostKillReap.ownsLeader pid (Some 777UL), Is.False)

                wait.TrySetResult(Outcome.Signalled(Some 9)) |> ignore
                let! _ = wait.Task

                let deadline = Stopwatch.StartNew()

                while PostKillReap.ownsLeader pid identity
                      && deadline.Elapsed < TimeSpan.FromSeconds 5.0 do
                    do! Task.Delay 10

                Assert.That(
                    PostKillReap.ownsLeader pid identity,
                    Is.False,
                    "a concluded leader's entry must be released, or a recycled pid inherits its verdict"
                )
            finally
                PostKillReap.clearLeadersForTests ()
        }
        :> Task

    [<Test>]
    member _.``an already-completed wait is answered without arming a budget (T-351)``() : Task =
        task {
            let adoptedBefore = PostKillReap.adoptedWaitCount ()
            let stopwatch = Stopwatch.StartNew()

            let! settled = PostKillReap.awaitWithin (TimeSpan.FromSeconds 30.0) (Task.FromResult(Outcome.Exited 3))

            stopwatch.Stop()

            Assert.That(settled, Is.EqualTo(ValueSome(Outcome.Exited 3)))
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.0))
            Assert.That(PostKillReap.adoptedWaitCount () - adoptedBefore, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``a wait that outlives the budget is adopted, not dropped (T-351)``() : Task =
        task {
            let stalled =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let adoptedBefore = PostKillReap.adoptedWaitCount ()
            let! settled = PostKillReap.awaitWithin (TimeSpan.FromMilliseconds 100.0) stalled.Task

            Assert.That(settled.IsNone, Is.True, "a wait that outlived its budget must not report an outcome")
            Assert.That(PostKillReap.adoptedWaitCount () - adoptedBefore, Is.EqualTo 1)

            // The ledger observes the adopted wait's eventual fault, so a late failure can never surface
            // as an unobserved task exception at finalization.
            stalled.TrySetException(InvalidOperationException "late reap failure") |> ignore
            do! Task.Delay 50
            Assert.That(stalled.Task.IsFaulted, Is.True)
        }
        :> Task

    [<Test>]
    member _.``a wait that faults inside the budget still surfaces its fault (T-351)``() : Task =
        task {
            let faulted =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let pending = PostKillReap.awaitWithin (TimeSpan.FromSeconds 30.0) faulted.Task
            faulted.SetException(InvalidOperationException "reap failed")

            try
                let! _ = pending
                Assert.Fail "the fault must propagate exactly as the unbounded await did"
            with :? InvalidOperationException ->
                // The expected fault: a bounded wait must not swallow a genuine reap failure.
                ()
        }
        :> Task
