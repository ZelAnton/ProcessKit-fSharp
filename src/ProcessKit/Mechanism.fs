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
