namespace ProcessKit

open System
open System.Collections.Generic

module internal Limits =
    let DefaultStopGrace = TimeSpan.FromSeconds 2.0

/// The Windows Job Object **UI restrictions** a process group can impose on its whole tree
/// (`JOBOBJECT_BASIC_UI_RESTRICTIONS`) — see `ProcessGroupOptions.WithUiRestrictions`.
///
/// These are the desktop-side counterpart of the resource caps: where the rest of `ResourceLimits`
/// bounds what a contained tree may *consume* (and which cores it may consume it on), these bound what
/// it may *do to the interactive session it happens to share with you* — read or overwrite the
/// clipboard, change display or system-wide parameters, create or switch desktops, or log the user
/// off / shut the machine down. A build plugin or a downloaded tool has no business doing any of that, and unlike a
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

/// Resource limits applied at group creation. Most are enforced on the group **as a whole** by its
/// kernel container; `CpuTimeMax` is the deliberate POSIX exception and is applied per spawned process
/// through `RLIMIT_CPU` before exec.
///
/// Whole-tree enforcement needs a real container — a **Windows Job Object** or a **Linux cgroup v2**.
/// On macOS and the Linux process-group fallback, requesting memory/process/quota/affinity limits fails
/// fast with `ProcessError.ResourceLimit`; CPU-time alone remains available through the per-child rlimit.
/// On Linux the cgroup v2 controllers can only be enabled when this process runs at the
/// real cgroup-v2 hierarchy root (not under a systemd scope, nor in an ordinary container); when
/// they cannot, group creation fails fast for the same reason.
[<Sealed>]
type ResourceLimits
    internal
    (
        memoryMax: int64 option,
        maxProcesses: int option,
        cpuQuota: float option,
        uiRestrictions: WindowsUiRestrictions,
        cpuAffinity: int list option,
        cpuTimeMax: TimeSpan option
    ) =

    /// No limits — the default.
    static member None = ResourceLimits(None, None, None, WindowsUiRestrictions.None, None, None)

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

    /// The CPU cores the tree is pinned to, in ascending order, or `None` when it may run on every core
    /// (the default). A fresh list each read, so a caller can never mutate the limit set through it.
    member _.CpuAffinity: IReadOnlyList<int> option =
        cpuAffinity
        |> Option.map (fun cores -> List.toArray cores :> IReadOnlyList<int>)

    /// The pinned cores as the plain list the backends encode from (a Job Object affinity bitmask, a
    /// cgroup v2 `cpuset.cpus` range string). Internal so the shipped surface keeps the `IReadOnlyList`
    /// shape used by every other public collection here, while the native layer avoids re-copying.
    member internal _.CpuAffinityCores = cpuAffinity

    /// Maximum CPU time consumed by the contained run. Windows enforces this for the Job as a whole;
    /// POSIX applies `RLIMIT_CPU` independently to each spawned process before it execs.
    member _.CpuTimeMax = cpuTimeMax

    /// A copy with the memory cap set. `bytes` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive cap could never let anything run, so it is a
    /// misconfiguration rather than a meaningful limit, and previously degraded silently (e.g. a
    /// negative value converting to a huge `unativeint` on Windows — effectively "unlimited").
    member _.WithMemoryMax(bytes: int64) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0L)
        ResourceLimits(Some bytes, maxProcesses, cpuQuota, uiRestrictions, cpuAffinity, cpuTimeMax)

    /// A copy with the live-process cap set. `count` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): the tree always has at least its own leader process, so a
    /// non-positive cap could never be satisfied.
    member _.WithMaxProcesses(count: int) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0)
        ResourceLimits(memoryMax, Some count, cpuQuota, uiRestrictions, cpuAffinity, cpuTimeMax)

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

        ResourceLimits(memoryMax, maxProcesses, Some cores, uiRestrictions, cpuAffinity, cpuTimeMax)

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

        ResourceLimits(memoryMax, maxProcesses, cpuQuota, restrictions, cpuAffinity, cpuTimeMax)

    /// A copy pinning the whole tree to `cores` — the CPU cores (zero-based logical processor indices)
    /// its processes may be scheduled on. The complement of `WithCpuQuota`: the quota bounds *how much*
    /// CPU the tree gets, this bounds *which* cores it gets it from, so a noisy child can be kept off the
    /// cores a latency-critical workload runs on. The set REPLACES any previous one, like every other
    /// dimension here; leave it unset for "every core".
    ///
    /// Rejected at the builder rather than deep in a native call: a `null` set
    /// (`ArgumentNullException`), an empty one (`ArgumentException` — no core to run on could never let
    /// anything run, so it is a misconfiguration rather than a limit), a negative index
    /// (`ArgumentOutOfRangeException`), and a repeated index (`ArgumentException` — an affinity set is a
    /// set, and a repeat is far more likely a typo in a generated list than an intent). The accepted set
    /// is stored in ascending order, so `[2; 0]` and `[0; 2]` are the same limit.
    ///
    /// **Needs a limit-capable mechanism**, like every other cap here: the Windows Job Object's affinity
    /// mask (`JOB_OBJECT_LIMIT_AFFINITY`) or the Linux cgroup v2 `cpuset` controller (`cpuset.cpus`). On
    /// macOS and the Linux process-group fallback there is no whole-tree primitive to pin with, so the
    /// group fails with `ProcessError.ResourceLimit` rather than running everywhere unpinned. Two
    /// platform limits are reported the same honest way at apply time rather than guessed at here (the
    /// machine that builds the limit set need not be the one it runs on): the Windows mask is a single
    /// pointer-sized word covering one processor group, so an index at or beyond its width (64 on x64)
    /// has no representation; and every requested core must actually exist on the host and be available
    /// to this process.
    member _.WithCpuAffinity(cores: seq<int>) =
        ArgumentNullException.ThrowIfNull(cores, nameof cores)
        let requested = List.ofSeq cores

        if List.isEmpty requested then
            raise (
                ArgumentException(
                    "the CPU-affinity set must not be empty — name at least one core to pin the tree to, or leave the affinity unset to allow every core",
                    nameof cores
                )
            )

        match requested |> List.tryFind (fun core -> core < 0) with
        | Some negative ->
            raise (
                ArgumentOutOfRangeException(
                    nameof cores,
                    negative,
                    "a CPU core index must not be negative (cores are numbered from 0)"
                )
            )
        | None -> ()

        match requested |> List.countBy id |> List.filter (fun (_, count) -> count > 1) with
        | [] -> ()
        | repeated ->
            let listed = repeated |> List.map (fst >> string) |> String.concat ", "

            raise (
                ArgumentException(
                    $"the CPU-affinity set lists core(s) {listed} more than once; it is a set of cores, so each may appear at most once",
                    nameof cores
                )
            )

        ResourceLimits(memoryMax, maxProcesses, cpuQuota, uiRestrictions, Some(List.sort requested), cpuTimeMax)

    /// A copy limiting CPU time. `duration` must be finite and strictly positive. POSIX rounds the
    /// soft `RLIMIT_CPU` up to whole seconds and gives the hard limit one additional second so the
    /// process can observe `SIGXCPU`; Windows uses the Job Object's 100-nanosecond tick precision.
    member _.WithCpuTimeMax(duration: TimeSpan) =
        if duration <= TimeSpan.Zero then
            raise (ArgumentOutOfRangeException(nameof duration, duration, "CPU time must be positive and finite"))

        ResourceLimits(memoryMax, maxProcesses, cpuQuota, uiRestrictions, cpuAffinity, Some duration)

    /// Whether any limit is set. Windows uses this to decide whether the fresh Job needs a limit block.
    /// POSIX dispatch uses `WholeTreeAny` below because CPU-time alone is enforceable without a container.
    member internal _.Any =
        memoryMax.IsSome
        || maxProcesses.IsSome
        || cpuQuota.IsSome
        || uiRestrictions <> WindowsUiRestrictions.None
        || cpuAffinity.IsSome
        || cpuTimeMax.IsSome

    /// Whether a whole-tree container controller is needed. CPU-time alone is excluded because POSIX
    /// can enforce it per child with `RLIMIT_CPU`, including on macOS and the Linux process-group fallback.
    member internal _.WholeTreeAny =
        memoryMax.IsSome
        || maxProcesses.IsSome
        || cpuQuota.IsSome
        || uiRestrictions <> WindowsUiRestrictions.None
        || cpuAffinity.IsSome

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
type ProcessGroupOptions internal (shutdownTimeout: TimeSpan, stopSignal: Signal, limits: ResourceLimits) =

    /// The defaults: a 2-second shutdown grace, `Signal.Term`, and no limits.
    new() = ProcessGroupOptions(Limits.DefaultStopGrace, Signal.Term, ResourceLimits.None)

    /// How long `ShutdownAsync` waits after the configured `StopSignal` before escalating (default 2s).
    member _.ShutdownTimeout = shutdownTimeout

    /// The soft signal sent by `ShutdownAsync` before hard-kill escalation (default `Signal.Term`).
    member _.StopSignal = stopSignal

    /// The whole-tree resource caps applied at creation.
    member _.Limits = limits

    /// A copy with the shutdown grace window set. A negative `timeout` is rejected
    /// (`ArgumentOutOfRangeException`); `TimeSpan.Zero` is valid (no grace — escalate immediately).
    member _.WithShutdownTimeout(timeout: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero)
        ProcessGroupOptions(timeout, stopSignal, limits)

    /// A copy using `signal` for graceful shutdown before escalation.
    member _.WithStopSignal(signal: Signal) =
        SignalValidation.gracefulStop (nameof signal) signal
        ProcessGroupOptions(shutdownTimeout, signal, limits)

    /// A copy capping the tree's total memory at `bytes`.
    member _.WithMemoryMax(bytes: int64) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithMemoryMax bytes)

    /// A copy capping the number of live processes in the tree at `count`.
    member _.WithMaxProcesses(count: int) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithMaxProcesses count)

    /// A copy capping the tree's CPU at `cores` cores' worth.
    member _.WithCpuQuota(cores: float) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithCpuQuota cores)

    /// A copy capping CPU time for each spawned run (or the whole Job on Windows).
    member _.WithCpuTimeMax(duration: TimeSpan) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithCpuTimeMax duration)

    /// A copy imposing the given Windows Job Object UI restrictions on the tree (clipboard, desktop,
    /// display/system settings, exit-Windows — see `WindowsUiRestrictions`). Windows-only: off Windows
    /// `ProcessGroup.Create` fails with `ProcessError.Unsupported` rather than silently running the
    /// tree unrestricted. See `ResourceLimits.WithUiRestrictions`.
    member _.WithUiRestrictions(restrictions: WindowsUiRestrictions) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithUiRestrictions restrictions)

    /// A copy pinning the tree to `cores` — the CPU cores its processes may be scheduled on (the Windows
    /// Job Object affinity mask, the Linux cgroup v2 `cpuset.cpus`). Needs a limit-capable mechanism:
    /// elsewhere `ProcessGroup.Create` fails with `ProcessError.ResourceLimit` rather than silently
    /// running the tree on every core. See `ResourceLimits.WithCpuAffinity`.
    member _.WithCpuAffinity(cores: seq<int>) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithCpuAffinity cores)

    /// A copy carrying a wholesale-replaced `ResourceLimits` set, keeping the shutdown window.
    /// Internal — used by `ProcessGroup.UpdateLimits` to refresh the `Options` snapshot a consumer
    /// reads back after a live limit update, so the whole limit set is swapped atomically rather than
    /// composed field-by-field.
    member internal _.WithLimits(newLimits: ResourceLimits) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, newLimits)
