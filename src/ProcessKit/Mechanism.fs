namespace ProcessKit

/// The OS primitive a `ProcessGroup` uses to contain a process tree.
///
/// Reported honestly (never a silent downgrade) so callers can reason about the
/// containment guarantee on the current platform.
[<RequireQualifiedAccess; NoComparison>]
type Mechanism =

    /// Windows Job Object.
    | JobObject

    /// Linux cgroup v2.
    | CgroupV2

    /// POSIX process group (macOS/BSD, or the Linux fallback).
    | ProcessGroup
