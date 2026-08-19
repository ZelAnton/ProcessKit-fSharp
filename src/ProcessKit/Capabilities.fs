namespace ProcessKit

open System
open System.Collections.Generic
open System.Runtime.InteropServices

/// What this host can do on ONE axis of the containment contract — the honest three-valued answer the
/// capability snapshot reports instead of a bare `bool`.
///
/// A boolean `false` says nothing about *why*, or about what would make it true, so a caller can neither
/// explain the refusal to an operator nor act on it. Every value here carries that missing half: a
/// `Qualified` capability states the qualification it holds under, and an `Unsupported` one states the
/// precondition that is missing. This mirrors the rest of the library, where an unavailable operation is
/// a typed `ProcessError` naming what it needed — never a silent downgrade.
///
/// The verb itself stays the authority at the moment it runs (see `ContainmentCapabilities`): this is a
/// point-in-time report, not a promise about a later call.
[<RequireQualifiedAccess; NoComparison>]
type Capability =

    /// Available on this host, unconditionally, for the options the snapshot was taken for.
    | Available

    /// Available, but only under the stated qualification — a platform ceiling (the Windows affinity mask
    /// covering one processor group), an approximation (the Windows CPU quota), or a precondition that
    /// cannot be probed without side effects (a Linux cgroup hierarchy that must be delegated from its
    /// real root). Read it before relying on the capability; the qualification is exactly what the
    /// corresponding verb still checks for itself.
    | Qualified of Qualification: string

    /// Not available here, with the precondition that is missing — a helper binary absent from every
    /// trusted directory, a kernel mechanism the OS does not have, a controller this cgroup hierarchy does
    /// not carry. The matching verb refuses with its own typed `ProcessError` (`Unsupported` where the
    /// mechanism has no such concept at all, `ResourceLimit` where the cap exists in principle but nothing
    /// here can enforce it), never a silent no-op.
    | Unsupported of Requires: string

    /// The qualification (`Qualified`) or the missing precondition (`Unsupported`) as text, and `None` when
    /// the capability is unconditionally `Available` — one accessor for a log line or a status page that
    /// does not want to match on the case.
    member this.Detail: string option =
        match this with
        | Capability.Available -> None
        | Capability.Qualified qualification -> Some qualification
        | Capability.Unsupported requires -> Some requires

/// One external binary this platform's spawn paths must load, and whether this host actually holds it.
///
/// ProcessKit shells out to a helper only where the OS offers no in-process equivalent — arming
/// `PR_SET_PDEATHSIG` inside the child, establishing a controlling terminal, running a `.cmd` shim. A
/// missing helper is never worked around silently: the affected verb fails with a typed `ProcessError`
/// naming the helper. This entry is that same fact, available *before* the spawn.
///
/// The POSIX security helpers are resolved only from a fixed list of trusted system directories and never
/// from `PATH`, so a helper present on `PATH` alone is reported missing here — deliberately, because the
/// spawn refuses it too (a `PATH`-hijackable binary would run with the caller's full privileges before any
/// drop). See the hardening guide.
[<Sealed>]
type PlatformHelper internal (name: string, purpose: string, availability: Capability) =

    /// The helper as it is named on this platform (`setpriv`, `setsid`, `prlimit`, `/bin/sh`, `cmd.exe`).
    /// Never a resolved absolute path: the snapshot reports capability, not this host's filesystem layout.
    member _.Name = name

    /// What ProcessKit needs it for — the knobs and paths that stop working without it.
    member _.Purpose = purpose

    /// Whether this host holds it where ProcessKit will look, and the precondition when it does not.
    member _.Availability = availability

/// What `ProcessGroup.Signal` / `RunningProcess.Signal` can actually deliver through the mechanism the
/// snapshot's options select. The three rows are the signal vocabulary's platform divergence: the
/// unconditional hard kill, the soft stop, and everything else.
[<Sealed>]
type SignalCapabilities internal (kill: Capability, softStop: Capability, arbitrary: Capability) =

    /// `Signal.Kill` — the unblockable kill. Everywhere the mechanism's own atomic tree kill (a Job Object
    /// terminate, `cgroup.kill`, `killpg` with `SIGKILL`).
    member _.Kill = kill

    /// `Signal.Int` / `Signal.Term` — the soft stop, and what `ProcessGroup.ShutdownAsync` sends before it
    /// escalates.
    member _.SoftStop = softStop

    /// Every other signal — `Signal.Hup`/`Quit`/`Usr1`/`Usr2` and the raw `Signal.Other n` escape hatch.
    member _.Arbitrary = arbitrary

