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

    /// A copy with the memory cap set. `bytes` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive cap could never let anything run, so it is a
    /// misconfiguration rather than a meaningful limit, and previously degraded silently (e.g. a
    /// negative value converting to a huge `unativeint` on Windows — effectively "unlimited").
    member _.WithMemoryMax(bytes: int64) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0L)
        ResourceLimits(Some bytes, maxProcesses, cpuQuota)

    /// A copy with the live-process cap set. `count` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): the tree always has at least its own leader process, so a
    /// non-positive cap could never be satisfied.
    member _.WithMaxProcesses(count: int) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0)
        ResourceLimits(memoryMax, Some count, cpuQuota)

    /// A copy with the CPU quota (in cores) set. `cores` must be a finite, strictly positive number —
    /// zero, negative, `NaN`, or `PositiveInfinity`/`NegativeInfinity` is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive quota could never let anything run, and an
    /// infinite one has no meaningful cgroup encoding. Also rejected: a value whose conversion into a
    /// cgroup v2 `cpu.max` "quota period" string (see `Native.Cgroup.cpuMaxValue`) would overflow
    /// `int64` once rounded to microseconds — the same math is mirrored here (rather than called into
    /// `Native.Cgroup`, which compiles after `Limits.fs`) so the rejection is identical on every
    /// platform, before a `ProcessGroup` is even created, instead of only surfacing later and only on
    /// the Linux backend.
    member _.WithCpuQuota(cores: float) =
        if Double.IsNaN cores || Double.IsInfinity cores || cores <= 0.0 then
            raise (
                ArgumentOutOfRangeException(
                    nameof cores,
                    cores,
                    "CPU quota must be a finite, positive number of cores"
                )
            )

        // Mirror of Native.Cgroup.cpuMaxValue's "quota period" microsecond conversion: period is
        // 100_000 microseconds (100ms), quota = round(cores * period). Reject any `cores` that would
        // make that rounded quota reach or exceed Int64.MaxValue (which `int64 quota` cannot represent).
        let cgroupPeriodMicroseconds = 100_000.0
        let cgroupQuotaMicroseconds = Math.Round(cores * cgroupPeriodMicroseconds)

        if
            Double.IsNaN cgroupQuotaMicroseconds
            || Double.IsInfinity cgroupQuotaMicroseconds
            || cgroupQuotaMicroseconds >= float Int64.MaxValue
        then
            raise (
                ArgumentOutOfRangeException(
                    nameof cores,
                    cores,
                    "CPU quota is too large to convert into a cgroup v2 cpu.max quota without overflowing int64"
                )
            )

        ResourceLimits(memoryMax, maxProcesses, Some cores)

    /// Whether any limit is set (so the group needs a limit-capable mechanism).
    member internal _.Any = memoryMax.IsSome || maxProcesses.IsSome || cpuQuota.IsSome

/// Options applied when creating a `ProcessGroup`: the graceful-shutdown window and whole-tree
/// resource limits.
[<Sealed>]
type ProcessGroupOptions internal (shutdownTimeout: TimeSpan, limits: ResourceLimits) =

    /// The defaults: a 2-second shutdown grace, no limits.
    new() = ProcessGroupOptions(TimeSpan.FromSeconds 2.0, ResourceLimits.None)

    /// How long `ShutdownAsync` waits after SIGTERM before escalating to SIGKILL (Unix; default 2s).
    member _.ShutdownTimeout = shutdownTimeout

    /// The whole-tree resource caps applied at creation.
    member _.Limits = limits

    /// A copy with the shutdown grace window set. A negative `timeout` is rejected
    /// (`ArgumentOutOfRangeException`); `TimeSpan.Zero` is valid (no grace — escalate immediately).
    member _.WithShutdownTimeout(timeout: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero)
        ProcessGroupOptions(timeout, limits)

    /// A copy capping the tree's total memory at `bytes`.
    member _.WithMemoryMax(bytes: int64) =
        ProcessGroupOptions(shutdownTimeout, limits.WithMemoryMax bytes)

    /// A copy capping the number of live processes in the tree at `count`.
    member _.WithMaxProcesses(count: int) =
        ProcessGroupOptions(shutdownTimeout, limits.WithMaxProcesses count)

    /// A copy capping the tree's CPU at `cores` cores' worth.
    member _.WithCpuQuota(cores: float) =
        ProcessGroupOptions(shutdownTimeout, limits.WithCpuQuota cores)
