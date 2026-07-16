namespace ProcessKit

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// Shared graceful-teardown shape for the backends that support one (cgroup, POSIX process group):
/// request termination (SIGTERM), poll until the tree is dead or `grace` elapses, then force-kill
/// whatever remains.
module private GracefulTeardown =

    let poll (terminate: unit -> unit) (alive: unit -> bool) (forceKill: unit -> unit) (grace: TimeSpan) : Task =
        task {
            terminate ()
            let stopwatch = Stopwatch.StartNew()

            while alive () && stopwatch.Elapsed < grace do
                do! Task.Delay 50

            if alive () then
                forceKill ()
        }
        :> Task

/// Kill and reap a single POSIX leader we `posix_spawn`ed, in this exact order: `killpg` SIGKILLs its
/// whole process group (any subtree it backgrounded), then `waitpid` reaps the leader itself (`killpg`
/// does not reap our own child). Shared by the cgroup and process-group backends' escapee/teardown
/// cleanup so the kill-then-reap pairing lives in one place.
module private PosixReap =

    /// Reap the leader `id`, gating its `killpg` through the shared liveness + identity choke so a pgid
    /// recycled since it was tracked is NEVER SIGKILLed (the wrong-target kill T-084 closes). `identity`
    /// is the start-time token captured at track time; a live number whose current identity differs from
    /// it is a recycled stranger and the `killpg` is skipped, while a matching or unknown token falls
    /// back to the by-number kill exactly as before (a leader reaped while descendants keep the pgid
    /// alive is still cleaned up). `reapLeader`'s `waitpid` only ever reaps our OWN child, so a recycled
    /// pid there is a harmless `ECHILD` — it needs no gate.
    let leader (id: int) (identity: uint64 option) =
        if Native.Posix.processGroupStillTracked id identity then
            Native.Posix.killProcessGroup id

        Native.Posix.reapLeader id

