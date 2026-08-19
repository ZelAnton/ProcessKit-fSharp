namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open NUnit.Framework
open ProcessKit

/// Synthetic `PlatformFacts` for every mechanism, so the capability snapshot's whole matrix is verifiable
/// from ANY build host — the same injected-dependency seam the containment layer already uses for
/// platform-shaped logic (`GracefulTeardown.pollUsing`, `CgroupMemberStats.sample`, `PosixReap.leaderUsing`).
///
/// The live-host tests further down cross-check the real snapshot against a really created group, so these
/// stay what they are meant to be: the matrix, not a substitute for the platform's own answer.
module private CapabilityFacts =

    /// A Windows host with everything present — a Job Object, ConPTY, the system `cmd.exe`.
    let windows: CapabilityProbe.PlatformFacts =
        { IsWindows = true
          IsLinux = false
          IsFreeBsd = false
          ParentDeathScope = KillOnParentDeathScope.WholeTree
          ConPtyAvailable = true
          CmdExeAvailable = true
          CgroupV2Available = false
          CgroupIoAvailable = false
          CgroupCpusetAvailable = false
          PtyHostRefusal = None
          PrivilegeDropHelperAvailable = false
          ControllingTerminalHelperAvailable = false
          ProcessLimitHelperAvailable = false
          SystemShellAvailable = false
          TrustedHelperDirectories = ""
          ProcessIdentityReaderAvailable = false
          AffinityMaskWidth = 64 }

    /// A mainstream Linux host: a usable cgroup v2 hierarchy carrying every controller, util-linux present.
    let linux: CapabilityProbe.PlatformFacts =
        { IsWindows = false
          IsLinux = true
          IsFreeBsd = false
          ParentDeathScope = KillOnParentDeathScope.DirectChildOnly
          ConPtyAvailable = false
          CmdExeAvailable = false
          CgroupV2Available = true
          CgroupIoAvailable = true
          CgroupCpusetAvailable = true
          PtyHostRefusal = None
          PrivilegeDropHelperAvailable = true
          ControllingTerminalHelperAvailable = true
          ProcessLimitHelperAvailable = true
          SystemShellAvailable = true
          TrustedHelperDirectories = "/usr/bin, /bin, /usr/sbin, /sbin"
          ProcessIdentityReaderAvailable = true
          AffinityMaskWidth = 64 }

    /// The same Linux host with no usable cgroup v2 hierarchy — a container, or a v1-only kernel.
    let linuxWithoutCgroup =
        { linux with
            CgroupV2Available = false
            CgroupIoAvailable = false
            CgroupCpusetAvailable = false }

    /// macOS/BSD: no whole-tree limit primitive, no util-linux helpers, no `pdeathsig` analogue. `setsid`
    /// there does not provide `--ctty`, so the PTY host gate refuses exactly as the spawn path would.
    let macOs: CapabilityProbe.PlatformFacts =
        { IsWindows = false
          IsLinux = false
          IsFreeBsd = false
          ParentDeathScope = KillOnParentDeathScope.Nothing
          ConPtyAvailable = false
          CmdExeAvailable = false
          CgroupV2Available = false
          CgroupIoAvailable = false
          CgroupCpusetAvailable = false
          PtyHostRefusal =
            Some(
                ProcessError.Unsupported
                    "Pty (needs the 'setsid --ctty' controlling-terminal helper from util-linux, not found in any trusted directory)"
            )
          PrivilegeDropHelperAvailable = false
          ControllingTerminalHelperAvailable = false
          ProcessLimitHelperAvailable = false
          SystemShellAvailable = true
          TrustedHelperDirectories = "/usr/bin, /bin, /usr/sbin, /sbin"
          ProcessIdentityReaderAvailable = true
          AffinityMaskWidth = 64 }

    /// The BSDs OTHER than FreeBSD: the same absent whole-tree primitive as macOS, plus the one fact that
    /// separates them for bare-pid adoption — no start-time identity reader this library can verify, so
    /// there is no anchor to take for a foreign number.
    let bsd =
        { macOs with
            ProcessIdentityReaderAvailable = false }

    /// FreeBSD: everything the other BSDs report, except that this one host HAS a whole-tree containment
    /// primitive — the `procctl(2)` process reaper — which is what selects `Mechanism.ProcessReaper` for a
    /// limit-free group here. It is still not a limit primitive, so the resource-limit row does not move
    /// with it; only its precondition text says so in the reaper's own terms.
    let freeBsd = { bsd with IsFreeBsd = true }

    let noLimits () = ProcessGroupOptions()

    let wholeTreeLimits () =
        ProcessGroupOptions().WithMemoryMax(64L * 1024L * 1024L)