/// What this host can enforce, per `ResourceLimits` dimension.
///
/// **These answer for the HOST, not for the mechanism the snapshot's options happen to select.** On Linux,
/// asking for a whole-tree cap is itself what selects the cgroup v2 mechanism, so reporting "unsupported"
/// for a limit-free options set would understate what the host can do — the honest question each member
/// answers is "if this cap were added to the options, could this host enforce it?". `Creation` and
/// `Mechanism` are the members that answer for the options as they stand.
[<Sealed>]
type ResourceLimitCapabilities
    internal
    (
        memoryMax: Capability,
        oomGroupKill: Capability,
        maxProcesses: Capability,
        cpuQuota: Capability,
        cpuTimeMax: Capability,
        cpuAffinity: Capability,
        ioMax: Capability,
        uiRestrictions: Capability,
        liveUpdate: Capability
    ) =

    /// `ResourceLimits.WithMemoryMax` — a whole-tree memory ceiling.
    member _.MemoryMax = memoryMax

    /// `ResourceLimits.WithOomGroupKill` — a cgroup v2-only policy that makes an OOM kill take the whole
    /// tree instead of one selected victim.
    member _.OomGroupKill = oomGroupKill

    /// `ResourceLimits.WithMaxProcesses` — a cap on the number of live processes in the tree.
    member _.MaxProcesses = maxProcesses

    /// `ResourceLimits.WithCpuQuota` — a fraction-of-a-core CPU ceiling for the tree.
    member _.CpuQuota = cpuQuota

    /// `ResourceLimits.WithCpuTimeMax` — consumed CPU time. The deliberate POSIX exception: it needs no
    /// whole-tree container, because POSIX applies it per spawned child through `RLIMIT_CPU`.
    member _.CpuTimeMax = cpuTimeMax

    /// `ResourceLimits.WithCpuAffinity` — pinning the tree to a set of cores.
    member _.CpuAffinity = cpuAffinity

    /// `ResourceLimits.WithIoMax` — directional disk I/O ceilings for one device or volume.
    member _.IoMax = ioMax

    /// `ResourceLimits.WithUiRestrictions` — the Windows Job Object desktop-session restrictions
    /// (clipboard, desktops, display/system parameters, exit-Windows).
    member _.UiRestrictions = uiRestrictions

    /// `ProcessGroup.UpdateLimits` — replacing the caps on a LIVE group without recreating it or
    /// restarting its children.
    member _.LiveUpdate = liveUpdate

/// A point-in-time, side-effect-free snapshot of what process containment can actually do on **this**
/// host for a given `ProcessGroupOptions` — obtained from `ProcessGroup.Capabilities`, without creating a
/// group, spawning a process, or touching any container.
///
/// `ProcessGroup.Mechanism` already reports the primitive a group ended up with, but only once one exists;
/// until now a caller who needed to pick a portable policy *before* spawning had to create a group and try
/// each operation to find out what it would get. This is the same honesty contract answered up front: for
/// every axis either a real availability on this platform and these options, or a typed `Capability` that
/// names the missing precondition — never a bare `false`.
///
/// **A snapshot, not a promise.** Every value is read from the platform facts in force at the moment of
/// the call: a mounted cgroup v2 hierarchy, a helper binary present in a trusted directory, the ConPTY
/// entry point exported by this Windows build. A host can gain or lose any of them afterwards — a package
/// installed, a filesystem unmounted — so the answer here is the answer for *now*, and the verb itself
/// remains the authority at the moment it runs, still returning its own typed `ProcessError`. It is
/// deliberately not cached for that reason. What it never does is create a process, a group, or a
/// container, and it neither reads nor reports any argv or environment value.
[<Sealed>]
type ContainmentCapabilities
    internal
    (
        mechanism: Mechanism option,
        creation: Capability,
        resourceLimits: ResourceLimitCapabilities,
        signals: SignalCapabilities,
        adoption: Capability,
        adoptionByPid: Capability,
        pty: Capability,
        ptyResize: Capability,
        killOnParentDeath: Capability,
        killOnParentDeathScope: KillOnParentDeathScope,
        helpers: PlatformHelper list
    ) =

    /// The OS primitive `ProcessGroup.Create` would contain the tree with for these options on this host —
    /// the same choice the real creation makes, decided from the same platform facts. `None` when these
    /// options cannot be honoured here at all, in which case `Creation` names what they would need.
    member _.Mechanism = mechanism

    /// Whether `ProcessGroup.Create(options)` can succeed on this host, and under what qualification.
    /// `Unsupported` carries the precondition behind the typed error `Create` returns (a
    /// `ProcessError.ResourceLimit` for a cap nothing here can enforce, a `ProcessError.Unsupported` for a
    /// concept this platform does not have at all).
    ///
    /// `Available` means that nothing in these options is refused up front here — it is not a promise that
    /// the OS call cannot fail. Creating the native container is exactly the part a snapshot must not run,
    /// so a Job the host will not hand out, or a cgroup whose controllers cannot be delegated, still
    /// surfaces at `Create`. Where that second failure is *expected* rather than exceptional — the cgroup
    /// v2 case, which cannot be settled without attempting the write — this is `Qualified` and says so.
    member _.Creation = creation

    /// What this host can enforce per resource-limit dimension. These answer for the host rather than for
    /// the mechanism these options select — see `ResourceLimitCapabilities`.
    member _.ResourceLimits = resourceLimits

    /// What the selected mechanism can deliver from the `Signal` vocabulary.
    member _.Signals = signals

    /// `ProcessGroup.Adopt` — bringing an already-running external process into the container. Follows the
    /// mechanism these options select: a Windows Job Object always can, a Linux cgroup v2 group can (which
    /// is what requesting whole-tree limits selects), and the POSIX process group cannot at all.
    member _.Adoption = adoption

    /// `ProcessGroup.AdoptByPid` — the same, from a **bare pid** rather than a `System.Diagnostics.Process`.
    /// A separate axis because it is answered by a different question and can differ from `Adoption` on the
    /// very same host: what it needs is an identity ANCHOR for the number, not a primitive that relocates a
    /// process. The Job Object and cgroup v2 mechanisms hold one inherently (a process object; kernel cgroup
    /// membership). The POSIX process group — which cannot `Adopt` at all — can still track a foreign pid
    /// against a re-verified start-time token, so it is `Qualified` wherever this host has a reader for one
    /// (Linux `/proc`, macOS `proc_pidinfo`) and `Unsupported` where it does not (the BSDs), rather than
    /// tracking a bare number teardown would later SIGKILL whoever holds.
    member _.AdoptionByPid = adoptionByPid

    /// `Command.Pty` — starting a child on a pseudo-terminal. Reported from the very host gate the spawn
    /// applies (the ConPTY entry point on Windows; the `setsid --ctty` helper plus `/dev/ptmx` on POSIX),
    /// so the report cannot drift from what a real PTY spawn would do.
    member _.Pty = pty

    /// `RunningProcess.ResizeAsync` — resizing a PTY's window. Available wherever a PTY run can be started
    /// here; a run started WITHOUT `Command.Pty` is refused with `ProcessError.Unsupported` on every
    /// platform, which is a property of the run rather than of this host.
    member _.PtyResize = ptyResize

    /// `Command.KillOnParentDeath` — reaping a child when its parent dies *suddenly* (a crash, `SIGKILL`,
    /// `TerminateProcess`), which no `Dispose` or finalizer can cover. `KillOnParentDeathScope` reports how
    /// far the cleanup reaches where it is available.
    member _.KillOnParentDeath = killOnParentDeath

    /// The platform-fixed *scope* of that cleanup — the whole tree (Windows), the direct child only
    /// (Linux), or nothing (macOS/BSD). Identical to `Command.KillOnParentDeathScope()`, and reported
    /// independently of whether the verb was requested.
    member _.KillOnParentDeathScope = killOnParentDeathScope

    /// The external helper binaries this platform's spawn paths must load, each with what it is needed for
    /// and whether this host holds it. Only the helpers that actually participate on this platform are
    /// listed. A fresh list each read, so a caller can never mutate the snapshot through it.
    member _.Helpers: IReadOnlyList<PlatformHelper> =
        List.toArray helpers :> IReadOnlyList<PlatformHelper>

