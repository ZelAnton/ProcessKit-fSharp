namespace ProcessKit

open System

module internal Limits =
    let DefaultStopGrace = TimeSpan.FromSeconds 2.0

/// The Windows Job Object **UI restrictions** a process group can impose on its whole tree
/// (`JOBOBJECT_BASIC_UI_RESTRICTIONS`) — see `ProcessGroupOptions.WithUiRestrictions`.
///
/// These are the desktop-side counterpart of the resource caps: where `ResourceLimits`'
/// memory/process/CPU caps bound what a contained tree may *consume*, these bound what it may *do to
/// the interactive session it happens to share with you* — read or overwrite the clipboard, change
/// display or system-wide parameters, create or switch desktops, or log the user off / shut the
/// machine down. A build plugin or a downloaded tool has no business doing any of that, and unlike a
/// resource cap there is no way to notice after the fact that it did.
///
/// A `[<Flags>]` set: combine the members with `|||` (F#) or `|` (C#), or take the whole set with
/// `All`. `None` (the default) leaves the Job's UI restrictions untouched, byte-identical to a group
/// created before this option existed.
///
/// **Windows-only, and honestly so.** The Job Object is the only primitive with this concept; POSIX
/// (and Linux cgroup v2) have no equivalent, so requesting any restriction there fails
/// `ProcessGroup.Create`/`UpdateLimits` with `ProcessError.Unsupported` rather than silently
/// dropping it — exactly as the Unix-only `Command.Uid`/`Umask` family fails on Windows. What the
/// restrictions do *not* do is sandbox the child's filesystem, network, or registry access; they are
/// one layer of a perimeter (see the hardening guide), not a sandbox.
[<Flags>]
type WindowsUiRestrictions =

    /// No UI restrictions — the default, and what a Job carries unless one is requested.
    | None = 0x00000000

    /// Deny the use of USER handles owned by processes *outside* the job
    /// (`JOB_OBJECT_UILIMIT_HANDLES`) — the broadest of the set: it stops a contained child from
    /// reaching into another process's windows, menus, or hooks. Also the most likely to break a
    /// legitimate GUI child, so opt in deliberately.
    | Handles = 0x00000001

    /// Deny reading the clipboard (`JOB_OBJECT_UILIMIT_READCLIPBOARD`) — a child that cannot read it
    /// cannot harvest whatever the user last copied (a password out of a password manager, say).
    | ReadClipboard = 0x00000002

    /// Deny writing to the clipboard (`JOB_OBJECT_UILIMIT_WRITECLIPBOARD`).
    | WriteClipboard = 0x00000004

    /// Deny changing system-wide parameters through `SystemParametersInfo`
    /// (`JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS`) — accessibility, input, and desktop settings.
    | SystemParameters = 0x00000008

    /// Deny changing display settings through `ChangeDisplaySettings`
    /// (`JOB_OBJECT_UILIMIT_DISPLAYSETTINGS`).
    | DisplaySettings = 0x00000010

    /// Give the job its own atom table instead of the global one (`JOB_OBJECT_UILIMIT_GLOBALATOMS`),
    /// so a contained child can neither read nor exhaust the session's global atoms.
    | GlobalAtoms = 0x00000020

    /// Deny creating or switching desktops (`JOB_OBJECT_UILIMIT_DESKTOP`).
    | Desktop = 0x00000040

    /// Deny logging the user off, shutting down, or restarting the machine
    /// (`JOB_OBJECT_UILIMIT_EXITWINDOWS`) — an untrusted child taking the host down with it is a real
    /// and cheap denial of service.
    | ExitWindows = 0x00000080

    /// Every restriction above at once — the "this child has no business touching the desktop
    /// session at all" set, and the sensible starting point for a genuinely untrusted child that is
    /// not a GUI application.
    | All = 0x000000FF

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
type ResourceLimits
    internal
    (memoryMax: int64 option, maxProcesses: int option, cpuQuota: float option, uiRestrictions: WindowsUiRestrictions) =

    /// No limits — the default.
    static member None = ResourceLimits(None, None, None, WindowsUiRestrictions.None)

    /// Maximum total memory for the tree, in bytes. `None` leaves memory unbounded.
    member _.MemoryMax = memoryMax

    /// Maximum number of live processes in the tree. `None` leaves the count unbounded.
    member _.MaxProcesses = maxProcesses

    /// CPU quota as a fraction of a single core (`0.5` = half a core, `2.0` = two cores). `None`
    /// leaves CPU unbounded. On Windows this is approximate (converted against the host core count).
    member _.CpuQuota = cpuQuota

    /// The Windows Job Object UI restrictions imposed on the tree (`WindowsUiRestrictions.None` — the
    /// default — imposes none). Windows-only: any other value fails `ProcessGroup.Create`/
    /// `UpdateLimits` with `ProcessError.Unsupported` off Windows, never a silent drop.
    member _.UiRestrictions = uiRestrictions

    /// A copy with the memory cap set. `bytes` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive cap could never let anything run, so it is a
    /// misconfiguration rather than a meaningful limit, and previously degraded silently (e.g. a
    /// negative value converting to a huge `unativeint` on Windows — effectively "unlimited").
    member _.WithMemoryMax(bytes: int64) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0L)
        ResourceLimits(Some bytes, maxProcesses, cpuQuota, uiRestrictions)

    /// A copy with the live-process cap set. `count` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): the tree always has at least its own leader process, so a
    /// non-positive cap could never be satisfied.
    member _.WithMaxProcesses(count: int) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0)
        ResourceLimits(memoryMax, Some count, cpuQuota, uiRestrictions)

    /// A copy with the CPU quota (in cores) set. `cores` must be a finite, strictly positive number —
    /// zero, negative, `NaN`, or `PositiveInfinity`/`NegativeInfinity` is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive quota could never let anything run, and an
    /// infinite one has no meaningful cgroup encoding. Also rejected: a value whose conversion into a
    /// cgroup v2 `cpu.max` "quota period" string would overflow `int64` once rounded to microseconds.
    /// The shared conversion validates this before a `ProcessGroup` is even created, rather than only
    /// surfacing later and only on the Linux backend.
    member _.WithCpuQuota(cores: float) =
        if Double.IsNaN cores || Double.IsInfinity cores || cores <= 0.0 then
            raise (
                ArgumentOutOfRangeException(nameof cores, cores, "CPU quota must be a finite, positive number of cores")
            )

        let cgroupQuotaMicroseconds = CgroupCpuMax.calculateQuota cores

        if CgroupCpuMax.isQuotaOverflow cgroupQuotaMicroseconds then
            raise (
                ArgumentOutOfRangeException(
                    nameof cores,
                    cores,
                    "CPU quota is too large to convert into a cgroup v2 cpu.max quota without overflowing int64"
                )
            )

        ResourceLimits(memoryMax, maxProcesses, Some cores, uiRestrictions)

    /// A copy imposing the given Windows Job Object UI restrictions on the whole tree (see
    /// `WindowsUiRestrictions`). `WindowsUiRestrictions.None` clears them again — the set REPLACES the
    /// one in force, like every other dimension here. A value carrying bits outside the defined set is
    /// rejected (`ArgumentOutOfRangeException`): those bits have no meaning to
    /// `SetInformationJobObject` and would otherwise be written to the Job as an undefined restriction
    /// class, which is a misconfiguration rather than a limit.
    ///
    /// **Windows-only.** Off Windows there is no Job Object (and no analogous primitive on POSIX or
    /// cgroup v2), so a group asked for any restriction fails at `ProcessGroup.Create`/`UpdateLimits`
    /// with `ProcessError.Unsupported` rather than running unrestricted — the same honest refusal
    /// `Command.Uid`/`Setsid`/`Umask` give on Windows.
    member _.WithUiRestrictions(restrictions: WindowsUiRestrictions) =
        if (int restrictions &&& ~~~(int WindowsUiRestrictions.All)) <> 0 then
            raise (
                ArgumentOutOfRangeException(
                    nameof restrictions,
                    restrictions,
                    "WindowsUiRestrictions carries bits outside the defined set (see WindowsUiRestrictions.All)"
                )
            )

        ResourceLimits(memoryMax, maxProcesses, cpuQuota, restrictions)

    /// Whether any limit is set (so the group needs a limit-capable mechanism). UI restrictions count:
    /// they too are applied through `SetInformationJobObject` and need the Job Object mechanism, so a
    /// group asking only for them must still take the limit-capable path rather than skip the apply.
    member internal _.Any =
        memoryMax.IsSome
        || maxProcesses.IsSome
        || cpuQuota.IsSome
        || uiRestrictions <> WindowsUiRestrictions.None

    /// The honest typed refusal for a mechanism that cannot impose UI restrictions, or `None` when
    /// none were requested. One definition shared by `ProcessGroup.Create` and both non-Job backends'
    /// `UpdateLimits`, so the create-time and update-time refusals can never drift apart — and so the
    /// requested-but-unenforceable case is `Unsupported` (this mechanism has no such concept at all)
    /// rather than the `ResourceLimit` used when a cap exists in principle but could not be applied.
    member internal _.UiRestrictionsUnsupported: ProcessError option =
        if uiRestrictions = WindowsUiRestrictions.None then
            Option.None
        else
            Some(
                ProcessError.Unsupported
                    $"Job Object UI restrictions ({uiRestrictions}) are a Windows-only primitive; this mechanism has no equivalent"
            )

