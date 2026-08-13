namespace ProcessKit

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// Shared graceful-teardown shape for all containment backends: request the platform/configured soft
/// stop, poll until the tree is dead or `grace` elapses, then force-kill whatever remains.
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

    /// Place an already-running EXTERNAL process (started by someone else — never spawned by us) into
    /// the container, so it thereafter obeys the same whole-tree rules as a `Track`ed child: kill-on-
    /// dispose, `Signal`/`Suspend`/`Resume`/`Members`/`MembersInfo`/`Stats`, and any resource limits.
    /// The argument is the target pid; the caller (`ProcessGroup.Adopt`) holds a live `Process` around
    /// this call, whose open OS handle pins the pid on Windows so the adopt cannot race a recycle.
    ///
    /// Unlike `Track`, the adopted process is NOT our child, so it is deliberately NOT recorded in the
    /// per-child reap ledger `Track` feeds (see K-016): we must never `waitpid` it (it would `ECHILD`)
    /// nor `killpg` its process group (which we do not own — a wrong-target kill). The container
    /// primitive alone contains and reaps it — the Windows Job's `KILL_ON_JOB_CLOSE` / a cgroup
    /// `cgroup.kill` at teardown — and the caller keeps their own `Process` as the exit-observation
    /// (wait) path. It joins `Members`/`Stats` for free because those read the live Job / `cgroup.procs`,
    /// not our tracking set.
    ///
    /// A backend whose mechanism genuinely cannot move a foreign process into its container returns
    /// `ProcessError.Unsupported` (the POSIX process group: `setpgid` only relocates our own children,
    /// and only before their `exec`) — never a silent no-op. A supported backend returns a typed
    /// `ProcessError.Adopt` on a runtime failure (a target that has already exited, missing rights, or a
    /// process already in an incompatible Job), never a fabricated success.
    abstract Adopt: int -> Result<unit, ProcessError>

    /// Stop tracking a reaped child (close its handle / drop it from the container's view).
    abstract Release: Native.Common.Spawned -> unit

    /// Wait for one contained child to conclude.
    abstract Wait: nativeint -> Task<Outcome>

    /// The pid behind a spawned child, when known.
    abstract PidOf: Native.Common.Spawned -> int option

    /// Hard-kill a single contained child (not the whole tree).
    abstract KillChild: Native.Common.Spawned -> unit

    /// Hard-kill the whole contained tree now (no grace) without releasing the container. A reusable
    /// backend may report a failure that leaves the container unsafe to reuse; final teardown remains
    /// best-effort through `HardRelease`.
    abstract KillTree: unit -> Result<unit, ProcessError>

    /// Gracefully kill the tree (configured soft signal → grace → hard kill) without releasing it.
    abstract GracefulKillTree: Signal -> TimeSpan -> Task

    /// Deliver a signal to one tracked child's own containment unit.
    abstract SignalChild: Native.Common.Spawned * Signal -> Result<unit, ProcessError>

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

    /// A point-in-time resource snapshot for each currently contained process. The backend owns the
    /// membership enumeration and platform-specific sampling so callers cannot accidentally sample a
    /// pid outside the container or turn a vanished member into a fabricated record.
    abstract MemberStats: unit -> Result<MemberStats list, ProcessError>

    /// Apply a new whole-tree resource-limit set to the LIVE container, replacing the caps in force
    /// without recreating it or restarting its children. A limit-capable mechanism (Windows Job Object
    /// / Linux cgroup v2) re-applies the caps to its live handle/controllers; a mechanism with no
    /// whole-tree limit primitive (the POSIX process group) returns `ProcessError.ResourceLimit`, the
    /// same honest, typed refusal `Create` gives — never a silent no-op. The `ResourceLimits` is a full
    /// replacement: a dimension left `None` is reset to unbounded, not left at its previous cap.
    ///
    /// The caps are applied through several sequential native writes, so a later one can fail after an
    /// earlier one landed. A limit-capable backend therefore captures the container's prior caps and
    /// best-effort restores them if a write fails partway, so an `Error` return leaves the live container
    /// on the PREVIOUS set (nothing net changed) — never a silent mix `Options.Limits` would misreport.
    /// Only if that restore itself fails is the state indeterminate, which the `ProcessError.ResourceLimit`
    /// message states explicitly.
    abstract UpdateLimits: ResourceLimits -> Result<unit, ProcessError>

    /// The hard teardown, run exactly once by the owning `ProcessGroup`: reap the tree and free the
    /// container (close the Job handle / `cgroup.kill` + rmdir / SIGKILL the pgids).
    abstract HardRelease: unit -> unit

