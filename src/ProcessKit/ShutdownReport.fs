namespace ProcessKit

open System

/// The fate of a graceful teardown's best-effort **soft-signal** tier — what actually happened to the
/// polite "please exit" request `ProcessGroup.ShutdownReportAsync` issues before the grace window, as
/// opposed to what it *tried* to do.
///
/// The soft signal is the group's configured `Options.StopSignal` (`Signal.Term` by default) on the Unix
/// mechanisms, and a best-effort `WM_CLOSE` on the Windows soft tier. It is deliberately **not** the hard
/// kill: escalation to `SIGKILL` / the atomic Job terminate is reported separately, by
/// `ShutdownReport.Escalated`.
[<RequireQualifiedAccess; NoComparison>]
type SoftSignalDelivery =

    /// The soft signal was delivered best-effort to the tree: a POSIX signal on the Unix mechanisms, or a
    /// `WM_CLOSE` that reached at least one live windowed member on the Windows soft tier. Carries the
    /// `Signal` attempted.
    ///
    /// "Best-effort" is exact: a member that ignores the signal and keeps running is still counted as
    /// *sent to* — whether the tree then drained within the grace is `ShutdownReport.DrainedWithinGrace`,
    /// not this.
    | Sent of Signal: Signal

    /// This platform has **no soft-signal tier** for the group, so the teardown could only hard-kill —
    /// there was nothing polite to send. The one case: a windowless Windows Job Object with no live member
    /// that owns a top-level window. Every Unix mechanism always has a real soft-signal tier (`SIGTERM`),
    /// so this never arises there.
    | Unsupported

    /// A soft-signal tier exists but the best-effort delivery **failed for every target this teardown
    /// could still reach**: a uid-changed (`sudo`/setuid) member that rejected the signal with `EPERM` on
    /// Unix. Carries the `Signal` that could not be delivered. The teardown proceeded to its
    /// grace/escalation regardless.
    ///
    /// **Precision differs by mechanism.** On the POSIX process-group mechanism this is exact: `Failed`
    /// fires only when NONE of the live tracked group leaders (or adopted processes) accepted the
    /// signal — a partial failure among several still-reachable members is reported `Sent`, since the
    /// soft phase genuinely reached at least one of them. On the Linux cgroup v2 mechanism the underlying
    /// broadcast stops describing itself as soon as it hits its first genuine per-member failure, even
    /// while other members of the same cgroup went on to receive the signal — so there `Failed` means "at
    /// least one member's delivery failed," which is a narrower guarantee than the POSIX process-group
    /// mechanism's "every" reading. Windows never produces `Failed` at all (see `Unsupported`).
    | Failed of Signal: Signal

    /// The soft `Signal` this fate concerns — `Some` for both `Sent` and `Failed`, `None` for
    /// `Unsupported`, where nothing soft could be sent at all.
    member this.AttemptedSignal: Signal option =
        match this with
        | SoftSignalDelivery.Sent signal
        | SoftSignalDelivery.Failed signal -> Some signal
        | SoftSignalDelivery.Unsupported -> None

/// The observed facts of one graceful `ProcessGroup` teardown, returned by
/// `ProcessGroup.ShutdownReportAsync`.
///
/// Where the fire-and-forget `ShutdownAsync` reports only success or a thrown exception, this carries
/// what the teardown **actually observed**: which soft signal was attempted and whether it landed, how
/// many members were alive before and after, whether the tree drained within the grace or had to be
/// hard-killed, and how long it really took. A consumer that owns its own end-of-run race (a deadline that
/// is not a fixed timeout, but a timeout x Ctrl-C x control-socket race) can report the *observed* tier
/// instead of re-deriving it from `ShutdownAsync`'s bare success.
///
/// # Point-in-time member counts
///
/// `MembersBefore`/`MembersAfter` count the same member set `ProcessGroup.Members` reports — the whole
/// tree on the Windows Job Object and Linux cgroup v2 mechanisms, the tracked group **leaders** on the
/// POSIX process-group fallback (macOS / the other BSDs / Linux without cgroup v2). Each is `None` only
/// if that membership read failed (an unreadable `cgroup.procs`, a failed Job Object query), never a
/// fabricated `0`.
///
/// # Unconditional teardown — same guarantee as `ShutdownAsync`
///
/// This still tears the group down exactly like `ShutdownAsync`: the soft signal, then the grace, then an
/// unconditional hard kill of any survivor, then release. There is no "spare the survivors, keep the
/// group usable" mode here — see `ProcessGroup.ShutdownReportAsync`'s own doc comment for why this port
/// does not offer one. `Escalated` reports whether that hard kill actually fired; it is never a choice the
/// caller can suppress.
///
/// # Sealed, accessor-only
///
/// A read-only snapshot the library produces: sealed with an internal constructor so it can gain fields
/// across minor releases without a breaking change, and each fact is exposed through a property
/// (documenting its own platform caveats) rather than a public field.
[<Sealed>]
type ShutdownReport
    internal
    (
        softSignal: SoftSignalDelivery,
        membersBefore: int option,
        membersAfter: int option,
        drainedWithinGrace: bool,
        escalated: bool,
        elapsed: TimeSpan
    ) =

    /// The fate of the best-effort soft-signal tier: `Sent`, `Unsupported`, or `Failed` (see
    /// `SoftSignalDelivery`). Distinct from the hard kill — see `Escalated`.
    member _.SoftSignal = softSignal

    /// The soft `Signal` the teardown attempted, or `None` where the platform has no soft-signal tier
    /// (`SoftSignalDelivery.Unsupported`). A convenience over `SoftSignal.AttemptedSignal`.
    member _.AttemptedSignal = softSignal.AttemptedSignal

    /// How many members were alive **before** the soft signal, or `None` if the membership could not be
    /// read. See the type-level note on which member set this counts.
    member _.MembersBefore = membersBefore

    /// How many members were still alive **after** the grace window and any hard kill, or `None` if the
    /// membership could not be read.
    member _.MembersAfter = membersAfter

    /// Whether the tree **drained within the grace window**, before any hard kill — every member exited in
    /// response to the soft signal in time. `false` means the grace elapsed with survivors still alive
    /// (which were then hard-killed — see `Escalated`), or that there was no soft tier to drain on (a
    /// windowless Windows Job Object), unless the group was already empty.
    member _.DrainedWithinGrace = drainedWithinGrace

    /// Whether the teardown **escalated to a hard kill** (`SIGKILL` / the atomic Job terminate) because
    /// the tree had not drained within the grace. `false` for a tree that drained in time, and for an
    /// already-empty group (nothing to kill).
    member _.Escalated = escalated

    /// How long the teardown **actually took** — from issuing the soft signal to the final drain/kill
    /// decision. An early drain reports a short duration (it does not spend the whole grace); a tree that
    /// rides out the grace reports roughly the grace plus the escalation.
    member _.Elapsed = elapsed
