namespace ProcessKit

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// The observed fate of one containment backend's best-effort graceful "soft signal" attempt — the
/// shared, backend-agnostic core behind the public `SoftSignalDelivery` (which additionally carries the
/// `Signal` attempted; that lives in `ProcessGroup.ShutdownReportAsync`, the one place that knows the
/// group's configured `Options.StopSignal`). Mirrors ProcessKit-rs's `sys::graceful::SoftDelivery`.
type internal SoftDelivery =

    /// The soft signal was delivered best-effort to the tree (a POSIX signal on Unix; at least one
    /// `WM_CLOSE` posted on the Windows soft tier).
    | Sent

    /// This platform/target has no soft-signal tier for the group, so the teardown could only hard-kill: a
    /// windowless Windows Job Object with no live windowed member. Every Unix backend always has a real
    /// `SIGTERM` tier, so this is never produced there.
    | Unsupported

    /// A soft-signal tier exists but the best-effort delivery failed for every target this teardown could
    /// still reach (a uid-changed member rejecting the signal on Unix). Exact ("every") on the POSIX
    /// process-group mechanism; on Linux cgroup v2 the broadcast primitive reports its first genuine
    /// per-member failure even when other members received the signal, so there this narrows to "at least
    /// one member's delivery failed" — see `SoftSignalDelivery.Failed`'s own doc for the full per-mechanism
    /// caveat this maps onto.
    | Failed

/// The observed facts of one whole-tree graceful teardown, gathered by a single `IContainmentBackend`
/// call to `GracefulKillTree`. The always-available, feature-agnostic core the public `ShutdownReport` is
/// built from (`ProcessGroup.ShutdownReportAsync` adds the member counts, before/after, and the elapsed
/// time — see that method's own comment for why those are measured at the caller rather than here).
/// Mirrors ProcessKit-rs's `sys::graceful::GracefulOutcome`.
type internal GracefulOutcome =
    {
        /// The fate of the best-effort soft-signal tier.
        Soft: SoftDelivery

        /// Whether the tree drained within the grace window, before any hard kill.
        Drained: bool

        /// Whether the driver escalated to a hard kill because the tree had not drained by the deadline.
        Escalated: bool
    }

/// Shared graceful-teardown shape for all containment backends: request the platform/configured soft
/// stop, poll until the tree is dead or `grace` elapses, then force-kill whatever remains.
module internal GracefulTeardown =

    let internal pollUsing
        (startClock: unit -> (unit -> TimeSpan))
        (delay: TimeSpan -> Task)
        (softSignal: unit -> SoftDelivery)
        (alive: unit -> bool)
        (forceKill: unit -> unit)
        (grace: TimeSpan)
        : Task<GracefulOutcome> =
        task {
            let soft = softSignal ()
            let elapsed = startClock ()
            let deadline = grace
            let pollInterval = TimeSpan.FromMilliseconds 50.0

            while alive () && elapsed () < deadline do
                let remaining = deadline - elapsed ()
                let boundedDelay = min pollInterval remaining

                let armableDelay =
                    if Timeouts.isArmable boundedDelay then
                        boundedDelay
                    else
                        Timeouts.clampArmable boundedDelay

                do! delay armableDelay

            let stillAlive = alive ()

            if stillAlive then
                forceKill ()

            return
                { Soft = soft
                  Drained = not stillAlive
                  Escalated = stillAlive }
        }

    let poll
        (softSignal: unit -> SoftDelivery)
        (alive: unit -> bool)
        (forceKill: unit -> unit)
        (grace: TimeSpan)
        : Task<GracefulOutcome> =
        pollUsing
            (fun () ->
                let stopwatch = Stopwatch.StartNew()
                fun () -> stopwatch.Elapsed)
            (fun duration -> Task.Delay duration)
            softSignal
            alive
            forceKill
            grace

/// Small helpers over the shared POSIX liveness + identity verdict (`Native.Posix.TrackedTarget`) that
/// both POSIX backends gate on, so the mapping from a verdict to "prune this record" is written once.
module internal TrackedTargets =

    /// Whether the verdict means nothing of ours is at that number any more — the ONE case that prunes a
    /// tracked record. `LeaderPid` is deliberately NOT gone: it is a live child of ours whose process
    /// group does not exist yet (a pty leader before its `setsid()`), reachable by pid.
    let isGone (target: Native.Posix.TrackedTarget) : bool =
        match target with
        | Native.Posix.TrackedTarget.Gone -> true
        | Native.Posix.TrackedTarget.Group
        | Native.Posix.TrackedTarget.LeaderPid -> false

