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

    let leader (id: int) =
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
            Ok(Native.Windows.membersWindows jobHandle)

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

        member _.Suspend() =
            Native.Windows.suspendWindows jobHandle
            Ok()

        member _.Resume() =
            Native.Windows.resumeWindows jobHandle
            Ok()

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
            // migration confirmation then fails.
            children.Add pid

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
                    PosixReap.leader pid

                Error(
                    ProcessError.ResourceLimit
                        $"the child could not be migrated into the cgroup (write to cgroup.procs failed): {detail}"
                )

        member _.Release(spawned) =
            // A run verb has reaped this child; stop tracking so teardown does not waitpid it again.
            // (The kernel already removed it from cgroup.procs.)
            children.Remove(int spawned.Handle) |> ignore

        member _.Wait(handle) = Native.Posix.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            Native.Posix.killProcess (int spawned.Handle)

        member _.KillTree() = Native.Cgroup.killCgroup cgroupPath

        member _.GracefulKillTree(grace) =
            GracefulTeardown.poll
                (fun () -> Native.Cgroup.terminateCgroup cgroupPath)
                (fun () -> Native.Cgroup.cgroupAlive cgroupPath)
                (fun () -> Native.Cgroup.killCgroup cgroupPath)
                grace

        member _.Members() =
            Ok(Native.Cgroup.cgroupMembers cgroupPath)

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
            Native.Cgroup.freezeCgroup cgroupPath true
            Ok()

        member _.Resume() =
            Native.Cgroup.freezeCgroup cgroupPath false
            Ok()

        member _.Stats() =
            let active = List.length (Native.Cgroup.cgroupMembers cgroupPath)
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
            // process group (wrong-target kill).
            for pid in children.Drain() do
                PosixReap.leader pid

            Native.Cgroup.removeCgroup cgroupPath

/// POSIX process-group backend (macOS/BSD, or Linux without cgroup delegation). Every `posix_spawn`
/// forms its own pgid, so a multi-child group holds several; `killpg` is the teardown.
type internal ProcessGroupBackend() =
    let children = TrackedChildren<int>()

    let anyChildAlive () =
        children.Snapshot() |> List.exists Native.Posix.processGroupAlive

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup
        member _.Spawn(command) = Native.Posix.spawnPosix command

        member _.Track(spawned) =
            // Each posix_spawn already formed its own process group (pgid = child pid), so the child is
            // contained by spawn itself; tracking the pgid is all that is needed. Always succeeds.
            children.Add(int spawned.Handle)
            Ok()

        member _.Release(spawned) =
            // A pgid is a whole group; the reaped leader may have left backgrounded members behind,
            // so only stop tracking once the group is actually empty.
            let pgid = int spawned.Handle

            if not (Native.Posix.processGroupAlive pgid) then
                children.Remove pgid |> ignore

        member _.Wait(handle) = Native.Posix.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            Native.Posix.killProcessGroup (int spawned.Handle)

        member _.KillTree() =
            for pgid in children.Snapshot() do
                Native.Posix.killProcessGroup pgid

        member _.GracefulKillTree(grace) =
            // Snapshot the pgids once so terminate and the final force-kill act on the same set.
            let pgids = children.Snapshot()

            GracefulTeardown.poll
                (fun () ->
                    for pgid in pgids do
                        Native.Posix.terminateProcessGroup pgid)
                anyChildAlive
                (fun () ->
                    for pgid in pgids do
                        if Native.Posix.processGroupAlive pgid then
                            Native.Posix.killProcessGroup pgid)
                grace

        member _.Members() = Ok(children.Snapshot())

        member _.Signal(signal) =
            let signalNum = Native.Posix.signalNumber signal
            let mutable firstFailure: (int * string) option = None

            // Broadcast to every tracked pgid regardless of an earlier failure — a member that has
            // already exited (ESRCH) must not abort delivery to the rest — but only the first genuine
            // delivery failure (e.g. EINVAL for an invalid signal number) is reported.
            for pgid in children.Snapshot() do
                match Native.Posix.signalProcessGroup pgid signalNum with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> ()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                    if firstFailure.IsNone then
                        firstFailure <- Some(errno, message)

            match firstFailure with
            | None -> Ok()
            | Some(errno, message) ->
                Error(
                    ProcessError.Io $"failed to deliver signal {signalNum} to process group: {message} (errno {errno})"
                )

        member _.Suspend() =
            for pgid in children.Snapshot() do
                Native.Posix.suspendProcessGroup pgid

            Ok()

        member _.Resume() =
            for pgid in children.Snapshot() do
                Native.Posix.resumeProcessGroup pgid

            Ok()

        member _.Stats() =
            let active =
                children.Snapshot() |> List.filter Native.Posix.processGroupAlive |> List.length

            Ok(ProcessGroupStats(active, None, None))

        member _.HardRelease() =
            // Each pgid's leader is a child we posix_spawned, so we must waitpid it ourselves — `killpg`
            // SIGKILLs the group but does not reap our own children. Reap the leaders we still track (a
            // run verb Releases the ones it already reaped); other group members reparent to init.
            // Drain (atomic take-and-clear), not Snapshot: a Snapshot would leave the tracking list
            // populated after teardown, and a concurrent per-child cleanup (a run's `Release`) could still
            // see (and re-reap) the same pgid — after the first killpg/waitpid the OS may reuse that pid,
            // so a second killpg would land on an unrelated process group (wrong-target kill).
            for pgid in children.Drain() do
                PosixReap.leader pgid
