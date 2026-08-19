namespace ProcessKit

/// How far a **soft stop** — a `Signal.Term`/`Signal.Int`-class request that asks a tree to exit
/// cleanly rather than hard-killing it — reaches on *this* group, right now: the honest answer to "if I
/// call `group.Signal(Signal.Term)` this instant, which of its members have a live target for that
/// request?", read with `ProcessGroup.SoftStopScope()` *before* the signal is attempted. This is a
/// capability report, not a delivery guarantee — see below.
///
/// This is a **capability report, not an action**: querying it delivers no signal, posts no `WM_CLOSE`,
/// spawns nothing, and does not mutate the group — asking never changes the answer a later `Signal` call
/// gets. A caller (a CLI wrapping this library, an orchestrator deciding whether a soft stop is worth
/// attempting at all) can state the real reach to its own operator instead of firing `Signal(Term)`,
/// parsing a `ProcessError.Unsupported` back, and guessing at the scope from a hard-coded platform
/// assumption.
///
/// # It reports the *soft* tier only
///
/// This describes only the graceful `Signal.Int`/`Signal.Term` soft-stop tier. The unconditional
/// **hard** kill — `Signal.Kill`, `ProcessGroup.KillAll`, and disposing the group — always tears the
/// whole tree down on every platform regardless of this value; that guarantee is unchanged and never
/// `Unsupported`.
///
/// # Runtime, per-group — not a fixed platform constant
///
/// Unlike `KillOnParentDeathScope` (fixed per platform at build time), this is read from the group's
/// **live membership** on every call, so the *same* build reports different scopes for different groups
/// — most visibly on Windows, where a group with a live console-CTRL leader (a child started with
/// `Command.WindowsCtrlSignals()`) or a live windowed member reports `OptInMembers`, while a group with
/// neither reports `Unsupported`. "Live" is checked, not assumed: a CTRL-capable leader whose handle is
/// still open (and so still registered) but has already exited does NOT count, because
/// `GenerateConsoleCtrlEvent` on its now-torn-down console process group would then fail.
///
/// The read is side-effect-free — it posts no `WM_CLOSE`, sends no CTRL+BREAK, and mutates nothing — but
/// it is a capability report, not a delivery guarantee: `OptInMembers` says a live target for the soft
/// stop exists, not that a later `Signal(Int/Term)` is certain to reach it (`GenerateConsoleCtrlEvent` can
/// still fail for reasons this read does not probe, such as the caller having no console to share).
///
/// | Mechanism | Scope | Why |
/// |---|---|---|
/// | Linux cgroup v2 | `WholeTree` | `Signal(Int/Term)` writes to every process in the cgroup. |
/// | FreeBSD process reaper | `WholeTree` | `PROC_REAP_KILL` reaches every process in every subtree the group owns, with no escapee at all — not even a child that `setsid`s away: this is the strongest form of the promise on any unix. |
/// | POSIX process group (macOS / the other BSDs / Linux without cgroup v2) | `WholeTree` | `killpg` reaches every tracked group leader and its descendants — a child that `setsid`s away escapes, the same documented weakness the kill-on-drop guarantee already has, not new to the soft stop. |
/// | Windows Job Object | `OptInMembers` or `Unsupported` | A Job Object has no POSIX signal; a soft stop reaches only members it can *trigger* — a console-CTRL leader (opted in via `Command.WindowsCtrlSignals()`) or any live windowed member (`WM_CLOSE`). `OptInMembers` when at least one such member is live, else `Unsupported`. |
[<RequireQualifiedAccess; NoComparison>]
type SoftStopScope =

    /// The soft stop reaches **every** process in the tree — the Linux cgroup v2 mechanism, the FreeBSD
    /// process reaper, and the POSIX process-group mechanism (macOS / the other BSDs / Linux without
    /// cgroup v2). On the process-group mechanism the one documented escapee is a child that `setsid`s
    /// away, the same weakening that already applies to the kill-on-drop guarantee; on the cgroup and the
    /// reaper there is no escapee at all.
    | WholeTree

    /// The soft stop reaches only the members that can *receive* it — a curated **subset** of the tree,
    /// not all of it. Windows only: a live console-CTRL leader (a direct child spawned with
    /// `Command.WindowsCtrlSignals()`) and/or any live member that owns a top-level window. A member that
    /// is neither is not reached by the soft stop and rides to the hard-kill fallback instead.
    | OptInMembers

    /// **No** soft stop is available on this group: not one member can receive a graceful
    /// `Signal.Int`/`Signal.Term`, so calling it would return `ProcessError.Unsupported`. Windows only,
    /// when the group has neither a console-CTRL leader nor a windowed member (an empty group, or a tree
    /// of plain windowless children with no console opt-in). The unconditional hard kill still works —
    /// this reports only the soft tier's absence.
    | Unsupported
