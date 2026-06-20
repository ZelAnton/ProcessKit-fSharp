namespace ProcessKit

open System

/// Resource limits enforced on a process group **as a whole** (not per process), applied to the
/// kernel container at creation time.
///
/// Enforcement needs a real container — a **Windows Job Object** or a **Linux cgroup v2**. On macOS
/// and the Linux process-group fallback there is no whole-tree limit primitive, so requesting *any*
/// limit there fails fast with `ProcessError.ResourceLimit` rather than silently leaving the tree
/// unbounded. On Linux the cgroup v2 controllers can only be enabled when this process runs at the
/// real cgroup-v2 hierarchy root (not under a systemd scope, nor in an ordinary container); when
/// they cannot, group creation fails fast for the same reason.
[<Sealed>]
type ResourceLimits internal (memoryMax: int64 option, maxProcesses: int option, cpuQuota: float option) =

    /// No limits — the default.
    static member None = ResourceLimits(None, None, None)

    /// Maximum total memory for the tree, in bytes. `None` leaves memory unbounded.
    member _.MemoryMax = memoryMax

    /// Maximum number of live processes in the tree. `None` leaves the count unbounded.
    member _.MaxProcesses = maxProcesses

    /// CPU quota as a fraction of a single core (`0.5` = half a core, `2.0` = two cores). `None`
    /// leaves CPU unbounded. On Windows this is approximate (converted against the host core count).
    member _.CpuQuota = cpuQuota

    /// A copy with the memory cap set.
    member _.WithMemoryMax(bytes: int64) =
        ResourceLimits(Some bytes, maxProcesses, cpuQuota)

    /// A copy with the live-process cap set.
    member _.WithMaxProcesses(count: int) =
        ResourceLimits(memoryMax, Some count, cpuQuota)

    /// A copy with the CPU quota (in cores) set.
    member _.WithCpuQuota(cores: float) =
        ResourceLimits(memoryMax, maxProcesses, Some cores)

    /// Whether any limit is set (so the group needs a limit-capable mechanism).
    member internal _.Any = memoryMax.IsSome || maxProcesses.IsSome || cpuQuota.IsSome

/// Options applied when creating a `ProcessGroup`: the graceful-shutdown window and whole-tree
/// resource limits.
[<Sealed>]
type ProcessGroupOptions internal (shutdownTimeout: TimeSpan, escalateToKill: bool, limits: ResourceLimits) =

    /// The defaults: a 2-second shutdown grace, escalate-to-kill on, no limits.
    new() = ProcessGroupOptions(TimeSpan.FromSeconds 2.0, true, ResourceLimits.None)

    /// How long `Shutdown` waits after SIGTERM before escalating to SIGKILL (Unix; default 2s).
    member _.ShutdownTimeout = shutdownTimeout

    /// Advisory: whether `Shutdown`'s graceful tier force-kills after the grace window (default
    /// true). Note this cannot fully leave a tree alive — releasing the container *is* a kill
    /// (closing a Job Object, `cgroup.kill`, `killpg`), so the kill-on-drop guarantee reaps any
    /// survivor regardless when the group is released.
    member _.EscalateToKill = escalateToKill

    /// The whole-tree resource caps applied at creation.
    member _.Limits = limits

    /// A copy with the shutdown grace window set.
    member _.WithShutdownTimeout(timeout: TimeSpan) =
        ProcessGroupOptions(timeout, escalateToKill, limits)

    /// A copy with escalate-to-kill set.
    member _.WithEscalateToKill(escalate: bool) =
        ProcessGroupOptions(shutdownTimeout, escalate, limits)

    /// A copy capping the tree's total memory at `bytes`.
    member _.WithMemoryMax(bytes: int64) =
        ProcessGroupOptions(shutdownTimeout, escalateToKill, limits.WithMemoryMax bytes)

    /// A copy capping the number of live processes in the tree at `count`.
    member _.WithMaxProcesses(count: int) =
        ProcessGroupOptions(shutdownTimeout, escalateToKill, limits.WithMaxProcesses count)

    /// A copy capping the tree's CPU at `cores` cores' worth.
    member _.WithCpuQuota(cores: float) =
        ProcessGroupOptions(shutdownTimeout, escalateToKill, limits.WithCpuQuota cores)
