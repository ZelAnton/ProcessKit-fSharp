namespace ProcessKit

open System
open System.Collections.Generic

module internal Limits =
    let DefaultStopGrace = TimeSpan.FromSeconds 2.0

/// A Unix **per-process** resource governed by `setrlimit(2)` and requested through
/// `Command.Rlimit` — the per-child complement of the whole-tree `ResourceLimits` below.
///
/// The two are different instruments and neither replaces the other. A `ResourceLimits` cap is
/// enforced by the group's kernel container on every process in the tree AT ONCE (one memory budget
/// shared by all of them); an rlimit is applied to the direct child before its program starts and is
/// then INHERITED individually by each descendant, so ten descendants each get their own copy of the
/// cap rather than a shared one. A descendant may lower its own limits further, and may raise its
/// soft value back up as far as the hard value it inherited — an rlimit is a robustness bound, not a
/// containment boundary (that is what the group is for).
///
/// Every value is in the resource's own native unit, exactly as the syscall takes it: **bytes** for
/// `Core`/`Data`/`FileSize`/`Stack`, **seconds** for `Cpu`, a **count** for `NoFile`. There is no
/// "unlimited" value — this API exists to LOWER a limit the child inherited, and raising a hard limit
/// needs privilege the kernel refuses to an ordinary caller (the refusal is honest: the child never
/// runs).
///
/// **Unix-only**, and honestly so: Windows has no `setrlimit` analogue, so a spawn carrying any rlimit
/// fails there with `ProcessError.Unsupported` rather than running the child uncapped. On POSIX the
/// limits are applied before the child's own program starts by the util-linux `prlimit` helper, loaded
/// only from a trusted system directory; a host that holds it in none of them (macOS/BSD, a minimal
/// image) fails with `ProcessError.ResourceLimit` — see `Command.Rlimit` for the full mechanism.
[<RequireQualifiedAccess; NoComparison>]
type RlimitResource =

    /// Maximum CPU time in **seconds** (`RLIMIT_CPU`). The soft value raises `SIGXCPU` (which
    /// terminates a child that does not handle it), the hard value is an unblockable `SIGKILL`.
    /// This is the one axis the whole-tree `ResourceLimits.CpuTimeMax` also targets — see
    /// `Command.Rlimit` for how the two compose.
    | Cpu

    /// Maximum size in **bytes** of a core dump (`RLIMIT_CORE`). `0, 0` disables core dumps outright,
    /// which is the honest default for a child handling secrets: a core file is a verbatim copy of
    /// its memory, written where the host's core pattern says rather than where this process decides.
    | Core

    /// Maximum size in **bytes** of the process data segment (`RLIMIT_DATA`) — the `brk`/`mmap`
    /// allocation arena on Linux, so an allocation past it fails inside the child (an allocator
    /// error) rather than taking memory from the rest of the host.
    | Data

    /// Maximum size in **bytes** of a file the process may create or extend (`RLIMIT_FSIZE`). A write
    /// past the soft value raises `SIGXFSZ`; a child that handles or blocks it gets `EFBIG` instead.
    /// The cap a runaway log or an unbounded temp file needs.
    | FileSize

    /// Maximum **number** of simultaneously open file descriptors (`RLIMIT_NOFILE`). Counts the
    /// descriptors ProcessKit itself hands the child (its stdio, any `Command.ExtraFd` channel), so
    /// leave headroom for them.
    | NoFile

    /// Maximum size in **bytes** of the process stack (`RLIMIT_STACK`). Bounds runaway recursion in
    /// the child's main thread; a thread the child creates itself is governed by whatever stack size
    /// that child requests.
    | Stack

    /// Every resource, in a fixed order — the enumerable form of the set `Name`/`TryFromName` map
    /// between, so a config layer can validate or document the accepted spellings without keeping its
    /// own copy of the list (which could silently fall behind a new resource).
    static member All: IReadOnlyList<RlimitResource> =
        [| RlimitResource.Cpu
           RlimitResource.Core
           RlimitResource.Data
           RlimitResource.FileSize
           RlimitResource.NoFile
           RlimitResource.Stack |]

    /// This resource's **stable machine identifier**: a short, lowercase `snake_case` string, part of
    /// the library's compatibility surface. Use it wherever a resource has to travel as text — a
    /// config file's key, a CLI flag, a structured log field — instead of hand-maintaining a mapping
    /// table. It is a diagnostic identifier rather than a wire format, but it is held stable all the
    /// same: a new resource gets a NEW identifier and an existing one is never renamed within a major
    /// version. `TryFromName` parses it back.
    member this.Name: string =
        match this with
        | RlimitResource.Cpu -> "cpu"
        | RlimitResource.Core -> "core"
        | RlimitResource.Data -> "data"
        | RlimitResource.FileSize -> "file_size"
        | RlimitResource.NoFile -> "no_file"
        | RlimitResource.Stack -> "stack"

    /// Parse a stable `Name` identifier back into a resource, or `None` for anything that is not
    /// EXACTLY one of them (matching is ordinal and case-sensitive: `"NoFile"` and `"nofile"` are both
    /// misses, only `"no_file"` hits). An honest miss, never a silent default — a config-driven caller
    /// that mistypes a resource gets nothing back to apply, instead of a limit quietly landing on the
    /// wrong axis or on none at all. Round-trips with `Name` for every resource.
    static member TryFromName(name: string) : RlimitResource option =
        RlimitResource.All |> Seq.tryFind (fun resource -> resource.Name = name)

    /// `TryFromName` for a caller that wants the miss as an error instead of an option — an unknown
    /// name raises `ArgumentException` listing every accepted spelling, so a mistyped config key fails
    /// where it is read rather than silently applying no limit. `null` raises `ArgumentNullException`.
    static member FromName(name: string) : RlimitResource =
        ArgumentNullException.ThrowIfNull(name, nameof name)

        match RlimitResource.TryFromName name with
        | Some resource -> resource
        | None ->
            let accepted =
                RlimitResource.All
                |> Seq.map (fun resource -> resource.Name)
                |> String.concat ", "

            raise (
                ArgumentException($"'{name}' is not a known rlimit resource; expected one of: {accepted}", nameof name)
            )

