namespace ProcessKit

open System
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

/// One OS containment primitive behind a `ProcessGroup`. Each implementation owns exactly the state
/// its mechanism needs — a Windows Job handle, a Linux cgroup path, or the tracked POSIX pgids — so
/// the type-state that used to be a runtime `match mechanism` is now structural, and `ProcessGroup`
/// is a thin orchestrator over this one interface.
type internal IContainmentBackend =

    /// The OS primitive this backend is.
    abstract Mechanism: Mechanism

    /// Spawn a child of this group (not yet tracked).
    abstract Spawn: Command -> Result<Native.Spawned, ProcessError>

    /// Start tracking a freshly-spawned child (place it in the container).
    abstract Track: Native.Spawned -> unit

    /// Stop tracking a reaped child (close its handle / drop it from the container's view).
    abstract Release: Native.Spawned -> unit

    /// Wait for one contained child to conclude.
    abstract Wait: nativeint -> Task<Outcome>

    /// The pid behind a spawned child, when known.
    abstract PidOf: Native.Spawned -> int option

    /// Hard-kill a single contained child (not the whole tree).
    abstract KillChild: Native.Spawned -> unit

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
    let childLock = obj ()
    let processHandles = List<nativeint>()

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject
        member _.Spawn(command) = Native.spawnWindows jobHandle command

        member _.Track(spawned) =
            lock childLock (fun () -> processHandles.Add spawned.Handle)

        member _.Release(spawned) =
            // Remove the handle before closing so the teardown drain can't double-close a reused
            // handle value; the Job still contains the tree.
            let wasTracked = lock childLock (fun () -> processHandles.Remove spawned.Handle)

            if wasTracked then
                Native.closeWindowsHandle spawned.Handle

        member _.Wait(handle) = Native.waitWindows handle

        member _.PidOf(spawned) =
            Some(Native.processIdWindows spawned.Handle)

        member _.KillChild(spawned) =
            Native.terminateWindowsProcess spawned.Handle

        member _.KillTree() = Native.terminateWindowsJob jobHandle

        member _.GracefulKillTree(_grace) =
            // No per-job graceful signal on Windows; this is the atomic Job kill.
            task { Native.terminateWindowsJob jobHandle } :> Task

        member _.Members() = Ok(Native.membersWindows jobHandle)

        member _.Signal(signal) =
            match signal with
            | Signal.Kill ->
                Native.terminateWindowsJob jobHandle
                Ok()
            | _ -> Error(ProcessError.Unsupported $"signal {signal} on Windows (only Signal.Kill is deliverable)")

        member _.Suspend() =
            Native.suspendWindows jobHandle
            Ok()

        member _.Resume() =
            Native.resumeWindows jobHandle
            Ok()

        member _.Stats() =
            match Native.jobStatsWindows jobHandle with
            | Some(active, cpu, peak) -> Ok(ProcessGroupStats(active, Some cpu, Some peak))
            | None -> Error(ProcessError.Io "failed to query Job Object accounting")

        member _.HardRelease() =
            let handles =
                lock childLock (fun () ->
                    let copy = List.ofSeq processHandles
                    processHandles.Clear()
                    copy)

            for handle in handles do
                Native.closeWindowsHandle handle

            Native.closeWindowsHandle jobHandle