/// Options applied when creating a `ProcessGroup`: the graceful-shutdown window and whole-tree
/// resource limits.
[<Sealed>]
type ProcessGroupOptions internal (shutdownTimeout: TimeSpan, limits: ResourceLimits) =

    /// The defaults: a 2-second shutdown grace, no limits.
    new() = ProcessGroupOptions(Limits.DefaultStopGrace, ResourceLimits.None)

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

    /// A copy imposing the given Windows Job Object UI restrictions on the tree (clipboard, desktop,
    /// display/system settings, exit-Windows — see `WindowsUiRestrictions`). Windows-only: off Windows
    /// `ProcessGroup.Create` fails with `ProcessError.Unsupported` rather than silently running the
    /// tree unrestricted. See `ResourceLimits.WithUiRestrictions`.
    member _.WithUiRestrictions(restrictions: WindowsUiRestrictions) =
        ProcessGroupOptions(shutdownTimeout, limits.WithUiRestrictions restrictions)

    /// A copy carrying a wholesale-replaced `ResourceLimits` set, keeping the shutdown window.
    /// Internal — used by `ProcessGroup.UpdateLimits` to refresh the `Options` snapshot a consumer
    /// reads back after a live limit update, so the whole limit set is swapped atomically rather than
    /// composed field-by-field.
    member internal _.WithLimits(newLimits: ResourceLimits) =
        ProcessGroupOptions(shutdownTimeout, newLimits)