/// Kill and reap a single POSIX leader we `posix_spawn`ed, in this exact order: `killpg` SIGKILLs its
/// whole process group (any subtree it backgrounded), then `waitpid` reaps the leader itself (`killpg`
/// does not reap our own child). Shared by the cgroup and process-group backends' escapee/teardown
/// cleanup so the kill-then-reap pairing lives in one place.
///
/// Every reap here is BOUNDED, per leader and for the drain as a whole, and never drops ownership: a
/// leader still alive when its bounded window ends is handed to the `PostKillReap` ledger, which keeps
/// the single eventual wait/reap for it (see `leaderUsing`).
module internal PosixReap =

    /// The shape of one leader's teardown, with its native dependencies injected so the bounded/handoff
    /// decisions can be exercised without wedging a real child (the `GracefulTeardown.pollUsing`
    /// pattern). `reapNow` is false once the drain's shared budget is spent: the kill is still
    /// delivered and the leader is still PROBED (one non-blocking `waitpid`, see `reapLeaderOnce`), but
    /// no window is spent waiting for it — the probe's verdict is what the handoff below needs.
    ///
    /// The order and the gates: a leader the ledger PROVABLY already owns (its captured identity
    /// matches) is left entirely alone — re-killing or re-waiting it would be exactly the second owner
    /// this contract exists to prevent (and, after the pid is reaped, a wrong-target kill). Otherwise
    /// the kill is gated AND ROUTED by the shared liveness + identity choke, so a pgid recycled since it
    /// was tracked is NEVER SIGKILLed (the wrong-target kill T-084 closes): `identity` is the start-time
    /// token captured at track time, and a live number whose current identity differs from it is a
    /// recycled stranger. A matching or unknown token falls back to the by-number kill exactly as before
    /// (a leader reaped while descendants keep the pgid alive is still cleaned up), and a leader whose
    /// GROUP does not exist yet — a pty child between `posix_spawn` and its helper's `setsid()` — is
    /// SIGKILLed through the exact-leader route (`TrackedTarget.LeaderPid`: the pid itself, then a
    /// `killpg` sweep behind it, in case the child won the race to `setsid()` since the probe) instead of
    /// being skipped as gone, which is what used to strand a just-spawned pty child on teardown. This is
    /// the LAST chance to reach that subtree — the drain erases the tracking record straight afterwards
    /// — which is why the kill here reaches both halves rather than the leader alone. The bounded reap's
    /// `waitpid` only ever reaps our OWN child, so a recycled pid there is a harmless `ECHILD` — it needs
    /// no gate.
    ///
    /// Routing the kill changes nothing about the REAP: there is still exactly one `reap` call per
    /// leader here and no second `waitpid` path (K-016). Which primitive delivered the SIGKILL is not
    /// evidence about who owns the wait — only the reap verdict below is.
    ///
    /// The handoff, however, is decided by the REAP's verdict and never by the choke. The choke answers
    /// "is that process group still ours?", and a group stays alive while any member does — including
    /// right after its leader was reaped by the very call above (`ProcessGroupBackend.Release`
    /// deliberately keeps a pgid tracked until the group empties, so a run that backgrounded a
    /// descendant arrives here in exactly that state). Handing such a pid to a waiter would open a wait
    /// on an ALREADY REAPED child: `ECHILD`, a fabricated `Unobserved`, and a blocking `waitpid` that a
    /// recycled number could make land on an unrelated child of ours (K-016). So only `StillRunning` —
    /// positive proof from `waitpid` that this is still our live, unreaped child, which no recycled
    /// number can fake — transfers the eventual wait/reap to the ledger, turning the old "left for the
    /// host to reap at exit" case into a reap we still observe exactly once.
    let leaderUsing
        (target: int -> uint64 option -> Native.Posix.TrackedTarget)
        (owned: int -> uint64 option -> bool)
        (killTarget: Native.Posix.TrackedTarget -> int -> unit)
        (reap: bool -> int -> LeaderReap)
        (adopt: int -> uint64 option -> unit)
        (reapNow: bool)
        (id: int)
        (identity: uint64 option)
        : unit =
        if not (owned id identity) then
            match target id identity with
            | Native.Posix.TrackedTarget.Gone ->
                // Gone by BOTH the group probe and the exact-pid probe, or positively recycled: there is
                // nothing of ours left to kill, and killing the number anyway is the wrong-target kill.
                ()
            | routed -> killTarget routed id

            match reap reapNow id with
            | LeaderReap.StillRunning -> adopt id identity
            | LeaderReap.Reaped
            | LeaderReap.Gone ->
                // Reaped here, or not ours to reap any more: in both cases this pid's single wait is
                // over or belongs to someone else, and opening another one is the K-016 double-wait.
                ()

    // Transfer the eventual wait/reap of `pid` to the ledger. `waitPosix` is deliberately the adopted
    // wait: it JOINS the one shared reap group for that pid rather than opening a second pidfd or
    // racing a second `waitpid` (K-016), so the handoff can never produce two reapers — and if a run
    // verb is already waiting on the same pid, that existing wait IS the group and simply keeps it.
    let private adoptLeader (pid: int) (identity: uint64 option) =
        PostKillReap.adoptLeader pid identity (fun target -> Native.Posix.waitPosix (nativeint target))
        |> ignore

    // The teardown reap on the real `waitpid`: its full ~200 ms window while the drain's budget lasts,
    // and a single non-blocking probe once it is spent — the same verdict either way.
    let private reapWithin (reapNow: bool) (id: int) : LeaderReap =
        if reapNow then
            Native.Posix.reapLeader id
        else
            Native.Posix.reapLeaderOnce id

    // One leader through the production gates. `reapNow` is threaded from the drain's budget.
    let private leaderWithin (reapNow: bool) (id: int) (identity: uint64 option) =
        leaderUsing
            Native.Posix.trackedTarget
            PostKillReap.ownsLeader
            Native.Posix.killTracked
            reapWithin
            adoptLeader
            reapNow
            id
            identity

    /// Kill and reap one leader now (the per-child escapee cleanup path).
    let leader (id: int) (identity: uint64 option) = leaderWithin true id identity

    /// Tear down a whole drained tracking set under ONE shared budget, with the clock and the per-leader
    /// step injected. Each leader's own reap is already bounded, but a group holding many of them would
    /// still add those windows up on the disposing thread; once `budget` is spent the remaining leaders
    /// are killed, probed once without waiting, and handed to the ledger if that probe finds them still
    /// running — so teardown stays bounded for the WHOLE group. The kill itself is never skipped or
    /// weakened, and neither is the answer about who owns the remaining wait: only the WAITING is.
    let drainUsing
        (startClock: unit -> (unit -> TimeSpan))
        (reapOne: bool -> int -> uint64 option -> unit)
        (budget: TimeSpan)
        (leaders: (int * uint64 option) list)
        : unit =
        let elapsed = startClock ()

        for pid, identity in leaders do
            reapOne (elapsed () < budget) pid identity

    /// `drainUsing` on the real clock, the production gates, and the configured post-kill budget.
    let drain (leaders: (int * uint64 option) list) =
        drainUsing
            (fun () ->
                let stopwatch = Stopwatch.StartNew()
                fun () -> stopwatch.Elapsed)
            leaderWithin
            (PostKillReap.budget ())
            leaders

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

    /// Place an already-running external process into the container from a **bare pid** — no `Process`,
    /// no handle, nothing but the number (`ProcessGroup.AdoptByPid`). Same ownership contract as `Adopt`
    /// (never reaped here, never `killpg`ed, listed and signalled with the group), and a different
    /// IDENTITY contract, which is the whole reason it is its own member rather than a second caller of
    /// `Adopt`.
    ///
    /// A pid is an address, not a handle: once a process is reaped the OS may hand its number to an
    /// unrelated one. So the number is used to FIND the process, and each backend then binds the group to
    /// an anchor of its own for whatever the number currently names — the process OBJECT behind one
    /// `OpenProcess` (Job Object), kernel-maintained cgroup membership plus a start-time read on either
    /// side of the write that moves it in (cgroup v2), or the pid PLUS the start-time token re-read
    /// before every later probe and delivery (POSIX process group). What no backend can check is the
    /// window BEFORE the call, in which the caller's number may already have changed hands.
    ///
    /// A backend that cannot take such an anchor at all returns `ProcessError.Unsupported` — a POSIX host
    /// with no start-time reader (the BSDs), where tracking the number would mean signalling whatever
    /// holds it at teardown — and a per-process failure (a pid that names nothing, a denied read or
    /// write, an assign the kernel refuses, a target this process may not signal, a number that changed
    /// hands during the call) is a typed `ProcessError.Adopt`. Never a silent downgrade to raw by-number
    /// containment, and never a member the group would be unable to kill.
    abstract AdoptByPid: int -> Result<unit, ProcessError>

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

    /// Gracefully kill the tree (configured soft signal → grace → hard kill) without releasing it. The
    /// returned `GracefulOutcome` is the observed-facts core `ProcessGroup.ShutdownReportAsync` builds its
    /// public `ShutdownReport` from; every other caller (`ShutdownAsync`, the per-run graceful-timeout
    /// path) simply discards it, exactly as before this member started reporting anything.
    abstract GracefulKillTree: Signal -> TimeSpan -> Task<GracefulOutcome>

    /// How far a soft stop (`Signal.Int`/`Signal.Term`) reaches on this backend's CURRENT live membership,
    /// right now — the answer `ProcessGroup.SoftStopScope()` reports. Side-effect-free: it must deliver no
    /// signal and touch no native mutation, only read the same live state `Signal`/`GracefulKillTree`
    /// consult, so asking never changes what a later soft stop would do.
    abstract SoftStopScope: unit -> SoftStopScope

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

    /// Post-run, per-axis evidence of whether a resource cap this group ever carried actually fired —
    /// read from this backend's own authoritative post-mortem counters, honoring `capped` (the axes
    /// `ProcessGroup` has ever configured on this group) so an axis never capped answers `NotTripped`
    /// without any native read. Called by `ProcessGroup`'s teardown while the container is still live,
    /// immediately before `HardRelease` — the counters (and, for cgroup v2, the directory itself) do not
    /// survive past that point. Never fails: a counter that cannot be read degrades to `Unknown`, never
    /// an exception or a fabricated verdict.
    abstract LimitEvidence: CappedAxes -> LimitEvidence

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

        member _.AdoptByPid(pid) =
            // The SAME native call, and that is the honest answer here rather than a shortcut: on this
            // mechanism a bare pid is no weaker than a `Process`, because the anchor is the process
            // OBJECT, not the number. `adoptIntoJob` uses the number exactly once, in its own
            // `OpenProcess`; from there everything — the `AssignProcessToJobObject`, the failure
            // disambiguation — runs on that handle, and the kernel keeps Job membership per object. So
            // there is no window AFTER this call in which a recycled number could put a stranger in the
            // Job, and nothing for a later re-verification to add: `Members`/`Suspend`/`Resume`/the Job
            // kill all act through the Job itself, and per-member sampling already re-checks each pid
            // against a creation-time generation snapshot.
            //
            // The one window that remains is the one no mechanism can close: whether the number still
            // named the intended process when the CALLER read it. `ProcessGroup.AdoptByPid` documents it;
            // `Adopt(Process)` is what closes it, because the caller's own open handle pins the pid.
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

                Task.FromResult
                    { Soft = SoftDelivery.Unsupported
                      Drained = false
                      Escalated = true }
            | Some ownedJob ->
                task {
                    try
                        return!
                            GracefulTeardown.poll
                                (fun () ->
                                    // A post reaching at least one windowed member is `Sent`; nothing to post
                                    // to (a windowless tree, or one already empty) is `Unsupported` — this
                                    // path never sends a CTRL+BREAK (see `Signal(Int/Term)` for that), so
                                    // there is no `Failed` case here: WM_CLOSE either reaches a window or
                                    // finds none.
                                    if Native.Windows.postCloseToJobWindows ownedJob > 0 then
                                        SoftDelivery.Sent
                                    else
                                        SoftDelivery.Unsupported)
                                (fun () -> Native.Windows.jobTreeAliveWindows ownedJob)
                                (fun () -> Native.Windows.terminateWindowsJob ownedJob |> ignore)
                                grace
                    finally
                        Native.Windows.closeWindowsHandle ownedJob
                }

        member _.SoftStopScope() =
            // Side-effect-free: read the CTRL-capable leaders this backend already tracks, plus a fresh
            // (non-mutating) windowed-member probe — without posting anything or sending a CTRL+BREAK.
            //
            // R-02: `ctrlGroups` entries are NOT a live-membership read by themselves — an entry is added
            // at `Track` and removed only at `Release`/`HardRelease`, i.e. it survives for as long as the
            // child's handle stays open, which can outlast the child's REAL exit (the handle is closed by
            // the owning `RunningProcess`'s own teardown, a separate lifecycle event). Counting a stale
            // entry would answer `OptInMembers` for a leader whose console process group the OS has
            // already torn down, where `Signal(Int/Term)`'s `GenerateConsoleCtrlEvent` on that group id
            // would then fail — exactly the "capability query promises more than delivery can back up"
            // class this whole report exists to avoid. So each entry is filtered through
            // `processHasExitedWindows` on its still-open handle (the same handle-based, never-a-pid
            // liveness read `terminateWindowsProcess` already uses) and only one that has NOT provably
            // exited counts as a live CTRL-capable leader; `None` (the OS would not say) is treated as
            // "could still be live" — the same fail-safe direction `MembersAfter` and every other honest
            // read here uses (never fabricate a negative from an inconclusive probe).
            //
            // A group with either a live CTRL-capable leader or a live windowed member can receive a soft
            // stop (`OptInMembers`); a group with neither has nothing to receive one at all
            // (`Unsupported`). This is still not a delivery GUARANTEE even for a target that passes both
            // checks: `GenerateConsoleCtrlEvent` can fail for reasons this read does not probe (e.g. no
            // console to share) — `OptInMembers` says a live target exists, not that delivery to it is
            // certain to succeed.
            let hasLiveCtrlLeader =
                ctrlGroups.Keys
                |> Seq.exists (fun handle ->
                    match Native.Windows.processHasExitedWindows handle with
                    | Some true -> false
                    | Some false
                    | None -> true)

            if hasLiveCtrlLeader || Native.Windows.hasWindowedMemberWindows jobHandle then
                SoftStopScope.OptInMembers
            else
                SoftStopScope.Unsupported

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
            | Some(active, cpu, peak, io) -> Ok(ProcessGroupStats(active, None, Some cpu, Some peak, Some io))
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

        member _.LimitEvidence(capped: CappedAxes) : LimitEvidence =
            // A Windows Job Object keeps no post-mortem record that any whole-tree cap fired (no
            // `memory.events`/`pids.events`/`cpu.stat` analogue — see `ProcessGroup.LimitEvidence`'s own
            // doc comment for the full per-axis reasoning), so a capped axis is always `Unknown`: real
            // evidence may or may not exist, but this backend cannot read it. An axis this group never
            // capped needs no query at all — nothing was capped, so nothing could have fired.
            let verdict (isCapped: bool) =
                if isCapped then
                    LimitVerdict.Unknown
                else
                    LimitVerdict.NotTripped

            // `Cpu` passes through `GuardCpuVerdict`: this Job's raw verdict is derived from `CpuQuota`
            // alone (`capped.Cpu`), so a `NotTripped` it reports must still be downgraded to `Unknown` when
            // this group also carries a `CpuTimeMax` — a Job-time kill has no accounting a Job Object keeps
            // (R-01).
            LimitEvidence(verdict capped.Memory, verdict capped.Processes, capped.GuardCpuVerdict(verdict capped.Cpu))

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
/// reaped with `cgroup.kill`, followed by a bounded attempt to remove the directory.
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

    // The cgroup DIRECTORY is reclaimed exactly once, however teardown is driven. `ProcessGroup` already
    // runs `HardRelease` behind its single `claimRelease` transition, so this guard is about the backend
    // holding on its own — tests drive it directly, and a `Dispose` racing a `ShutdownAsync` must not be
    // able to do this twice. The kill and the reap drain are safely repeatable (a second pass drains an
    // already-empty ledger); the `rmdir` is not. A repeat would spend the drain budget again under the
    // group's lifecycle lock, and — on a path the OS has since handed to a NEW cgroup, since the name
    // carries this process's pid, which another process inherits once we exit — could remove a directory
    // that is no longer ours. `ref` rather than a plain mutable so the `Interlocked` claim can take its
    // address (the convention `PostKillReap`'s counters already follow).
    let directoryReleased = ref 0

    // How many times the reclaim above actually RAN, and what it concluded if it kept the directory. Both
    // are per-INSTANCE on purpose: they are what a test asserts its own teardown on, rather than the
    // process-wide counters in `Native.Cgroup` — which another fixture's cgroup teardown, or a
    // finalizer-driven one, can move at any moment (K-148).
    let directoryReclaims = ref 0
    let mutable retainedDetail: string option = None

    // Pull and remove the captured identity for `pid` (defaulting to `None`), so the shared reap can gate
    // its `killpg` on it. Removal keeps `identities` in lockstep with `children`.
    let takeIdentity (pid: int) : uint64 option =
        match identities.TryRemove pid with
        | true, token -> token
        | false, _ -> None

    // Is `pid` still the SAME tracked child, not a recycled number — and where does an operation on it
    // go? Mirrors `ProcessGroupBackend.targetOf` (a cgroup child is its own process-group leader —
    // spawned with POSIX_SPAWN_SETPGROUP, pgid == pid — so the pgid choke applies; a `Command.Pty` child
    // under this backend runs the same `setsid --ctty` helper, so it too has the pre-`setsid()` window
    // the `LeaderPid` verdict covers): gate the by-number liveness probe through the captured start-time
    // identity so a pid recycled since it was tracked is reported gone and never SIGKILLed (the
    // wrong-target kill T-084 closes). A matching identity, or an unknown token on either side, defers to
    // the by-number verdict, so no coverage is lost. Used by `KillChild`, the one remaining per-child raw
    // kill path (the per-member signal path is already pidfd-pinned via `Native.Cgroup.deliverIdentitySafe`).
    let targetOf (pid: int) : Native.Posix.TrackedTarget =
        let captured =
            match identities.TryGetValue pid with
            | true, token -> token
            | false, _ -> None

        Native.Posix.trackedTarget pid captured

    new(cgroupPath: string) = CgroupBackend(cgroupPath, ResourceLimits.None)

    /// Internal diagnostic (not public API — the `Native.Posix.pidfdActive` convention): how many times
    /// this backend has actually run the post-kill cgroup-directory reclaim. Never more than one, however
    /// teardown is driven, because only the claim winner runs it.
    member _.DirectoryReclaims =
        System.Threading.Volatile.Read(&directoryReclaims.contents)

    /// Internal diagnostic (not public API): why THIS backend's teardown could not reclaim its cgroup
    /// directory, or `None` when it did (or has not run yet). Per-instance, so it says what happened to
    /// this cgroup rather than to whichever one the process last gave up on.
    member _.RetainedCgroupDetail = retainedDetail

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.CgroupV2

        member _.Spawn(command) =
            // Spawn through the self-migrating cgroup launcher (a tiny `/bin/sh` that writes its own pid
            // into this cgroup's cgroup.procs, then `exec`s the real program in place), so the target's
            // pid is already a cgroup member before it runs a single instruction — closing the old
            // spawn->migrate window where a descendant forked in that first instant could escape the
            // limits. See `Native.Posix.spawnPosixIntoCgroup`. `Spawned.Handle` is the launcher pid,
            // which becomes the target's pid unchanged across `exec`.
            // One rewrite applies BOTH per-process caps that have to reach the child before its program
            // starts: this group's whole-tree `CpuTimeMax` and any `Command.Rlimit` the command carries.
            // Folding them here, in one place, is what keeps the shared CPU-time axis honest — applying
            // them separately would let whichever ran last overwrite the other (see
            // `Native.Posix.withProcessLimits`, which takes the stricter of the two).
            Native.Posix.withProcessLimits currentLimits.CpuTimeMax command
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

        member _.AdoptByPid(pid) =
            // Same containment as `Adopt` — one write to `cgroup.procs`, after which kernel-maintained
            // membership (not our tracking set) is what `cgroup.kill`/`cgroup.freeze`/the per-member
            // signal sweep/`Members`/`Stats` all work from — with the identity handling a BARE number
            // requires around it.
            //
            // The anchor is read BEFORE the write and again AFTER it. Unlike Windows, nothing here pins a
            // pid: `/proc/<pid>/stat`'s start time can only DETECT that the number changed hands across
            // the write, never prevent it. So:
            //
            //  * no readable identity up front → refuse. Adopting on the strength of the number alone is
            //    precisely what this path exists to avoid, and the anchor is also what keeps this
            //    member's per-member stats identity-safe afterwards (see `adoptedIdentities`).
            //  * a changed identity across the write → the write has already landed on a STRANGER, and
            //    cgroup v2 membership is exclusive, so it is now in our cgroup and would be killed by our
            //    teardown. Move it back out (to the cgroup our own directory lives in) and report the
            //    race honestly, saying which of the two states it left behind.
            //  * an identity that has become unreadable → the process exited during the call (the
            //    ordinary case: a member leaves `cgroup.procs` on exit) or its `/proc` entry is no longer
            //    readable. Neither is PROOF of a recycle, and the established rule here is that only two
            //    KNOWN, differing tokens are (`Native.Posix.trackedTarget`), so the adoption stands with
            //    the anchor captured up front — and every later per-member read stays gated on it.
            match Native.Posix.readProcessIdentity pid with
            | None ->
                Error(
                    ProcessError.Adopt(
                        pid,
                        "the process's start-time identity could not be read (/proc/<pid>/stat is missing — the process is gone — or unreadable under a hidepid mount or another user); a bare pid is never adopted on the strength of the number alone"
                    )
                )
            | Some anchor ->
                match Native.Cgroup.adoptIntoCgroup cgroupPath pid with
                | Error detail -> Error(ProcessError.Adopt(pid, detail))
                | Ok() ->
                    match Native.Posix.readProcessIdentity pid with
                    | Some current when current <> anchor ->
                        let aftermath =
                            match Native.Cgroup.releaseFromCgroup cgroupPath pid with
                            | Ok() ->
                                "it was moved back out into the parent cgroup, so this group's teardown no longer reaches it (its previous cgroup cannot be restored — the kernel does not report which one a task left)"
                            | Error detail ->
                                $"and it could NOT be moved back out ({detail}), so it remains a member of this group's cgroup and WILL be killed by this group's teardown"

                        Error(
                            ProcessError.Adopt(
                                pid,
                                $"the pid changed hands while the adoption ran: the start-time identity read after the cgroup.procs write differs from the one read before it, so the process now holding this number is not the one named — {aftermath}"
                            )
                        )
                    | _ ->
                        adoptedIdentities[pid] <- Some anchor
                        Ok()

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
            // keeping `identities` in lockstep with `children`. This kill is by pid on purpose (one child,
            // not its group), so both live verdicts deliver the same way — the choke's routing matters
            // here only in that a pre-`setsid()` pty leader is no longer mistaken for a vanished target.
            let pid = int spawned.Handle

            if TrackedTargets.isGone (targetOf pid) then
                identities.TryRemove pid |> ignore
                children.Remove pid |> ignore
            else
                Native.Posix.killProcess pid

        member _.KillTree() =
            Native.Cgroup.killCgroup cgroupPath
            |> Result.mapError (fun message -> ProcessError.Io $"failed to kill cgroup: {message}")

        member _.GracefulKillTree (signal) (grace) =
            let signalNum = Native.Posix.signalNumber signal

            GracefulTeardown.poll
                (fun () ->
                    match Native.Cgroup.signalCgroup cgroupPath signalNum with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> SoftDelivery.Sent
                    | Native.Common.SignalDelivery.DeliveryFailed _ -> SoftDelivery.Failed)
                (fun () -> Native.Cgroup.cgroupAlive cgroupPath)
                (fun () ->
                    Native.Cgroup.killCgroup cgroupPath |> ignore

                    // `cgroup.kill` only STARTS the tree leaving (R-01 — the same caveat `HardRelease`'s
                    // own post-kill drain wait documents, T-363): the kernel drops a member from
                    // `cgroup.procs` only once it actually exits, which can land after this write
                    // returns. `ShutdownReportAsync` samples `MembersAfter` right behind this call while
                    // the cgroup is still live — wait, BOUNDED (the same `DefaultDrainBudget` the final
                    // `rmdir` wait uses), for the cgroup to actually read empty, so `MembersAfter`
                    // reflects processes that have REALLY left rather than merely been asked to. No
                    // removal happens here (that is `HardRelease`'s job, once this method's caller is
                    // done reading `Members()`); this only waits on the same membership read
                    // `cgroupAlive`/`Members()` already use. Best-effort like every other bounded wait in
                    // this file: a cgroup still populated or unreadable at the deadline simply leaves
                    // `MembersAfter` reporting what is honestly still there.
                    let deadline = Native.Cgroup.DefaultDrainBudget
                    let stopwatch = Stopwatch.StartNew()

                    while Native.Cgroup.cgroupAlive cgroupPath && stopwatch.Elapsed < deadline do
                        System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds 2.0))
                grace

        member _.SoftStopScope() =
            // `Signal(Int/Term)` writes to every process in the cgroup unconditionally — always the whole
            // tree, on every live membership, so this needs no native read at all.
            SoftStopScope.WholeTree

        member _.SignalChild(spawned, signal) =
            let pid = int spawned.Handle
            let signalNum = Native.Posix.signalNumber signal

            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                // Route by the choke's verdict: the child's own process group while it has one, the exact
                // leader pid while it does not yet (a pty child before its `setsid()`), nothing at all for
                // a target that is gone by both probes.
                let target = targetOf pid

                if TrackedTargets.isGone target then
                    Ok()
                else
                    match Native.Posix.signalTracked target pid signalNum with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> Ok()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        Error(
                            ProcessError.Io
                                $"failed to deliver signal {signalNum} to this run: {message} (errno {errno})"
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
                let cpu, peakMemory, peakProcesses, io = Native.Cgroup.cgroupStats cgroupPath
                Ok(ProcessGroupStats(active, peakProcesses, cpu, peakMemory, io))

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

        member _.LimitEvidence(capped: CappedAxes) : LimitEvidence =
            // The only backend with real evidence to read — see `Native.Cgroup.limitEvidence` for the
            // exact counters (`memory.events`'s `oom`, `pids.events`'s `max`, `cpu.stat`'s
            // `nr_throttled`) and the honest-Unknown fallback when a file/key is missing.
            Native.Cgroup.limitEvidence cgroupPath capped

        member _.HardRelease() =
            // Final disposal must remain bounded and best-effort: it proceeds with the drain and reclaim
            // attempt even when the reusable kill reported an error — an unverified thaw after either
            // atomic cgroup.kill or the legacy sweep, or a sweep that left the cgroup populated
            // (including a kernel without pidfd, which kills nothing rather than downgrading to a racy
            // raw kill). An unreclaimable cgroup is recorded below rather than reported as removed.
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
            //
            // The whole drain runs under one bounded budget (`PosixReap.drain`): the identities are
            // snapshotted WITH the pids in a single pass, before any reaping, so the off-lock handoff
            // that follows works from that snapshot rather than re-reading a dictionary a concurrent
            // per-child cleanup may be mutating (K-086).
            children.Drain()
            |> List.map (fun pid -> pid, takeIdentity pid)
            |> PosixReap.drain

            adoptedIdentities.Clear()

            // `cgroup.kill` (and the legacy sweep behind it) only START the tree leaving: the kernel drops
            // a member from the cgroup when that member EXITS, which can land after the kill write has
            // returned. Removing the directory right then answers `EBUSY` — an error the old best-effort
            // `rmdir` swallowed whole, leaving an empty-but-permanent cgroup behind on teardown after
            // teardown until the hierarchy filled up with them. So wait, BOUNDED, for the cgroup to
            // actually empty and retry the removal inside that same budget (T-363).
            //
            // The wait is a membership question, never a reap one, which is what makes it work for an
            // ADOPTED member too: nothing here may `waitpid` a process that is not our child, and it does
            // not have to — a member leaves `cgroup.procs` on exit, before anyone reaps it. It is also why
            // the wait sits AFTER the reap drain above rather than replacing it: by the time it runs, our
            // own children are usually already gone and the very first probe finds the cgroup drained.
            if System.Threading.Interlocked.Exchange(&directoryReleased.contents, 1) = 0 then
                System.Threading.Interlocked.Increment(&directoryReclaims.contents) |> ignore

                match Native.Cgroup.releaseCgroup cgroupPath with
                | Native.Cgroup.Release.Removed -> ()
                | Native.Cgroup.Release.Retained detail ->
                    // `HardRelease` is `unit` by contract and this backend holds no logger, so the honest
                    // thing left is to record the verdict where it can be inspected rather than hide it: a
                    // cgroup this teardown could not reclaim is a directory that accumulates.
                    retainedDetail <- Some detail
                    Native.Cgroup.noteRetainedCgroup cgroupPath detail

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

    // Foreign processes adopted BY BARE PID (`ProcessGroup.AdoptByPid`), each with the start-time anchor
    // captured when it was adopted. A SEPARATE ledger from `children`, and deliberately so — the two
    // differ in every way that matters to teardown:
    //
    //  * `children` holds pgids WE created, so its entries may be `killpg`ed and MUST be `waitpid`ed (we
    //    own their exit status). An adopted process is neither our child (a `waitpid` would `ECHILD`)
    //    nor the leader of a group we own (a `killpg` would sweep a stranger's tree) — see K-016.
    //  * its identity token is a plain `uint64`, not an option: this mechanism refuses to adopt a pid
    //    whose identity it cannot read, so an entry here always has an anchor to re-verify against,
    //    and there is no "unknown token → fall back to the by-number verdict" case to degrade into.
    //
    // What the group gives such a process is exactly what it can honestly give: it is listed by
    // `Members`, sampled by `MemberStats`, receives `Signal`/`Suspend`/`Resume`, and is SIGKILLed by
    // `KillTree`/teardown — each of those re-reading the anchor first (`Native.Posix.adoptedStillOurs`)
    // so a recycled number is pruned rather than signalled. Processes it forks AFTERWARDS are not
    // contained: this mechanism contains by tracking, and POSIX has no primitive that moves a foreign,
    // already-`exec`ed process into our process group.
    let adopted = ConcurrentDictionary<int, uint64>()

    // A point-in-time copy of the adopted ledger, taken before any delivery — the same
    // snapshot-then-work discipline `children.Snapshot()` follows, and what the off-lock graceful poll
    // must hold on to instead of re-reading a dictionary a concurrent teardown can clear (K-086).
    let adoptedSnapshot () : (int * uint64) list =
        adopted |> Seq.map (fun entry -> entry.Key, entry.Value) |> List.ofSeq

    // Drop an adopted pid from tracking — used when its anchor no longer matches (recycled) or nothing
    // is at the number any more. Nothing is reaped or killed here: it never was ours to reap.
    let untrackAdopted (pid: int) = adopted.TryRemove pid |> ignore

    // The single liveness + identity choke every probe/signal/kill path funnels through, so the reuse
    // check is never duplicated per call site: is `pgid` still the SAME live group we tracked, and where
    // does an operation on it go? It gates the by-number liveness probe through the pgid's captured
    // start-time identity — a recycled pgid (a live number whose current identity differs from the
    // captured one) is reported NOT ours, so callers prune it and never signal it. A matching identity,
    // or an unknown token on either side (a leader reaped while descendants keep the pgid alive, or a
    // platform without a reader), defers to the by-number verdict, so no platform loses coverage. A
    // freshly-spawned pty leader whose group does not exist yet answers `LeaderPid`: still ours, still
    // tracked, reachable by pid until its `setsid()` runs (see `Native.Posix.trackedTarget`).
    let targetOf (pgid: int) : Native.Posix.TrackedTarget =
        let captured =
            match identities.TryGetValue pgid with
            | true, token -> token
            | false, _ -> None

        Native.Posix.trackedTarget pgid captured

    // The read-only form for membership/stats/pruning questions: is this pgid still ours at all, by
    // either route? Delivery paths must use `targetOf` itself so they signal the right target.
    let stillOurs (pgid: int) : bool =
        not (TrackedTargets.isGone (targetOf pgid))

    // Drop a pgid from tracking entirely (both the pgid set and its identity token) — used when the choke
    // finds it drained or recycled. Returns whether this call was the one that removed it.
    let untrack (pgid: int) : bool =
        identities.TryRemove pgid |> ignore
        children.Remove pgid

    // Broadcast to every tracked pgid that is still ours, routed by the choke's verdict (its group, or
    // the exact leader pid while that group does not exist yet); a recycled or vanished pgid is pruned
    // instead, so a control operation can never target an unrelated process group. Continue after
    // failures to give every remaining group a chance to receive the operation, then report the first
    // delivery failure.
    //
    // The adopted pass that follows is the same shape over the OTHER ledger, with the one difference the
    // ownership demands: delivery is to the exact pid (`deliverAdopted`, which re-reads the anchor), never
    // to a process group we did not create. Its pruning is driven by the SAME verdict that decided the
    // delivery rather than by a second probe — a `TargetGone` there means either a recycled number or an
    // `ESRCH`, and in both cases nothing of ours is at that number any more.
    let sweep
        (deliver: Native.Posix.TrackedTarget -> int -> Native.Common.SignalDelivery)
        (deliverAdopted: int -> uint64 -> Native.Common.SignalDelivery)
        (describeFailure: int -> string -> string)
        : Result<unit, ProcessError> =
        let mutable firstFailure: (int * string) option = None

        let note (errno: int) (message: string) =
            if firstFailure.IsNone then
                firstFailure <- Some(errno, message)

        for pgid in children.Snapshot() do
            let target = targetOf pgid

            if TrackedTargets.isGone target then
                untrack pgid |> ignore
            else
                match deliver target pgid with
                | Native.Common.SignalDelivery.Delivered
                | Native.Common.SignalDelivery.TargetGone -> ()
                | Native.Common.SignalDelivery.DeliveryFailed(errno, message) -> note errno message

        for pid, anchor in adoptedSnapshot () do
            match deliverAdopted pid anchor with
            | Native.Common.SignalDelivery.Delivered -> ()
            | Native.Common.SignalDelivery.TargetGone -> untrackAdopted pid
            | Native.Common.SignalDelivery.DeliveryFailed(errno, message) -> note errno message

        match firstFailure with
        | None -> Ok()
        | Some(errno, message) -> Error(ProcessError.Io(describeFailure errno message))

    new() = ProcessGroupBackend(ResourceLimits.None)

    /// Kill and reap every leader this backend currently tracks, and SIGKILL every adopted foreign pid —
    /// the escalation half of `GracefulKillTree`, taken as a fresh snapshot at the moment of the call.
    ///
    /// Not on `IContainmentBackend` (so no implementor fan-out, K-055): its one external caller is
    /// `ProcessReaperBackend`, which LAYERS over this backend on FreeBSD and needs exactly this — the
    /// reaper's own `PROC_REAP_KILL` reaches the whole tree, but only the ledger here knows which of those
    /// processes are OUR children and therefore ours to `waitpid`. Without it a reaper escalation would
    /// SIGKILL the tree and leave our own leaders as zombies nobody waits for until final teardown (R-01 /
    /// K-179: the kill and the reap belong together wherever something reads membership right behind
    /// them).
    ///
    /// Reentrant by `PosixReap`'s own contract: a pgid a previous pass already reaped probes `Gone` on the
    /// next one, so the kill is skipped and the reap probe is a harmless no-op. Nothing is untracked here
    /// — this is the escalation, not the teardown; `HardRelease` still owns the take-and-clear drain.
    member internal _.HardKillAndReapTracked() : unit =
        children.Snapshot()
        |> List.map (fun pgid ->
            pgid,
            match identities.TryGetValue pgid with
            | true, token -> token
            | false, _ -> None)
        |> PosixReap.drain

        // Adopted foreign pids: killed by their exact pid while the anchor still matches, never reaped
        // (they are not our children) and never `killpg`ed (that group is not ours) — the same asymmetry
        // `GracefulKillTree`'s own escalation and `HardRelease` both observe.
        for pid, anchor in adoptedSnapshot () do
            Native.Posix.killAdopted pid anchor |> ignore

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessGroup

        member _.Spawn(command) =
            // Both per-child caps in one rewrite: this group's whole-tree `CpuTimeMax` and any
            // `Command.Rlimit` the command carries, with the stricter value winning on the CPU-time axis
            // they share (see `Native.Posix.withProcessLimits`). A command with neither is passed through
            // untouched.
            Native.Posix.withProcessLimits initialLimits.CpuTimeMax command
            |> Result.bind Native.Posix.spawnPosix

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

        member _.AdoptByPid(pid) =
            // Unlike `Adopt`, this one IS supported here — because it asks for something this mechanism
            // can actually do. `Adopt` fails above for a real reason: there is no kernel primitive that
            // MOVES a foreign, already-`exec`ed process into our process group, and this backend's
            // containment used to be nothing but that group. Bare-pid adoption does not need the move:
            // this mechanism contains by TRACKING (the same way each spawned pgid is tracked), and an
            // anchor makes tracking a foreign number safe. Nothing about the target is changed — no
            // `setpgid` is attempted on it, which POSIX would refuse for a stranger anyway and which,
            // where it did succeed, would make it a process-group leader and move where a terminal's
            // job-control signals reach it.
            //
            // What that buys, stated exactly: the adopted process is listed, signalled, suspended/resumed
            // and SIGKILLed with the group, every one of those re-verifying the anchor first. What it
            // does NOT buy: processes it forks afterwards (they inherit ITS process group, not our
            // tracking), and its exit status, which stays with its real parent — this library never
            // `waitpid`s it.
            //
            // Three distinct refusals, never conflated. A host with no start-time reader at all (the BSDs)
            // has no anchor to take for ANY pid: `Unsupported`, the whole-platform answer, rather than
            // silently tracking a bare number that teardown would later SIGKILL whoever holds. A host
            // with a reader that cannot read THIS pid (already gone, `hidepid`, another user's process on
            // macOS) is a per-process `Adopt` failure. And a pid this process may not SIGNAL is a
            // per-process `Adopt` failure too — see the signalability gate below, which is the one thing
            // the anchor read cannot answer.
            if not (Native.Posix.processIdentityReaderAvailable ()) then
                Error(
                    ProcessError.Unsupported
                        "adopting an external process by bare pid needs a start-time identity anchor for it (Linux /proc/<pid>/stat, macOS proc_pidinfo); this platform ships no reader ProcessKit can verify, and a pid tracked by number alone would let teardown SIGKILL whatever holds that number later"
                )
            else
                match Native.Posix.readProcessIdentity pid with
                | None ->
                    Error(
                        ProcessError.Adopt(
                            pid,
                            "the process's start-time identity could not be read (it is already gone, or its /proc entry is unreadable under a hidepid mount or for another user); a bare pid is never tracked on the strength of the number alone"
                        )
                    )
                | Some anchor ->
                    // The anchor proves we can IDENTIFY this process; it says nothing about whether we can
                    // CONTROL it, and on Linux the two come apart routinely (`/proc/<pid>/stat` is
                    // world-readable, so another user's process reads back a perfectly good anchor while
                    // every `kill` to it fails EPERM — unreported, because the teardown kill is
                    // fire-and-forget). Accepting such a pid would report containment of a process this
                    // group cannot signal or SIGKILL: the silent downgrade of the kill-on-dispose
                    // guarantee. The other two mechanisms refuse it by construction — a denied
                    // `OpenProcess`, a denied `cgroup.procs` write — so this one asks the same question
                    // explicitly, with the probe that answers it and delivers nothing.
                    match Native.Posix.ensureAdoptedSignalable pid with
                    | Error detail -> Error(ProcessError.Adopt(pid, detail))
                    | Ok() ->
                        adopted[pid] <- anchor
                        Ok()

        member _.Release(spawned) =
            // A pgid is a whole group; the reaped leader may have left backgrounded members behind, so
            // only stop tracking once the group is actually empty — or the pgid has been recycled by an
            // unrelated process (the choke's identity check), which must likewise stop tracking so a
            // stranger is never signalled. An empty GROUP is not on its own proof the child is gone: a
            // pty leader before its `setsid()` has no group yet, and the choke keeps it tracked (by pid)
            // rather than dropping a live child from the container.
            let pgid = int spawned.Handle

            if not (stillOurs pgid) then
                untrack pgid |> ignore

        member _.Wait(handle) = Native.Posix.waitPosix handle
        member _.PidOf(spawned) = Some(int spawned.Handle)

        member _.KillChild(spawned) =
            // Hard-kill this one child's group — but only while it is still OURS. A recycled pgid
            // (identity differs) must never be SIGKILLed (wrong-target kill); gate it through the choke
            // and prune it instead. A pty leader whose group does not exist yet is SIGKILLed by pid (and
            // swept by `killpg` behind it, for the case it became a group leader between the probe and
            // the kill) rather than skipped, so an immediate kill after the spawn cannot leave it — or a
            // subtree it just forked — running.
            let pgid = int spawned.Handle
            let target = targetOf pgid

            if TrackedTargets.isGone target then
                untrack pgid |> ignore
            else
                Native.Posix.killTracked target pgid

        member _.KillTree() =
            for pgid in children.Snapshot() do
                let target = targetOf pgid

                if TrackedTargets.isGone target then
                    untrack pgid |> ignore
                else
                    Native.Posix.killTracked target pgid

            // Adopted foreign pids: the EXACT pid, and only while its anchor still matches — `killAdopted`
            // re-reads it and reports whether the SIGKILL went out, so the mismatch that skips the kill is
            // the same verdict that prunes the entry. No `killpg` behind it (that group is not ours), and
            // no reap after it (it is not our child — its real parent, or `init`, reaps it).
            for pid, anchor in adoptedSnapshot () do
                if not (Native.Posix.killAdopted pid anchor) then
                    untrackAdopted pid

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

            // All three phases route through the SAME snapshotted-token verdict: what is delivered, and
            // where it is delivered, are one decision per phase (never a liveness answer from one probe
            // paired with a delivery to a different target). The verdict is re-probed per phase — only
            // the identity TOKEN is snapshotted (K-086) — so a child that becomes a group leader during
            // the grace period is escalated against its group, not its bare pid.
            let targetSnap (pgid: int) : Native.Posix.TrackedTarget =
                match Map.tryFind pgid identitySnapshot with
                | Some token -> Native.Posix.trackedTarget pgid token
                | None -> Native.Posix.TrackedTarget.Gone

            // The adopted ledger gets the same treatment for the same reason: a concurrent `HardRelease`
            // clears it, so the poll — which runs OFF the lifecycle lock — must work from a copy taken
            // here rather than re-reading it (K-086). The anchor itself never changes for a given entry;
            // it is the identity re-read against it that happens per phase.
            let adoptedPids = adoptedSnapshot ()

            let anyAdoptedAliveSnap () =
                adoptedPids
                |> List.exists (fun (pid, anchor) -> Native.Posix.adoptedStillOurs pid anchor)

            let anyChildAliveSnap () =
                pgids |> List.exists (fun pgid -> not (TrackedTargets.isGone (targetSnap pgid)))
                || anyAdoptedAliveSnap ()

            GracefulTeardown.poll
                // The soft sweep: `signalTracked` sends an OBSERVABLE signal, so a `LeaderPid` verdict
                // reaches exactly one of the two targets — the group if it exists by now, the pid only on
                // its `ESRCH` — never both. No child is asked to handle the same stop signal twice.
                //
                // R-03: the fate reported here is `SoftDelivery.Failed` — the internal core of the public
                // `SoftSignalDelivery.Failed`, documented as "the best-effort delivery failed for EVERY
                // target" — so it must actually mean that, not merely "at least one live target's
                // delivery failed" (a caller who reads `Failed` for a tree where most members drained
                // fine would wrongly conclude the whole soft phase was a no-op). `anyDelivered` tracks
                // whether at least one live target genuinely received the signal; `Failed` fires only when
                // there was a failure AND nothing was delivered to anyone. A vacuous sweep (every target
                // already `TargetGone`, including an already-empty tree) has neither a delivery nor a
                // failure, so it is `Sent` — unchanged from before.
                (fun () ->
                    let mutable anyDelivered = false
                    let mutable anyFailed = false

                    for pgid in pgids do
                        let target = targetSnap pgid

                        if not (TrackedTargets.isGone target) then
                            match Native.Posix.signalTracked target pgid (Native.Posix.signalNumber signal) with
                            | Native.Common.SignalDelivery.Delivered -> anyDelivered <- true
                            | Native.Common.SignalDelivery.TargetGone -> ()
                            | Native.Common.SignalDelivery.DeliveryFailed _ -> anyFailed <- true

                    for pid, anchor in adoptedPids do
                        match Native.Posix.signalAdopted pid anchor (Native.Posix.signalNumber signal) with
                        | Native.Common.SignalDelivery.Delivered -> anyDelivered <- true
                        | Native.Common.SignalDelivery.TargetGone -> ()
                        | Native.Common.SignalDelivery.DeliveryFailed _ -> anyFailed <- true

                    if anyFailed && not anyDelivered then
                        SoftDelivery.Failed
                    else
                        SoftDelivery.Sent)
                anyChildAliveSnap
                // The escalation. `killTracked` is what makes this whole-tree even for a child still in
                // its pre-`setsid()` window: the `LeaderPid` route SIGKILLs the pid and sweeps `killpg`
                // behind it, so a subtree the child forked between the probe and this kill goes with it.
                // This sweep stands on its own — a graceful `Stop` need not be followed by a
                // `HardRelease` (the group may keep running), so it cannot borrow teardown's kill.
                //
                // REAPS too, via `PosixReap.drain` (R-01): a bare `killpg`/`kill` here would SIGKILL the
                // leader but leave it a zombie until something `waitpid`s it — and `ShutdownReportAsync`
                // samples `MembersAfter` (`Members()` -> `stillOurs` -> `killpg(pgid, 0)`) right behind
                // this call, while the leader is still live, so an unreaped zombie is honestly still
                // "there" and would count. `PosixReap.drain` is the SAME kill-then-reap-under-one-budget
                // sequence `HardRelease` runs at final teardown (`children.Drain() |> List.map ... |>
                // PosixReap.drain`), reused here so `MembersAfter` reflects a tree that has ACTUALLY gone,
                // not merely been signalled. It is safe to run again from `HardRelease` right after this
                // returns: a pgid this call already reaped reads `Gone` on the next probe (no member left
                // to keep the group alive), so the second pass kills nothing and its own reap probe is a
                // harmless no-op — exactly the reentrant contract `PosixReap`'s own doc describes for the
                // per-escapee/whole-drain callers.
                (fun () ->
                    pgids
                    |> List.map (fun pgid ->
                        pgid,
                        match Map.tryFind pgid identitySnapshot with
                        | Some token -> token
                        | None -> None)
                    |> PosixReap.drain

                    // The adopted half of the escalation: the exact pid, anchor re-read one last time
                    // inside `killAdopted`. Nothing is pruned here — this snapshot is the poll's own copy
                    // (the ledger it came from may have been drained by a concurrent `HardRelease`), so
                    // the anchor verdict is used only to decide the delivery. Never reaped: an adopted
                    // process is not our child (see `PosixReap`'s own doc on that distinction) — a
                    // `waitpid` on it would `ECHILD`, so its real parent/`init` reaps it, same as before.
                    for pid, anchor in adoptedPids do
                        Native.Posix.killAdopted pid anchor |> ignore)
                grace

        member _.SoftStopScope() =
            // `killpg`/`signalTracked` reaches every tracked group leader and its descendants
            // unconditionally — always the whole tracked tree, on every live membership (the one
            // documented escapee, a `setsid`'d child, is the same weakening the kill-on-drop guarantee
            // already has — not new to the soft stop), so this needs no native read at all.
            SoftStopScope.WholeTree

        member _.SignalChild(spawned, signal) =
            let pgid = int spawned.Handle
            let signalNum = Native.Posix.signalNumber signal

            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                let target = targetOf pgid

                if TrackedTargets.isGone target then
                    Ok()
                else
                    match Native.Posix.signalTracked target pgid signalNum with
                    | Native.Common.SignalDelivery.Delivered
                    | Native.Common.SignalDelivery.TargetGone -> Ok()
                    | Native.Common.SignalDelivery.DeliveryFailed(errno, message) ->
                        Error(
                            ProcessError.Io
                                $"failed to deliver signal {signalNum} to this run: {message} (errno {errno})"
                        )

        member _.Members() =
            // Report only the pgids still ours and alive (choke-gated): a drained or recycled pgid is not
            // a member of this group, while a freshly-spawned pty leader whose group does not exist yet
            // is (it is a live child of ours). Adopted foreign pids are members on the same terms, gated
            // on their own anchor. This is a read — it does not prune (a mutating op does).
            let tracked = children.Snapshot() |> List.filter stillOurs

            let adoptedMembers =
                adoptedSnapshot ()
                |> List.filter (fun (pid, anchor) -> Native.Posix.adoptedStillOurs pid anchor)
                |> List.map fst

            Ok(tracked @ adoptedMembers)

        member _.Signal(signal) =
            let signalNum = Native.Posix.signalNumber signal

            // Refuse a non-deliverable number (signal 0 — a liveness probe — or a negative) at the API
            // boundary, before the delivery loop, so it can never look like a delivered signal. This also
            // covers a group whose pgids have all drained/recycled, where the loop would otherwise signal
            // nobody and report a vacuous success.
            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                sweep
                    (fun target pgid -> Native.Posix.signalTracked target pgid signalNum)
                    (fun pid anchor -> Native.Posix.signalAdopted pid anchor signalNum)
                    (fun errno message ->
                        $"failed to deliver signal {signalNum} to process group: {message} (errno {errno})")

        member _.Suspend() =
            sweep Native.Posix.suspendTracked Native.Posix.suspendAdopted (fun errno message ->
                $"failed to suspend process group: {message} (errno {errno})")

        member _.Resume() =
            sweep Native.Posix.resumeTracked Native.Posix.resumeAdopted (fun errno message ->
                $"failed to resume process group: {message} (errno {errno})")

        member _.Stats() =
            let tracked = children.Snapshot() |> List.filter stillOurs |> List.length

            let adoptedAlive =
                adoptedSnapshot ()
                |> List.filter (fun (pid, anchor) -> Native.Posix.adoptedStillOurs pid anchor)
                |> List.length

            Ok(ProcessGroupStats(tracked + adoptedAlive, None, None, None, None))

        member _.MemberStats() =
            let pids = children.Snapshot() |> List.filter stillOurs

            let tracked =
                pids
                |> List.choose (fun pid ->
                    let identity =
                        match identities.TryGetValue pid with
                        | true, token -> token
                        | false, _ -> None

                    Native.Posix.readMemberStatsWithIdentity pid identity)

            // Adopted members sample against their own anchor, which the shared reader gates the metric
            // read on before AND after it — so a number recycled mid-read contributes nothing rather
            // than a stranger's CPU/memory figures.
            let adoptedSampled =
                adoptedSnapshot ()
                |> List.choose (fun (pid, anchor) -> Native.Posix.readMemberStatsWithIdentity pid (Some anchor))

            Ok(tracked @ adoptedSampled)

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

        member _.LimitEvidence(_capped: CappedAxes) : LimitEvidence =
            // The POSIX process-group mechanism has no whole-tree resource accounting at all — the same
            // reason `Create`/`UpdateLimits` refuse any whole-tree cap on it in the first place. Every axis
            // is `Unknown` UNCONDITIONALLY, deliberately ignoring `capped` — including for an axis this
            // group never capped, unlike the Windows Job Object backend's `NotTripped` for that case.
            // `capped` is NOT always `false` here, despite this mechanism refusing every whole-tree cap:
            // `ProcessGroup.UpdateLimits` records an axis a request NAMES on the sticky `CappedAxes` before
            // attempting the apply, so `capped.X = true` is reachable on this backend too, from a request
            // this mechanism then refuses (see the "an axis named by a failed UpdateLimits still joins the
            // sticky cap record" test). Ignoring `capped` here is a deliberate choice, not a because-it-
            // can't-happen shortcut: there is no evidence apparatus on this mechanism at all, not "a cap may
            // have fired unseen" — gating on `capped` would still have nothing honest to read (R-02).
            LimitEvidence(LimitVerdict.Unknown, LimitVerdict.Unknown, LimitVerdict.Unknown)

        member _.HardRelease() =
            // Each pgid's leader is a child we posix_spawned, so we must waitpid it ourselves — `killpg`
            // SIGKILLs the group but does not reap our own children. Reap the leaders we still track (a
            // run verb Releases the ones it already reaped); other group members reparent to init.
            // Passing each pgid's captured identity to `PosixReap.leader` gates AND routes its kill
            // through the choke, so teardown never SIGKILLs a pgid recycled since it was tracked (a
            // wrong-target kill) and still reaches a leader whose group does not exist yet (a pty child
            // before its `setsid()`: SIGKILLed by pid, with a `killpg` sweep behind it so a subtree it
            // forked in the meantime cannot outlive the record this call is about to erase — after the
            // drain there is no later pass). Drain (atomic take-and-clear), not Snapshot: a Snapshot
            // would leave the tracking list populated after teardown, and a concurrent per-child cleanup
            // (a run's `Release`) could still see (and re-reap) the same pgid — after the first
            // killpg/waitpid the OS may reuse that pid, so a second killpg would land on an unrelated
            // process group (wrong-target kill).
            //
            // The whole drain runs under one bounded budget (`PosixReap.drain`), and the identity tokens
            // are snapshotted WITH the pgids in a single pass before any reaping starts — the same
            // snapshot-before-off-lock-work rule `GracefulKillTree` follows (K-086), so the handoff that
            // may outlive this call never re-reads `identities` a concurrent cleanup can mutate.
            children.Drain()
            |> List.map (fun pgid ->
                let identity =
                    match identities.TryRemove pgid with
                    | true, token -> token
                    | false, _ -> None

                pgid, identity)
            |> PosixReap.drain

            // Adopted foreign pids are the deliberate exception to everything above: kill, never reap.
            // Each is SIGKILLed by its EXACT pid while its anchor still matches — no `killpg` (that group
            // is not ours) and no `waitpid`/ledger handoff (it is not our child; a wait would `ECHILD`,
            // and its real parent or `init` reaps it). Drained first, so this teardown owns the kill
            // exactly once however it is driven, and so a recycled entry cannot survive into a later pass.
            for pid, anchor in adoptedSnapshot () do
                // Take-then-kill, so this drain is exactly as one-shot as `children.Drain()` above: only
                // the call that actually removes the entry delivers its SIGKILL, and a concurrent
                // teardown that got there first owns that delivery instead of both of them repeating it.
                match adopted.TryRemove pid with
                | true, _ -> Native.Posix.killAdopted pid anchor |> ignore
                | false, _ -> ()

/// FreeBSD kernel **process-reaper** backend (`Mechanism.ProcessReaper`): `procctl(2)`'s `PROC_REAP_*`
/// commands layered over the POSIX process-group backend.
///
/// # What this layer adds, and what it reuses
///
/// Everything about *starting* and *reaping a child* stays with `ProcessGroupBackend`: the `posix_spawn`
/// into its own pgid, the per-child `RLIMIT_CPU` rewrite, the tracked-leader ledger and its captured
/// start-time identities, the single-waiter discipline (K-016), the adopted-pid ledger. This backend adds
/// exactly the whole-tree semantics on top:
///
///  - **membership** comes from `PROC_REAP_GETPIDS` — the real tree — instead of the tracked group leaders,
///  - **delivery** (`KillTree`/`Signal`/`Suspend`/`Resume`/the graceful tiers) goes through
///    `PROC_REAP_KILL`, which reaches the `setsid` escapees `killpg` cannot.
///
/// That composition is deliberate, and it is not "silently reusing the process group as the FreeBSD
/// mechanism": the two layers answer different questions, and the reaper genuinely cannot answer the first
/// one — it has no spawn primitive, no exit-status channel, and no way to know which of the processes it
/// enumerates are OUR children and therefore ours to `waitpid`. Re-implementing that half here would
/// duplicate the invariants this codebase has already paid for (single waiter per child, identity-gated
/// by-number kills, snapshot-then-work under teardown races) as a second, divergent copy. What the
/// mechanism REPORTS is honest either way: a group backed by this type reports `Mechanism.ProcessReaper`
/// and really does contain the whole descendant tree, while a FreeBSD host where `PROC_REAP_ACQUIRE` failed
/// never gets this type at all — it gets the plain `ProcessGroupBackend` and says `Mechanism.ProcessGroup`
/// (see `ProcessGroup.Create`).
///
/// # Scoping one group inside a process-wide reaper
///
/// Reaper status is a property of the PROCESS, so several live groups share one reaper tree. Each group is
/// scoped by the kernel's own subtree tag instead: every child this process forks roots a subtree named by
/// its own pid, and every descendant of that child carries that pid in `pi_subtree` for life. This backend
/// records the pids of the children *it* tracked and addresses only those subtrees, so one group can never
/// enumerate, signal or kill another group's tree — nor a child the embedding application started for
/// itself.
///
/// A root is released only on the kernel's own positive answer that nothing is left under it (an empty
/// descendant listing, or an `ESRCH` from `PROC_REAP_KILL`), never merely because its process group drained
/// or because a child was `Release`d. That asymmetry with `ProcessGroupBackend` is the whole point: a pgid
/// empties at exactly the moment a `setsid` escapee stops being reachable through `killpg`, which is the
/// moment the reaper is the ONLY thing that can still reach into that subtree.
type internal ProcessReaperBackend
    (group: ProcessGroupBackend, ops: Native.FreeBsd.ReaperOps, initialLimits: ResourceLimits) =

    let contained = group :> IContainmentBackend

    // The subtree roots this group owns, behind one lock — the same single-gate discipline
    // `TrackedChildren` applies to the POSIX ledger, and for the same reason: a spawn racing a teardown
    // must serialize on record / prune / snapshot so a root can never be missed by teardown or aliased
    // after it is dropped. `nextSeq` stamps each root so `pruneRoots` can tell a root recorded by a
    // concurrent spawn (which the listing being judged could not have contained) from one the kernel
    // really reports as empty.
    let sync = obj ()
    let items = List<Native.FreeBsd.Root>()
    let mutable nextSeq = 0UL

    // The stamp a root recorded from now on will carry. Read BEFORE a listing, always, so the prune that
    // listing drives keeps anything recorded after it.
    let seqMark () = lock sync (fun () -> nextSeq)

    let rootSnapshot () : Native.FreeBsd.Root list = lock sync (fun () -> List.ofSeq items)

    let rootPids () : Set<int> =
        lock sync (fun () -> items |> Seq.map (fun root -> root.Pid) |> Set.ofSeq)

    let pruneWith (listing: Native.FreeBsd.Listing) (since: uint64) =
        lock sync (fun () ->
            let kept = Native.FreeBsd.pruneRoots listing since (List.ofSeq items)
            items.Clear()
            items.AddRange kept)

    // Forget one specific root, matched by its STAMP as well as its pid: if a concurrent `Track` replaced
    // the entry in between — a new child that inherited the recycled number — the replacement stays.
    let forget (root: Native.FreeBsd.Root) =
        lock sync (fun () -> items.RemoveAll(fun item -> item = root) |> ignore)

    // Record a freshly-tracked child as a subtree root, AFTER forgetting the roots the kernel reports as
    // empty — the "prune, then track" order the POSIX ledger uses, and for the same reason: a spawn is
    // precisely the moment a pid this group still remembers can be handed out again, so it is the moment a
    // stale root is most expensive to keep. An unreadable listing prunes nothing (under-pruning keeps a
    // stale root; over-pruning drops containment). A new entry always REPLACES an existing one for the same
    // pid with a FRESH stamp, which is what tells a concurrent membership read "recorded after your
    // listing; keep it".
    let recordRoot (pid: int) =
        if pid > 0 then
            let since = seqMark ()
            let listing = ops.Descendants() |> Result.toOption

            lock sync (fun () ->
                match listing with
                | Some listing ->
                    let kept = Native.FreeBsd.pruneRoots listing since (List.ofSeq items)
                    items.Clear()
                    items.AddRange kept
                | None -> ()

                items.RemoveAll(fun root -> root.Pid = pid) |> ignore
                items.Add { Pid = pid; Seq = nextSeq }
                nextSeq <- nextSeq + 1UL)

    // One reaper read: collect the re-parented corpses it exposes (the duty that being the reaper takes
    // on) and, unless the caller is bound by a probe-only contract, prune the roots it proves empty.
    let readListing (prune: bool) : Result<Native.FreeBsd.Listing, string> =
        let since = seqMark ()

        match ops.Descendants() with
        | Error message -> Error message
        | Ok listing ->
            Native.FreeBsd.sweepStrayZombies ops listing

            if prune then
                pruneWith listing since

            Ok listing

    let readMembers (prune: bool) : Result<int list, string> =
        readListing prune
        |> Result.map (fun listing -> Native.FreeBsd.membersOf (rootPids ()) listing.Entries)

    let membershipError (message: string) =
        ProcessError.Io $"could not read the process reaper's descendant tree (procctl(PROC_REAP_GETPIDS)): {message}"

    let describeDeliveryFailure (failure: Native.FreeBsd.DeliveryFailure) =
        match failure with
        | Native.FreeBsd.DeliveryFailure.SyscallFailure(errno, message, _) -> $"{message} (errno {errno})"
        | Native.FreeBsd.DeliveryFailure.PartialFailure(killed, firstFailingPid) ->
            $"partial delivery: {killed} member(s) received the signal, but the first failing member was {firstFailingPid}"

    // Deliver `signalNum` to every root this group owns, bracketed by the pruning that makes a stale root
    // safe: the root set is refreshed against the kernel immediately BEFORE the sweep and the targets are
    // taken from it AFTERWARDS, and a root the kernel answers `ESRCH` for during the sweep is released
    // there and then. Delivery is the one operation where a stale root is actually dangerous — a recycled
    // number would aim `PROC_REAP_KILL` at a subtree this group does not own — so it is the one operation
    // that pays for a fresh read.
    //
    // Note the deliberate DIFFERENCE from `ProcessGroupBackend.GracefulKillTree`, which snapshots its
    // pgids and identity tokens up front and works from that copy for every phase (K-086). The two are not
    // the same kind of state. There, the snapshot preserves the identity TOKEN that makes a by-number kill
    // safe, and a concurrent teardown clearing `identities` would leave the poll unable to prove a target
    // is still ours. Here the root set IS the target list, and a stale entry is precisely the wrong-target
    // hazard — so re-reading it after a prune is what makes the delivery safe, not what endangers it.
    // Re-reading can only ever NARROW the list, and only for one of two reasons: the kernel positively
    // reported that subtree empty (nothing left to signal), or a concurrent teardown cleared the set — and
    // that teardown is itself SIGKILLing the same tree.
    let deliver (signalNum: int) : Native.FreeBsd.SweepOutcome =
        readListing true |> ignore
        let outcome = Native.FreeBsd.deliverToRoots ops signalNum (rootSnapshot ())

        for drained in outcome.Drained do
            forget drained

        outcome

    let deliverResult
        (signalNum: int)
        (describe: Native.FreeBsd.DeliveryFailure -> string)
        : Result<unit, ProcessError> =
        match (deliver signalNum).Failure with
        | None -> Ok()
        | Some failure -> Error(ProcessError.Io(describe failure))

    // The whole-tree hard kill, shared by `KillTree` and `Signal(Signal.Kill)`. BOTH layers sweep,
    // deliberately: the reaper kill is the one that reaches the whole tree, while the process-group sweep
    // keeps the shared ledger's own bookkeeping (liveness pruning, the adopted-pid kills) and its honest
    // per-pgid verdict. Doubling delivery is harmless HERE AND ONLY HERE — `SIGKILL` cannot be handled,
    // blocked or counted, so a process the first sweep killed simply is not there for the second.
    // `Signal(Int/Term)`, whose signal a child can observe, never does this.
    let killWholeTree () : Result<unit, ProcessError> =
        let reaper =
            deliverResult (Native.Posix.signalNumber Signal.Kill) (fun failure ->
                $"failed to SIGKILL the process reaper's tree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}")

        let posix = contained.KillTree()

        match reaper with
        | Error error -> Error error
        | Ok() -> posix

    new(group: ProcessGroupBackend, initialLimits: ResourceLimits) =
        ProcessReaperBackend(group, Native.FreeBsd.native, initialLimits)

    /// Internal diagnostic (the `Native.Posix.pidfdActive` convention, not public API): the subtree roots
    /// this group currently owns. Lets a test assert the record/prune/forget lifecycle without a live tree.
    member _.Roots = rootSnapshot ()

    /// Test seam: record `pid` as a subtree root WITHOUT going through `Track`, which would also enter it
    /// in the POSIX layer's ledger and so drag that layer's real `killpg`/`waitpid` primitives into a test
    /// that only means to exercise the reaper half (they are libc calls, unavailable on a Windows build
    /// host). Production always goes through `Track`; nothing here bypasses a check that path makes — this
    /// IS the second half of `Track`, called on its own.
    member internal _.RecordRootForTests(pid: int) = recordRoot pid

    interface IContainmentBackend with
        member _.Mechanism = Mechanism.ProcessReaper

        member _.Spawn(command) = contained.Spawn command

        member _.Track(spawned) =
            // Track through the POSIX ledger FIRST (it owns the child's wait/reap and its identity token),
            // then record the same pid as a reaper subtree root. There is no fallible step between the two,
            // so the window in which a child exists but belongs to no subtree is the same instruction-wide
            // one the spawn's own guard already covers.
            match contained.Track spawned with
            | Error error -> Error error
            | Ok() ->
                match contained.PidOf spawned with
                | Some pid -> recordRoot pid
                | None -> ()

                Ok()

        member _.Adopt(pid) =
            // The reaper contains this process's own DESCENDANTS; a foreign process is not one, and no
            // `PROC_REAP_*` call can make it one. `PROC_REAP_ACQUIRE` deliberately does not even re-attach
            // children this process forked BEFORE it ran ("we do not reattach existing children"), which is
            // the sharpest statement of that boundary. So this verb is exactly as impossible here as on the
            // POSIX process group underneath, and is refused by it — never a root recorded for a subtree
            // that names nothing, which teardown would then believe it had contained.
            contained.Adopt pid

        member _.AdoptByPid(pid) =
            // Delegated verbatim, and the reaper does not change the answer: it is a MEMBERSHIP mechanism
            // for this process's own descendants, not an identity mechanism for an arbitrary number. A
            // process an outside supervisor started is not a descendant at all, so no `PROC_REAP_*` call of
            // ours reaches it; what would be left is the POSIX layer tracking a bare number with no
            // start-time token behind it, which is precisely what it refuses to do (FreeBSD ships no
            // start-time reader this library can verify).
            contained.AdoptByPid pid

        member _.Release(spawned) =
            // The POSIX ledger stops tracking a reaped child; the reaper root deliberately does NOT go with
            // it. A root is released only on the kernel's own positive answer that its subtree is empty
            // (the ordinary prune, or an `ESRCH` from a delivery) — dropping it here would forget the
            // subtree at exactly the moment a `setsid` escapee under it stops being reachable any other way.
            contained.Release spawned

        member _.Wait(handle) = contained.Wait handle
        member _.PidOf(spawned) = contained.PidOf spawned

        member _.KillChild(spawned) =
            // This one child's whole SUBTREE, which is what "its own containment unit" means on this
            // mechanism — `setsid` escapees included, where the POSIX layer's `killpg` would miss them. The
            // POSIX kill still runs behind it: it is identity-gated, it prunes the tracked ledger, and it is
            // the layer that reaps. A doubled `SIGKILL` is invisible to the target (see `killWholeTree`).
            match contained.PidOf spawned with
            | Some pid -> ops.SignalSubtree pid (Native.Posix.signalNumber Signal.Kill) |> ignore
            | None -> ()

            contained.KillChild spawned

        member _.KillTree() = killWholeTree ()

        member _.GracefulKillTree (signal) (grace) =
            let signalNum = Native.Posix.signalNumber signal

            // Every phase reads the LIVE root set rather than a copy snapshotted up front, which is the
            // deliberate difference from `ProcessGroupBackend.GracefulKillTree` — see `deliver`'s own
            // comment for why the two kinds of state pull in opposite directions here. The poll still runs
            // OFF the group's lifecycle lock; what a concurrent teardown can do to this set is remove
            // roots, and every phase fails safe on that: a delivery signals fewer subtrees (the teardown
            // that removed them is SIGKILLing the same tree), and the liveness probe below can only report
            // "drained" for a group whose roots that teardown has already killed.
            GracefulTeardown.poll
                (fun () ->
                    // The soft sweep, through the reaper ONLY — never also the process-group sweep
                    // underneath. A soft `Term`/`Int` is a signal the child OBSERVES, and delivering it
                    // twice would make a child that treats the second request as "force quit now" skip its
                    // own graceful path. The reaper already covers every process `killpg` would reach, plus
                    // the escapees, each exactly once.
                    //
                    // A syscall-level failure keeps the existing "every target" rule: it is `Failed` only
                    // when nothing was delivered. A FreeBSD `rk_fpid` partial response is different: the
                    // kernel explicitly says that one member refused after others were reached, so it is
                    // always `Failed`. A vacuous sweep (every subtree already drained, including an empty
                    // group) has neither, so it is `Sent` — the same rule the POSIX backend applies.
                    let outcome = deliver signalNum

                    match outcome.Failure with
                    | Some(Native.FreeBsd.DeliveryFailure.PartialFailure _) -> SoftDelivery.Failed
                    | Some _ when not outcome.AnyDelivered -> SoftDelivery.Failed
                    | _ -> SoftDelivery.Sent)
                (fun () ->
                    // Probe-only: this is the drain check, so it must not prune the tracked root set. An
                    // unreadable listing NEVER guesses "drained" (that would end the grace early and hand
                    // the tree an unearned hard kill) — it falls back to the containment still in force
                    // underneath, the POSIX layer's own liveness answer.
                    match readListing false with
                    | Ok listing -> not (List.isEmpty (Native.FreeBsd.membersOf (rootPids ()) listing.Entries))
                    | Error _ ->
                        match contained.Members() with
                        | Ok members -> not (List.isEmpty members)
                        | Error _ -> true)
                (fun () ->
                    // The escalation: SIGKILL every subtree this group still owns — the reaper's maximum
                    // reach — then run the POSIX layer's kill-AND-REAP over its tracked leaders. The reap
                    // half is load-bearing, not tidiness: `ShutdownReportAsync` samples `MembersAfter` behind
                    // this call (R-01 / K-179). The reaper's own membership already excludes zombies by
                    // construction, so an unreaped corpse could not be counted there anyway; what the reap
                    // prevents is our own leaders lingering as zombies after a NON-terminal graceful stop,
                    // where nothing else would collect them until final teardown.
                    deliver (Native.Posix.signalNumber Signal.Kill) |> ignore
                    group.HardKillAndReapTracked())
                grace

        member _.SoftStopScope() =
            // `PROC_REAP_KILL` addresses every process in every subtree this group owns on every live
            // membership — so this needs no native read at all. `WholeTree` describes that target scope,
            // not delivery: a member can refuse the later call and the result-returning signal verb reports
            // that partial outcome. Unlike the POSIX process group, whose one documented escapee is a child
            // that `setsid`s away, nothing here escapes the reaper's target tree.
            SoftStopScope.WholeTree

        member _.SignalChild(spawned, signal) =
            let signalNum = Native.Posix.signalNumber signal

            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                match contained.PidOf spawned with
                | None -> Ok()
                | Some pid ->
                    // This child's whole subtree, once — the reaper's reach rather than `killpg`'s, and
                    // deliberately NOT also the POSIX per-child delivery: an observable signal must not be
                    // delivered twice to the same child.
                    match ops.SignalSubtree pid signalNum with
                    | Native.FreeBsd.SubtreeDelivery.Delivered
                    | Native.FreeBsd.SubtreeDelivery.SubtreeGone -> Ok()
                    | Native.FreeBsd.SubtreeDelivery.DeliveryFailed(errno, message, firstFailingPid) ->
                        if Native.FreeBsd.isHonestFailure ops errno firstFailingPid then
                            let failure =
                                Native.FreeBsd.DeliveryFailure.SyscallFailure(errno, message, firstFailingPid)

                            Error(
                                ProcessError.Io
                                    $"failed to deliver signal {signalNum} to this run's subtree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}"
                            )
                        else
                            // An `EPERM` against a target that is not a positively live member is the
                            // "already dead" case every unix backend here keeps as a best-effort success.
                            Ok()
                    | Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(killed, firstFailingPid) ->
                        let failure = Native.FreeBsd.DeliveryFailure.PartialFailure(killed, firstFailingPid)

                        Error(
                            ProcessError.Io
                                $"failed to deliver signal {signalNum} to this run's subtree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}"
                        )

        member _.Members() =
            // The WHOLE TREE, not just the tracked group leaders: every live descendant of every child this
            // group started, read from the kernel's own reaper listing. This is the headline difference from
            // the POSIX process group, which can only report the leaders it tracks and is blind to anything
            // that `setsid`ed away. Zombies are excluded — a member that has exited is not a member — which
            // is also what keeps a membership read taken right after a kill honest.
            match readMembers true with
            | Ok members -> Ok members
            | Error message -> Error(membershipError message)

        member _.Signal(signal) =
            let signalNum = Native.Posix.signalNumber signal

            // Refuse a non-deliverable number (signal 0 — a liveness probe — or a negative) at the API
            // boundary, before any delivery, so a probe can never masquerade as a delivered signal. It is
            // also what keeps this backend clear of `PROC_REAP_KILL`'s own refusal of any number below 1
            // (`EINVAL`): such a request never reaches the kernel here.
            match Native.Posix.ensureDeliverable signalNum with
            | Error error -> Error error
            | Ok() ->
                match signal with
                | Signal.Kill -> killWholeTree ()
                | _ ->
                    deliverResult signalNum (fun failure ->
                        $"failed to deliver signal {signalNum} to the process reaper's tree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}")

        member _.Suspend() =
            deliverResult Native.Posix.suspendSignalNumber (fun failure ->
                $"failed to suspend the process reaper's tree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}")

        member _.Resume() =
            deliverResult Native.Posix.resumeSignalNumber (fun failure ->
                $"failed to resume the process reaper's tree (procctl(PROC_REAP_KILL)): {describeDeliveryFailure failure}")

        member _.Stats() =
            // The active count is the WHOLE tree — the exact process count a cgroup or a Job Object would
            // report, not the POSIX backend's live-leader tally. Every MEASUREMENT stays absent, and the
            // reaper changes nothing about that: it is a containment relationship, not a container, and
            // carries no CPU/memory accumulator, no I/O counter, and no peak record at all. Reporting the
            // one thing it genuinely knows — who is in the tree right now — and leaving the rest `None` is
            // the honest answer; zeroes would not be.
            match readMembers true with
            | Ok members -> Ok(ProcessGroupStats(List.length members, None, None, None, None))
            | Error message -> Error(membershipError message)

        member _.MemberStats() =
            // Membership from the reaper (the whole tree), metrics from the shared POSIX reader — which on
            // FreeBSD honestly reports a member with every metric absent: there is no `/proc` here by
            // default and this port carries no `sysctl(KERN_PROC)` reader, so a CPU/memory figure would have
            // to be invented. A member that vanished between the enumeration and its read is omitted rather
            // than fabricated, exactly as on the other POSIX backends.
            match readMembers true with
            | Ok members -> Ok(members |> List.choose Native.Posix.readMemberStats)
            | Error message -> Error(membershipError message)

        member _.UpdateLimits(limits) =
            // A reaper accounts for nothing (see `Stats`), so there is no whole-tree cap to update — the
            // exact reason `ProcessGroup.Create` already refuses to build a limited group on this mechanism.
            // Refuse the update the same honest, typed way rather than pretending to have applied caps no
            // kernel container is enforcing, and never through an `RLIMIT_*` surrogate: a per-process rlimit
            // bounds each process separately and is not a whole-tree cap, so presenting one as the answer
            // here would be exactly the silent downgrade this library rules out. The classification mirrors
            // the POSIX process group's, because the reasons are the same class of reason: `Unsupported`
            // where the concept has no counterpart on this platform at all, `ResourceLimit` where the cap
            // exists as a concept but nothing here can enforce it.
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
                            "the FreeBSD process reaper contains a whole tree but accounts for nothing in it, so it has no whole-tree resource-limit primitive to update (needs a Windows Job Object or Linux cgroup v2)"
                    )

        member _.LimitEvidence(_capped: CappedAxes) : LimitEvidence =
            // No whole-tree accounting means no post-mortem counters either, so every axis is `Unknown`
            // UNCONDITIONALLY — deliberately ignoring `capped`, exactly as the POSIX process group does and
            // for the same reason. `Unknown` here means "there is no evidence apparatus on this mechanism at
            // all", never "a cap may have fired unseen": this mechanism refuses to carry a cap in the first
            // place.
            LimitEvidence(LimitVerdict.Unknown, LimitVerdict.Unknown, LimitVerdict.Unknown)

        member _.HardRelease() =
            // The order matters, and it is the reverse of the layering. First the reaper's own SIGKILL,
            // whose reach is the whole tree including anything that `setsid`ed out of every process group
            // this backend knows about. Then a BOUNDED drain: an orphan the reaper inherited becomes a
            // zombie OF THIS PROCESS when it dies, and after this call there is no later reaper read to
            // sweep it — so this is the one moment that must wait for the corpses it just created (see
            // `Native.FreeBsd.DrainBudget` for why the wait is bounded and why only teardown pays it).
            // Finally the POSIX layer's own teardown, which kills and REAPS our own tracked leaders (the
            // reaper cannot: `waitpid` is the ledger's job) and clears both of its ledgers.
            deliver (Native.Posix.signalNumber Signal.Kill) |> ignore

            Native.FreeBsd.drainDeadUsing
                (fun () ->
                    let stopwatch = Stopwatch.StartNew()
                    fun () -> stopwatch.Elapsed)
                (fun duration -> System.Threading.Thread.Sleep duration)
                (fun () -> readListing true)
                rootPids
                Native.FreeBsd.DrainBudget

            contained.HardRelease()

            // The roots are dropped only now, after the kill, the drain and the POSIX teardown have all run:
            // until then each one is this group's only handle on a `setsid` escapee under it, and a set
            // cleared early would put such a process beyond every sweep this backend owns.
            lock sync (fun () -> items.Clear())