/// The mechanism `ProcessGroup.Create` picks for one `ProcessGroupOptions`, or the typed refusal it fails
/// with — decided from platform facts ALONE, before any native container is created.
[<RequireQualifiedAccess; NoComparison>]
type internal MechanismChoice =

    /// `Create` goes on to build this mechanism's container (which can still fail natively — a Job whose
    /// limits will not apply, a cgroup whose controllers cannot be delegated).
    | Selected of Mechanism

    /// `Create` refuses these options on this host with this typed error, having created nothing.
    | Refused of ProcessError

/// The single up-front decision behind BOTH `ProcessGroup.Create` and the capability snapshot: which
/// mechanism a set of options selects on this platform, or the typed error it is refused with.
///
/// It lives here, apart from `Create`, so the two cannot drift: a snapshot that predicted a mechanism
/// `Create` would not actually pick — or called a refusal survivable — would be exactly the fabricated
/// availability this feature exists to avoid. `Create` keeps everything that follows the decision (the
/// native Job/cgroup creation and its own failures); this is only the part that is decidable without side
/// effects, which is what makes it shareable with a probe that must create nothing.
module internal MechanismSelection =

    /// The decision, with the platform facts injected. The cgroup probes are thunks so they run only on
    /// the branch that consults them — `Create` must not pay a filesystem probe on a Windows or limit-free
    /// POSIX group — and so a test can drive every mechanism from any build host.
    let chooseUsing
        (isWindows: bool)
        (isLinux: bool)
        (cgroupV2Available: unit -> bool)
        (cgroupIoAvailable: unit -> bool)
        (options: ProcessGroupOptions)
        : MechanismChoice =
        let limits = options.Limits

        if isWindows then
            if limits.OomGroupKill then
                MechanismChoice.Refused(
                    ProcessError.Unsupported
                        "whole-tree OOM kill is a Linux cgroup v2 memory.oom.group policy; Windows Job Objects have no equivalent"
                )
            elif options.StopSignal <> Signal.Term then
                MechanismChoice.Refused(
                    ProcessError.Unsupported
                        $"ProcessGroupOptions.StopSignal({options.StopSignal}) on Windows; only the default Signal.Term contract maps to the existing WM_CLOSE/CTRL+BREAK graceful path"
                )
            else
                MechanismChoice.Selected Mechanism.JobObject
        else
            // Job Object UI restrictions are refused before any other off-Windows dispatch: unlike the
            // resource caps — which a cgroup v2 hierarchy CAN enforce, and whose absence is therefore a
            // `ResourceLimit` — a clipboard/desktop/exit-Windows restriction has no analogue in any POSIX
            // primitive at all, so `Unsupported` is the honest classification. Never a group that silently
            // runs its tree unrestricted.
            match limits.UiRestrictionsUnsupported with
            | Some error -> MechanismChoice.Refused error
            | None when limits.IoMax.IsSome && not isLinux ->
                MechanismChoice.Refused(
                    ProcessError.Unsupported
                        "whole-tree disk I/O rate limits require Linux cgroup v2 io.max or a Windows Job Object I/O rate controller"
                )
            | None when limits.OomGroupKill && not isLinux ->
                MechanismChoice.Refused(
                    ProcessError.Unsupported
                        "whole-tree OOM kill is a Linux cgroup v2 memory.oom.group policy; this platform has no equivalent"
                )
            | None ->
                if isLinux && limits.WholeTreeAny then
                    if cgroupV2Available () then
                        if limits.IoMax.IsSome && not (cgroupIoAvailable ()) then
                            MechanismChoice.Refused(
                                ProcessError.Unsupported
                                    "the Linux cgroup v2 hierarchy does not expose the io controller required by io.max"
                            )
                        else
                            MechanismChoice.Selected Mechanism.CgroupV2
                    elif limits.OomGroupKill then
                        MechanismChoice.Refused(
                            ProcessError.Unsupported
                                "whole-tree OOM kill requires an available Linux cgroup v2 memory.oom.group mechanism"
                        )
                    else
                        MechanismChoice.Refused(
                            ProcessError.ResourceLimit
                                "cgroup v2 is not mounted; whole-tree resource limits need a Windows Job Object or Linux cgroup v2"
                        )
                elif limits.WholeTreeAny then
                    // macOS / BSD, or Linux without cgroup v2 — no whole-tree limit primitive.
                    MechanismChoice.Refused(
                        ProcessError.ResourceLimit
                            "this platform has no whole-tree resource-limit primitive (needs a Windows Job Object or Linux cgroup v2)"
                    )
                else
                    // No limits: the POSIX group forms when children are spawned (each becomes its own pgid).
                    MechanismChoice.Selected Mechanism.ProcessGroup

    /// The decision on the real platform — what `ProcessGroup.Create` dispatches on.
    let choose (options: ProcessGroupOptions) : MechanismChoice =
        chooseUsing
            (RuntimeInformation.IsOSPlatform OSPlatform.Windows)
            (RuntimeInformation.IsOSPlatform OSPlatform.Linux)
            Native.Cgroup.cgroupV2Available
            Native.Cgroup.cgroupIoAvailable
            options