/// One per-process rlimit as configured on a `Command`: the resource, and the soft and hard values to
/// apply to it — the pair `setrlimit(2)` itself takes. Read back from `Command.Rlimits`; built by
/// `Command.Rlimit`, which validates the pair at the builder boundary.
///
/// The **soft** value is the limit actually in force (exceeding it raises this resource's signal or
/// fails the operation); the **hard** value is the ceiling the child may raise its own soft value back
/// up to. Both are in the resource's native unit (see `RlimitResource`), and `Soft` never exceeds
/// `Hard`.
[<Sealed>]
type Rlimit internal (resource: RlimitResource, soft: int64, hard: int64) =

    /// The resource this pair caps.
    member _.Resource = resource

    /// The soft limit — the value in force for the child, in the resource's native unit.
    member _.Soft = soft

    /// The hard limit — the ceiling the child may raise its own soft value back to, in the resource's
    /// native unit. Never below `Soft`.
    member _.Hard = hard

    /// The canonical one-line rendering `<name>=<soft>:<hard>` (e.g. `no_file=64:128`), built from the
    /// resource's stable `Name` — what a diagnostic (a dry-run render, a log line) shows for this
    /// limit. Carries no argv or environment value, so it is safe to log.
    override _.ToString() = $"{resource.Name}={soft}:{hard}"

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

