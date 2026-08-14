namespace ProcessKit

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// The one **bounded post-kill reap** contract, shared by every completion path that has ALREADY
/// delivered a hard kill and is only waiting for the physical reap: the run's timeout race
/// (`Timeouts.raceTimeoutWithCts`), the handle's own `Kill()` (which is what the cancellation path
/// fires), `RunningProcess.StopAsync`'s soft->hard escalation, and the POSIX teardown drain
/// (`PosixReap`, behind `ProcessGroup.ShutdownAsync`/`Dispose`).
///
/// Why it exists: a *delivered* SIGKILL / `TerminateProcess` is not a promise that the child is
/// reapable now. A child wedged in uninterruptible (`D`-state) sleep defers even SIGKILL until its
/// I/O unblocks, and the native wait can stall on the same edge. The caller's disposition is already
/// decided at that point (timed out / cancelled / stopped), so blocking on the reap cannot change the
/// answer — it only breaks the bounded-teardown promise the same caller just made.
///
/// The contract has three parts, and every path here applies all three:
///
/// 1. **Bounded.** The wait *after* the kill lasts at most the budget (`budget ()`), never forever.
///
/// 2. **Ownership transfer, never abandonment.** When the budget elapses, the SINGLE remaining right
///    to await/reap that target passes to this ledger, which observes the adopted task's fault so a
///    late failure can never surface as an unobserved task exception (the `observeFault` rule K-084
///    applies to an abandoned wait too). No path ever starts a SECOND waiter for the same
///    pid/pgid/handle: on POSIX the adopted wait is `Native.Posix.waitPosix`, which joins the one
///    shared reap group instead of opening a second pidfd or triggering a second `waitpid` (K-016);
///    on Windows the adopted wait already holds its own duplicated handle (K-025). Teardown asks
///    `ownsLeader` first and stands down for a leader this ledger owns, so a group
///    `Shutdown`/`Dispose` never re-kills or re-waits it.
///
/// 3. **Identity safety.** An adopted leader is recorded together with the start-time token captured
///    while it was still definitively ours — the snapshot-before-off-lock-work rule (K-086/K-025) —
///    so a later probe can tell the process we adopted apart from a stranger that recycled its
///    number. The entry is dropped the moment the adopted wait concludes, so a recycled pid can never
///    inherit the previous owner's "already adopted" verdict and be skipped by a teardown that really
///    does own it.
///
/// Deliberately NOT a general "kill things later" service: it starts no thread, owns no queue, and
/// polls nothing. It is bookkeeping plus fault observation over waits the OS is already driving.
module internal PostKillReap =

    /// How long a completion path waits for the physical reap after its hard kill was delivered. The
    /// ordinary just-killed child is reaped in a millisecond or two, so this is a ceiling for the
    /// pathological case, not a latency anyone pays. Matches the ProcessKit-rs prototype's
    /// `PUMP_TEARDOWN` (5s), which bounds the same post-kill `child.wait()` there.
    let DefaultBudget = TimeSpan.FromSeconds 5.0

    /// The largest delay a BCL timer accepts, mirroring `Timeouts.maxArmable`. Duplicated rather than
    /// reused because this module is a *dependency* of `Timeouts` (it is compiled before it): the
    /// timeout race itself bounds its post-kill wait through here.
    let private maxArmable = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// Clamp `duration` into the range a BCL timer can be armed with, so a budget can never throw
    /// synchronously on a teardown path (the same reason `Timeouts.clampArmable` exists).
    let armable (duration: TimeSpan) =
        if duration < TimeSpan.Zero then TimeSpan.Zero
        elif duration > maxArmable then maxArmable
        else duration

    /// `a + b`, saturating at the largest armable delay instead of overflowing — `TimeSpan.MaxValue`
    /// plus anything throws, and a caller-supplied grace period may legitimately be enormous. Used to
    /// build the "grace, then the post-kill budget" window a graceful stop is allowed to take in
    /// total, mirroring the prototype's `grace.saturating_add(PUMP_TEARDOWN)`.
    let plus (a: TimeSpan) (b: TimeSpan) = armable (armable a + armable b)

    /// Test seam: production NEVER assigns this, so `budget ()` is `DefaultBudget` everywhere. A
    /// regression that must not pay the real five seconds sets it (and restores it) around the call,
    /// exactly like `Native.Cgroup.killCgroupWriteTestHook`. Tests in this repository run
    /// sequentially, so a single process-wide seam is safe here.
    let mutable budgetOverrideForTests: TimeSpan option = None

    /// The budget in force for this call — the default, or a test seam's override.
    let budget () =
        match budgetOverrideForTests with
        | Some value -> armable value
        | None -> DefaultBudget

    // Monotonic counters, for tests that assert the ledger adopted a wait EXACTLY once (the
    // "no second wait owner" invariant) rather than inferring it from timing.
    let private adoptedWaits = ref 0
    let private adoptedLeaders = ref 0

    /// How many waits have been handed to the ledger since the process started (test diagnostic).
    let adoptedWaitCount () = Volatile.Read(&adoptedWaits.contents)

    /// How many native leaders have been adopted since the process started (test diagnostic).
    let adoptedLeaderCount () = Volatile.Read(&adoptedLeaders.contents)

    // The leaders whose eventual wait/reap this ledger owns, each with the start-time identity token
    // captured while it was still ours. `ConcurrentDictionary` because teardown, a run's own reap and
    // the adopted wait's completion all touch it from different threads.
    let private leaders = ConcurrentDictionary<int, uint64 option>()

    /// Hand the single remaining right to await `wait` to the ledger: nothing will await it again, so
    /// observe its fault here. Purely observational — it never reads a result or replaces an
    /// exception, so a genuine awaiter that still exists (a shared exit wait fanned out to several
    /// verbs) keeps seeing the original outcome or fault unchanged.
    let adoptWait (wait: Task) : unit =
        Interlocked.Increment(&adoptedWaits.contents) |> ignore

        wait.ContinueWith(
            Action<Task>(fun completed -> completed.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    /// Await `wait` for at most `budget`: `ValueSome outcome` when the reap landed inside the window
    /// (the ordinary case — the caller then reports the REAL outcome), `ValueNone` when it did not, in
    /// which case `wait` has been adopted by the ledger and the caller must report its own already-
    /// decided disposition instead of blocking any longer.
    ///
    /// An already-completed wait answers without arming anything, so a fast child pays no budget at
    /// all; a wait that faults inside the window still throws from here, exactly as the unbounded
    /// `let! _ = wait` this replaces did.
    let awaitWithin (budget: TimeSpan) (wait: Task<'T>) : Task<'T voption> =
        if wait.IsCompleted then
            task {
                let! value = wait
                return ValueSome value
            }
        else
            task {
                use budgetCts = new CancellationTokenSource()
                let waitBase = wait :> Task
                let expiry = Task.Delay(armable budget, budgetCts.Token)
                let! winner = Task.WhenAny(waitBase, expiry)

                if obj.ReferenceEquals(winner, waitBase) then
                    // The reap landed in time. Cancel the losing timer so it cannot outlive the
                    // decided race (the same discipline `Timeouts.raceTimeoutWithCts` applies to its
                    // own deadline timer).
                    budgetCts.Cancel()
                    let! value = wait
                    return ValueSome value
                else
                    adoptWait waitBase
                    return ValueNone
            }

    /// Does the ledger already own the eventual wait/reap for `pid`? `identity` is the caller's
    /// captured start-time token for the pid it is about to act on: ownership only carries over when
    /// the two tokens cannot be shown to differ — a *different* known token is positive proof the
    /// number was recycled (mirroring `Native.Posix.processGroupStillTracked`'s rule), so the caller
    /// is looking at a stranger and this ledger's entry says nothing about it.
    let ownsLeader (pid: int) (identity: uint64 option) : bool =
        match leaders.TryGetValue pid with
        | true, adopted ->
            match adopted, identity with
            | Some a, Some b -> a = b
            | _ -> true
        | false, _ -> false

    /// Transfer ownership of the eventual wait/reap of leader `pid` to the ledger, starting that wait
    /// through `beginWait` (production passes `Native.Posix.waitPosix`, so the adopted wait JOINS the
    /// one shared reap group instead of becoming a second, racing waiter — K-016). Returns whether
    /// this call is the one that adopted it: a second adopt for the same live pid is refused, so the
    /// tree can never acquire two owners.
    let adoptLeader (pid: int) (identity: uint64 option) (beginWait: int -> Task<Outcome>) : bool =
        if not (leaders.TryAdd(pid, identity)) then
            false
        else
            let started =
                try
                    Ok(beginWait pid)
                with ex ->
                    Error ex

            match started with
            | Error _ ->
                // The eventual wait could not even be started (a native registration failure), so
                // this entry owns nothing. Drop it rather than leave the pid marked as adopted, which
                // would make every later teardown stand down for a leader nobody is waiting on. The
                // exception itself is not propagated: this IS the best-effort last step of a teardown
                // that has already delivered its kill, and raising here would break the caller's own
                // bounded teardown for a reap it cannot retry anyway.
                leaders.TryRemove pid |> ignore
                false
            | Ok wait ->
                Interlocked.Increment(&adoptedLeaders.contents) |> ignore
                adoptWait (wait :> Task)

                // Release the entry as soon as the adopted wait concludes: the pid becomes reusable
                // right after the reap, and a stale "already adopted" verdict on a recycled number
                // would make a later teardown skip a leader it genuinely owns.
                wait.ContinueWith(
                    Action<Task<Outcome>>(fun _ -> leaders.TryRemove pid |> ignore),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                |> ignore

                true

    /// Test seam: forget every adopted leader. Only for tests that adopt synthetic pids and must not
    /// leave the ledger holding entries that a later test's real pid could collide with.
    let clearLeadersForTests () = leaders.Clear()