/// The set of children a backend tracks, behind a single lock — the concurrency invariant every
/// backend needs: a spawn racing a `Dispose`/teardown must serialize on add / remove / snapshot so a
/// child can never be missed by teardown or double-released. One small type so the lock is applied
/// consistently and can't be forgotten.
type internal TrackedChildren<'T when 'T: equality>() =
    let gate = obj ()
    let items = List<'T>()

    /// Track a freshly-spawned child.
    member _.Add(item: 'T) = lock gate (fun () -> items.Add item)

    /// Stop tracking a child; returns true when it was still tracked (false if already removed).
    member _.Remove(item: 'T) : bool = lock gate (fun () -> items.Remove item)

    /// A point-in-time copy of the tracked children.
    member _.Snapshot() : 'T list = lock gate (fun () -> List.ofSeq items)

    /// Atomically take and clear the tracked children — for the one-shot teardown drain.
    member _.Drain() : 'T list =
        lock gate (fun () ->
            let copy = List.ofSeq items
            items.Clear()
            copy)

/// One OS containment primitive behind a `ProcessGroup`. Each implementation owns exactly the state
/// its mechanism needs — a Windows Job handle, a Linux cgroup path, or the tracked POSIX pgids — so
/// the type-state that used to be a runtime `match mechanism` is now structural, and `ProcessGroup`
/// is a thin orchestrator over this one interface.
type internal IContainmentBackend =

    /// The OS primitive this backend is.
    abstract Mechanism: Mechanism

    /// Spawn a child of this group (not yet tracked).
    abstract Spawn: Command -> Result<Native.Common.Spawned, ProcessError>

    /// Start tracking a freshly-spawned child (place it in the container). Returns `Error` when the
    /// child could not actually be contained — e.g. the cgroup backend fails to migrate it into the
    /// cgroup — in which case the child has already been killed and reaped, so no live, uncontained,
    /// untracked child is left behind for the caller to clean up. The Windows Job and POSIX
    /// process-group backends always succeed (the child is contained by spawn itself).
    abstract Track: Native.Common.Spawned -> Result<unit, ProcessError>

    /// Stop tracking a reaped child (close its handle / drop it from the container's view).
    abstract Release: Native.Common.Spawned -> unit

    /// Wait for one contained child to conclude.
    abstract Wait: nativeint -> Task<Outcome>

    /// The pid behind a spawned child, when known.
    abstract PidOf: Native.Common.Spawned -> int option

    /// Hard-kill a single contained child (not the whole tree).
    abstract KillChild: Native.Common.Spawned -> unit

    /// Hard-kill the whole contained tree now (no grace) without releasing the container.
    abstract KillTree: unit -> unit

    /// Gracefully kill the tree (SIGTERM → grace → SIGKILL) without releasing the container.
    abstract GracefulKillTree: TimeSpan -> Task

    /// The pids currently in the group — a point-in-time snapshot.
    abstract Members: unit -> Result<int list, ProcessError>

    /// Broadcast a signal to every process in the group.
    abstract Signal: Signal -> Result<unit, ProcessError>

    /// Suspend (freeze) every process in the group.
    abstract Suspend: unit -> Result<unit, ProcessError>

    /// Resume a suspended tree.
    abstract Resume: unit -> Result<unit, ProcessError>

    /// A snapshot of the group's resource usage.
    abstract Stats: unit -> Result<ProcessGroupStats, ProcessError>

    /// The hard teardown, run exactly once by the owning `ProcessGroup`: reap the tree and free the
    /// container (close the Job handle / `cgroup.kill` + rmdir / SIGKILL the pgids).
    abstract HardRelease: unit -> unit

/// Windows Job Object backend. Closing the job handle triggers `KILL_ON_JOB_CLOSE`; the tracked
/// process handles (closed on reap or teardown) are only for waiting.
type internal JobObjectBackend(jobHandle: nativeint) =
    let children = TrackedChildren<nativeint>()

    // Children spawned with `Command.WindowsCtrlSignals()` (CREATE_NEW_PROCESS_GROUP), mapped by their
    // process HANDLE to their console process-group id (= pid), so `Signal.Int`/`Signal.Term` can
    // `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, groupId)` each of them. Keyed by handle, not pid, so
    // it stays in lockstep with `children`: while we hold a child's handle open the OS cannot recycle
    // its pid, so a stored group id is never stale (no wrong-target CTRL+BREAK). Entries are added at
    // `Track` and removed at exactly the same points the handle is closed.
    let ctrlGroups = ConcurrentDictionary<nativeint, int>()

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject

        member _.Spawn(command) =
            Native.Windows.spawnWindows jobHandle command

        member _.Track(spawned) =
            // The child was assigned to the Job while still suspended at spawn, so it is already
            // contained — tracking its handle is only for waiting. Always succeeds.
            children.Add spawned.Handle

            if spawned.WindowsCtrlGroup then
                // Its console process-group id is its pid; the handle is still open here, so the pid is
                // live and unambiguous. Records it as CTRL+BREAK-capable for `Signal.Int`/`Signal.Term`.
                ctrlGroups[spawned.Handle] <- Native.Windows.processIdWindows spawned.Handle

            Ok()

        member _.Release(spawned) =
            // Remove the handle before closing so the teardown drain can't double-close a reused
            // handle value; the Job still contains the tree.
            if children.Remove spawned.Handle then
                ctrlGroups.TryRemove spawned.Handle |> ignore
                Native.Windows.closeWindowsHandle spawned.Handle

        member _.Wait(handle) = Native.Windows.waitWindows handle

        member _.PidOf(spawned) =
            Some(Native.Windows.processIdWindows spawned.Handle)

        member _.KillChild(spawned) =
            Native.Windows.terminateWindowsProcess spawned.Handle

        member _.KillTree() =
            Native.Windows.terminateWindowsJob jobHandle

        member _.GracefulKillTree(_grace) =
            // No per-job graceful signal on Windows; this is the atomic Job kill.
            task { Native.Windows.terminateWindowsJob jobHandle } :> Task

        member _.Members() =
            // `membersWindows` already returns a `Result` — it grows the buffer to the whole job and
            // surfaces a genuine query failure as `ProcessError.Io` rather than a fabricated empty list.
            Native.Windows.membersWindows jobHandle

        member _.Signal(signal) =
            match signal with
            | Signal.Kill ->
                Native.Windows.terminateWindowsJob jobHandle
                Ok()
            | Signal.Int
            | Signal.Term ->
                // Best-effort SOFT stop: deliver a console CTRL+BREAK to every child started with
                // `Command.WindowsCtrlSignals()`, targeting each child's OWN process group id (its pid)
                // so the caller's own console group is never signalled. `Signal.Int`/`Signal.Term` both
                // map to CTRL+BREAK — the closest Windows analogue — because CREATE_NEW_PROCESS_GROUP
                // disables the child's CTRL+C and only CTRL+BREAK can be group-targeted.
                let groups = ctrlGroups.Values |> List.ofSeq

                if List.isEmpty groups then
                    // No child in this group can receive a Ctrl event — honest Unsupported, never a
                    // silent downgrade to the Job kill.
                    Error(
                        ProcessError.Unsupported
                            $"{signal} on Windows is deliverable only to a child started with Command.WindowsCtrlSignals() (CREATE_NEW_PROCESS_GROUP); this group has none"
                    )
                else
                    // Send to every capable child; a member whose send fails (e.g. the caller has no
                    // console to share) is not silently ignored — the first genuine failure is reported.
                    let mutable firstFailure: string option = None

                    for groupId in groups do
                        match Native.Windows.sendConsoleCtrlBreakWindows groupId with
                        | Ok() -> ()
                        | Error message ->
                            if firstFailure.IsNone then
                                firstFailure <- Some message

                    match firstFailure with
                    | None -> Ok()
                    | Some message ->
                        Error(
                            ProcessError.Unsupported
                                $"{signal} on Windows could not be delivered as a console CTRL+BREAK (GenerateConsoleCtrlEvent failed: {message}); the caller may have no console to share with the child"
                        )
            | _ ->
                Error(
                    ProcessError.Unsupported
                        $"signal {signal} on Windows (only Signal.Kill, and Signal.Int/Signal.Term to a child started with Command.WindowsCtrlSignals(), are deliverable)"
                )

        member _.Suspend() = Native.Windows.suspendWindows jobHandle

        member _.Resume() = Native.Windows.resumeWindows jobHandle

        member _.Stats() =
            match Native.Windows.jobStatsWindows jobHandle with
            | Some(active, cpu, peak) -> Ok(ProcessGroupStats(active, Some cpu, Some peak))
            | None -> Error(ProcessError.Io "failed to query Job Object accounting")

        member _.HardRelease() =
            ctrlGroups.Clear()

            for handle in children.Drain() do
                Native.Windows.closeWindowsHandle handle

            Native.Windows.closeWindowsHandle jobHandle

/// Linux cgroup v2 backend (the `limits` mechanism). Membership lives in `cgroup.procs`; the tree is
/// reaped with `cgroup.kill` and the directory removed.
type internal CgroupBackend(cgroupPath: string) =
    let children = TrackedChildren<int>()

    // The start-time identity token captured for each tracked leader pid at `Track` (see
    // `ProcessGroupBackend`), kept in a parallel dictionary keyed alongside `children`. The cgroup's own
    // per-member signal path is already pid-reuse-safe via pidfd (`Native.Cgroup.deliverIdentitySafe`);
    // this token gates the ONLY remaining by-number `killpg`, the shared `PosixReap.leader` escapee/
    // teardown reap, so it too can never SIGKILL a pid recycled since it was tracked. `None` when
    // unreadable, degrading that reap to the by-number kill exactly as before.
    let identities = ConcurrentDictionary<int, uint64 option>()

    // Pull and remove the captured identity for `pid` (defaulting to `None`), so the shared reap can gate
    // its `killpg` on it. Removal keeps `identities` in lockstep with `children`.
    let takeIdentity (pid: int) : uint64 option =
        match identities.TryRemove pid with
        | true, token -> token
        | false, _ -> None

    // Is `pid` still the SAME tracked child, not a recycled number? Mirrors `ProcessGroupBackend.stillOurs`
    // (a cgroup child is its own process-group leader — spawned with POSIX_SPAWN_SETPGROUP, pgid == pid —
    // so the pgid choke applies): gate the by-number liveness probe through the captured start-time
    // identity so a pid recycled since it was tracked is reported gone and never SIGKILLed (the
    // wrong-target kill T-084 closes). A matching identity, or an unknown token on either side, defers to
    // the by-number verdict, so no coverage is lost. Used by `KillChild`, the one remaining per-child raw
    // kill path (the per-member signal path is already pidfd-pinned via `Native.Cgroup.deliverIdentitySafe`).
    let stillOurs (pid: int) : bool =
        let captured =
            match identities.TryGetValue pid with
            | true, token -> token
            | false, _ -> None

        Native.Posix.processGroupStillTracked pid captured

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.CgroupV2

        member _.Spawn(command) =
            // Spawn through the self-migrating cgroup launcher (a tiny `/bin/sh` that writes its own pid
            // into this cgroup's cgroup.procs, then `exec`s the real program in place), so the target's
            // pid is already a cgroup member before it runs a single instruction — closing the old
            // spawn->migrate window where a descendant forked in that first instant could escape the
            // limits. See `Native.Posix.spawnPosixIntoCgroup`. `Spawned.Handle` is the launcher pid,
            // which becomes the target's pid unchanged across `exec`.
            Native.Posix.spawnPosixIntoCgroup command (System.IO.Path.Combine(cgroupPath, "cgroup.procs"))

        member _.Track(spawned) =
            let pid = int spawned.Handle
            // Track the pid first so teardown can always reap it (cgroup.kill SIGKILLs but does not
            // waitpid our own children), and so a concurrent HardRelease can see it even if the
            // migration confirmation then fails. Capture the leader's start-time identity alongside it
            // (see `identities`) so the shared teardown reap's `killpg` is pid-reuse-safe.
            children.Add pid
            identities[pid] <- Native.Posix.readProcessIdentity pid

            // Confirm (and idempotently re-apply) the cgroup migration the launcher already performed
            // in `Spawn`: the target starts already inside the cgroup, so this parent-side write to
            // cgroup.procs is a confirmation whose real value is honest error classification (see
            // `Native.Cgroup.migrateToCgroup` — a write success or an ESRCH on a fast-exited target is
            // `Ok`; a genuine open/write failure means the cgroup could not be joined). On a genuine
            // FAILURE the launcher's own self-migrate failed too, so the target never ran — but the
            // launcher process itself is still ours to reap: drop it from tracking, then killpg its
            // group + reap the leader, and report an honest error, leaving no live, uncontained child.
            match Native.Cgroup.migrateToCgroup cgroupPath pid with
            | Ok() -> Ok()
            | Error detail ->
                // Reap ONLY if this call is the one that takes the pid out of tracking. A `HardRelease`
                // that raced this spawn may have already drained and reaped it (`Remove` then returns
                // false); reaping again would `killpg`/`waitpid` a pid the OS may have recycled for an
                // unrelated process group — a wrong-target kill / double-reap. `ProcessGroup` now serializes
                // spawn+track against release under one lock, so in practice `Remove` wins here; the guard
                // keeps the backend correct on its own even when driven concurrently (e.g. in tests).
                if children.Remove pid then
                    PosixReap.leader pid (takeIdentity pid)
                else
                    identities.TryRemove pid |> ignore

                Error(
                    ProcessError.ResourceLimit
                        $"the child could not be migrated into the cgroup (write to cgroup.procs failed): {detail}"
                )

        member _.Release(spawned) =
            // A run verb has reaped this child; stop tracking so teardown does not waitpid it again.
            // (The kernel already removed it from cgroup.procs.)
            let pid = int spawned.Handle
            identities.TryRemove pid |> ignore
            children.Remove pid |> ignore

        member _.Wait(handle) = Native.Posix.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            // Hard-kill this one child — but only while it is still OURS. A recycled pid (identity differs)
            // must never be SIGKILLed (wrong-target kill); gate it through the choke and prune it instead,
            // keeping `identities` in lockstep with `children`.
            let pid = int spawned.Handle

            if stillOurs pid then
                Native.Posix.killProcess pid
            else
                identities.TryRemove pid |> ignore
                children.Remove pid |> ignore

        member _.KillTree() = Native.Cgroup.killCgroup cgroupPath

        member _.GracefulKillTree(grace) =
            GracefulTeardown.poll
                (fun () -> Native.Cgroup.terminateCgroup cgroupPath)
                (fun () -> Native.Cgroup.cgroupAlive cgroupPath)
                (fun () -> Native.Cgroup.killCgroup cgroupPath)
                grace

        member _.Members() =
            // `cgroupMembers` already distinguishes "read, and it's empty" from "the read failed" — surface
            // a read failure honestly as `ProcessError.Io` rather than reporting a fabricated empty group.
            match Native.Cgroup.cgroupMembers cgroupPath with
            | Ok members -> Ok members
            | Error message ->
                Error(ProcessError.Io $"could not read cgroup.procs to list the group's members: {message}")

        member _.Signal(signal) =
            match signal with
            | Signal.Kill ->
                Native.Cgroup.killCgroup cgroupPath // atomic whole-subtree SIGKILL
                Ok()
            | _ ->
                let signalNum = Native.Posix.signalNumber signal

                match Native.Cgroup.signalCgroup cgroupPath signalNum with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> Ok()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                    Error(ProcessError.Io $"failed to deliver signal {signalNum} to cgroup: {message} (errno {errno})")

        member _.Suspend() =
            match Native.Cgroup.freezeCgroup cgroupPath true with
            | Ok() -> Ok()
            | Error message -> Error(ProcessError.Io $"failed to freeze cgroup: {message}")

        member _.Resume() =
            match Native.Cgroup.freezeCgroup cgroupPath false with
            | Ok() -> Ok()
            | Error message -> Error(ProcessError.Io $"failed to thaw cgroup: {message}")

        member _.Stats() =
            // The active-process count comes from the same read as `Members`: a read failure must
            // propagate as an honest error, not be silently reported as zero active processes.
            match Native.Cgroup.cgroupMembers cgroupPath with
            | Error message ->
                Error(
                    ProcessError.Io $"could not read cgroup.procs for stats (active process count unknown): {message}"
                )
            | Ok members ->
                let active = List.length members
                let cpu, peak = Native.Cgroup.cgroupStats cgroupPath
                Ok(ProcessGroupStats(active, cpu, peak))

        member _.HardRelease() =
            Native.Cgroup.killCgroup cgroupPath

            // cgroup.kill SIGKILLs everything in the cgroup but does not reap our own children, and a
            // child that failed to migrate runs outside the cgroup entirely. Every child is also its own
            // process-group leader, so killpg cleans up an escapee's subtree; then reap the leader.
            // Drain (atomic take-and-clear), not Snapshot: a Snapshot would leave the tracking list
            // populated after teardown, and a concurrent per-child cleanup (a run's `Release`, or a
            // `Track` migration-failure reap) could still see (and re-reap) the same pid — after the first
            // killpg/waitpid the OS may reuse that pid, so a second killpg would land on an unrelated
            // process group (wrong-target kill). The captured identity gates each reap's `killpg` so it is
            // also safe against a pid recycled since it was tracked.
            for pid in children.Drain() do
                PosixReap.leader pid (takeIdentity pid)

            Native.Cgroup.removeCgroup cgroupPath

/// POSIX process-group backend (macOS/BSD, or Linux without cgroup delegation). Every `posix_spawn`
/// forms its own pgid, so a multi-child group holds several; `killpg` is the teardown.
type internal ProcessGroupBackend() =
    let children = TrackedChildren<int>()

    // The start-time identity token captured for each tracked pgid at `Track`, kept in a parallel
    // dictionary keyed alongside `children` (mirroring `JobObjectBackend.ctrlGroups`). `None` when no
    // reliable token could be read (a non-Linux/macOS POSIX, or a pgid already gone at track time), in
    // which case the choke below degrades to the by-number liveness verdict — never losing coverage. An
    // entry is added at `Track` and removed wherever a pgid leaves tracking, so the two structures stay
    // in lockstep (see `untrack`).
    let identities = ConcurrentDictionary<int, uint64 option>()

    // The single liveness + identity choke every probe/signal/kill path funnels through, so the reuse
    // check is never duplicated per call site: is `pgid` still the SAME live group we tracked? It gates
    // the by-number liveness probe through the pgid's captured start-time identity — a recycled pgid (a
    // live number whose current identity differs from the captured one) is reported NOT ours, so callers
    // prune it and never signal it. A matching identity, or an unknown token on either side (a leader
    // reaped while descendants keep the pgid alive, or a platform without a reader), defers to the
    // by-number verdict, so no platform loses coverage.
    let stillOurs (pgid: int) : bool =
        let captured =
            match identities.TryGetValue pgid with
            | true, token -> token
            | false, _ -> None

        Native.Posix.processGroupStillTracked pgid captured

    // Drop a pgid from tracking entirely (both the pgid set and its identity token) — used when the choke
    // finds it drained or recycled. Returns whether this call was the one that removed it.
    let untrack (pgid: int) : bool =
        identities.TryRemove pgid |> ignore
        children.Remove pgid

    let anyChildAlive () =
        children.Snapshot() |> List.exists stillOurs

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup
        member _.Spawn(command) = Native.Posix.spawnPosix command

        member _.Track(spawned) =
            // Each posix_spawn already formed its own process group (pgid = child pid), so the child is
            // contained by spawn itself; tracking the pgid is all that is needed. Capture the leader's
            // start-time identity now, while the pgid is definitively ours and its leader (pid == pgid)
            // is freshly alive, so a later probe can tell it apart from a process that recycles the
            // number (`None` when unreadable — the choke then degrades to the by-number verdict). Always
            // succeeds.
            let pgid = int spawned.Handle
            children.Add pgid
            identities[pgid] <- Native.Posix.readProcessIdentity pgid
            Ok()

        member _.Release(spawned) =
            // A pgid is a whole group; the reaped leader may have left backgrounded members behind, so
            // only stop tracking once the group is actually empty — or the pgid has been recycled by an
            // unrelated process (the choke's identity check), which must likewise stop tracking so a
            // stranger is never signalled.
            let pgid = int spawned.Handle

            if not (stillOurs pgid) then
                untrack pgid |> ignore

        member _.Wait(handle) = Native.Posix.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            // Hard-kill this one child's group — but only while it is still OURS. A recycled pgid
            // (identity differs) must never be SIGKILLed (wrong-target kill); gate it through the choke
            // and prune it instead.
            let pgid = int spawned.Handle

            if stillOurs pgid then
                Native.Posix.killProcessGroup pgid
            else
                untrack pgid |> ignore

        member _.KillTree() =
            for pgid in children.Snapshot() do
                if stillOurs pgid then
                    Native.Posix.killProcessGroup pgid
                else
                    untrack pgid |> ignore

        member _.GracefulKillTree(grace) =
            // Snapshot the pgids once so terminate and the final force-kill act on the same set; each
            // delivery is gated through the choke, so a pgid recycled since it was tracked gets neither
            // the SIGTERM nor the SIGKILL (the poll's `anyChildAlive` is choke-gated too).
            let pgids = children.Snapshot()

            GracefulTeardown.poll
                (fun () ->
                    for pgid in pgids do
                        if stillOurs pgid then
                            Native.Posix.terminateProcessGroup pgid)
                anyChildAlive
                (fun () ->
                    for pgid in pgids do
                        if stillOurs pgid then
                            Native.Posix.killProcessGroup pgid)
                grace

        member _.Members() =
            // Report only the pgids still ours and alive (choke-gated): a drained or recycled pgid is not
            // a member of this group. This is a read — it does not prune (a mutating op does that).
            Ok(children.Snapshot() |> List.filter stillOurs)

        member _.Signal(signal) =
            let signalNum = Native.Posix.signalNumber signal
            let mutable firstFailure: (int * string) option = None

            // Broadcast to every tracked pgid that is still ours; a pgid recycled since it was tracked
            // (the choke reports it gone) is pruned and never signalled — the wrong-target delivery this
            // closes. A member that has already exited (ESRCH) must not abort delivery to the rest, and
            // only the first genuine delivery failure (e.g. EINVAL for an invalid signal number) is
            // reported.
            for pgid in children.Snapshot() do
                if stillOurs pgid then
                    match Native.Posix.signalProcessGroup pgid signalNum with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> ()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        if firstFailure.IsNone then
                            firstFailure <- Some(errno, message)
                else
                    untrack pgid |> ignore

            match firstFailure with
            | None -> Ok()
            | Some(errno, message) ->
                Error(
                    ProcessError.Io $"failed to deliver signal {signalNum} to process group: {message} (errno {errno})"
                )

        member _.Suspend() =
            let mutable firstFailure: (int * string) option = None

            for pgid in children.Snapshot() do
                if stillOurs pgid then
                    match Native.Posix.suspendProcessGroup pgid with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> ()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        if firstFailure.IsNone then
                            firstFailure <- Some(errno, message)
                else
                    untrack pgid |> ignore

            match firstFailure with
            | None -> Ok()
            | Some(errno, message) ->
                Error(ProcessError.Io $"failed to suspend process group: {message} (errno {errno})")

        member _.Resume() =
            let mutable firstFailure: (int * string) option = None

            for pgid in children.Snapshot() do
                if stillOurs pgid then
                    match Native.Posix.resumeProcessGroup pgid with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> ()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        if firstFailure.IsNone then
                            firstFailure <- Some(errno, message)
                else
                    untrack pgid |> ignore

            match firstFailure with
            | None -> Ok()
            | Some(errno, message) ->
                Error(ProcessError.Io $"failed to resume process group: {message} (errno {errno})")

        member _.Stats() =
            let active = children.Snapshot() |> List.filter stillOurs |> List.length
            Ok(ProcessGroupStats(active, None, None))

        member _.HardRelease() =
            // Each pgid's leader is a child we posix_spawned, so we must waitpid it ourselves — `killpg`
            // SIGKILLs the group but does not reap our own children. Reap the leaders we still track (a
            // run verb Releases the ones it already reaped); other group members reparent to init.
            // Passing each pgid's captured identity to `PosixReap.leader` gates its `killpg` through the
            // choke, so teardown never SIGKILLs a pgid recycled since it was tracked (a wrong-target
            // kill). Drain (atomic take-and-clear), not Snapshot: a Snapshot would leave the tracking list
            // populated after teardown, and a concurrent per-child cleanup (a run's `Release`) could still
            // see (and re-reap) the same pgid — after the first killpg/waitpid the OS may reuse that pid,
            // so a second killpg would land on an unrelated process group (wrong-target kill).
            for pgid in children.Drain() do
                let identity =
                    match identities.TryRemove pgid with
                    | true, token -> token
                    | false, _ -> None

                PosixReap.leader pgid identity