/// Directional disk I/O rate limits for one device or volume. Linux uses `Target` as a cgroup v2
/// `major:minor` device key. Windows uses it as the NT device name of one volume; Windows Job Objects
/// enforce one aggregate read/write bandwidth and IOPS ceiling for that volume.
[<Sealed>]
type IoMax
    internal
    (
        target: string,
        readBytesPerSecond: int64 option,
        writeBytesPerSecond: int64 option,
        readOperationsPerSecond: int64 option,
        writeOperationsPerSecond: int64 option
    ) =

    member _.Target = target
    member _.ReadBytesPerSecond = readBytesPerSecond
    member _.WriteBytesPerSecond = writeBytesPerSecond
    member _.ReadOperationsPerSecond = readOperationsPerSecond
    member _.WriteOperationsPerSecond = writeOperationsPerSecond

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
        oomGroupKill: bool,
        maxProcesses: int option,
        cpuQuota: float option,
        uiRestrictions: WindowsUiRestrictions,
        cpuAffinity: int list option,
        cpuTimeMax: TimeSpan option,
        ioMax: IoMax option
    ) =

    /// No limits — the default.
    static member None =
        ResourceLimits(None, false, None, None, WindowsUiRestrictions.None, None, None, None)

    /// Maximum total memory for the tree, in bytes. `None` leaves memory unbounded.
    member _.MemoryMax = memoryMax

    /// Whether a Linux cgroup v2 OOM event kills the whole contained tree atomically. This is a
    /// cgroup-only policy; requesting it on another mechanism is refused with `ProcessError.Unsupported`.
    member _.OomGroupKill = oomGroupKill

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

    /// The directional disk I/O ceiling for one explicit device/volume, or `None` when no I/O cap is
    /// requested. The target and rates are preserved exactly so `ProcessGroup.Options` reflects the
    /// full limit set that the backend accepted.
    member _.IoMax = ioMax

    /// A copy with the memory cap set. `bytes` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): a non-positive cap could never let anything run, so it is a
    /// misconfiguration rather than a meaningful limit, and previously degraded silently (e.g. a
    /// negative value converting to a huge `unativeint` on Windows — effectively "unlimited").
    member _.WithMemoryMax(bytes: int64) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0L)
        ResourceLimits(Some bytes, oomGroupKill, maxProcesses, cpuQuota, uiRestrictions, cpuAffinity, cpuTimeMax, ioMax)

    /// A copy that asks Linux cgroup v2 to treat the cgroup as one OOM unit (`memory.oom.group=1`),
    /// so the kernel kills the whole tree instead of selecting one victim. Unsupported outside cgroup v2.
    member _.WithOomGroupKill() =
        ResourceLimits(memoryMax, true, maxProcesses, cpuQuota, uiRestrictions, cpuAffinity, cpuTimeMax, ioMax)

    /// A copy with the live-process cap set. `count` must be positive — zero or negative is rejected
    /// (`ArgumentOutOfRangeException`): the tree always has at least its own leader process, so a
    /// non-positive cap could never be satisfied.
    member _.WithMaxProcesses(count: int) =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0)
        ResourceLimits(memoryMax, oomGroupKill, Some count, cpuQuota, uiRestrictions, cpuAffinity, cpuTimeMax, ioMax)

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

        ResourceLimits(
            memoryMax,
            oomGroupKill,
            maxProcesses,
            Some cores,
            uiRestrictions,
            cpuAffinity,
            cpuTimeMax,
            ioMax
        )

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

        ResourceLimits(memoryMax, oomGroupKill, maxProcesses, cpuQuota, restrictions, cpuAffinity, cpuTimeMax, ioMax)

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

        ResourceLimits(
            memoryMax,
            oomGroupKill,
            maxProcesses,
            cpuQuota,
            uiRestrictions,
            Some(List.sort requested),
            cpuTimeMax,
            ioMax
        )

    /// A copy limiting CPU time. `duration` must be finite and strictly positive. POSIX rounds the
    /// soft `RLIMIT_CPU` up to whole seconds and gives the hard limit one additional second so the
    /// process can observe `SIGXCPU`; Windows uses the Job Object's 100-nanosecond tick precision.
    member _.WithCpuTimeMax(duration: TimeSpan) =
        if duration <= TimeSpan.Zero then
            raise (ArgumentOutOfRangeException(nameof duration, duration, "CPU time must be positive and finite"))

        ResourceLimits(
            memoryMax,
            oomGroupKill,
            maxProcesses,
            cpuQuota,
            uiRestrictions,
            cpuAffinity,
            Some duration,
            ioMax
        )

    /// A copy applying directional disk I/O ceilings to one explicit device or volume. `target` is
    /// a Linux cgroup v2 `major:minor` key or a Windows NT volume device name. A `None` rate leaves
    /// that direction unbounded; at least one rate must be supplied. `Some` rates must be positive.
    /// Linux can enforce all four directions independently. Windows has one aggregate bandwidth and
    /// one aggregate IOPS field, so it accepts the request only when read/write pairs are equal.
    member _.WithIoMax
        (
            target: string,
            readBytesPerSecond: int64 option,
            writeBytesPerSecond: int64 option,
            readOperationsPerSecond: int64 option,
            writeOperationsPerSecond: int64 option
        ) =
        ArgumentNullException.ThrowIfNull(target, nameof target)

        if String.IsNullOrWhiteSpace target then
            raise (ArgumentException("the I/O limit target must not be empty", nameof target))

        let validateRate (name: string) (rate: int64 option) =
            match rate with
            | Some value when value <= 0L ->
                raise (ArgumentOutOfRangeException(name, value, "an I/O rate must be positive; use None to remove it"))
            | _ -> ()

        validateRate (nameof readBytesPerSecond) readBytesPerSecond
        validateRate (nameof writeBytesPerSecond) writeBytesPerSecond
        validateRate (nameof readOperationsPerSecond) readOperationsPerSecond
        validateRate (nameof writeOperationsPerSecond) writeOperationsPerSecond

        if
            readBytesPerSecond.IsNone
            && writeBytesPerSecond.IsNone
            && readOperationsPerSecond.IsNone
            && writeOperationsPerSecond.IsNone
        then
            raise (ArgumentException("at least one I/O rate must be supplied", nameof readBytesPerSecond))

        let ioMax =
            IoMax(target, readBytesPerSecond, writeBytesPerSecond, readOperationsPerSecond, writeOperationsPerSecond)

        ResourceLimits(
            memoryMax,
            oomGroupKill,
            maxProcesses,
            cpuQuota,
            uiRestrictions,
            cpuAffinity,
            cpuTimeMax,
            Some ioMax
        )

    /// Convenience overload for callers that use zero as the unbounded sentinel. Positive values set
    /// a ceiling; zero removes that directional ceiling. Negative values are rejected.
    member this.WithIoMax
        (
            target: string,
            readBytesPerSecond: int64,
            writeBytesPerSecond: int64,
            readOperationsPerSecond: int64,
            writeOperationsPerSecond: int64
        ) =
        let asOption (name: string) (value: int64) =
            if value < 0L then
                raise (ArgumentOutOfRangeException(name, value, "an I/O rate cannot be negative"))

            if value = 0L then None else Some value

        this.WithIoMax(
            target,
            asOption (nameof readBytesPerSecond) readBytesPerSecond,
            asOption (nameof writeBytesPerSecond) writeBytesPerSecond,
            asOption (nameof readOperationsPerSecond) readOperationsPerSecond,
            asOption (nameof writeOperationsPerSecond) writeOperationsPerSecond
        )

    /// Whether any limit is set. Windows uses this to decide whether the fresh Job needs a limit block.
    /// POSIX dispatch uses `WholeTreeAny` below because CPU-time alone is enforceable without a container.
    member internal _.Any =
        memoryMax.IsSome
        || oomGroupKill
        || maxProcesses.IsSome
        || cpuQuota.IsSome
        || uiRestrictions <> WindowsUiRestrictions.None
        || cpuAffinity.IsSome
        || cpuTimeMax.IsSome
        || ioMax.IsSome

    /// Whether a whole-tree container controller is needed. CPU-time alone is excluded because POSIX
    /// can enforce it per child with `RLIMIT_CPU`, including on macOS and the Linux process-group fallback.
    member internal _.WholeTreeAny =
        memoryMax.IsSome
        || oomGroupKill
        || maxProcesses.IsSome
        || cpuQuota.IsSome
        || uiRestrictions <> WindowsUiRestrictions.None
        || cpuAffinity.IsSome
        || ioMax.IsSome

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

    /// The honest typed refusal for disk I/O on a mechanism without a whole-tree I/O controller.
    member internal _.IoMaxUnsupported: ProcessError option =
        if ioMax.IsNone then
            Option.None
        else
            Some(
                ProcessError.Unsupported
                    "whole-tree disk I/O rate limits require Linux cgroup v2 io.max or a Windows Job Object I/O rate controller; this mechanism has no equivalent"
            )

