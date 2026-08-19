namespace ProcessKit.Native

open System
open System.Runtime.InteropServices

/// FreeBSD kernel **process reaper** — `procctl(2)`'s `PROC_REAP_*` commands, the one whole-tree
/// containment primitive any unix outside Linux offers.
///
/// Acquiring reaper status (`PROC_REAP_ACQUIRE`) makes this process the reaper of its entire descendant
/// tree: every descendant, however deeply forked and whether or not it called `setsid`, stays inside that
/// tree, can be enumerated (`PROC_REAP_GETPIDS`) and can be signalled one subtree at a time
/// (`PROC_REAP_KILL` with `REAPER_KILL_SUBTREE`). That closes the documented escape hatch of the POSIX
/// process-group backend — a child that `setsid`s out of the group `killpg` addresses — and is surfaced
/// as `Mechanism.ProcessReaper`, never as a silent upgrade of `Mechanism.ProcessGroup`.
///
/// # One reaper per PROCESS, many groups
///
/// Reaper status is a property of a **process**, not of a container object: there is no way to open
/// several independent reaper scopes inside one process. So this layer acquires it once, process-wide
/// (`acquireReaperStatus`), and each `ProcessGroup` scopes itself with the kernel's own subtree tag
/// instead. Every process this one forks roots a *subtree* identified by its own pid (`pi_subtree ==
/// pi_pid`), and every descendant of that child carries that pid in `pi_subtree` for life — fixed at fork
/// and never rewritten, not even when the process is later re-parented. A group therefore records the
/// pids of the children **it** started (its roots) and addresses only those subtrees, so one group can
/// never kill or count another group's tree, nor a child the embedding application spawned for itself.
///
/// Reaper status is acquired lazily and **never released**: the flag is shared by every live group in the
/// process (and possibly by the application, which may have acquired it first — `EBUSY`, treated as
/// success), so releasing it for one group would strip containment from the others. It owns no resource;
/// it is a process flag whose only effect is the re-parenting described next.
///
/// # The obligation it takes on: re-parented orphans
///
/// Being the reaper means an orphaned descendant — one whose parent died first, the classic daemonising
/// double fork — is re-parented to **us** instead of to `init`. That is exactly the containment we want,
/// but it transfers `init`'s duty with it: when such a process exits it becomes a zombie *of this
/// process* and someone must `wait` for it. Nothing else will. `sweepStrayZombies` discharges that duty
/// without ever touching a process some other owner is waiting for: it reaps only entries with
/// `pi_subtree <> pi_pid`, i.e. processes this process did **not** fork itself. (The kernel's
/// `REAPER_PIDINFO_CHILD` flag is deliberately NOT the test — it means "is currently a direct child",
/// which a re-parented orphan also is, so using it would `waitpid` a child whose exit status this
/// library's own run verbs are waiting for. See K-016 on why exactly one waiter per child is
/// load-bearing.)
///
/// # What it does not provide
///
/// A reaper is a containment *relationship*, not a container: there is no aggregate memory/CPU/pids
/// counter anywhere behind `procctl`, so whole-tree resource limits stay refused here exactly as on the
/// POSIX process group — never approximated with an `RLIMIT_*` surrogate. FreeBSD also has no `/proc` by
/// default and this port carries no `sysctl(KERN_PROC)` reader, so per-member metrics stay honestly
/// absent (`Native.Posix.readMemberStats` already reports that shape on a bare BSD).
///
/// # Testability
///
/// Everything above the four syscalls is pure and driven through the injected `ReaperOps` seam, so the
/// containment logic — membership, root pruning, the zombie sweep's selection rule, the `EPERM`
/// discrimination, the listing's growth/truncation loop — is exercised deterministically from ANY build
/// host. That is the same seam convention `GracefulTeardown.pollUsing`, `CgroupMemberStats.sample` and
/// `MechanismSelection.chooseUsing` already follow, and here it is what makes a backend whose native path
/// only ever runs on FreeBSD reviewable and testable on Windows/Linux/macOS.
module internal FreeBsd =

    /// Whether this host is FreeBSD — the ONE gate every `procctl` call site below sits behind, so a
    /// FreeBSD-only syscall is never issued on macOS, on another BSD, or on Linux. `OSPlatform.FreeBSD`
    /// is the runtime's own identification (shipped since .NET 5), not a probe of our own.
    let isFreeBsd = RuntimeInformation.IsOSPlatform OSPlatform.FreeBSD

    // ----------------------------------------------------------------------------------
    // `procctl(2)` reaper ABI
    //
    // The command numbers, flag bits and struct layouts are mirrored from FreeBSD's <sys/procctl.h>.
    // Rather than modelling four blittable structs, each one is read and written through explicit byte
    // offsets into an unmanaged buffer — the same style `Native.Posix` already uses for macOS's
    // `proc_pidinfo`, which keeps the layout visible at the call site and lets a hermetic test assert the
    // decoding against a synthetic buffer. `ReaperAbiTests` pins every size and offset below.
    // ----------------------------------------------------------------------------------

    /// `idtype_t P_PID` — address the command at one process by pid (the only target `PROC_REAP_ACQUIRE`
    /// accepts at all, and the one every command here uses: this process).
    [<Literal>]
    let private P_PID = 0

    /// `PROC_REAP_ACQUIRE` — become the reaper of this process's descendant tree.
    [<Literal>]
    let private PROC_REAP_ACQUIRE = 2

    /// `PROC_REAP_STATUS` — read reaper status (also the cheap descendant COUNT that sizes a listing).
    [<Literal>]
    let private PROC_REAP_STATUS = 4

    /// `PROC_REAP_GETPIDS` — enumerate the descendants of this process's reaper tree.
    [<Literal>]
    let private PROC_REAP_GETPIDS = 5

    /// `PROC_REAP_KILL` — deliver one signal to a whole (sub)tree in a single call.
    [<Literal>]
    let private PROC_REAP_KILL = 6

    /// `REAPER_STATUS_OWNED` — the queried process holds reaper status itself, as opposed to merely
    /// belonging to some other process's reaper tree.
    [<Literal>]
    let private REAPER_STATUS_OWNED = 0x0000_0001

    /// `REAPER_PIDINFO_VALID` — the kernel filled this array slot. `PROC_REAP_GETPIDS` reports no element
    /// count, so a slot without this bit is how the filled prefix ends.
    [<Literal>]
    let private REAPER_PIDINFO_VALID = 0x0000_0001

    /// `REAPER_PIDINFO_ZOMBIE` — the descendant has exited and awaits a `wait(2)`. Reported since FreeBSD
    /// 12.1; on an older kernel the bit is simply never set, which costs the zombie sweep a candidate but
    /// can never mislabel a live process as dead.
    [<Literal>]
    let private REAPER_PIDINFO_ZOMBIE = 0x0000_0008

    /// `REAPER_KILL_SUBTREE` — deliver only to the subtree rooted at `rk_subtree` (one direct child of
    /// this reaper), not to this process's whole tree. This is what scopes a delivery to ONE group.
    [<Literal>]
    let private REAPER_KILL_SUBTREE = 0x0000_0002

    /// `struct procctl_reaper_status`: `rs_flags`(0) `rs_children`(4) `rs_descendants`(8) `rs_reaper`(12)
    /// `rs_pid`(16) `rs_pad0[15]`(20).
    [<Literal>]
    let internal ReaperStatusSize = 80

    [<Literal>]
    let internal ReaperStatusFlagsOffset = 0

    [<Literal>]
    let internal ReaperStatusDescendantsOffset = 8

    [<Literal>]
    let internal ReaperStatusReaperOffset = 12

    /// `struct procctl_reaper_pidinfo`: `pi_pid`(0) `pi_subtree`(4) `pi_flags`(8) `pi_pad0[15]`(12).
    [<Literal>]
    let internal PidInfoSize = 72

    /// `struct procctl_reaper_pids`: `rp_count`(0) `rp_pad0[15]`(4) `rp_pids`(64, a pointer).
    [<Literal>]
    let internal ReaperPidsPointerOffset = 64

    /// `struct procctl_reaper_kill`: `rk_sig`(0) `rk_flags`(4) `rk_subtree`(8) `rk_killed`(12)
    /// `rk_fpid`(16) `rk_pad0[15]`(20).
    [<Literal>]
    let internal ReaperKillSize = 80

    [<Literal>]
    let internal ReaperKillFirstFailingPidOffset = 16

    // errno values FreeBSD reports back from these commands. `ESRCH`/`EPERM`/`EINVAL` share their
    // numbers with Linux and macOS; `EBUSY` (16) is the "already a reaper" answer `PROC_REAP_ACQUIRE`
    // gives, which is a success for this layer's purposes.
    [<Literal>]
    let private EPERM = 1

    [<Literal>]
    let private ESRCH = 3

    [<Literal>]
    let private EBUSY = 16

    /// `WNOHANG` — never block in the stray-zombie sweep.
    [<Literal>]
    let private WNOHANG = 1

    // `int procctl(idtype_t idtype, id_t id, int cmd, void *data)`. FreeBSD's `id_t` is a 64-bit integer
    // (`__int64_t`), and `idtype_t` is a C enum, i.e. an `int` — getting either width wrong would corrupt
    // the argument register layout, so both are spelled out here rather than inferred.
    [<DllImport("libc", SetLastError = true)>]
    extern int private procctl(int idtype, int64 id, int cmd, nativeint data)

    [<DllImport("libc", SetLastError = true)>]
    extern int private getpid()

    // Only ever called for a re-parented orphan (never for a process this one forked itself), and always
    // with `WNOHANG`, so it cannot block and cannot steal a `Child`'s exit status. A pid that is not (or
    // is no longer) our child simply answers `ECHILD`.
    [<DllImport("libc", SetLastError = true)>]
    extern int private waitpid(int pid, int& status, int options)

    /// One descendant, as `PROC_REAP_GETPIDS` reports it.
    [<Struct; NoComparison>]
    type PidInfo =
        {
            /// The descendant's pid.
            Pid: int

            /// The pid of the direct child of this reaper that roots the subtree this process belongs to.
            /// Fixed at fork and **never** rewritten, not even on re-parenting — which is what makes it,
            /// and not `REAPER_PIDINFO_CHILD`, the reliable "did we fork this ourselves?" test.
            Subtree: int

            /// The raw `pi_flags` bitmask.
            Flags: int
        }

        /// Whether the kernel actually filled this slot (`REAPER_PIDINFO_VALID`).
        member this.IsValid = (this.Flags &&& REAPER_PIDINFO_VALID) <> 0

        /// Whether the descendant has already exited and awaits a `wait(2)`.
        member this.IsZombie = (this.Flags &&& REAPER_PIDINFO_ZOMBIE) <> 0

        /// Whether **this process forked it directly** — the subtree it belongs to is the one it roots.
        /// Such a process is owned by whoever started it (one of this library's own run verbs, or the
        /// embedding application), so the zombie sweep must never `wait` for it: that would steal the exit
        /// status its owner is waiting for and free the pid for reuse behind its back (K-016).
        member this.IsOwnFork = this.Pid = this.Subtree

    /// One `PROC_REAP_GETPIDS` answer: the descendants the kernel reported, plus whether it may have had
    /// more to say than the buffer could hold.
    ///
    /// `Truncated` is load-bearing rather than diagnostic — see `pruneRoots`, which is a no-op on a
    /// truncated listing. Every other READER of the entries (membership, the zombie sweep, the liveness
    /// probe behind the `EPERM` discrimination) is safe with a partial list: each fails towards "fewer
    /// members / cannot prove liveness", never towards forgetting containment.
    [<NoComparison>]
    type Listing =
        { Entries: PidInfo list
          Truncated: bool }

        /// The listing of a process the kernel says has no descendants at all — complete by construction.
        static member Empty = { Entries = []; Truncated = false }

    /// The outcome of one `PROC_REAP_KILL` aimed at a single subtree.
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type SubtreeDelivery =

        /// The kernel walked the subtree and delivered the signal.
        | Delivered

        /// errno `ESRCH`: nothing in that subtree matched — it has drained. A best-effort success, and the
        /// one positive kernel answer that releases a root (see `pruneRoots`).
        | SubtreeGone

        /// Any other errno. `FirstFailingPid` is the kernel's `rk_fpid`, copied back **even on error**,
        /// which is what lets the `EPERM` classification name the offending member instead of guessing.
        | DeliveryFailed of Errno: int * Message: string * FirstFailingPid: int

    /// One subtree root a group owns: the pid of a child it started, plus the order in which it was
    /// recorded. The sequence number is what lets `pruneRoots` tell a root a concurrent spawn recorded
    /// after a listing was taken (which that listing could not possibly have contained) from one the
    /// kernel really reports as empty.
    [<Struct>]
    type Root = { Pid: int; Seq: uint64 }

    /// The native reaper primitives one containment backend drives, injected as a record so everything
    /// built on them stays exercisable from any build host.
    [<NoComparison; NoEquality>]
    type ReaperOps =
        {
            /// Every descendant of this process, as the kernel's reaper tree sees it — including ones that
            /// `setsid`ed away, ones re-parented to us, and zombies.
            Descendants: unit -> Result<Listing, string>

            /// Deliver a raw signal number to one subtree (`PROC_REAP_KILL` + `REAPER_KILL_SUBTREE`),
            /// taking the root pid first and the signal second.
            SignalSubtree: int -> int -> SubtreeDelivery

            /// `waitpid(pid, WNOHANG)` for one re-parented corpse. Best-effort and never blocking.
            ReapZombie: int -> unit
        }

    // ----------------------------------------------------------------------------------
    // Pure containment logic over a listing (no syscalls; all of it hermetically testable)
    // ----------------------------------------------------------------------------------

    /// Whether `entry` is a live member of the tree rooted at one of `roots`.
    ///
    /// A zombie is excluded deliberately: it is a dead process awaiting a `wait`, and reporting it as a
    /// member would make `Members()`/the graceful driver's drain check claim a tree is still up after
    /// everything in it has exited. That exclusion is also what keeps this mechanism clear of K-179: the
    /// escalation kill's membership read cannot count an unreaped corpse as a survivor.
    let isMember (roots: Set<int>) (entry: PidInfo) : bool =
        entry.IsValid && not entry.IsZombie && roots.Contains entry.Subtree

    /// Whether `entry` is a zombie this process must `wait` for — a descendant it did **not** fork itself,
    /// which therefore reached us only by being re-parented when its own parent died.
    ///
    /// Deliberately NOT restricted to any one group's roots: the re-parenting happens because this process
    /// is the reaper at all, so the duty spans the whole process, and a corpse left by a group that has
    /// since been disposed must still be collected by whoever sweeps next.
    let isStrayZombie (entry: PidInfo) : bool =
        entry.IsValid && entry.IsZombie && not entry.IsOwnFork

    /// Whether anything is still alive **below** one of `roots` — the condition the teardown drain waits
    /// out. The roots themselves are excluded: they are processes some run verb owns and reaps, so waiting
    /// for one to disappear would be waiting on someone else's `wait`.
    let hasLiveDescendant (roots: Set<int>) (entries: PidInfo list) : bool =
        entries
        |> List.exists (fun entry -> not entry.IsOwnFork && isMember roots entry)

    /// The live member pids of `roots`' subtrees, in listing order.
    let membersOf (roots: Set<int>) (entries: PidInfo list) : int list =
        entries
        |> List.choose (fun entry -> if isMember roots entry then Some entry.Pid else None)

    /// Forget every root whose subtree the kernel no longer knows anything about — no live member, no
    /// zombie, nothing.
    ///
    /// This is the recycled-number defence, and on this platform it is the *only* one available: the POSIX
    /// backend can additionally identity-gate a tracked id before signalling it, but
    /// `Native.Posix.readProcessIdentity` has no reader on the BSDs, so PROMPTNESS is the whole
    /// mitigation. A subtree is named by a pid; once everything under that pid is gone and the number is
    /// reaped, the OS can hand it to a new child of this same process, and a root kept past that point
    /// would alias the newcomer's subtree. What remains after pruning at every point that could make a
    /// root stale (each spawn, each membership read, each delivery sweep and its `ESRCH`es, and throughout
    /// the teardown drain) is a narrow window — and even then the mistake is confined to another tree of
    /// this same process, never to an unrelated process the way a recycled *pgid* could be.
    ///
    /// Two things deliberately do **not** prune:
    ///
    ///  - a **truncated** listing — it proves nothing about the roots it never reached, and treating its
    ///    silence as "empty" would drop live subtrees precisely when the tree is forking fastest;
    ///  - a root stamped at or after `since`, the sequence mark taken BEFORE the listing was read. Such a
    ///    root was recorded by a concurrent spawn the listing could not possibly contain, so pruning it
    ///    would drop a brand-new child's subtree and silently narrow teardown to what `killpg` reaches.
    let pruneRoots (listing: Listing) (since: uint64) (roots: Root list) : Root list =
        if listing.Truncated then
            roots
        else
            roots
            |> List.filter (fun root ->
                root.Seq >= since
                || listing.Entries |> List.exists (fun entry -> entry.Subtree = root.Pid))

    /// Collect every re-parented corpse the listing exposes. Safe to run from any thread, as often as
    /// convenient: two groups sweeping concurrently just race to a harmless `ECHILD` no-op.
    let sweepStrayZombies (ops: ReaperOps) (listing: Listing) : unit =
        for entry in listing.Entries do
            if isStrayZombie entry then
                ops.ReapZombie entry.Pid

    /// Whether `pid` is a positively **live, non-zombie** descendant right now. Only against such a target
    /// is a delivery `EPERM` a genuine containment gap worth surfacing, rather than the harmless "the
    /// target was already dead" case that must stay a success.
    ///
    /// Anything less than a positive live answer — an unreadable listing, a pid the tree no longer knows,
    /// a pid a truncated listing did not reach, a kernel too old to report `REAPER_PIDINFO_ZOMBIE` for a
    /// corpse — answers `false`, which is the fail-safe direction and exactly what the plain POSIX backend
    /// already does on a host with no state reader.
    let isLiveDescendant (ops: ReaperOps) (pid: int) : bool =
        pid > 0
        && (match ops.Descendants() with
            | Error _ -> false
            | Ok listing ->
                listing.Entries
                |> List.exists (fun entry -> entry.Pid = pid && entry.IsValid && not entry.IsZombie))

    /// Classify a `PROC_REAP_KILL` failure: a real containment failure worth surfacing, or one of the
    /// benign outcomes the POSIX backend has always swallowed?
    ///
    ///  - `EPERM` — a member refused the signal. Surfaced only against a positively live, non-zombie
    ///    member (the genuine `sudo`/set-uid containment gap). Against a corpse, or when liveness cannot be
    ///    established, it stays swallowed, so an ordinary teardown of a tree with unreaped children does
    ///    not fail spuriously.
    ///  - anything else (`EINVAL` for a malformed request, `ECAPMODE` in a Capsicum sandbox, …) — the tree
    ///    was **not** signalled, whatever the target's state. Surfaced rather than hidden.
    ///
    /// `ESRCH` never reaches here: `SubtreeDelivery` classifies it as `SubtreeGone`, a success.
    let isHonestFailure (ops: ReaperOps) (errno: int) (firstFailingPid: int) : bool =
        if errno = EPERM then
            isLiveDescendant ops firstFailingPid
        else
            true

    /// The verdict of one delivery sweep across every root a group owns.
    [<NoComparison>]
    type SweepOutcome =
        {
            /// The first honest failure `(errno, message)`, or `None` when the sweep succeeded. Every root
            /// is still visited before this is returned, so one failing subtree never leaves another
            /// unsignalled.
            Failure: (int * string) option

            /// The roots the kernel answered `ESRCH` for during the sweep: their subtrees drained between
            /// the pre-sweep prune and the delivery, so they are released now rather than at the next read.
            Drained: Root list

            /// Whether at least one root's subtree genuinely received the signal — what tells a vacuous
            /// sweep (everything already gone) from one where delivery failed for every live target, which
            /// the graceful report's `SoftDelivery.Failed` is defined in terms of.
            AnyDelivered: bool
        }

    /// Deliver `signalNum` to every root's subtree — the whole tree, each process exactly once, `setsid`
    /// escapees included. Never short-circuits: a failing subtree must not leave the rest unsignalled.
    let deliverToRoots (ops: ReaperOps) (signalNum: int) (roots: Root list) : SweepOutcome =
        let mutable failure: (int * string) option = None
        let mutable anyDelivered = false
        let drained = ResizeArray<Root>()

        for root in roots do
            match ops.SignalSubtree root.Pid signalNum with
            | SubtreeDelivery.Delivered -> anyDelivered <- true
            | SubtreeDelivery.SubtreeGone -> drained.Add root
            | SubtreeDelivery.DeliveryFailed(errno, message, firstFailingPid) ->
                if failure.IsNone && isHonestFailure ops errno firstFailingPid then
                    failure <- Some(errno, message)

        { Failure = failure
          Drained = List.ofSeq drained
          AnyDelivered = anyDelivered }

    /// How long a teardown may block waiting for the tree it just `SIGKILL`ed to actually die, so the
    /// re-parented corpses can be collected before the last sweeper — this group — is gone.
    ///
    /// This wait is what makes "the tree is gone once the group is disposed" true on this mechanism rather
    /// than nearly true: an orphan the reaper inherited stays visible until *this* process `wait`s for it,
    /// and after teardown there is no later call to sweep it. A killed process has no handler to run and
    /// dies as soon as it is scheduled, so the loop normally ends within a poll or two; the budget caps
    /// only pathological cases, and it is not entered at all unless the group really has descendants below
    /// its roots (the ordinary one-child group does not).
    ///
    /// **The number is this project's accepted ceiling, not a fresh judgement**: it is the same 100 ms the
    /// Linux cgroup backend blocks for while `cgroup.procs` drains (`Native.Cgroup.DefaultDrainBudget`),
    /// which is the bound this codebase documents wherever a teardown may stall a worker thread. The verbs
    /// reachable from async code (`KillTree`, the graceful escalation) deliberately do NOT drain at all:
    /// they leave a live group behind, and every later reaper read sweeps the corpses anyway.
    let DrainBudget = TimeSpan.FromMilliseconds 100.0

    /// Poll interval for that drain — mirroring the cgroup loop's 2 ms rather than spinning faster, since
    /// each poll here costs two syscalls and a killed tree is gone long before the difference shows.
    let DrainPoll = TimeSpan.FromMilliseconds 2.0

    /// The post-kill corpse drain, with its clock, its sleep and its reaper read injected (the
    /// `GracefulTeardown.pollUsing` seam shape), so the bound and the exit conditions are testable without
    /// a real tree.
    ///
    /// `readListing` is the caller's full reaper read — the one that ALSO sweeps stray zombies, which is
    /// what actually makes the loop terminate: a re-parented corpse disappears from the tree only once
    /// this process has `wait`ed for it. An unreadable listing ends the drain rather than spinning on a
    /// question the kernel will not answer.
    let drainDeadUsing
        (startClock: unit -> (unit -> TimeSpan))
        (sleep: TimeSpan -> unit)
        (readListing: unit -> Result<Listing, string>)
        (roots: unit -> Set<int>)
        (budget: TimeSpan)
        : unit =
        let elapsed = startClock ()
        let mutable draining = true

        while draining do
            match readListing () with
            | Error _ -> draining <- false
            | Ok listing ->
                // Zombies are excluded from `hasLiveDescendant` on purpose: the read above already
                // `wait`ed for the ones that are ours, and the rest belong to a live parent that is
                // counted here in its own right.
                if not (hasLiveDescendant (roots ()) listing.Entries) || elapsed () >= budget then
                    draining <- false
                else
                    sleep DrainPoll

    // ----------------------------------------------------------------------------------
    // The descendant listing: sizing, growth, and the truncation verdict
    // ----------------------------------------------------------------------------------

    /// Extra slots requested over the descendant count `PROC_REAP_STATUS` just reported, so the common
    /// case of a child forked between the two calls is absorbed without a second round trip.
    [<Literal>]
    let internal GetPidsSlack = 16

    /// How many times a listing may double its buffer before settling for what it got. A tree that keeps
    /// outgrowing four doublings is being forked into faster than it can be read, so a best-effort answer
    /// beats failing the read — but that answer is MARKED truncated rather than passed off as complete.
    [<Literal>]
    let internal GetPidsGrowAttempts = 4

    /// Ceiling on a single listing, so a runaway descendant count cannot turn into an unbounded
    /// allocation. Far above any realistic tree — FreeBSD's default system-wide process limit is well
    /// under this.
    [<Literal>]
    let internal GetPidsMax = 1048576

    /// The listing loop with its two kernel reads injected: `descendantCount` is `PROC_REAP_STATUS`'s
    /// `rs_descendants` (one cheap syscall that both sizes the buffer and lets a childless process skip
    /// the listing entirely), and `fill capacity` is one `PROC_REAP_GETPIDS` into a buffer of that many
    /// slots, returning the prefix the kernel actually filled.
    ///
    /// A filled prefix SHORTER than the buffer proves the listing is complete; an exactly-full buffer may
    /// have been truncated, so it grows and re-reads — and, once the attempts are spent, says so instead
    /// of pretending.
    let listUsing
        (descendantCount: unit -> Result<int, string>)
        (fill: int -> Result<PidInfo list, string>)
        : Result<Listing, string> =
        match descendantCount () with
        | Error message -> Error message
        | Ok count when count <= 0 -> Ok Listing.Empty
        | Ok count ->
            let rec grow (capacity: int) (attempt: int) =
                match fill capacity with
                | Error message -> Error message
                | Ok entries ->
                    if List.length entries < capacity then
                        Ok { Entries = entries; Truncated = false }
                    elif attempt + 1 >= GetPidsGrowAttempts || capacity >= GetPidsMax then
                        Ok { Entries = entries; Truncated = true }
                    else
                        grow (min GetPidsMax (capacity * 2)) (attempt + 1)

            grow (min GetPidsMax (max 1 (count + GetPidsSlack))) 0

    /// Decode one `struct procctl_reaper_pidinfo` out of a `PROC_REAP_GETPIDS` buffer. Internal (not
    /// private) so the ABI offsets are pinned by a hermetic test against a synthetic buffer rather than
    /// only by a FreeBSD run.
    let readPidInfoAt (buffer: nativeint) (index: int) : PidInfo =
        let entry = buffer + nativeint (index * PidInfoSize)

        { Pid = Marshal.ReadInt32(entry, 0)
          Subtree = Marshal.ReadInt32(entry, 4)
          Flags = Marshal.ReadInt32(entry, 8) }

    /// Read the filled prefix of a `PROC_REAP_GETPIDS` buffer: the kernel reports no element count, so the
    /// prefix ends at the first slot without `REAPER_PIDINFO_VALID` (the buffer is zeroed before the
    /// call). Internal for the same reason as `readPidInfoAt`.
    let readFilledPrefix (buffer: nativeint) (capacity: int) : PidInfo list =
        let entries = ResizeArray<PidInfo>()
        let mutable index = 0
        let mutable stop = false

        while not stop && index < capacity do
            let entry = readPidInfoAt buffer index

            if entry.IsValid then
                entries.Add entry
                index <- index + 1
            else
                stop <- true

        List.ofSeq entries

    // ----------------------------------------------------------------------------------
    // The four syscalls
    // ----------------------------------------------------------------------------------

    // Every `procctl` call below is addressed at THIS process (`P_PID` + our own pid) — the only target
    // `PROC_REAP_ACQUIRE` accepts at all, and the process whose reaper tree the other three commands are
    // about. `data` is null for the commands that take none (the kernel rejects a non-null pointer there
    // with EINVAL) and otherwise a fully-initialized buffer of that command's struct.
    let private procctlSelf (cmd: int) (data: nativeint) : Result<unit, int> =
        if procctl (P_PID, int64 (getpid ()), cmd, data) = 0 then
            Ok()
        else
            Error(Marshal.GetLastWin32Error())

    // Allocate a zeroed unmanaged buffer for one command struct. Zeroing is required, not hygiene:
    // `PROC_REAP_GETPIDS` delimits its answer by the slots it did NOT write.
    let private allocZeroed (size: int) : nativeint =
        let buffer = Marshal.AllocHGlobal size
        Marshal.Copy(Array.zeroCreate<byte> size, 0, buffer, size)
        buffer

    // The same errno-to-text rendering `Native.Common.classifySignalDelivery` uses, so a reaper delivery
    // failure reads like every other signal failure in this library.
    let private errnoText (errno: int) =
        System.ComponentModel.Win32Exception(errno).Message

    /// Read `PROC_REAP_STATUS`, returning `(flags, descendantCount, reaperPid)`.
    let private reapStatus () : Result<int * int * int, string> =
        let buffer = allocZeroed ReaperStatusSize

        try
            match procctlSelf PROC_REAP_STATUS buffer with
            | Error errno -> Error $"procctl(PROC_REAP_STATUS) failed: {errnoText errno} (errno {errno})"
            | Ok() ->
                Ok(
                    Marshal.ReadInt32(buffer, ReaperStatusFlagsOffset),
                    Marshal.ReadInt32(buffer, ReaperStatusDescendantsOffset),
                    Marshal.ReadInt32(buffer, ReaperStatusReaperOffset)
                )
        finally
            Marshal.FreeHGlobal buffer

    /// Make this process the reaper of its descendant tree, **once** per process, and report whether it
    /// now genuinely holds that status.
    ///
    /// Two non-failures are folded into success: `EBUSY` means the process is ALREADY a reaper (a second
    /// group, or an application that acquired the status before this library did), and being `init` in a
    /// jail has the same effect from birth. Either way the containment this backend needs is in place,
    /// which is why the outcome is confirmed by reading `PROC_REAP_STATUS` back rather than inferred from
    /// the return code. A genuine failure is not fatal: `ProcessGroup.Create` then falls back to the plain
    /// POSIX process-group backend and reports `Mechanism.ProcessGroup`, so the mechanism query never
    /// overstates the containment actually in force.
    ///
    /// Acquisition is a permanent, process-wide side effect, so it happens exactly once and NEVER from a
    /// capability probe (`ProcessGroup.Capabilities` must create nothing) — only from a real
    /// `ProcessGroup.Create` on FreeBSD.
    let private acquireOnce () : bool =
        let requested =
            match procctlSelf PROC_REAP_ACQUIRE IntPtr.Zero with
            | Ok() -> true
            | Error errno -> errno = EBUSY

        requested
        && (match reapStatus () with
            | Error _ -> false
            | Ok(flags, _, reaperPid) -> (flags &&& REAPER_STATUS_OWNED) <> 0 || reaperPid = getpid ())

    let private acquisition = lazy (acquireOnce ())

    /// Acquire (once) and report reaper status for this process. Always `false` off FreeBSD, so no caller
    /// needs its own platform guard.
    let acquireReaperStatus () : bool = isFreeBsd && acquisition.Force()

    /// One `PROC_REAP_GETPIDS` into a buffer of `capacity` slots.
    let private fillDescendants (capacity: int) : Result<PidInfo list, string> =
        let entries = allocZeroed (capacity * PidInfoSize)
        let request = allocZeroed (ReaperPidsPointerOffset + IntPtr.Size)

        try
            Marshal.WriteInt32(request, 0, capacity)
            Marshal.WriteIntPtr(request, ReaperPidsPointerOffset, entries)

            match procctlSelf PROC_REAP_GETPIDS request with
            | Error errno -> Error $"procctl(PROC_REAP_GETPIDS) failed: {errnoText errno} (errno {errno})"
            | Ok() -> Ok(readFilledPrefix entries capacity)
        finally
            Marshal.FreeHGlobal request
            Marshal.FreeHGlobal entries

    /// Every descendant of this process, as the kernel's reaper tree sees it.
    let descendants () : Result<Listing, string> =
        if not isFreeBsd then
            Error "the procctl(2) process reaper is a FreeBSD facility; this host is not FreeBSD"
        else
            listUsing (fun () -> reapStatus () |> Result.map (fun (_, count, _) -> count)) fillDescendants

    /// Deliver `signalNum` to the subtree rooted at `root` in one call (`PROC_REAP_KILL` +
    /// `REAPER_KILL_SUBTREE`) — the delivery that reaches a `setsid` escapee `killpg` cannot.
    let signalSubtree (root: int) (signalNum: int) : SubtreeDelivery =
        if not isFreeBsd then
            SubtreeDelivery.DeliveryFailed(
                0,
                "the procctl(2) process reaper is a FreeBSD facility; this host is not FreeBSD",
                0
            )
        else
            let request = allocZeroed ReaperKillSize

            try
                Marshal.WriteInt32(request, 0, signalNum)
                Marshal.WriteInt32(request, 4, REAPER_KILL_SUBTREE)
                Marshal.WriteInt32(request, 8, root)

                match procctlSelf PROC_REAP_KILL request with
                | Ok() -> SubtreeDelivery.Delivered
                | Error errno when errno = ESRCH -> SubtreeDelivery.SubtreeGone
                | Error errno ->
                    // `rk_fpid` is copied back even on error, so the EPERM classification can name the
                    // member that refused instead of guessing.
                    SubtreeDelivery.DeliveryFailed(
                        errno,
                        errnoText errno,
                        Marshal.ReadInt32(request, ReaperKillFirstFailingPidOffset)
                    )
            finally
                Marshal.FreeHGlobal request

    /// `waitpid(pid, WNOHANG)` for one re-parented corpse.
    let reapZombie (pid: int) : unit =
        if isFreeBsd && pid > 0 then
            let mutable status = 0
            waitpid (pid, &status, WNOHANG) |> ignore

    /// The production seam: the real syscalls.
    let native: ReaperOps =
        { Descendants = descendants
          SignalSubtree = signalSubtree
          ReapZombie = reapZombie }
