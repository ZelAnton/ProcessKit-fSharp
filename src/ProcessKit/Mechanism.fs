namespace ProcessKit

/// The OS primitive a `ProcessGroup` uses to contain a process tree.
///
/// Reported honestly (never a silent downgrade) so callers can reason about the
/// containment guarantee on the current platform.
[<RequireQualifiedAccess; NoComparison>]
type Mechanism =

    /// Windows Job Object.
    | JobObject

    /// Linux cgroup v2. Resource limits apply to each child and every descendant it forks *after* it
    /// has been migrated into the cgroup. A child is migrated (its pid written to `cgroup.procs`)
    /// immediately after it is spawned; a descendant it forks in the brief window before that write
    /// completes is created in the parent cgroup and stays there, so it is covered by kill-on-drop
    /// teardown (the whole subtree is reaped) but not by the resource limits. If a child cannot be
    /// migrated at all, it is killed and reaped and the spawn fails with `ProcessError.ResourceLimit`
    /// rather than being left to run unconstrained.
    | CgroupV2

    /// POSIX process group (macOS/BSD, or the Linux fallback).
    | ProcessGroup

    /// FreeBSD kernel **process reaper** — `procctl(2)`'s `PROC_REAP_ACQUIRE`, layered over the POSIX
    /// process group. Acquiring reaper status makes this process the reaper of its whole descendant tree,
    /// so every descendant — however deeply forked, and whether or not it called `setsid` — stays inside
    /// that tree, can be enumerated (`PROC_REAP_GETPIDS`) and can be signalled per subtree in one call
    /// (`PROC_REAP_KILL`). That closes the one documented escape hatch of `ProcessGroup` (a child that
    /// `setsid`s out of the group `killpg` addresses) and reports it as its own mechanism rather than as a
    /// silent upgrade of `ProcessGroup`.
    ///
    /// A reaper is a containment relationship, not a container: it carries **no** resource accounting at
    /// all, so whole-tree resource limits stay refused here exactly as on `ProcessGroup` — never
    /// approximated through a per-process `RLIMIT_*` surrogate.
    ///
    /// Only ever reported on FreeBSD, and only when `procctl(PROC_REAP_ACQUIRE)` actually succeeded for
    /// this process: a FreeBSD host where it did not falls back to `ProcessGroup` and says so, so the
    /// mechanism query never overstates the containment really in force.
    | ProcessReaper