/// The post-run verdict for **one** resource-limit axis — did a cap this group carried actually
/// **engage** while the tree ran? Read from `ProcessGroup.LimitEvidence()`; see that member for exactly
/// when it becomes available.
///
/// This answers a different question than `ProcessError.ResourceLimit`: that error is about
/// **admission** — "could the cap you asked for even be applied?" This verdict is about what only the
/// container itself can answer afterwards — "did a cap on this axis then actually fire?" A `Tripped`
/// verdict is returned only on **authoritative kernel/OS evidence** recorded by the group's own
/// container; exit codes and signals are never consulted, because a cap-driven kill and a self-inflicted
/// crash can look identical from the outside, and inferring from them would manufacture exactly the
/// false verdict this type exists to avoid.
[<RequireQualifiedAccess; NoComparison>]
type LimitVerdict =

    /// The kernel/OS recorded that this cap engaged: the tree was OOM-killed under its memory cap,
    /// denied a fork by its process cap, or throttled by its CPU quota.
    | Tripped

    /// This cap did **not** engage. Either its counter is present and reads zero, or this axis was never
    /// capped on this group at all — both are the same honest "no"; neither is a fallback for missing
    /// evidence (that is `Unknown`).
    | NotTripped

    /// **No authoritative evidence is available**, so the answer is refused rather than guessed at. The
    /// containment mechanism keeps no post-mortem record for this axis at all (every axis on a Windows
    /// Job Object, or the POSIX process-group fallback — neither persists a "this cap fired" counter), or
    /// the specific counter file/key could not be read on this run (an older kernel, a controller without
    /// that accounting, a cgroup already gone).
    | Unknown