/// Builds a `ContainmentCapabilities` from a set of platform facts.
///
/// The facts are a record rather than live probe calls so the snapshot for EVERY mechanism can be
/// exercised deterministically from any build host — the `GracefulTeardown.pollUsing` /
/// `CgroupMemberStats.sample` seam this codebase already uses for platform-shaped logic. `current` is the
/// one place that reads the real host, and it reads each fact from the very probe the corresponding spawn
/// or creation path consults, so a reported capability cannot drift from the behaviour it describes.
module internal CapabilityProbe =

    /// Everything about the host the snapshot depends on, read once per snapshot. Every field is a
    /// point-in-time observation, never cached across calls.
    type PlatformFacts =
        {
            /// This host is Windows (the Job Object mechanism, ConPTY, `cmd.exe`).
            IsWindows: bool

            /// This host is Linux (the cgroup v2 mechanism, `PR_SET_PDEATHSIG`, util-linux helpers).
            IsLinux: bool

            /// The platform-fixed scope of `Command.KillOnParentDeath` cleanup (`KillOnParentDeathScope.Current`).
            ParentDeathScope: KillOnParentDeathScope

            /// Windows only: this build exports `CreatePseudoConsole` (Windows 10 1809+).
            ConPtyAvailable: bool

            /// Windows only: `cmd.exe` is present in the Windows system directory (the `.cmd`/`.bat` shim host).
            CmdExeAvailable: bool

            /// Linux only: a usable cgroup v2 hierarchy is mounted (its root's `cgroup.controllers` is non-empty).
            CgroupV2Available: bool

            /// Linux only: that hierarchy advertises the `io` controller (`io.max`).
            CgroupIoAvailable: bool

            /// Linux only: that hierarchy advertises the `cpuset` controller (`cpuset.cpus`, the affinity pin).
            CgroupCpusetAvailable: bool

            /// POSIX only: the typed refusal a `Command.Pty` spawn would give this host up front, or `None`
            /// when it can honour a PTY — the spawn's own gate, not a second opinion.
            PtyHostRefusal: ProcessError option

            /// POSIX only: the `setpriv` privilege-drop / `--pdeathsig` helper resolves in a trusted directory.
            PrivilegeDropHelperAvailable: bool

            /// POSIX only: the `setsid` controlling-terminal helper resolves in a trusted directory.
            ControllingTerminalHelperAvailable: bool

            /// POSIX only: the `prlimit` helper that applies `Command.Rlimit` — the PER-PROCESS
            /// `setrlimit(2)` caps, not the whole-tree `ResourceLimits` above — resolves in a trusted
            /// directory.
            ProcessLimitHelperAvailable: bool

            /// POSIX only: `/bin/sh` is present and executable (the cgroup launcher, the `RLIMIT_CPU` shim,
            /// and the `KillOnParentDeath` pre-arm guard all `exec` it by absolute path).
            SystemShellAvailable: bool

            /// POSIX only: this host has a start-time identity reader at all (Linux `/proc/<pid>/stat`,
            /// macOS `proc_pidinfo`) — the anchor bare-pid adoption takes for a foreign number on the
            /// process-group mechanism. Read from the very probe that path consults.
            ProcessIdentityReaderAvailable: bool

            /// POSIX only: the trusted directories the security helpers are searched in, as an error message
            /// spells them — so a reported precondition names the list that was really searched.
            TrustedHelperDirectories: string

            /// Windows only: how many cores one Job Object affinity mask can name on this host (its
            /// pointer-sized word: 64 on x64, 32 on x86).
            AffinityMaskWidth: int
        }

    // ------------------------------------------------------------------------------------------------
    // The qualification / precondition texts. Each states the fact a caller needs to act on, and is
    // written once so the same condition cannot be described two ways across the axes that share it.
    // ------------------------------------------------------------------------------------------------

    /// Why a mounted cgroup v2 hierarchy is still not proof that a cap will apply. Deliberately a
    /// QUALIFICATION rather than an availability claim: enabling a controller (writing the parent's
    /// `cgroup.subtree_control`) is permitted only at the real hierarchy root, and a cgroup NAMESPACE root
    /// — what an ordinary container or a systemd scope/service sees — looks identical from inside, so no
    /// side-effect-free probe can settle it. "The hierarchy exists" and "the cap can be enforced" are
    /// neighbouring facts, not the same one.
    let private cgroupRootQualification =
        "the cgroup v2 controllers must be delegable from the REAL cgroup v2 hierarchy root; inside a container or a systemd scope/service that write is refused and ProcessGroup.Create fails with ProcessError.ResourceLimit"

    let private cgroupControllerMissing (controller: string) (file: string) =
        $"the cgroup v2 '{controller}' controller ({file}); this hierarchy's cgroup.controllers does not list it, so no such cap can be enforced here"

    let private noWholeTreeContainer =
        "a whole-tree container - a Windows Job Object or a usable Linux cgroup v2 hierarchy; this host has neither, so ProcessGroup.Create refuses the cap with ProcessError.ResourceLimit rather than running the tree unbounded"

    let private cgroupNotMounted =
        "a mounted, usable Linux cgroup v2 hierarchy (its root's cgroup.controllers must be non-empty); without one ProcessGroup.Create refuses a whole-tree cap with ProcessError.ResourceLimit"

    let private uiRestrictionsWindowsOnly =
        "a Windows Job Object; UI restrictions bound what a tree may do to the interactive desktop session, which no POSIX primitive - cgroup v2 included - has an equivalent for"

    let private systemShellMissing (purpose: string) =
        $"'/bin/sh', which ProcessKit execs by absolute path to {purpose}; it was not found there"

    /// The trusted-directory helper precondition, worded as the spawn's own refusal does — including that
    /// a copy sitting on `PATH` does not count, because the spawn will not load it either.
    let private trustedHelperMissing (helper: string) (directories: string) (purpose: string) =
        $"the '{helper}' helper (util-linux) for {purpose}, loaded only from a trusted system directory ({directories}) and never from PATH so it cannot be hijacked; it was not found in any of them (present on mainstream Linux, absent on macOS/BSD)"

    let private availableWhen (available: bool) (requires: string) =
        if available then
            Capability.Available
        else
            Capability.Unsupported requires

    /// The precondition inside a creation refusal, as the missing-requirement text this snapshot reports.
    /// The typed error's own `Detail`/`Operation` IS that text — `Message` only wraps it in a class prefix
    /// ("unsupported: ", "resource limit could not be enforced: ") that would read wrong after "requires".
    /// So the refusal a caller sees here and the one `ProcessGroup.Create` returns say the same thing, in
    /// the shape each of them is read in.
    let private refusalRequirement (error: ProcessError) : string =
        match error with
        | ProcessError.Unsupported operation -> operation
        | ProcessError.ResourceLimit detail -> detail
        | other -> other.Message

    // ------------------------------------------------------------------------------------------------
    // Per-axis answers
    // ------------------------------------------------------------------------------------------------

    /// The whole-tree limit dimensions, answered for the HOST (see `ResourceLimitCapabilities`): the
    /// question is what could be enforced if the cap were requested, not what the current options selected.
    let private resourceLimits (facts: PlatformFacts) : ResourceLimitCapabilities =
        let cpuTimeMax =
            if facts.IsWindows then
                Capability.Available
            else
                // The POSIX exception: no container needed, but the rlimit is applied by a `/bin/sh`
                // `ulimit -t` shim that execs the target in place, so a host without `/bin/sh` cannot
                // apply it at all.
                availableWhen
                    facts.SystemShellAvailable
                    (systemShellMissing "apply the CpuTimeMax RLIMIT_CPU (ulimit -t) before exec'ing the child")

        if facts.IsWindows then
            ResourceLimitCapabilities(
                Capability.Available,
                Capability.Unsupported
                    "a Linux cgroup v2 memory.oom.group policy; Windows Job Objects have no equivalent",
                Capability.Available,
                Capability.Qualified
                    "approximate: the fraction-of-a-core quota is converted against this host's logical processor count into the Job Object's CPU rate control",
                cpuTimeMax,
                Capability.Qualified
                    $"the Job Object affinity mask is one pointer-sized word covering a single processor group, so only cores 0-{facts.AffinityMaskWidth - 1} are nameable on this host, and the set must be a subset of this process's own affinity; anything else is refused with ProcessError.ResourceLimit at creation",
                Capability.Qualified
                    "needs a Windows build whose Job Object carries the I/O rate control API (Windows 10 1607 / Server 2016 and later); an older build refuses the cap with ProcessError.Unsupported when the group is created",
                Capability.Available,
                Capability.Available
            )
        elif facts.IsLinux && facts.CgroupV2Available then
            // Every cgroup-backed dimension states BOTH halves of what a caller has to know: that asking
            // for the cap is itself what selects the cgroup v2 mechanism (so this answer is not a claim
            // about a limit-free group, which is a POSIX process group and enforces nothing), and that a
            // mounted hierarchy still has to be delegable from its real root.
            let cgroupCap (file: string) =
                Capability.Qualified
                    $"{file}, enforced by the Linux cgroup v2 mechanism that requesting any whole-tree cap selects; {cgroupRootQualification}"

            ResourceLimitCapabilities(
                cgroupCap "memory.max",
                cgroupCap "memory.oom.group",
                cgroupCap "pids.max",
                cgroupCap "cpu.max",
                cpuTimeMax,
                (if facts.CgroupCpusetAvailable then
                     cgroupCap "cpuset.cpus"
                 else
                     Capability.Unsupported(cgroupControllerMissing "cpuset" "cpuset.cpus")),
                (if facts.CgroupIoAvailable then
                     cgroupCap "io.max"
                 else
                     Capability.Unsupported(cgroupControllerMissing "io" "io.max")),
                Capability.Unsupported uiRestrictionsWindowsOnly,
                Capability.Qualified
                    $"ProcessGroup.UpdateLimits rewrites the live cgroup's controller files in place, so it needs a group created WITH whole-tree limits (which is what selects the cgroup v2 mechanism); a limit-free Linux group uses the POSIX process group and is refused with ProcessError.ResourceLimit - and {cgroupRootQualification}"
            )
        else
            // Linux without a usable cgroup v2 hierarchy, or macOS/BSD, which have no whole-tree primitive
            // at all. Both refuse a whole-tree cap at creation rather than running the tree unbounded; the
            // preconditions differ, so they are worded apart.
            let unsupported =
                Capability.Unsupported(
                    if facts.IsLinux then
                        cgroupNotMounted
                    else
                        noWholeTreeContainer
                )

            ResourceLimitCapabilities(
                unsupported,
                unsupported,
                unsupported,
                unsupported,
                cpuTimeMax,
                unsupported,
                unsupported,
                Capability.Unsupported uiRestrictionsWindowsOnly,
                unsupported
            )

    /// The signal vocabulary, per selected mechanism.
    let private signals (mechanism: Mechanism) : SignalCapabilities =
        match mechanism with
        | Mechanism.JobObject ->
            SignalCapabilities(
                Capability.Available,
                Capability.Qualified
                    "best-effort on Windows: a console CTRL+BREAK to each child started with Command.WindowsCtrlSignals() and a WM_CLOSE posted to every member with a top-level window; a group with neither is refused with ProcessError.Unsupported, never downgraded to a kill",
                Capability.Unsupported
                    "a POSIX mechanism; Windows maps only Signal.Kill (the Job terminate) and the best-effort Signal.Int/Signal.Term soft stop, and refuses every other signal with ProcessError.Unsupported"
            )
        | Mechanism.CgroupV2
        | Mechanism.ProcessGroup -> SignalCapabilities(Capability.Available, Capability.Available, Capability.Available)

    /// `ProcessGroup.Adopt`, per selected mechanism.
    let private adoption (facts: PlatformFacts) (mechanism: Mechanism) : Capability =
        match mechanism with
        | Mechanism.JobObject -> Capability.Available
        | Mechanism.CgroupV2 -> Capability.Available
        | Mechanism.ProcessGroup ->
            Capability.Unsupported(
                if facts.IsLinux then
                    "a group created WITH whole-tree resource limits, which is what selects the Linux cgroup v2 mechanism (setpgid only relocates our own children, and only before they exec)"
                else
                    "a Windows Job Object or a Linux cgroup v2 group; no POSIX primitive can move a foreign process into a process group (setpgid only relocates our own children, and only before they exec)"
            )

    /// `ProcessGroup.AdoptByPid`, per selected mechanism. It parts company with `adoption` on exactly one
    /// row — the POSIX process group, which cannot RELOCATE a foreign process but can TRACK one against an
    /// identity anchor, so the question there is whether this host can read such an anchor at all.
    let private adoptionByPid (facts: PlatformFacts) (mechanism: Mechanism) : Capability =
        match mechanism with
        | Mechanism.JobObject
        | Mechanism.CgroupV2 -> Capability.Available
        | Mechanism.ProcessGroup ->
            if facts.ProcessIdentityReaderAvailable then
                // A real qualification, not a footnote: the containment this mechanism gives an adopted
                // pid is narrower than the other two mechanisms give, and both halves of that are things
                // a caller has to plan around.
                Capability.Qualified
                    "the process is tracked INDIVIDUALLY against a start-time anchor re-verified before every signal and kill: it is listed, signalled and killed with the group, but processes it forks afterwards are NOT contained (no POSIX primitive moves a foreign, already-exec'ed process into our process group), a number whose anchor stops matching is dropped from the group rather than signalled, and a pid this process may not signal (another user's, a protected one) is refused at adoption rather than accepted as a member the group could not kill"
            else
                Capability.Unsupported
                    "a start-time identity reader for the pid (Linux /proc/<pid>/stat, macOS proc_pidinfo); this platform ships none ProcessKit can verify, so there is no anchor to bind the group to and a bare number would let teardown SIGKILL whatever holds it later — refused rather than downgraded"

    /// `Command.KillOnParentDeath`, from the same per-platform gate the POSIX spawn path applies.
    let private killOnParentDeath (facts: PlatformFacts) : Capability =
        if facts.IsWindows then
            Capability.Available
        elif facts.IsLinux then
            if not facts.PrivilegeDropHelperAvailable then
                Capability.Unsupported(
                    trustedHelperMissing
                        "setpriv"
                        facts.TrustedHelperDirectories
                        "arming PR_SET_PDEATHSIG (--pdeathsig)"
                )
            elif not facts.SystemShellAvailable then
                Capability.Unsupported(
                    systemShellMissing
                        "run the guard that detects a parent which died before PR_SET_PDEATHSIG could be armed"
                )
            else
                Capability.Qualified
                    "reaches the DIRECT CHILD only (the parent-death signal is not inherited across a fork), is reset when the child execs a set-uid/set-gid image, and is armed through the setpriv helper"
        else
            Capability.Unsupported
                "PR_SET_PDEATHSIG or an equivalent; macOS/BSD have none, so a KillOnParentDeath request fails the spawn with ProcessError.Unsupported rather than pretending the cleanup happens"

    /// `Command.Pty`, from the very host gate the spawn applies.
    let private pty (facts: PlatformFacts) : Capability =
        if facts.IsWindows then
            availableWhen
                facts.ConPtyAvailable
                "Windows 10 version 1809 (build 17763) or later, whose kernel32 exports the ConPTY API (CreatePseudoConsole); an older build is refused with ProcessError.Unsupported rather than falling back to pipes"
        else
            match facts.PtyHostRefusal with
            | None -> Capability.Available
            | Some refusal -> Capability.Unsupported(refusalRequirement refusal)

    /// The helpers this platform's spawn paths load, with the purpose each one serves.
    let private helpers (facts: PlatformFacts) : PlatformHelper list =
        if facts.IsWindows then
            [ PlatformHelper(
                  "cmd.exe",
                  "launching a .cmd/.bat program shim (cmd.exe /d /c), taken from the Windows system directory and never from PATH or %ComSpec%",
                  availableWhen
                      facts.CmdExeAvailable
                      "'cmd.exe' in the Windows system directory; without it a .cmd/.bat program cannot be launched (a batch file is not a directly-launchable image)"
              ) ]
        else
            [ PlatformHelper(
                  "setpriv",
                  "the Command.Uid/Gid/Groups privilege drop, and arming Command.KillOnParentDeath (--pdeathsig)",
                  availableWhen
                      facts.PrivilegeDropHelperAvailable
                      (trustedHelperMissing
                          "setpriv"
                          facts.TrustedHelperDirectories
                          "the privilege drop and KillOnParentDeath")
              )
              PlatformHelper(
                  "setsid",
                  "the Command.Pty controlling terminal (setsid --ctty), which makes the pty the child's controlling tty before it execs",
                  availableWhen
                      facts.ControllingTerminalHelperAvailable
                      (trustedHelperMissing "setsid" facts.TrustedHelperDirectories "the Pty controlling terminal")
              )
              PlatformHelper(
                  "prlimit",
                  "the Command.Rlimit per-process setrlimit(2) caps, which it applies to itself before it execs the target in place",
                  availableWhen
                      facts.ProcessLimitHelperAvailable
                      (trustedHelperMissing
                          "prlimit"
                          facts.TrustedHelperDirectories
                          "the Command.Rlimit per-process caps")
              )
              PlatformHelper(
                  "/bin/sh",
                  "the cgroup v2 self-migrating launcher, the CpuTimeMax (RLIMIT_CPU) shim, and the KillOnParentDeath pre-arm guard",
                  availableWhen
                      facts.SystemShellAvailable
                      (systemShellMissing
                          "run the cgroup launcher, the RLIMIT_CPU shim, and the KillOnParentDeath guard")
              ) ]

    /// The snapshot for `options` on the given facts — the whole computation, with nothing read from the
    /// live host, so every mechanism is reachable from any build platform.
    let snapshot (facts: PlatformFacts) (options: ProcessGroupOptions) : ContainmentCapabilities =
        let choice =
            MechanismSelection.chooseUsing
                facts.IsWindows
                facts.IsLinux
                (fun () -> facts.CgroupV2Available)
                (fun () -> facts.CgroupIoAvailable)
                options

        // The axes that follow from the MECHANISM cannot be answered when no group can be created with
        // these options: reporting what some other mechanism would have done would be exactly the
        // fabricated availability this snapshot exists to avoid. They carry the creation precondition
        // instead. The host-level axes (limits, PTY, kill-on-parent-death, helpers) are unaffected — they
        // are facts about the host, not about a group that was never created.
        let mechanism, creation, signalCapabilities, adoptionCapability, adoptionByPidCapability =
            match choice with
            | MechanismChoice.Selected mechanism ->
                let creation =
                    match mechanism with
                    | Mechanism.CgroupV2 -> Capability.Qualified cgroupRootQualification
                    | Mechanism.JobObject when options.Limits.IoMax.IsSome ->
                        Capability.Qualified
                            "the requested io.max needs a Windows build whose Job Object carries the I/O rate control API (Windows 10 1607 / Server 2016 and later); an older build fails the creation with ProcessError.Unsupported"
                    | Mechanism.JobObject
                    | Mechanism.ProcessGroup -> Capability.Available

                Some mechanism, creation, signals mechanism, adoption facts mechanism, adoptionByPid facts mechanism
            | MechanismChoice.Refused error ->
                let refusal = Capability.Unsupported(refusalRequirement error)

                None, refusal, SignalCapabilities(refusal, refusal, refusal), refusal, refusal

        let ptyCapability = pty facts

        let ptyResize =
            match ptyCapability with
            | Capability.Available -> Capability.Available
            | Capability.Qualified qualification -> Capability.Qualified qualification
            | Capability.Unsupported requires ->
                Capability.Unsupported $"a PTY run, which this host cannot start: {requires}"

        ContainmentCapabilities(
            mechanism,
            creation,
            resourceLimits facts,
            signalCapabilities,
            adoptionCapability,
            adoptionByPidCapability,
            ptyCapability,
            ptyResize,
            killOnParentDeath facts,
            facts.ParentDeathScope,
            helpers facts
        )

    /// Read the real host, once, for one snapshot. Each fact comes from the same probe the corresponding
    /// spawn / creation path consults, and the per-platform fields are read only on the platform that has
    /// them — a POSIX helper probe never runs on Windows (where `/bin` is not even a meaningful path), and
    /// the cgroup files are read only on Linux.
    let current (options: ProcessGroupOptions) : ContainmentCapabilities =
        let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
        let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

        let facts =
            { IsWindows = isWindows
              IsLinux = isLinux
              ParentDeathScope = KillOnParentDeathScope.Current
              ConPtyAvailable = isWindows && Native.Windows.conptyAvailable ()
              CmdExeAvailable = isWindows && Native.Windows.systemCmdExeAvailable ()
              CgroupV2Available = isLinux && Native.Cgroup.cgroupV2Available ()
              CgroupIoAvailable = isLinux && Native.Cgroup.cgroupIoAvailable ()
              CgroupCpusetAvailable = isLinux && Native.Cgroup.cgroupControllerAvailable "cpuset"
              PtyHostRefusal = (if isWindows then None else Native.Posix.ptyHostSupport ())
              PrivilegeDropHelperAvailable = not isWindows && Native.Posix.privilegeDropHelperAvailable ()
              ControllingTerminalHelperAvailable = not isWindows && Native.Posix.controllingTerminalHelperAvailable ()
              ProcessLimitHelperAvailable = not isWindows && Native.Posix.processLimitHelperAvailable ()
              SystemShellAvailable = not isWindows && Native.Posix.systemShellAvailable ()
              ProcessIdentityReaderAvailable = not isWindows && Native.Posix.processIdentityReaderAvailable ()
              TrustedHelperDirectories =
                (if isWindows then
                     ""
                 else
                     Native.Posix.trustedHelperDirectoriesInUseText ())
              AffinityMaskWidth = IntPtr.Size * 8 }

        snapshot facts options