/// The public capability snapshot (`ProcessGroup.Capabilities`): the per-mechanism matrix on synthetic
/// platform facts, the "no axis is ever a bare no" invariant, and — on whichever OS the suite happens to
/// run — a cross-check that the snapshot agrees with what `ProcessGroup.Create` really does here.
[<TestFixture>]
type CapabilityTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    // ------------------------------------------------------------------------------------------------
    // Assertion helpers. Each takes the axis name so a failure says WHICH capability disagreed, and every
    // message is the single argument of `Assert.Fail` rather than an `Assert.That` message overload
    // (ambiguous for F#'s overload resolution).
    // ------------------------------------------------------------------------------------------------

    let available (axis: string) (capability: Capability) =
        match capability with
        | Capability.Available -> Assert.That(capability.Detail.IsNone, Is.True)
        | other -> Assert.Fail $"{axis}: expected Available, got {other}"

    let qualified (axis: string) (fragment: string) (capability: Capability) =
        match capability with
        | Capability.Qualified qualification ->
            if not (qualification.Contains(fragment, StringComparison.Ordinal)) then
                Assert.Fail $"{axis}: the qualification did not mention '{fragment}': {qualification}"
        | other -> Assert.Fail $"{axis}: expected Qualified, got {other}"

    /// Unsupported, and the precondition it states mentions `fragment`.
    let unsupported (axis: string) (fragment: string) (capability: Capability) =
        match capability with
        | Capability.Unsupported requires ->
            if not (requires.Contains(fragment, StringComparison.Ordinal)) then
                Assert.Fail $"{axis}: the precondition did not mention '{fragment}': {requires}"
        | other -> Assert.Fail $"{axis}: expected Unsupported, got {other}"

    /// Unsupported, whatever precondition it names (used where the wording is platform-dependent; the
    /// invariant test below is what proves every precondition is non-empty).
    let refused (axis: string) (capability: Capability) = unsupported axis "" capability

    let helperEntry (name: string) (capabilities: ContainmentCapabilities) : PlatformHelper =
        match capabilities.Helpers |> Seq.tryFind (fun entry -> entry.Name = name) with
        | Some entry -> entry
        | None ->
            let listed =
                capabilities.Helpers |> Seq.map (fun entry -> entry.Name) |> String.concat ", "

            Assert.Fail $"expected a '{name}' platform helper; the snapshot listed: {listed}"
            failwith "unreachable"

    // ------------------------------------------------------------------------------------------------
    // The matrix, per mechanism, on synthetic facts
    // ------------------------------------------------------------------------------------------------

    [<Test>]
    member _.``Windows reports the Job Object matrix - adoption, narrow signals, whole-tree parent death``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.windows (CapabilityFacts.noLimits ())

        Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.JobObject))
        available "Creation" caps.Creation

        // `AssignProcessToJobObject` adopts with or without limits — from a `Process` or from a bare pid
        // alike, because the anchor here is the process OBJECT the adopt's own `OpenProcess` returns.
        available "Adoption" caps.Adoption
        available "AdoptionByPid" caps.AdoptionByPid

        // The narrow Windows signal mapping, reported as three distinct answers rather than one bool.
        available "Signals.Kill" caps.Signals.Kill
        qualified "Signals.SoftStop" "CTRL+BREAK" caps.Signals.SoftStop
        unsupported "Signals.Arbitrary" "POSIX mechanism" caps.Signals.Arbitrary

        // The Job Object's own limit dimensions, including the ones that are real but qualified.
        available "MemoryMax" caps.ResourceLimits.MemoryMax
        available "MaxProcesses" caps.ResourceLimits.MaxProcesses
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax
        available "UiRestrictions" caps.ResourceLimits.UiRestrictions
        available "LiveUpdate" caps.ResourceLimits.LiveUpdate
        qualified "CpuQuota" "approximate" caps.ResourceLimits.CpuQuota
        qualified "CpuAffinity" "cores 0-63" caps.ResourceLimits.CpuAffinity
        qualified "IoMax" "I/O rate control" caps.ResourceLimits.IoMax
        unsupported "OomGroupKill" "cgroup v2" caps.ResourceLimits.OomGroupKill

        // Kill-on-parent-death needs no opt-in here and reaches the whole tree.
        available "KillOnParentDeath" caps.KillOnParentDeath
        Assert.That(caps.KillOnParentDeathScope, Is.EqualTo KillOnParentDeathScope.WholeTree)

        available "Pty" caps.Pty
        available "PtyResize" caps.PtyResize

        // Only the helpers this platform actually loads are listed.
        available "cmd.exe" (helperEntry "cmd.exe" caps).Availability
        Assert.That(caps.Helpers.Count, Is.EqualTo 1)

    [<Test>]
    member _.``Windows without ConPTY refuses a PTY and its resize with the build precondition``() =
        let caps =
            CapabilityProbe.snapshot
                { CapabilityFacts.windows with
                    ConPtyAvailable = false }
                (CapabilityFacts.noLimits ())

        unsupported "Pty" "Windows 10 version 1809" caps.Pty
        unsupported "PtyResize" "a PTY run, which this host cannot start" caps.PtyResize

    [<Test>]
    member _.``Linux with whole-tree limits selects cgroup v2, which is what makes adoption available``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.linux (CapabilityFacts.wholeTreeLimits ())

        Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.CgroupV2))

        // A mounted hierarchy is not proof the caps will apply: the controllers still have to be
        // delegable from the REAL cgroup root, which no side-effect-free probe can settle.
        qualified "Creation" "REAL cgroup v2 hierarchy root" caps.Creation
        available "Adoption" caps.Adoption
        available "AdoptionByPid" caps.AdoptionByPid

        available "Signals.Kill" caps.Signals.Kill
        available "Signals.SoftStop" caps.Signals.SoftStop
        available "Signals.Arbitrary" caps.Signals.Arbitrary

        qualified "MemoryMax" "memory.max" caps.ResourceLimits.MemoryMax
        qualified "OomGroupKill" "memory.oom.group" caps.ResourceLimits.OomGroupKill
        qualified "MaxProcesses" "pids.max" caps.ResourceLimits.MaxProcesses
        qualified "CpuQuota" "cpu.max" caps.ResourceLimits.CpuQuota
        qualified "CpuAffinity" "cpuset.cpus" caps.ResourceLimits.CpuAffinity
        qualified "IoMax" "io.max" caps.ResourceLimits.IoMax
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax
        unsupported "UiRestrictions" "Windows Job Object" caps.ResourceLimits.UiRestrictions

        qualified "KillOnParentDeath" "DIRECT CHILD only" caps.KillOnParentDeath
        Assert.That(caps.KillOnParentDeathScope, Is.EqualTo KillOnParentDeathScope.DirectChildOnly)

    [<Test>]
    member _.``a cgroup hierarchy missing a controller refuses only the caps that need it``() =
        let caps =
            CapabilityProbe.snapshot
                { CapabilityFacts.linux with
                    CgroupCpusetAvailable = false }
                (CapabilityFacts.wholeTreeLimits ())

        unsupported "CpuAffinity" "'cpuset' controller" caps.ResourceLimits.CpuAffinity
        // Everything the missing controller has nothing to do with is untouched.
        qualified "MemoryMax" "memory.max" caps.ResourceLimits.MemoryMax
        qualified "IoMax" "io.max" caps.ResourceLimits.IoMax

    [<Test>]
    member _.``a limit-free Linux group is a process group, yet the host's cgroup limits are not understated``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.linux (CapabilityFacts.noLimits ())

        // The options select the POSIX mechanism, and the mechanism-shaped axes say so honestly.
        Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.ProcessGroup))
        available "Creation" caps.Creation
        unsupported "Adoption" "whole-tree resource limits" caps.Adoption

        // ...yet the BARE-PID door is open here, and that divergence is the point of the second axis:
        // this mechanism cannot RELOCATE a foreign process, but it can TRACK one against the start-time
        // anchor this host can read — with the narrower containment stated in the qualification.
        qualified "AdoptionByPid" "tracked INDIVIDUALLY" caps.AdoptionByPid

        // ...but the limit dimensions answer for the HOST. Reporting them Unsupported here — merely
        // because THESE options asked for no cap — would understate a host that can enforce every one of
        // them the moment a cap is requested (which is exactly what selects the cgroup mechanism).
        qualified "MemoryMax" "memory.max" caps.ResourceLimits.MemoryMax
        qualified "MaxProcesses" "pids.max" caps.ResourceLimits.MaxProcesses

    [<Test>]
    member _.``Linux without cgroup v2 reports no mechanism for limited options and says what is missing``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.linuxWithoutCgroup (CapabilityFacts.wholeTreeLimits ())

        // No group can be created with these options here, so there is no mechanism to report and every
        // mechanism-shaped axis carries the creation precondition instead of another mechanism's answer.
        Assert.That(caps.Mechanism, Is.EqualTo(None: Mechanism option))
        unsupported "Creation" "cgroup v2 is not mounted" caps.Creation
        unsupported "Adoption" "cgroup v2 is not mounted" caps.Adoption
        unsupported "AdoptionByPid" "cgroup v2 is not mounted" caps.AdoptionByPid
        unsupported "Signals.Kill" "cgroup v2 is not mounted" caps.Signals.Kill

        // Host-level axes are unaffected: they are facts about the host, not about a group never created.
        unsupported "MemoryMax" "mounted, usable Linux cgroup v2" caps.ResourceLimits.MemoryMax
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax
        qualified "KillOnParentDeath" "DIRECT CHILD only" caps.KillOnParentDeath
        available "Pty" caps.Pty

    [<Test>]
    member _.``macOS refuses whole-tree limits, adoption, PTY and parent-death cleanup with preconditions``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.macOs (CapabilityFacts.noLimits ())

        Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.ProcessGroup))
        unsupported "Adoption" "Windows Job Object or a Linux cgroup v2" caps.Adoption
        // macOS has `proc_pidinfo`, so a bare pid CAN be anchored and tracked here.
        qualified "AdoptionByPid" "tracked INDIVIDUALLY" caps.AdoptionByPid
        unsupported "MemoryMax" "whole-tree container" caps.ResourceLimits.MemoryMax
        unsupported "CpuAffinity" "whole-tree container" caps.ResourceLimits.CpuAffinity
        unsupported "LiveUpdate" "whole-tree container" caps.ResourceLimits.LiveUpdate
        unsupported "UiRestrictions" "Windows Job Object" caps.ResourceLimits.UiRestrictions
        unsupported "KillOnParentDeath" "PR_SET_PDEATHSIG" caps.KillOnParentDeath
        Assert.That(caps.KillOnParentDeathScope, Is.EqualTo KillOnParentDeathScope.Nothing)
        unsupported "Pty" "controlling-terminal helper" caps.Pty

        // The CPU-time rlimit is the deliberate POSIX exception and survives the absent container.
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax

        // Every util-linux helper is listed as missing WITH its precondition, rather than omitted — the
        // per-process rlimit helper (`Command.Rlimit`) included, since a host that cannot load it cannot
        // apply those caps either and a snapshot that stayed silent about it would overstate this host.
        unsupported "setpriv" "trusted system directory" (helperEntry "setpriv" caps).Availability
        unsupported "setsid" "trusted system directory" (helperEntry "setsid" caps).Availability
        unsupported "prlimit" "trusted system directory" (helperEntry "prlimit" caps).Availability
        available "/bin/sh" (helperEntry "/bin/sh" caps).Availability

    [<Test>]
    member _.``a POSIX host with no start-time reader refuses bare-pid adoption instead of tracking a number``() =
        // The BSD row. Everything macOS reports stays the same except the one axis that depends on an
        // identity anchor: with no reader there is nothing to bind the group to, and the honest answer is
        // a typed refusal naming the missing reader — never a downgrade to tracking the bare number,
        // which would let teardown SIGKILL whatever holds it by then.
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.bsd (CapabilityFacts.noLimits ())

        Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.ProcessGroup))
        unsupported "AdoptionByPid" "start-time identity reader" caps.AdoptionByPid

        // The two adoption axes agree that this host cannot adopt, for two DIFFERENT reasons, and each
        // says its own.
        unsupported "Adoption" "no POSIX primitive can move a foreign process" caps.Adoption

        // Nothing else moves with it: the reader is not a helper binary and not a container.
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax
        available "Signals.Arbitrary" caps.Signals.Arbitrary

    [<Test>]
    member _.``FreeBSD selects the process reaper for a limit-free group and predicts it honestly``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.freeBsd (CapabilityFacts.noLimits ())

        Assert.That(
            caps.Mechanism,
            Is.EqualTo(Some Mechanism.ProcessReaper),
            "a limit-free group on FreeBSD must prefer the whole-tree procctl reaper over the plain process group"
        )

        // The one axis where this mechanism's prediction is weaker than its behaviour, and says so:
        // acquiring reaper status is a permanent process-wide side effect a snapshot must not perform.
        qualified "Creation" "PROC_REAP_ACQUIRE" caps.Creation
        qualified "Creation" "falls back to the POSIX process group" caps.Creation

        // The full POSIX signal vocabulary, delivered through PROC_REAP_KILL instead of killpg — a wider
        // reach on the same three rows, so no row is qualified for being better.
        available "Signals.Kill" caps.Signals.Kill
        available "Signals.SoftStop" caps.Signals.SoftStop
        available "Signals.Arbitrary" caps.Signals.Arbitrary

        // Containment is not adoption: the reaper holds this process's own descendants, and there is no
        // procctl call that pulls a foreign process into that relationship.
        unsupported "Adoption" "contains only this process's own descendants" caps.Adoption
        unsupported "AdoptionByPid" "start-time identity reader" caps.AdoptionByPid

        // A whole-tree CONTAINER is exactly what the reaper is not, and the precondition says why in the
        // reaper's own terms rather than claiming this host has no whole-tree mechanism at all.
        unsupported "MemoryMax" "keeps no aggregate memory/CPU/pids accounting" caps.ResourceLimits.MemoryMax
        unsupported "MaxProcesses" "process reaper" caps.ResourceLimits.MaxProcesses
        unsupported "LiveUpdate" "process reaper" caps.ResourceLimits.LiveUpdate
        unsupported "UiRestrictions" "Windows Job Object" caps.ResourceLimits.UiRestrictions

        // The CPU-time rlimit is the deliberate POSIX exception and survives the absent container here too.
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax

    [<Test>]
    member _.``FreeBSD refuses whole-tree limits exactly as the other BSDs do - the reaper is not a container``() =
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.freeBsd (CapabilityFacts.wholeTreeLimits ())

        Assert.That(
            caps.Mechanism,
            Is.EqualTo None,
            "asking for a cap this host cannot enforce must report no mechanism at all, not the reaper"
        )

        unsupported "Creation" "process reaper" caps.Creation

        // The mechanism-dependent axes carry the creation precondition rather than another mechanism's
        // answer; the host-level ones are unaffected.
        refused "Adoption" caps.Adoption
        refused "Signals.Kill" caps.Signals.Kill
        available "CpuTimeMax" caps.ResourceLimits.CpuTimeMax

    [<Test>]
    member _.``a missing POSIX helper degrades exactly the axes that need it``() =
        let withoutSetpriv =
            CapabilityProbe.snapshot
                { CapabilityFacts.linux with
                    PrivilegeDropHelperAvailable = false }
                (CapabilityFacts.noLimits ())

        unsupported "KillOnParentDeath" "'setpriv' helper" withoutSetpriv.KillOnParentDeath
        // A missing privilege-drop helper says nothing about the pseudo-terminal.
        available "Pty" withoutSetpriv.Pty
        // ...nor about the per-process rlimit helper, which is a separate binary.
        available "prlimit" (helperEntry "prlimit" withoutSetpriv).Availability

        // The per-process rlimit helper is reported on its own axis: a host holding `setpriv`/`setsid` but
        // not `prlimit` (a minimal image) is exactly the case a caller must be able to see BEFORE a
        // `Command.Rlimit` spawn refuses with ProcessError.ResourceLimit.
        let withoutPrlimit =
            CapabilityProbe.snapshot
                { CapabilityFacts.linux with
                    ProcessLimitHelperAvailable = false }
                (CapabilityFacts.noLimits ())

        unsupported "prlimit" "'prlimit' helper" (helperEntry "prlimit" withoutPrlimit).Availability
        available "setpriv" (helperEntry "setpriv" withoutPrlimit).Availability
        available "setsid" (helperEntry "setsid" withoutPrlimit).Availability
        // The helper applies per-process caps only, so no whole-tree limit dimension moves with it.
        available "CpuTimeMax" withoutPrlimit.ResourceLimits.CpuTimeMax
        qualified "MemoryMax" "memory.max" withoutPrlimit.ResourceLimits.MemoryMax

        let withoutShell =
            CapabilityProbe.snapshot
                { CapabilityFacts.linux with
                    SystemShellAvailable = false }
                (CapabilityFacts.noLimits ())

        // `/bin/sh` is what applies the RLIMIT_CPU and runs the pre-arm guard, so both go with it.
        unsupported "CpuTimeMax" "/bin/sh" withoutShell.ResourceLimits.CpuTimeMax
        unsupported "KillOnParentDeath" "/bin/sh" withoutShell.KillOnParentDeath
        unsupported "/bin/sh" "/bin/sh" (helperEntry "/bin/sh" withoutShell).Availability

    [<Test>]
    member _.``no axis is ever a bare no - every qualified or unsupported answer carries its reason``() =
        let factSets =
            [ "windows", CapabilityFacts.windows
              "windows without conpty",
              { CapabilityFacts.windows with
                  ConPtyAvailable = false
                  CmdExeAvailable = false }
              "linux", CapabilityFacts.linux
              "linux without cgroup v2", CapabilityFacts.linuxWithoutCgroup
              "linux without helpers",
              { CapabilityFacts.linux with
                  PrivilegeDropHelperAvailable = false
                  ControllingTerminalHelperAvailable = false
                  ProcessLimitHelperAvailable = false
                  SystemShellAvailable = false }
              "macos", CapabilityFacts.macOs
              "bsd", CapabilityFacts.bsd
              "freebsd", CapabilityFacts.freeBsd ]

        let optionSets =
            [ "no limits", CapabilityFacts.noLimits ()
              "whole-tree limits", CapabilityFacts.wholeTreeLimits ()
              "ui restrictions", ProcessGroupOptions().WithUiRestrictions WindowsUiRestrictions.All ]

        for factsName, facts in factSets do
            for optionsName, options in optionSets do
                let caps = CapabilityProbe.snapshot facts options

                let namedAxes =
                    [ "Creation", caps.Creation
                      "Adoption", caps.Adoption
                      "AdoptionByPid", caps.AdoptionByPid
                      "Pty", caps.Pty
                      "PtyResize", caps.PtyResize
                      "KillOnParentDeath", caps.KillOnParentDeath
                      "Signals.Kill", caps.Signals.Kill
                      "Signals.SoftStop", caps.Signals.SoftStop
                      "Signals.Arbitrary", caps.Signals.Arbitrary
                      "MemoryMax", caps.ResourceLimits.MemoryMax
                      "OomGroupKill", caps.ResourceLimits.OomGroupKill
                      "MaxProcesses", caps.ResourceLimits.MaxProcesses
                      "CpuQuota", caps.ResourceLimits.CpuQuota
                      "CpuTimeMax", caps.ResourceLimits.CpuTimeMax
                      "CpuAffinity", caps.ResourceLimits.CpuAffinity
                      "IoMax", caps.ResourceLimits.IoMax
                      "UiRestrictions", caps.ResourceLimits.UiRestrictions
                      "LiveUpdate", caps.ResourceLimits.LiveUpdate ]

                let helperAxes =
                    caps.Helpers
                    |> Seq.map (fun entry -> $"helper {entry.Name}", entry.Availability)
                    |> List.ofSeq

                for axis, capability in namedAxes @ helperAxes do
                    let where = $"{factsName} / {optionsName} / {axis}"

                    match capability with
                    | Capability.Available ->
                        if capability.Detail.IsSome then
                            Assert.Fail $"{where}: an unconditional Available must carry no detail"
                    | Capability.Qualified text
                    | Capability.Unsupported text ->
                        if String.IsNullOrWhiteSpace text then
                            Assert.Fail $"{where}: a non-Available capability must state its reason"

                        if capability.Detail <> Some text then
                            Assert.Fail $"{where}: Detail must expose the same text the case carries"

                // A snapshot either names the mechanism it would get, or explains why it gets none.
                match caps.Mechanism, caps.Creation with
                | Some _, _
                | None, Capability.Unsupported _ -> ()
                | None, other -> Assert.Fail $"{factsName} / {optionsName}: no mechanism, yet Creation reported {other}"

    [<Test>]
    member _.``a helper entry always says what it is needed for``() =
        for facts in [ CapabilityFacts.windows; CapabilityFacts.linux; CapabilityFacts.macOs ] do
            let caps = CapabilityProbe.snapshot facts (CapabilityFacts.noLimits ())

            for entry in caps.Helpers do
                if String.IsNullOrWhiteSpace entry.Name || String.IsNullOrWhiteSpace entry.Purpose then
                    Assert.Fail $"a listed helper must carry both a name and a purpose (got '{entry.Name}')"

        // The list is a fresh copy each read, so a consumer cannot mutate the snapshot through it.
        let caps =
            CapabilityProbe.snapshot CapabilityFacts.linux (CapabilityFacts.noLimits ())

        Assert.That(obj.ReferenceEquals(caps.Helpers, caps.Helpers), Is.False)

    // ------------------------------------------------------------------------------------------------
    // The live host: the snapshot must agree with what `ProcessGroup.Create` really does here
    // ------------------------------------------------------------------------------------------------

    [<Test>]
    member _.``the snapshot names the mechanism a really created group reports``() =
        match ProcessGroup.Create() with
        | Error error -> Assert.Fail $"a default group could not be created on this host: {error.Message}"
        | Ok group ->
            use group = group
            let caps = ProcessGroup.Capabilities()

            Assert.That(caps.Mechanism, Is.EqualTo(Some group.Mechanism))
            available "Creation" caps.Creation

    [<Test>]
    member _.``the snapshot agrees with Create on which option sets this host can honour``() =
        // Each of these is refused by `Create` on at least one platform and accepted on another, so the
        // pairing is a real cross-check of the shared decision rather than an all-Ok tautology.
        let candidates =
            [ "default", ProcessGroupOptions()
              "memory cap", ProcessGroupOptions().WithMemoryMax(64L * 1024L * 1024L)
              "cpu-time only", ProcessGroupOptions().WithCpuTimeMax(TimeSpan.FromSeconds 5.0)
              "ui restrictions", ProcessGroupOptions().WithUiRestrictions WindowsUiRestrictions.All
              "oom group kill", ProcessGroupOptions().WithOomGroupKill()
              "non-default stop signal", ProcessGroupOptions().WithStopSignal Signal.Int ]

        for name, options in candidates do
            let caps = ProcessGroup.Capabilities options

            match ProcessGroup.Create options with
            | Ok group ->
                use group = group

                match caps.Mechanism with
                | Some predicted when predicted = group.Mechanism -> ()
                | Some predicted ->
                    Assert.Fail $"{name}: the snapshot predicted {predicted}, the live group reported {group.Mechanism}"
                | None -> Assert.Fail $"{name}: the snapshot reported no mechanism, but Create succeeded"
            | Error error ->
                match caps.Mechanism, caps.Creation with
                | None, Capability.Unsupported _ ->
                    // Refused by both, and the snapshot named what the options would have needed.
                    ()
                | Some _, Capability.Qualified _ ->
                    // The snapshot named the mechanism AND warned that creating it can still fail here —
                    // a cgroup hierarchy whose controllers cannot be delegated from this process is
                    // exactly that case. A qualification that came true is agreement, not a mismatch.
                    ()
                | _, creation ->
                    Assert.Fail
                        $"{name}: Create refused ({error.Message}) but the snapshot reported {creation} for mechanism {caps.Mechanism}"

    [<Test>]
    member _.``the live snapshot reports this platform's own parent-death scope, mechanism and helpers``() =
        let caps = ProcessGroup.Capabilities()

        // The scope has one source of truth, which the POSIX spawn gate itself mirrors.
        let commandScope = (Command.create "noop").KillOnParentDeathScope()
        Assert.That(caps.KillOnParentDeathScope, Is.EqualTo commandScope)

        if isWindows then
            Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.JobObject))
            available "Adoption" caps.Adoption
            available "AdoptionByPid" caps.AdoptionByPid
            available "KillOnParentDeath" caps.KillOnParentDeath
            available "Signals.Kill" caps.Signals.Kill
            qualified "Signals.SoftStop" "CTRL+BREAK" caps.Signals.SoftStop
            refused "Signals.Arbitrary" caps.Signals.Arbitrary
            refused "OomGroupKill" caps.ResourceLimits.OomGroupKill
            available "MemoryMax" caps.ResourceLimits.MemoryMax

            // A Windows host has the system `cmd.exe`, and it is the only helper Windows loads.
            available "cmd.exe" (helperEntry "cmd.exe" caps).Availability
        else
            Assert.That(caps.Mechanism, Is.EqualTo(Some Mechanism.ProcessGroup))
            // Both POSIX mechanisms deliver the whole signal vocabulary.
            available "Signals.Arbitrary" caps.Signals.Arbitrary
            refused "UiRestrictions" caps.ResourceLimits.UiRestrictions

            // A limit-free POSIX group cannot `Adopt` a `Process` on any host, while `AdoptByPid` depends
            // on whether THIS host can read a start-time anchor — so the pair is cross-checked against the
            // very reader the adoption path consults, rather than assumed per platform.
            refused "Adoption" caps.Adoption

            if Native.Posix.processIdentityReaderAvailable () then
                qualified "AdoptionByPid" "tracked INDIVIDUALLY" caps.AdoptionByPid
            else
                unsupported "AdoptionByPid" "start-time identity reader" caps.AdoptionByPid

            // The helper list is the POSIX one; whether each is present depends on the host.
            for name in [ "setpriv"; "setsid"; "prlimit"; "/bin/sh" ] do
                helperEntry name caps |> ignore

            // ...and the `prlimit` answer is cross-checked against the very resolution a `Command.Rlimit`
            // spawn performs, so the snapshot cannot advertise a helper this host would refuse to load
            // (or hide one it holds) — the same shape the `AdoptionByPid` pairing above uses.
            let prlimitAvailability = (helperEntry "prlimit" caps).Availability

            if (Native.Posix.trustedHelperPathForTests "prlimit").IsSome then
                available "prlimit" prlimitAvailability
            else
                unsupported "prlimit" "'prlimit' helper" prlimitAvailability