/// Windows Job Object backend. Closing the job handle triggers `KILL_ON_JOB_CLOSE`; the tracked
/// process handles (closed on reap or teardown) are only for waiting.
type internal JobObjectBackend(jobHandle: nativeint, initialLimits: ResourceLimits) =
    let children = TrackedChildren<nativeint>()
    let mutable currentLimits = initialLimits

    // Children spawned with `Command.WindowsCtrlSignals()` (CREATE_NEW_PROCESS_GROUP), mapped by their
    // process HANDLE to their console process-group id (= pid), so `Signal.Int`/`Signal.Term` can
    // `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, groupId)` each of them. Keyed by handle, not pid, so it
    // stays in lockstep with `children`: while a child's handle is open the OS cannot recycle its pid, so
    // its stored group id cannot become a wrong target. Entries are added at `Track` and removed at exactly
    // the points the handle is closed — `Release` and `HardRelease`. That non-staleness holds only because
    // BOTH the removal AND the delivery run under the owning `ProcessGroup`'s `sync` lifecycle lock:
    // `Signal` snapshots `ctrlGroups.Values` and delivers every CTRL+BREAK while holding `sync`, and every
    // removal holds `sync` too (`HardRelease` via teardown, and the per-run shared `Release` since T-204),
    // so no entry can be dropped and its handle closed midway through a delivery — which would let a
    // CTRL+BREAK land on a recycled pid (the wrong-target class T-084 closed for POSIX kill / T-162 for the
    // Windows Job handle).
    let ctrlGroups = ConcurrentDictionary<nativeint, int>()

    new(jobHandle: nativeint) = JobObjectBackend(jobHandle, ResourceLimits.None)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.JobObject

        member _.Spawn(command) =
            Native.Windows.spawnWindows jobHandle command

        member _.Track(spawned) =
            // The child was assigned to the Job while still suspended at spawn, so it is already
            // contained — tracking its handle is only for waiting. Always succeeds.
            children.Add spawned.Handle

            if spawned.WindowsCtrlGroup then
                // Its console process-group id is its pid; the handle is still open here, so a successful
                // lookup is live and unambiguous. A failed lookup leaves the child contained but NOT
                // CTRL+BREAK-capable: registering group 0 would broadcast to the caller's console.
                match Native.Windows.processIdWindows spawned.Handle with
                | Some processId -> ctrlGroups[spawned.Handle] <- processId
                | None -> ()

            Ok()

        member _.Adopt(pid) =
            // Assign the external process into the Job. Once assigned it is a full Job member — killed by
            // KILL_ON_JOB_CLOSE at teardown, enumerated by `membersWindows`, swept by suspend/resume,
            // bound by the Job's limits — with NO per-child tracking here: it is not our child, so it has
            // no reap ledger entry and no `ctrlGroups` mapping (a foreign process was not started with
            // `Command.WindowsCtrlSignals()`, so it is not CTRL+BREAK-targetable through us).
            Native.Windows.adoptIntoJob jobHandle pid

        member _.Release(spawned) =
            // Remove the handle before closing so the teardown drain can't double-close a reused
            // handle value; the Job still contains the tree.
            if children.Remove spawned.Handle then
                ctrlGroups.TryRemove spawned.Handle |> ignore
                Native.Windows.closeWindowsHandle spawned.Handle

        member _.Wait(handle) = Native.Windows.waitWindows handle

        member _.PidOf(spawned) =
            Native.Windows.processIdWindows spawned.Handle

        member _.KillChild(spawned) =
            // `KillChild` is the interface's deliberately fire-and-forget verb (it backs
            // `RunningProcess.Kill()`, which is `unit` by design, like `Process.Kill()`), so there is no
            // result channel to report a refused terminate on. Discarding it here is NOT the fabricated
            // success this backend used to report from `SignalChild`/`Signal`/`KillTree`: a caller who needs
            // to know whether the kill landed uses `RunningProcess.Signal(Signal.Kill)` or simply awaits the
            // run — a child that refused to die keeps its exit wait pending instead of being reported dead.
            Native.Windows.terminateWindowsProcess spawned.Handle |> ignore

        member _.KillTree() =
            Native.Windows.terminateWindowsJob jobHandle

        member _.GracefulKillTree (_signal) (grace) =
            // A best-effort SOFT phase before the atomic Job kill. Windows has no per-job graceful
            // signal, but a WINDOWED child (Electron/GUI) closes gracefully on a `WM_CLOSE` posted to
            // its top-level windows — so post one to every member's windows, poll up to `grace` for the
            // tree to drain (the same shape the POSIX/cgroup backends use), then UNCONDITIONALLY force-
            // kill whatever is still alive. The hard kill is never removed or weakened: it is the
            // deterministic fallback regardless of the WM_CLOSE outcome (a child with no window, or one
            // that vetoes the close, is force-killed exactly as before). `grace = 0` skips the poll wait
            // and hard-kills at once (the WM_CLOSE post is a harmless no-op then).
            //
            // The WHOLE poll — the WM_CLOSE post, the liveness query, and the final force-kill — runs on
            // our OWN duplicate of the Job handle, never the backend's `jobHandle` (T-162). Only the
            // graceful START is serialized by the group's `sync`/`releasedFlag` lifecycle lock
            // (`ProcessGroup.gracefulKillWhenLive`); the poll loop that follows runs OFF that lock, so a
            // concurrent `DisposeAsync`/teardown can win `claimRelease` and `HardRelease` — closing
            // `jobHandle` — while this poll is still in flight (the `StopAsync` vs `Dispose` race). Polling
            // or terminating on that just-closed handle would be a use-after-close whose recycled value
            // could `TerminateJobObject` an unrelated Job. The duplicate is taken SYNCHRONOUSLY here — this
            // prefix runs under the lifecycle lock with the group still live, so `jobHandle` is guaranteed
            // open — and keeps the Job object itself alive for the bounded grace window even if the backend
            // closes its handle underneath us; it is closed when the poll concludes, at which point
            // `KILL_ON_JOB_CLOSE` is the final backstop. Mirrors how `waitWindows` waits on its own
            // duplicate of a child's process handle. If duplication ever fails (near-impossible under the
            // lock with a valid handle), fall back to an immediate hard kill on the still-valid `jobHandle`
            // rather than poll a handle we cannot protect — the unconditional kill-on-drop guarantee holds
            // either way.
            //
            // Both hard kills below discard the terminate's `Result`, and that is a different thing from
            // the fabricated `Ok()` this task removed from `KillTree`/`Signal`/`SignalChild`: the graceful
            // teardown's contract is `Task`, so it makes no success CLAIM to falsify. What it must not do is
            // react to a refusal by waiting longer, and it does not — `GracefulTeardown.poll` is bounded by
            // `grace` and force-kills at most once at the end, so a refused terminate returns immediately
            // instead of spinning on a tree that will not die. The observable consequence of the refusal
            // reaches the caller through the exit wait it is already awaiting (`StopAsync`/`ShutdownAsync`
            // resolve on the child's real conclusion), never as a fake "the tree is gone".
            match Native.Windows.duplicateJobHandle jobHandle with
            | None ->
                Native.Windows.terminateWindowsJob jobHandle |> ignore
                Task.CompletedTask
            | Some ownedJob ->
                task {
                    try
                        do!
                            GracefulTeardown.poll
                                (fun () -> Native.Windows.postCloseToJobWindows ownedJob |> ignore)
                                (fun () -> Native.Windows.jobTreeAliveWindows ownedJob)
                                (fun () -> Native.Windows.terminateWindowsJob ownedJob |> ignore)
                                grace
                    finally
                        Native.Windows.closeWindowsHandle ownedJob
                }
                :> Task

        member _.SignalChild(spawned, signal) =
            match signal with
            | Signal.Kill ->
                // The honest outcome of the native terminate: a still-live child that refused to die is a
                // `ProcessError.Io`, while one that had already exited stays an idempotent `Ok` (see
                // `terminateWindowsProcess`, which classifies through this very handle).
                Native.Windows.terminateWindowsProcess spawned.Handle
            | Signal.Int
            | Signal.Term ->
                let ctrlDelivered =
                    match ctrlGroups.TryGetValue spawned.Handle with
                    | true, groupId -> Native.Windows.sendConsoleCtrlBreakWindows groupId |> Result.isOk
                    | false, _ -> false

                let windowsClosed =
                    match Native.Windows.processIdWindows spawned.Handle with
                    | Some pid -> Native.Windows.postCloseToProcessWindows pid
                    | None -> 0

                if ctrlDelivered || windowsClosed > 0 then
                    Ok()
                else
                    Error(
                        ProcessError.Unsupported
                            $"{signal} on this Windows run needs Command.WindowsCtrlSignals() with a shared console or a top-level window that can receive WM_CLOSE"
                    )
            | _ -> Error(ProcessError.Unsupported $"signal {signal} has no Windows per-run mapping")

        member _.Members() =
            // `membersWindows` already returns a `Result` — it grows the buffer to the whole job and
            // surfaces a genuine query failure as `ProcessError.Io` rather than a fabricated empty list.
            Native.Windows.membersWindows jobHandle

        member _.Signal(signal) =
            match signal with
            | Signal.Kill ->
                // Same honesty as `KillTree` (this IS the whole-tree kill, reached through the signal
                // vocabulary): a refused `TerminateJobObject` on a Job that still holds live members is a
                // `ProcessError.Io`, an already-drained Job stays an idempotent `Ok`.
                Native.Windows.terminateWindowsJob jobHandle
            | Signal.Int
            | Signal.Term ->
                // Best-effort SOFT stop combining TWO complementary, individually-targeted deliveries so
                // the caller's own console/windows are never touched:
                //   1. a console CTRL+BREAK to every child started with `Command.WindowsCtrlSignals()`,
                //      targeting each child's OWN process group id (its pid) — CTRL+BREAK, not CTRL+C,
                //      because CREATE_NEW_PROCESS_GROUP disables the child's CTRL+C and only CTRL+BREAK
                //      can be group-targeted; reaches CONSOLE children;
                //   2. a WM_CLOSE posted to the top-level windows of every member (targeted by pid via
                //      GetWindowThreadProcessId, so no foreign window is hit) — reaches WINDOWED children
                //      (Electron/GUI tools), which have no console to CTRL+BREAK.
                // `Signal.Int`/`Signal.Term` both map to this soft stop — the closest Windows analogue.
                let groups = ctrlGroups.Values |> List.ofSeq
                // Count the CTRL+BREAKs actually generated vs. those that genuinely failed (e.g. the
                // caller has no console to share), so success/failure below reflects real delivery.
                let mutable ctrlDelivered = 0
                let mutable ctrlFailure: string option = None

                for groupId in groups do
                    match Native.Windows.sendConsoleCtrlBreakWindows groupId with
                    | Ok() -> ctrlDelivered <- ctrlDelivered + 1
                    | Error message ->
                        if ctrlFailure.IsNone then
                            ctrlFailure <- Some message

                // WM_CLOSE to every windowed member; the count is how many top-level windows were posted
                // to (0 = no member has a window — a no-op, not an error).
                let windowsClosed = Native.Windows.postCloseToJobWindows jobHandle

                if ctrlDelivered > 0 || windowsClosed > 0 then
                    // At least one soft signal was delivered best-effort (a CTRL+BREAK generated and/or a
                    // WM_CLOSE posted to a window). Success is delivery, not the child's compliance — a
                    // child may install its own handler or veto the close.
                    Ok()
                elif List.isEmpty groups then
                    // No CTRL-capable child AND no windowed member: the group truly has nothing to receive
                    // a soft signal — honest Unsupported, never a silent downgrade to the Job kill. (This
                    // is the preserved pre-WM_CLOSE Unsupported case, now also requiring "no windows".)
                    Error(
                        ProcessError.Unsupported
                            $"{signal} on Windows is deliverable only as a console CTRL+BREAK to a child started with Command.WindowsCtrlSignals() (CREATE_NEW_PROCESS_GROUP) or as a WM_CLOSE to a member with a top-level window; this group has neither"
                    )
                else
                    // There ARE CTRL-capable children but every CTRL+BREAK genuinely failed, and no member
                    // has a window to absorb a WM_CLOSE either — nothing was delivered. Honest failure
                    // rather than a false Ok.
                    let detail = ctrlFailure |> Option.defaultValue "unknown error"

                    Error(
                        ProcessError.Unsupported
                            $"{signal} on Windows could not be delivered as a console CTRL+BREAK (GenerateConsoleCtrlEvent failed: {detail}) and no member has a top-level window to receive a WM_CLOSE"
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
            | Some(active, cpu, peak, io) -> Ok(ProcessGroupStats(active, Some cpu, Some peak, Some io))
            | None -> Error(ProcessError.Io "failed to query Job Object accounting")

        member _.MemberStats() =
            Native.Windows.readMemberStats jobHandle

        member _.UpdateLimits(limits) =
            // Re-apply the whole limit set to the live Job via `SetInformationJobObject` (the UI
            // restrictions first, then the memory / active-process caps AND the CPU-affinity mask together
            // in one extended-limit write, then the CPU rate cap) — the same call `Create` uses, which
            // cleanly REPLACES the caps in force: a dimension now `None` is written back as unbounded
            // rather than left at its previous cap. This runs synchronously under the group's lifecycle
            // lock (via `ProcessGroup.WhenLive`), so it works on the still-open `jobHandle` exactly like
            // `Suspend`/`Resume`/`Stats`/`Members` — no handle duplication is needed here, unlike the
            // OFF-lock graceful poll (K-025/T-162). `applyWindowsJobLimits` best-effort restores the prior
            // extended-limit block if the CPU-rate write fails after it, so a genuine apply failure is an
            // honest `ProcessError.ResourceLimit` AND the live Job is back on the previous set (T-207).
            if limits.OomGroupKill then
                Error(
                    ProcessError.Unsupported
                        "whole-tree OOM kill is a Linux cgroup v2 memory.oom.group policy; Windows Job Objects have no equivalent"
                )
            else
                let result =
                    Native.Windows.applyWindowsJobLimitsWithPrevious jobHandle currentLimits limits

                match result with
                | Ok() ->
                    currentLimits <- limits
                    Ok()
                | Error message when
                    (currentLimits.IoMax.IsSome || limits.IoMax.IsSome)
                    && Native.Windows.isIoRateControlUnsupported message
                    ->
                    Error(ProcessError.Unsupported message)
                | Error message -> Error(ProcessError.ResourceLimit message)

        member _.HardRelease() =
            ctrlGroups.Clear()

            for handle in children.Drain() do
                Native.Windows.closeWindowsHandle handle

            Native.Windows.closeWindowsHandle jobHandle

/// The identity-safe cgroup member sampling pipeline. The production backend supplies the kernel's
/// membership reads; tests can supply point-in-time membership and a deterministic reread without
/// pretending that synthetic pids exist in a real cgroup.
module internal CgroupMemberStats =

    let sample
        (pids: int list)
        (trackedIdentities: IReadOnlyDictionary<int, uint64 option>)
        (adoptedIdentities: IReadOnlyDictionary<int, uint64 option>)
        (currentMembership: unit -> Result<int list, string>)
        : Result<MemberStats list, string> =
        let snapshot =
            pids
            |> List.choose (fun pid ->
                // Tracked and adopted leaders have a pinned identity. Descendants and other
                // externally-created members capture theirs immediately before sampling.
                match trackedIdentities.TryGetValue pid with
                | true, Some identity -> Some(pid, identity)
                | true, None -> None
                | false, _ ->
                    match adoptedIdentities.TryGetValue pid with
                    | true, Some identity -> Some(pid, identity)
                    | true, None -> None
                    | false, _ ->
                        Native.Posix.readProcessIdentity pid
                        |> Option.map (fun identity -> pid, identity))

        let sampled =
            snapshot
            |> List.choose (fun (pid, identity) -> Native.Posix.readMemberStatsWithIdentity pid (Some identity))

        match currentMembership () with
        | Error message -> Error message
        | Ok current ->
            let currentPids = Set.ofList current
            Ok(sampled |> List.filter (fun stats -> currentPids.Contains stats.Pid))

/// Linux cgroup v2 backend (the `limits` mechanism). Membership lives in `cgroup.procs`; the tree is
/// reaped with `cgroup.kill` and the directory removed.
type internal CgroupBackend(cgroupPath: string, initialLimits: ResourceLimits) =
    let children = TrackedChildren<int>()
    let mutable currentLimits = initialLimits

    // The start-time identity token captured for each tracked leader pid at `Track` (see
    // `ProcessGroupBackend`), kept in a parallel dictionary keyed alongside `children`. The cgroup's own
    // per-member signal path is already pid-reuse-safe via pidfd (`Native.Cgroup.deliverIdentitySafe`);
    // this token gates the ONLY remaining by-number `killpg`, the shared `PosixReap.leader` escapee/
    // teardown reap, so it too can never SIGKILL a pid recycled since it was tracked. `None` when
    // unreadable, degrading that reap to the by-number kill exactly as before.
    let identities = ConcurrentDictionary<int, uint64 option>()

    // Adoption is whole-cgroup containment, but adopted processes are not our children and therefore do
    // not belong in `children` or its wait/reap ledger. Keep their start-time tokens separately so their
    // per-member stats remain identity-safe after an exit/reuse, while descendants and other cgroup
    // members are resolved from the point-in-time cgroup membership snapshot below.
    let adoptedIdentities = ConcurrentDictionary<int, uint64 option>()

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

    new(cgroupPath: string) = CgroupBackend(cgroupPath, ResourceLimits.None)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.CgroupV2

        member _.Spawn(command) =
            // Spawn through the self-migrating cgroup launcher (a tiny `/bin/sh` that writes its own pid
            // into this cgroup's cgroup.procs, then `exec`s the real program in place), so the target's
            // pid is already a cgroup member before it runs a single instruction — closing the old
            // spawn->migrate window where a descendant forked in that first instant could escape the
            // limits. See `Native.Posix.spawnPosixIntoCgroup`. `Spawned.Handle` is the launcher pid,
            // which becomes the target's pid unchanged across `exec`.
            let effective =
                match currentLimits.CpuTimeMax with
                | Some duration -> Native.Posix.withCpuTimeLimit duration command
                | None -> Ok command

            effective
            |> Result.bind (fun wrapped ->
                Native.Posix.spawnPosixIntoCgroup wrapped (System.IO.Path.Combine(cgroupPath, "cgroup.procs")))

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

        member _.Adopt(pid) =
            // Write the foreign pid into cgroup.procs. It then joins the cgroup for every whole-tree op —
            // cgroup.kill at teardown, cgroup.freeze, the per-member signal sweep, the resource limits —
            // and shows up in `Members`/`Stats` automatically because those read cgroup.procs (the kernel's
            // own membership view), not our `children` set. Crucially it is NOT added to `children`: that
            // ledger exists so teardown can `waitpid`/`killpg` OUR OWN spawned leaders, and an adopted
            // process is neither our child (a `waitpid` would `ECHILD`) nor the leader of a pgid we own (a
            // `killpg` would be a wrong-target kill) — see K-016. cgroup.kill alone SIGKILLs it at teardown;
            // its real parent/init reaps it.
            match Native.Cgroup.adoptIntoCgroup cgroupPath pid with
            | Ok() ->
                adoptedIdentities[pid] <- Native.Posix.readProcessIdentity pid
                Ok()
            | Error detail -> Error(ProcessError.Adopt(pid, detail))

        member _.Release(spawned) =
            // A run verb has reaped this child; stop tracking so teardown does not waitpid it again.
            // (The kernel already removed it from cgroup.procs.)
            let pid = int spawned.Handle
            identities.TryRemove pid |> ignore
            adoptedIdentities.TryRemove pid |> ignore
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

        member _.KillTree() =
            Native.Cgroup.killCgroup cgroupPath
            |> Result.mapError (fun message -> ProcessError.Io $"failed to kill cgroup: {message}")

        member _.GracefulKillTree (signal) (grace) =
            let signalNum = Native.Posix.signalNumber signal

            GracefulTeardown.poll
                (fun () -> Native.Cgroup.signalCgroup cgroupPath signalNum |> ignore)
                (fun () -> Native.Cgroup.cgroupAlive cgroupPath)
                (fun () -> Native.Cgroup.killCgroup cgroupPath |> ignore)
                grace

        member _.SignalChild(spawned, signal) =
            let pid = int spawned.Handle
            let signalNum = Native.Posix.signalNumber signal

            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() when not (stillOurs pid) -> Ok()
            | Ok() ->
                match Native.Posix.signalProcessGroup pid signalNum with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> Ok()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                    Error(
                        ProcessError.Io $"failed to deliver signal {signalNum} to this run: {message} (errno {errno})"
                    )

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
                Native.Cgroup.killCgroup cgroupPath
                |> Result.mapError (fun message -> ProcessError.Io $"failed to kill cgroup: {message}")
            | _ ->
                let signalNum = Native.Posix.signalNumber signal

                // Refuse a non-deliverable number (signal 0 — a liveness probe — or a negative) at the API
                // boundary, before the identity-safe pidfd broadcast, so it can never look like a delivered
                // signal. This also covers an empty cgroup, where the per-member broadcast would otherwise
                // signal nobody and report a vacuous success.
                match Native.Posix.ensureDeliverable signalNum with
                | Error error -> Error error
                | Ok() ->
                    match Native.Cgroup.signalCgroup cgroupPath signalNum with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> Ok()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        Error(
                            ProcessError.Io $"failed to deliver signal {signalNum} to cgroup: {message} (errno {errno})"
                        )

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
                let cpu, peak, io = Native.Cgroup.cgroupStats cgroupPath
                Ok(ProcessGroupStats(active, cpu, peak, io))

        member _.MemberStats() =
            // The cgroup membership read is the authoritative point-in-time list. A second membership
            // read after per-pid sampling removes a recycled pid that became a foreign process while the
            // `/proc` files were being read; a failure is propagated rather than treated as an empty set.
            match Native.Cgroup.cgroupMembers cgroupPath with
            | Error message ->
                Error(
                    ProcessError.Io $"could not read cgroup.procs for per-member stats (membership unknown): {message}"
                )
            | Ok pids ->
                match
                    CgroupMemberStats.sample
                        pids
                        (identities :> IReadOnlyDictionary<int, uint64 option>)
                        (adoptedIdentities :> IReadOnlyDictionary<int, uint64 option>)
                        (fun () -> Native.Cgroup.cgroupMembers cgroupPath)
                with
                | Error message ->
                    Error(
                        ProcessError.Io
                            $"could not re-read cgroup.procs after per-member stats (membership unknown): {message}"
                    )
                | Ok sampled -> Ok sampled

        member _.UpdateLimits(limits) =
            // Rewrite the cgroup's controller files in place (`memory.max`/`memory.oom.group`/`pids.max`/`cpu.max`/
            // `cpuset.cpus`), enabling any controller the new caps newly need in the parent's
            // `cgroup.subtree_control` first (`cpuset` is the one a hierarchy most often lacks entirely,
            // and its absence fails the update before any file is written). REPLACE semantics: a dimension
            // now `None` is reset to that controller file's OWN "unbounded" sentinel — `max` for
            // `memory.max`/`pids.max`/`cpu.max`, but a blank line (written as `"\n"`) for `cpuset.cpus`,
            // which does not accept `max` at all and whose value would silently survive a zero-length
            // write. The sentinel is per-file and never inherited: a dimension added to this plan must
            // bring its own. Either way the reset only happens where that controller file already exists
            // (a never-enabled controller is already unbounded), never left at its previous cap. Runs under
            // the group's lifecycle lock (via `ProcessGroup.WhenLive`), so the cgroup directory can't be
            // removed by a concurrent teardown mid-write. `updateCgroupLimits` best-effort restores any
            // controller file it already rewrote if a later write fails, so a genuine write/delegation
            // failure is an honest `ProcessError.ResourceLimit` AND the live cgroup is back on the previous
            // set (T-207).
            //
            // Job Object UI restrictions have no cgroup counterpart to rewrite, so an update carrying any
            // is refused up front with the same `Unsupported` `ProcessGroup.Create` gives here — the caps
            // in force are left exactly as they were, rather than a partial apply that silently dropped
            // the restriction half of the requested set.
            if
                limits.IoMax.IsSome
                && Native.Cgroup.cgroupV2Available ()
                && not (Native.Cgroup.cgroupIoAvailable ())
            then
                Error(
                    ProcessError.Unsupported
                        "the Linux cgroup v2 hierarchy does not expose the io controller required by io.max"
                )
            elif limits.CpuTimeMax <> currentLimits.CpuTimeMax then
                Error(
                    ProcessError.ResourceLimit
                        "CPU-time is a per-child POSIX rlimit and cannot be changed for processes already running in this cgroup; create a new group for a different CpuTimeMax"
                )
            else
                match limits.UiRestrictionsUnsupported with
                | Some error -> Error error
                | None ->
                    match Native.Cgroup.updateCgroupLimitsWithPrevious cgroupPath currentLimits limits with
                    | Ok() ->
                        currentLimits <- limits
                        Ok()
                    | Error message -> Error(ProcessError.ResourceLimit message)

        member _.HardRelease() =
            // Final disposal must remain bounded and best-effort: it removes the cgroup even when the
            // reusable kill reported an error — an unverified thaw of the legacy freezer, or a sweep
            // that left the cgroup populated (including a kernel without pidfd, which kills nothing
            // rather than downgrading to a racy raw kill).
            Native.Cgroup.killCgroup cgroupPath |> ignore

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

            adoptedIdentities.Clear()
            Native.Cgroup.removeCgroup cgroupPath

/// POSIX process-group backend (macOS/BSD, or Linux without cgroup delegation). Every `posix_spawn`
/// forms its own pgid, so a multi-child group holds several; `killpg` is the teardown.
type internal ProcessGroupBackend(initialLimits: ResourceLimits) =
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

    // Broadcast to every tracked pgid that is still ours; a recycled pgid is pruned instead, so a
    // control operation can never target an unrelated process group. Continue after failures to give
    // every remaining group a chance to receive the operation, then report the first delivery failure.
    let sweep
        (deliver: int -> Native.Common.SignalDelivery)
        (describeFailure: int -> string -> string)
        : Result<unit, ProcessError> =
        let mutable firstFailure: (int * string) option = None

        for pgid in children.Snapshot() do
            if stillOurs pgid then
                match deliver pgid with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> ()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                    if firstFailure.IsNone then
                        firstFailure <- Some(errno, message)
            else
                untrack pgid |> ignore

        match firstFailure with
        | None -> Ok()
        | Some(errno, message) -> Error(ProcessError.Io(describeFailure errno message))

    new() = ProcessGroupBackend(ResourceLimits.None)

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(command) =
            match initialLimits.CpuTimeMax with
            | Some duration ->
                Native.Posix.withCpuTimeLimit duration command
                |> Result.bind Native.Posix.spawnPosix
            | None -> Native.Posix.spawnPosix command

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

        member _.Adopt(_pid) =
            // The POSIX process-group mechanism CANNOT move a foreign process into our group: `setpgid`
            // only changes the process group of one of OUR OWN children, and only before it `exec`s. There
            // is no kernel primitive to relocate an unrelated, already-`exec`ed process into another
            // process group. Refuse honestly and typed — never a silent no-op that would pretend the
            // process is contained when the kill-on-dispose guarantee could not actually reach it. (On
            // Linux, adoption needs a cgroup v2-backed group — one created WITH resource limits; a plain,
            // limit-free group falls back to this mechanism and cannot adopt.)
            Error(
                ProcessError.Unsupported
                    "adopting an external process into a POSIX process group is not possible (setpgid only relocates our own children, and only before exec); adoption needs a Windows Job Object or a Linux cgroup v2 group (created with resource limits)"
            )

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

            Ok()

        member _.GracefulKillTree (signal) (grace) =
            // Snapshot the pgids and their identity tokens together. The poll runs off the lifecycle lock,
            // so concurrent HardRelease may remove live entries from `identities`; all three poll phases
            // must keep using the token captured while this graceful shutdown owned the pgid snapshot.
            let pgids = children.Snapshot()

            let identitySnapshot =
                pgids
                |> List.map (fun pgid ->
                    pgid,
                    match identities.TryGetValue pgid with
                    | true, token -> token
                    | false, _ -> None)
                |> Map.ofList

            let stillOursSnap (pgid: int) : bool =
                match Map.tryFind pgid identitySnapshot with
                | Some token -> Native.Posix.processGroupStillTracked pgid token
                | None -> false

            let anyChildAliveSnap () = pgids |> List.exists stillOursSnap

            GracefulTeardown.poll
                (fun () ->
                    for pgid in pgids do
                        if stillOursSnap pgid then
                            Native.Posix.signalProcessGroup pgid (Native.Posix.signalNumber signal)
                            |> ignore)
                anyChildAliveSnap
                (fun () ->
                    for pgid in pgids do
                        if stillOursSnap pgid then
                            Native.Posix.killProcessGroup pgid)
                grace

        member _.SignalChild(spawned, signal) =
            let pgid = int spawned.Handle
            let signalNum = Native.Posix.signalNumber signal

            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() when not (stillOurs pgid) -> Ok()
            | Ok() ->
                match Native.Posix.signalProcessGroup pgid signalNum with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> Ok()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                    Error(
                        ProcessError.Io $"failed to deliver signal {signalNum} to this run: {message} (errno {errno})"
                    )

        member _.Members() =
            // Report only the pgids still ours and alive (choke-gated): a drained or recycled pgid is not
            // a member of this group. This is a read — it does not prune (a mutating op does that).
            Ok(children.Snapshot() |> List.filter stillOurs)

        member _.Signal(signal) =
            let signalNum = Native.Posix.signalNumber signal

            // Refuse a non-deliverable number (signal 0 — a liveness probe — or a negative) at the API
            // boundary, before the delivery loop, so it can never look like a delivered signal. This also
            // covers a group whose pgids have all drained/recycled, where the loop would otherwise signal
            // nobody and report a vacuous success.
            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                sweep (fun pgid -> Native.Posix.signalProcessGroup pgid signalNum) (fun errno message ->
                    $"failed to deliver signal {signalNum} to process group: {message} (errno {errno})")

        member _.Suspend() =
            sweep Native.Posix.suspendProcessGroup (fun errno message ->
                $"failed to suspend process group: {message} (errno {errno})")

        member _.Resume() =
            sweep Native.Posix.resumeProcessGroup (fun errno message ->
                $"failed to resume process group: {message} (errno {errno})")

        member _.Stats() =
            let active = children.Snapshot() |> List.filter stillOurs |> List.length
            Ok(ProcessGroupStats(active, None, None, None))

        member _.MemberStats() =
            let pids = children.Snapshot() |> List.filter stillOurs

            pids
            |> List.choose (fun pid ->
                let identity =
                    match identities.TryGetValue pid with
                    | true, token -> token
                    | false, _ -> None

                Native.Posix.readMemberStatsWithIdentity pid identity)
            |> Ok

        member _.UpdateLimits(limits) =
            // The POSIX process-group mechanism has no whole-tree limit primitive to update — the exact
            // reason `ProcessGroup.Create` already refuses to build a limited group over it. Refuse the
            // update the same honest, typed way rather than pretending to have applied caps that no
            // kernel container is enforcing (a silent no-op would be a false success). A requested Job
            // Object UI restriction is refused with `Unsupported` rather than `ResourceLimit`, matching
            // `Create`: a resource cap exists as a concept here and merely cannot be enforced, while a
            // clipboard/desktop restriction has no POSIX counterpart at all.
            if limits.CpuTimeMax <> initialLimits.CpuTimeMax then
                Error(
                    ProcessError.ResourceLimit
                        "CPU-time is applied with RLIMIT_CPU before each child exec and cannot be changed for processes already running; create a new group for a different CpuTimeMax"
                )
            elif limits.OomGroupKill then
                Error(
                    ProcessError.Unsupported "whole-tree OOM kill requires a Linux cgroup v2 memory.oom.group mechanism"
                )
            elif limits.IoMax.IsSome then
                Error(
                    ProcessError.Unsupported
                        "whole-tree disk I/O rate limits require Linux cgroup v2 io.max or a Windows Job Object I/O rate controller"
                )
            elif not limits.WholeTreeAny then
                Ok()
            else
                match limits.UiRestrictionsUnsupported with
                | Some error -> Error error
                | None ->
                    Error(
                        ProcessError.ResourceLimit
                            "the POSIX process-group mechanism has no whole-tree resource-limit primitive to update (needs a Windows Job Object or Linux cgroup v2)"
                    )

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