/// Linux cgroup v2 backend (the `limits` mechanism). Membership lives in `cgroup.procs`; the tree is
/// reaped with `cgroup.kill` and the directory removed.
type internal CgroupBackend(cgroupPath: string) =
    let childLock = obj ()
    let childPids = List<int>()

    let pidSnapshot () =
        lock childLock (fun () -> List.ofSeq childPids)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.CgroupV2
        member _.Spawn(command) = Native.spawnPosix command

        member _.Track(spawned) =
            // Track the pid so teardown can reap it (cgroup.kill SIGKILLs but does not waitpid our own
            // children) and clean it up as a fallback if it failed to migrate into the cgroup and so
            // escapes cgroup.kill.
            lock childLock (fun () -> childPids.Add(int spawned.Handle))
            Native.migrateToCgroup cgroupPath (int spawned.Handle)

        member _.Release(spawned) =
            // A run verb has reaped this child; stop tracking so teardown does not waitpid it again.
            // (The kernel already removed it from cgroup.procs.)
            lock childLock (fun () -> childPids.Remove(int spawned.Handle) |> ignore)

        member _.Wait(handle) = Native.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)
        member _.KillChild(spawned) = Native.killProcess (int spawned.Handle)
        member _.KillTree() = Native.killCgroup cgroupPath

        member _.GracefulKillTree(grace) =
            GracefulTeardown.poll
                (fun () -> Native.terminateCgroup cgroupPath)
                (fun () -> Native.cgroupAlive cgroupPath)
                (fun () -> Native.killCgroup cgroupPath)
                grace

        member _.Members() = Ok(Native.cgroupMembers cgroupPath)

        member _.Signal(signal) =
            match signal with
            | Signal.Kill -> Native.killCgroup cgroupPath // atomic whole-subtree SIGKILL
            | _ -> Native.signalCgroup cgroupPath (Native.signalNumber signal)

            Ok()

        member _.Suspend() =
            Native.freezeCgroup cgroupPath true
            Ok()

        member _.Resume() =
            Native.freezeCgroup cgroupPath false
            Ok()

        member _.Stats() =
            let active = List.length (Native.cgroupMembers cgroupPath)
            let cpu, peak = Native.cgroupStats cgroupPath
            Ok(ProcessGroupStats(active, cpu, peak))

        member _.HardRelease() =
            Native.killCgroup cgroupPath

            // cgroup.kill SIGKILLs everything in the cgroup but does not reap our own children, and a
            // child that failed to migrate runs outside the cgroup entirely. Every child is also its own
            // process-group leader, so killpg cleans up an escapee's subtree; then reap the leader.
            for pid in pidSnapshot () do
                Native.killProcessGroup pid
                Native.reapLeader pid

            Native.removeCgroup cgroupPath

/// POSIX process-group backend (macOS/BSD, or Linux without cgroup delegation). Every `posix_spawn`
/// forms its own pgid, so a multi-child group holds several; `killpg` is the teardown.
type internal ProcessGroupBackend() =
    let childLock = obj ()
    let childPgids = List<int>()

    let pgidSnapshot () =
        lock childLock (fun () -> List.ofSeq childPgids)

    let anyChildAlive () =
        pgidSnapshot () |> List.exists Native.processGroupAlive

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup
        member _.Spawn(command) = Native.spawnPosix command

        member _.Track(spawned) =
            lock childLock (fun () -> childPgids.Add(int spawned.Handle))

        member _.Release(spawned) =
            // A pgid is a whole group; the reaped leader may have left backgrounded members behind,
            // so only stop tracking once the group is actually empty.
            let pgid = int spawned.Handle

            if not (Native.processGroupAlive pgid) then
                lock childLock (fun () -> childPgids.Remove pgid |> ignore)

        member _.Wait(handle) = Native.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            Native.killProcessGroup (int spawned.Handle)

        member _.KillTree() =
            for pgid in pgidSnapshot () do
                Native.killProcessGroup pgid

        member _.GracefulKillTree(grace) =
            // Snapshot the pgids once so terminate and the final force-kill act on the same set.
            let pgids = pgidSnapshot ()

            GracefulTeardown.poll
                (fun () ->
                    for pgid in pgids do
                        Native.terminateProcessGroup pgid)
                anyChildAlive
                (fun () ->
                    for pgid in pgids do
                        if Native.processGroupAlive pgid then
                            Native.killProcessGroup pgid)
                grace

        member _.Members() = Ok(pgidSnapshot ())

        member _.Signal(signal) =
            let signalNum = Native.signalNumber signal

            for pgid in pgidSnapshot () do
                Native.signalProcessGroup pgid signalNum |> ignore

            Ok()

        member _.Suspend() =
            for pgid in pgidSnapshot () do
                Native.suspendProcessGroup pgid

            Ok()

        member _.Resume() =
            for pgid in pgidSnapshot () do
                Native.resumeProcessGroup pgid

            Ok()

        member _.Stats() =
            let active = pgidSnapshot () |> List.filter Native.processGroupAlive |> List.length
            Ok(ProcessGroupStats(active, None, None))

        member _.HardRelease() =
            // Each pgid's leader is a child we posix_spawned, so we must waitpid it ourselves — `killpg`
            // SIGKILLs the group but does not reap our own children. Reap the leaders we still track (a
            // run verb Releases the ones it already reaped); other group members reparent to init.
            for pgid in pgidSnapshot () do
                Native.killProcessGroup pgid
                Native.reapLeader pgid