/// Which resource-limit axes a `ProcessGroup` has carried a cap on at any point in its life — the
/// STICKY record `ProcessGroup` keeps (recorded at `Create` and at every later `UpdateLimits`) so
/// `ProcessGroup.LimitEvidence` stays honest across a live cap change: a cap that fired and was later
/// lifted must still be answered from the container's own counter, not reported `NotTripped` for an
/// axis nobody ever asked the container about. Recorded conservatively — every axis an `UpdateLimits`
/// call NAMES joins the record whether that call then succeeds or fails, mirroring ProcessKit-rs's
/// `CappedAxes` (see `group.rs`'s `update_limits_with`): neither backend applies its caps atomically, so
/// a failure part-way through can still have reached the OS for that axis, and skipping the record on
/// failure would let `LimitEvidence` answer `NotTripped` for it without ever reading a counter.
///
/// `CpuTimeMax` is tracked separately from `Cpu` (which reflects `ResourceLimits.CpuQuota` only): it has
/// no post-mortem counter on ANY mechanism (no Job-time or `RLIMIT_CPU` "this fired" record exists), so it
/// cannot itself back a `LimitEvidence` axis — but its presence still has to prevent the `Cpu` axis from
/// fabricating a `NotTripped` it cannot honestly stand behind. See `GuardCpuVerdict` (T-381/R-01).
[<Struct; NoComparison>]
type internal CappedAxes =
    { Memory: bool
      Processes: bool
      Cpu: bool
      CpuTimeMax: bool }

    /// No axis capped yet — the state of a freshly created, limit-free group.
    static member None =
        { Memory = false
          Processes = false
          Cpu = false
          CpuTimeMax = false }

    /// A copy recording every axis `limits` configures, keeping every axis already recorded — sticky,
    /// never un-records.
    member this.Record(limits: ResourceLimits) : CappedAxes =
        { Memory = this.Memory || limits.MemoryMax.IsSome
          Processes = this.Processes || limits.MaxProcesses.IsSome
          Cpu = this.Cpu || limits.CpuQuota.IsSome
          CpuTimeMax = this.CpuTimeMax || limits.CpuTimeMax.IsSome }

    /// The final guard every backend's raw `Cpu`-axis verdict passes through before it reaches a
    /// `LimitEvidence`. `Cpu` (and every counter a backend reads for it — a Job's CPU-rate control, cgroup
    /// v2's `cpu.stat` `nr_throttled`) is about `ResourceLimits.CpuQuota` alone; it says nothing about
    /// whether `ResourceLimits.CpuTimeMax` (Windows Job-time / POSIX `RLIMIT_CPU`) fired, and NO mechanism
    /// here keeps a post-mortem counter for that axis at all. So a raw `NotTripped` — "the quota mechanism
    /// did not throttle, or CPU was never capped at all" — is downgraded to `Unknown` whenever this group
    /// also carries a `CpuTimeMax` cap: an unattributable RLIMIT_CPU/job-time kill could be sitting right
    /// behind that "no", and reporting it anyway would be exactly the fabricated verdict this type exists
    /// to avoid (T-381/R-01). A raw `Tripped` is never downgraded — real quota-throttle evidence stays
    /// `Tripped` regardless of what else was capped.
    member this.GuardCpuVerdict(raw: LimitVerdict) : LimitVerdict =
        match raw with
        | LimitVerdict.NotTripped when this.CpuTimeMax -> LimitVerdict.Unknown
        | other -> other

/// Post-run evidence of whether the resource caps a `ProcessGroup` carried actually fired — one
/// `LimitVerdict` per axis, read from the container the group itself owns. See
/// `ProcessGroup.LimitEvidence()` for when it becomes available and what each axis means.
///
/// Deliberately per-axis rather than one whole-group verdict: folding three honest three-valued
/// verdicts into one would have to merge `NotTripped` and `Unknown` together, turning "we have no
/// evidence" into "no". Sealed with an internal constructor — built only by the backend that reads it.
///
/// Covers only `MemoryMax`, `MaxProcesses`, and `CpuQuota` (the `Cpu` axis is additionally guarded
/// against `CpuTimeMax` — see `CappedAxes.GuardCpuVerdict`). `IoMax` and `CpuAffinity` have **no**
/// corresponding axis at all: no containment mechanism here keeps a post-mortem "this whole-tree I/O or
/// affinity cap engaged" counter, so there is nothing honest to report for them, ever — not even
/// `Unknown`. `WindowsUiRestrictions` and `OomGroupKill` are policy toggles rather than caps that "fire",
/// so they are likewise out of scope.
[<Sealed>]
type LimitEvidence internal (memory: LimitVerdict, processes: LimitVerdict, cpu: LimitVerdict) =

    /// The verdict for `ResourceLimits.MemoryMax`.
    member _.Memory = memory

    /// The verdict for `ResourceLimits.MaxProcesses`.
    member _.Processes = processes

    /// The verdict for `ResourceLimits.CpuQuota`. When this group also carries a `ResourceLimits.CpuTimeMax`
    /// cap, a `NotTripped` this axis would otherwise report is downgraded to `Unknown` instead — neither a
    /// Job Object's accounting nor cgroup v2's `cpu.stat` `nr_throttled` can attribute a Windows job-time or
    /// POSIX `RLIMIT_CPU` trip, so "the quota did not throttle" is not the same as "no CPU cap fired" once a
    /// `CpuTimeMax` is also configured (see `CappedAxes.GuardCpuVerdict`, T-381/R-01).
    member _.Cpu = cpu

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

    /// A copy enabling cgroup v2 whole-tree OOM kills. Linux cgroup v2-only; creation is refused
    /// with `ProcessError.Unsupported` on mechanisms that cannot provide this semantic.
    member _.WithOomGroupKill() =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithOomGroupKill())

    /// A copy capping the number of live processes in the tree at `count`.
    member _.WithMaxProcesses(count: int) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithMaxProcesses count)

    /// A copy capping the tree's CPU at `cores` cores' worth.
    member _.WithCpuQuota(cores: float) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithCpuQuota cores)

    /// A copy capping CPU time for each spawned run (or the whole Job on Windows).
    member _.WithCpuTimeMax(duration: TimeSpan) =
        ProcessGroupOptions(shutdownTimeout, stopSignal, limits.WithCpuTimeMax duration)

    /// A copy applying directional disk I/O ceilings to one explicit Linux device or Windows volume;
    /// see `ResourceLimits.WithIoMax` for target syntax and platform semantics.
    member _.WithIoMax
        (
            target: string,
            readBytesPerSecond: int64 option,
            writeBytesPerSecond: int64 option,
            readOperationsPerSecond: int64 option,
            writeOperationsPerSecond: int64 option
        ) =
        ProcessGroupOptions(
            shutdownTimeout,
            stopSignal,
            limits.WithIoMax(
                target,
                readBytesPerSecond,
                writeBytesPerSecond,
                readOperationsPerSecond,
                writeOperationsPerSecond
            )
        )

    /// Convenience overload using zero as the unbounded sentinel; see `ResourceLimits.WithIoMax`.
    member _.WithIoMax
        (
            target: string,
            readBytesPerSecond: int64,
            writeBytesPerSecond: int64,
            readOperationsPerSecond: int64,
            writeOperationsPerSecond: int64
        ) =
        ProcessGroupOptions(
            shutdownTimeout,
            stopSignal,
            limits.WithIoMax(
                target,
                readBytesPerSecond,
                writeBytesPerSecond,
                readOperationsPerSecond,
                writeOperationsPerSecond
            )
        )

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
