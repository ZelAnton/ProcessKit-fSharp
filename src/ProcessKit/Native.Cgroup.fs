namespace ProcessKit.Native

open ProcessKit
open System
open System.IO
open System.Runtime.InteropServices
open ProcessKit.Native.Common
open ProcessKit.Native.Posix

/// Linux cgroup v2 — the `limits` backend and cgroup-scoped tree control. All plain file I/O over
/// /sys/fs/cgroup, plus per-member signal delivery. Depends on `Native.Common` (`SignalDelivery`) and
/// `Native.Posix` (the `SIGKILL`/`SIGTERM` numbers, and the pidfd primitives
/// `pidfdOpenChecked`/`pidfdSendSignalChecked`/`closePidfd` that make every per-member delivery —
/// arbitrary signals, and the legacy teardown sweep's SIGKILL — identity-safe against pid recycling),
/// so it compiles after both.
module internal Cgroup =

    // ----------------------------------------------------------------------------------
    // Linux cgroup v2 (the `limits` backend) — all plain file I/O over /sys/fs/cgroup
    // ----------------------------------------------------------------------------------

    // The **usable** cgroup v2 root (one whose `cgroup.controllers` lists at least one controller). On a
    // pure-v2 host it is /sys/fs/cgroup; on a systemd **hybrid** host the v2 hierarchy is at
    // /sys/fs/cgroup/unified, so probe both. Crucially, require a NON-EMPTY `cgroup.controllers`: a
    // hybrid host's v2 mount exists but its controllers file is empty (memory/cpu/pids stay on v1), so
    // limits can't be enforced there — treat that as "no usable v2 root" and fall back to the clear
    // fail-fast `ResourceLimit` error rather than a later low-level `subtree_control` write failure.
    // A plain function (not a cached value), so the probe runs only when the limits backend is used (not
    // at module load on Windows/macOS) AND re-checks each call, so it self-heals if v2 is mounted later.
    let private cgroupRoot () : string option =
        [ "/sys/fs/cgroup"; "/sys/fs/cgroup/unified" ]
        |> List.tryFind (fun root ->
            try
                let controllers = Path.Combine(root, "cgroup.controllers")
                File.Exists controllers && (File.ReadAllText controllers).Trim() <> ""
            with _ ->
                // An unreadable candidate (denied permission, a torn-down mount) simply isn't a usable
                // v2 root — treat it as absent and try the next candidate.
                false)

    /// True when a **usable** cgroup v2 hierarchy is mounted (its root's `cgroup.controllers` is
    /// non-empty) — including the systemd hybrid mount at /sys/fs/cgroup/unified when it has controllers.
    let cgroupV2Available () = (cgroupRoot ()).IsSome

    /// True when the usable hierarchy advertises `controller` in its root `cgroup.controllers` — the
    /// kernel's own list of what this hierarchy carries at all. `cpuset` is the one a hierarchy most
    /// often lacks entirely, and (unlike `memory`/`pids`/`cpu`) its presence is not implied by cgroup v2
    /// being mounted.
    ///
    /// Deliberately a NARROW question: it says the controller exists here, NOT that a cap using it can be
    /// enforced — enabling it in the parent's `cgroup.subtree_control` is a separate act the kernel
    /// permits only at the real hierarchy root. Those are neighbouring facts and must not be conflated.
    let cgroupControllerAvailable (controller: string) =
        match cgroupRoot () with
        | None -> false
        | Some root ->
            try
                File
                    .ReadAllText(Path.Combine(root, "cgroup.controllers"))
                    .Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.contains controller
            with _ ->
                false

    /// True when the usable hierarchy advertises the cgroup v2 `io` controller. A mounted v2
    /// hierarchy without `io` is a real but unsupported configuration for `io.max`, not a reason to
    /// fall back to the unbounded process-group backend.
    ///
    /// The internal override exists only for the focused live-update regression test, which needs to
    /// model a hierarchy losing the controller without mutating the host's real cgroup configuration.
    let mutable internal cgroupIoAvailableForTests: bool option = None

    let cgroupIoAvailable () =
        match cgroupIoAvailableForTests with
        | Some available -> available
        | None -> cgroupControllerAvailable "io"

    // This process's own cgroup path (the `0::<path>` line of /proc/self/cgroup), defaulting to "/".
    let private selfCgroupRelative () =
        try
            File.ReadAllLines "/proc/self/cgroup"
            |> Array.tryPick (fun line ->
                if line.StartsWith "0::" then
                    Some(line.Substring(3).Trim())
                else
                    None)
            |> Option.defaultValue "/"
        with _ ->
            "/"

    // Format a per-core CPU fraction as a cgroup v2 `cpu.max` value ("quota period", microseconds).
    let private cpuMaxValue (cores: float) =
        let quota = CgroupCpuMax.calculateQuota cores
        CgroupCpuMax.formatCpuMax quota

    /// Render a set of core indices as a cgroup v2 cpuset list — the grammar `cpuset.cpus` accepts and
    /// prints back: each run of consecutive indices collapses into `lo-hi`, a lone index stays bare, and
    /// the runs are joined with commas (`[0; 2; 3]` → `"0,2-3"`). Sorted and de-duplicated defensively, so
    /// the rendering does not depend on the caller having normalized first.
    let formatCpuList (cores: int list) : string =
        let runs =
            cores
            |> List.distinct
            |> List.sort
            |> List.fold
                (fun runs core ->
                    match runs with
                    | (lo, hi) :: rest when core = hi + 1 -> (lo, core) :: rest
                    | _ -> (core, core) :: runs)
                []
            |> List.rev

        runs
        |> List.map (fun (lo, hi) -> if lo = hi then string lo else $"{lo}-{hi}")
        |> String.concat ","

    /// Render one cgroup v2 `io.max` device line. The kernel's unbounded sentinel is the literal
    /// `max` per nested key; omitting a key would preserve an older live value during replacement.
    let formatIoMax (ioMax: IoMax) : string =
        let parts = ioMax.Target.Split(':', StringSplitOptions.None)

        let validDevice =
            parts.Length = 2
            && parts
               |> Array.forall (fun part ->
                   match UInt32.TryParse part with
                   | true, _ -> part.Length > 0
                   | false, _ -> false)

        if not validDevice then
            raise (ArgumentException($"I/O target '{ioMax.Target}' is not a Linux major:minor device key"))

        let render value =
            value |> Option.map string |> Option.defaultValue "max"

        $"{ioMax.Target} rbps={render ioMax.ReadBytesPerSecond} wbps={render ioMax.WriteBytesPerSecond} riops={render ioMax.ReadOperationsPerSecond} wiops={render ioMax.WriteOperationsPerSecond}"

    // Enable the controllers the given limits need (only the missing ones) in the parent's
    // `cgroup.subtree_control`. Shared by creation and the live update, so both enable exactly the
    // controllers their cap set requires. Raises on failure (notably EBUSY writing subtree_control when
    // this process is not at the real cgroup root, or ENOENT for a controller this hierarchy does not
    // carry at all — `cpuset` is the one most often absent, so a CPU-affinity pin fails fast there rather
    // than being quietly skipped).
    let private enableNeededControllers (parent: string) (limits: ResourceLimits) =
        let needed =
            [ if limits.MemoryMax.IsSome || limits.OomGroupKill then
                  "memory"
              if limits.MaxProcesses.IsSome then
                  "pids"
              if limits.CpuQuota.IsSome then
                  "cpu"
              if limits.CpuAffinityCores.IsSome then
                  "cpuset"
              if limits.IoMax.IsSome then
                  "io" ]

        let subtreeFile = Path.Combine(parent, "cgroup.subtree_control")

        let alreadyEnabled =
            try
                (File.ReadAllText subtreeFile).Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                |> Set.ofArray
            with _ ->
                Set.empty

        let toEnable = needed |> List.filter (fun c -> not (alreadyEnabled.Contains c))

        if not (List.isEmpty toEnable) then
            let spec = toEnable |> List.map (fun c -> "+" + c) |> String.concat " "
            File.WriteAllText(subtreeFile, spec)

    // Enable the controllers the requested limits need (only the missing ones) in the parent's
    // `cgroup.subtree_control`, then write the caps into the child cgroup. Raises on failure (notably
    // EBUSY writing subtree_control when this process is not at the real cgroup root).
    let private applyCgroupLimits (parent: string) (cgroupPath: string) (limits: ResourceLimits) =
        enableNeededControllers parent limits

        match limits.MemoryMax with
        | Some bytes -> File.WriteAllText(Path.Combine(cgroupPath, "memory.max"), string bytes)
        | None -> ()

        if limits.OomGroupKill then
            File.WriteAllText(Path.Combine(cgroupPath, "memory.oom.group"), "1")

        match limits.MaxProcesses with
        | Some n -> File.WriteAllText(Path.Combine(cgroupPath, "pids.max"), string n)
        | None -> ()

        match limits.CpuQuota with
        | Some cores -> File.WriteAllText(Path.Combine(cgroupPath, "cpu.max"), cpuMaxValue cores)
        | None -> ()

        match limits.CpuAffinityCores with
        | Some cores -> File.WriteAllText(Path.Combine(cgroupPath, "cpuset.cpus"), formatCpuList cores)
        | None -> ()

        match limits.IoMax with
        | Some ioMax -> File.WriteAllText(Path.Combine(cgroupPath, "io.max"), formatIoMax ioMax)
        | None -> ()

    // A process-wide counter making each cgroup name unique without relying on `CreateDirectory`
    // failing on an existing path (it is idempotent, so a TOCTOU "exists?" check could collide).
    let mutable private nextCgroupId = 0

    /// Create a fresh limit cgroup under this process's own cgroup and apply `limits`. Returns the
    /// new cgroup's absolute path, or an error message (the dir is removed on a limit failure).
    let createCgroup (limits: ResourceLimits) : Result<string, string> =
        match cgroupRoot () with
        | None -> Error "cgroup v2 is not mounted"
        | Some root ->

            try
                let rel = (selfCgroupRelative ()).TrimStart('/')
                let parent = Path.Combine(root, rel)
                let id = System.Threading.Interlocked.Increment(&nextCgroupId)
                let path = Path.Combine(parent, $"processkit-{Environment.ProcessId}-{id}")
                Directory.CreateDirectory path |> ignore

                if limits.Any then
                    try
                        applyCgroupLimits parent path limits
                        Ok path
                    with ex ->
                        (try
                            Directory.Delete path
                         with _ ->
                             ())

                        Error ex.Message
                else
                    Ok path
            with ex ->
                Error ex.Message

    // A cgroup interface file is cleared by writing a BLANK line, never by writing zero bytes: a
    // zero-length write does not reach the kernel's parser at all, so the value would silently stay put.
    // Only `cpuset.cpus` can read back blank (an unpinned cpuset prints just a newline); `memory.max`/
    // `pids.max`/`cpu.max` always carry a value, so this only ever substitutes for the cpuset case.
    let private restorePayload (prior: string) =
        if String.IsNullOrWhiteSpace prior then "\n" else prior

    /// Test-only hook for the ordered controller-file writes below. It is deliberately invoked before
    /// the real write so a test can fail a selected native-equivalent step while the rollback ledger still
    /// contains every earlier successful write. Production leaves it unset.
    let mutable internal controllerWriteTestHook: (string -> string -> unit) option =
        None

    let private writeControllerFile (file: string) (content: string) =
        match controllerWriteTestHook with
        | Some hook -> hook file content
        | None -> ()

        File.WriteAllText(file, content)

    /// Apply a new limit set to an EXISTING cgroup in place (the live `ProcessGroup.UpdateLimits` path),
    /// without recreating the cgroup or restarting its members. Enables any controller the new caps
    /// newly need in the parent's `cgroup.subtree_control`, then rewrites `memory.max`/`pids.max`/
    /// `cpu.max`/`cpuset.cpus`. REPLACE semantics, mirroring the Windows Job path: a dimension now `None`
    /// is reset to the controller's own "unbounded" sentinel — `max` for the three caps, a blank line for
    /// `cpuset.cpus` (an empty cpuset means "inherit the parent's cores", i.e. unpinned; `max` is not a
    /// value it accepts) — but only where that controller's interface file already exists (a controller
    /// never enabled at creation is already unbounded, and its file would not exist to write). Returns an
    /// error message on any write/delegation failure (e.g. EBUSY when not at the real cgroup root, or
    /// ENOENT enabling a `cpuset` controller this hierarchy lacks), which the backend turns into
    /// `ProcessError.ResourceLimit`.
    ///
    /// The caps are written one controller file at a time, so a later write could fail after an earlier
    /// one already landed. To keep the honest `UpdateLimits` contract — a failed apply leaves the live
    /// cgroup on the PREVIOUS set — each file's prior content is captured just before it is overwritten,
    /// and a mid-sequence failure best-effort restores the files already changed back to exactly what the
    /// kernel had. So an `Error` return means the cgroup is back on the previous set (nothing net changed),
    /// never a silent mix that `Options.Limits` would misreport (T-207). Only if even that restore fails is
    /// the state genuinely indeterminate, and the error says so distinctly.
    let updateCgroupLimitsWithPrevious
        (cgroupPath: string)
        (previousLimits: ResourceLimits)
        (limits: ResourceLimits)
        : Result<unit, string> =
        try
            // The cgroup is always a subdirectory of its parent (`.../<parent>/processkit-<pid>-<id>`),
            // so `GetDirectoryName` yields the real parent; the null case (a root/empty path) can't arise
            // for a tracked cgroup but is handled honestly rather than assumed away.
            match Path.GetDirectoryName cgroupPath with
            | null -> Error $"could not determine the parent cgroup of {cgroupPath}"
            | parent ->
                enableNeededControllers parent limits

                // The controller files to (re)write, in apply order, each paired with the content the new
                // set wants and with that file's own "unbounded" sentinel: `Some v` writes the cap; `None`
                // resets to the sentinel — but only where the controller file already exists (a
                // never-enabled controller is already unbounded, with no file to reset). The sentinel is
                // per-file because `cpuset.cpus` does not speak `max`: an empty cpuset is what means "every
                // core the parent allows", and it is written as a blank line.
                let plan =
                    [ "memory.max", (limits.MemoryMax |> Option.map string), "max"
                      "memory.oom.group", (if limits.OomGroupKill then Some "1" else None), "0"
                      "pids.max", (limits.MaxProcesses |> Option.map string), "max"
                      "cpu.max", (limits.CpuQuota |> Option.map cpuMaxValue), "max"
                      "cpuset.cpus", (limits.CpuAffinityCores |> Option.map formatCpuList), "\n" ]

                let ioPlan =
                    match previousLimits.IoMax, limits.IoMax with
                    | None, None -> []
                    | Some previous, None -> [ "io.max", formatIoMax (IoMax(previous.Target, None, None, None, None)) ]
                    | None, Some requested -> [ "io.max", formatIoMax requested ]
                    | Some previous, Some requested when previous.Target = requested.Target ->
                        [ "io.max", formatIoMax requested ]
                    | Some previous, Some requested ->
                        let clearPrevious = formatIoMax (IoMax(previous.Target, None, None, None, None))
                        [ "io.max", clearPrevious; "io.max", formatIoMax requested ]

                // Files already overwritten, with their PRIOR content, so a later failure can undo them.
                // The same file may occur more than once (old io.max target, then new target); keeping both
                // entries makes the reverse ledger restore each native step in the exact opposite order.
                let applied = System.Collections.Generic.List<string * string>()

                try
                    for (fileName, value, unsetSentinel) in plan do
                        let file = Path.Combine(cgroupPath, fileName)

                        let content =
                            match value with
                            | Some v -> Some v
                            | None -> if File.Exists file then Some unsetSentinel else None

                        match content with
                        | None -> ()
                        | Some text ->
                            // Capture the current kernel value BEFORE overwriting it, so a rollback restores
                            // exactly this file's prior state.
                            let prior = File.ReadAllText file
                            writeControllerFile file text
                            applied.Add(file, prior)

                    for (fileName, text) in ioPlan do
                        let file = Path.Combine(cgroupPath, fileName)
                        let prior = File.ReadAllText file
                        writeControllerFile file text
                        applied.Add(file, prior)

                    Ok()
                with writeEx ->
                    // A cap write failed partway. Put the already-changed files back to their prior kernel
                    // values so the live cgroup and the readable Options snapshot both stay on the previous
                    // set — the file that failed changed nothing, so it is not among `applied` and is left
                    // untouched. If even the restore throws, the state is genuinely indeterminate; surface
                    // that distinctly so the caller never treats `Options.Limits` as authoritative.
                    try
                        for (file, prior) in Seq.rev applied do
                            writeControllerFile file (restorePayload prior)

                        Error writeEx.Message
                    with restoreEx ->
                        Error
                            $"failed to apply the cgroup limits ({writeEx.Message}) and could not roll the already-written controller files back to the previous set ({restoreEx.Message}); the cgroup's limits may be partially applied"
        with ex ->
            Error ex.Message

    /// Backwards-compatible test seam for a direct file update with no previously configured I/O
    /// policy. Live backends use `updateCgroupLimitsWithPrevious` so a target change can clear the old
    /// device key and restore it on a later-write failure.
    let updateCgroupLimits (cgroupPath: string) (limits: ResourceLimits) : Result<unit, string> =
        updateCgroupLimitsWithPrevious cgroupPath ResourceLimits.None limits

    // Raw libc for the cgroup.procs write. Done via `open`/`write`/`close` rather than
    // `File.WriteAllText` so the exact errno is available (`Marshal.GetLastWin32Error`), which is what
    // lets `migrateToCgroup` tell a genuine failure (ENOENT/EACCES on OPEN) apart from a fast target
    // that has already exited (ESRCH on WRITE) — a distinction .NET's exception types blur.
    [<Literal>]
    let private O_WRONLY = 1

    // errno: the pid written to cgroup.procs no longer exists. Only reachable on the WRITE (open of a
    // valid cgroup.procs succeeds first), i.e. the launcher already migrated and a fast target exited.
    [<Literal>]
    let private ESRCH = 3

    [<DllImport("libc", SetLastError = true, EntryPoint = "open", CharSet = CharSet.Ansi)>]
    extern int private openWrite(string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "write")>]
    extern nativeint private writeAll(int fd, byte[] buffer, nativeint count)

    [<DllImport("libc", SetLastError = true, EntryPoint = "close")>]
    extern int private closeFd(int fd)

    /// Test-only seam (same pattern as `PipelineRunner.stageSpawnedTestHook`): when set, transforms the
    /// raw `write()` return value before `migrateToCgroup` classifies it. A genuine short write on
    /// `cgroup.procs` is (per the kernel's atomic per-write handling) effectively unprovokable for a
    /// payload this small, so this is how the short-write branch gets exercised deterministically.
    /// Reset to `None` after use.
    ///
    /// NOT thread-safe: this is a process-wide, unsynchronized mutable, the same convention already
    /// used by `PipelineRunner.stageSpawnedTestHook` and other test-only hooks in this suite. It relies
    /// on tests that set it running sequentially (no `[<Parallelizable>]`) and always resetting it in a
    /// `finally`; do not set it from tests that may run concurrently with other users of this hook.
    let mutable internal migrateWriteTestHook: (nativeint -> nativeint) option = None

    /// Confirm the child was placed into the cgroup, and belt-and-suspenders migrate it. The `/bin/sh`
    /// launcher (`Native.Posix.spawnPosixIntoCgroup`) already writes the child's own pid into
    /// `cgroup.procs` before it `exec`s the target, so the target starts already contained; this parent
    /// write is an idempotent confirmation whose real value is honest error classification:
    ///
    ///  * write succeeds → the pid is in the cgroup (migrated & confirmed) → `Ok`.
    ///  * ESRCH on write → the cgroup opened fine but the pid is gone → the launcher already migrated it
    ///    and a fast target exited before this write landed (a self-write of `$$` can never ESRCH in the
    ///    launcher, so a writable cgroup means the launcher's migration succeeded) → `Ok`.
    ///  * open fails (missing/unwritable cgroup) or any other write error → a genuine failure the
    ///    launcher hit too, so the target never ran → `Error`.
    ///
    /// The caller (`CgroupBackend.Track`) turns an `Error` into an honest spawn failure — killing and
    /// reaping the launcher/target — rather than silently downgrading to an unconstrained run.
    let migrateToCgroup (cgroupPath: string) (pid: int) : Result<unit, string> =
        let procs = Path.Combine(cgroupPath, "cgroup.procs")
        let fd = openWrite (procs, O_WRONLY)

        if fd < 0 then
            let errno = Marshal.GetLastWin32Error()
            Error $"could not open {procs} for the cgroup migration write (errno {errno})"
        else
            try
                let payload = System.Text.Encoding.ASCII.GetBytes(string pid)
                let rawWritten = writeAll (fd, payload, nativeint payload.Length)

                let written =
                    match migrateWriteTestHook with
                    | Some hook -> hook rawWritten
                    | None -> rawWritten

                if written >= 0n && written = nativeint payload.Length then
                    Ok()
                elif written >= 0n then
                    // A short write: fewer bytes landed than the pid's decimal payload. The kernel
                    // handles cgroup.procs writes atomically in practice, so this is unreachable in
                    // practice - but a partial pid is neither a confirmed migration nor a clean
                    // failure signal (errno is not set on a short, non-negative write), so treat it
                    // as an honest migration failure rather than silently reporting success.
                    Error $"short write migrating pid {pid} to {procs} ({written} of {payload.Length} bytes)"
                else
                    let errno = Marshal.GetLastWin32Error()

                    if errno = ESRCH then
                        // The launcher already placed the process in the cgroup and a fast target exited
                        // before this confirmation write; the target ran inside the cgroup, not a failure.
                        Ok()
                    else
                        Error $"writing pid {pid} to {procs} failed (errno {errno})"
            finally
                closeFd fd |> ignore

    /// Adopt an already-running EXTERNAL process into this cgroup by writing its pid to `cgroup.procs`.
    /// Shares the raw `open`/`write`/`close` shape with `migrateToCgroup`, but classifies the outcome for
    /// ADOPTION rather than confirmation — the ESRCH case is the crucial difference:
    ///
    ///  * write succeeds → the foreign process is now a member of the cgroup (and thereby bound by its
    ///    limits and reachable by `cgroup.kill`/`cgroup.freeze`/the per-member signal sweep) → `Ok`.
    ///  * ESRCH on write → the pid no longer exists: the process we were asked to adopt exited before the
    ///    write landed. For a spawn confirmation `migrateToCgroup` treats this as success (the launcher had
    ///    already migrated it); for an ADOPTION it is an honest FAILURE — there was nothing live to adopt,
    ///    a lost adopt-vs-exit race, never a silent success.
    ///  * open fails (ENOENT/EACCES — missing or unwritable cgroup, e.g. the group was torn down) or any
    ///    other write error → a genuine failure → `Error`.
    ///
    /// Note the residual Linux hazard the caller documents: unlike Windows (where the caller's open process
    /// handle pins the pid), Linux has no handle to pin a foreign pid, so in the small window between the
    /// caller's liveness check and this write a pid could in principle be recycled to a different process.
    /// `ProcessGroup.Adopt`'s pre-write `HasExited` guard plus this ESRCH check catch the common
    /// dead-pid race; the recycled-to-a-stranger window cannot be fully closed by number alone.
    let adoptIntoCgroup (cgroupPath: string) (pid: int) : Result<unit, string> =
        let procs = Path.Combine(cgroupPath, "cgroup.procs")
        let fd = openWrite (procs, O_WRONLY)

        if fd < 0 then
            let errno = Marshal.GetLastWin32Error()
            Error $"could not open {procs} to adopt the process (errno {errno})"
        else
            try
                let payload = System.Text.Encoding.ASCII.GetBytes(string pid)
                let written = writeAll (fd, payload, nativeint payload.Length)

                if written >= 0n && written = nativeint payload.Length then
                    Ok()
                elif written >= 0n then
                    Error $"short write adopting pid {pid} into {procs} ({written} of {payload.Length} bytes)"
                else
                    let errno = Marshal.GetLastWin32Error()

                    if errno = ESRCH then
                        Error $"the process (pid {pid}) no longer exists; there is nothing live to adopt"
                    else
                        Error $"writing pid {pid} to {procs} to adopt it failed (errno {errno})"
            finally
                closeFd fd |> ignore

    /// Move `pid` back OUT of this cgroup, into the cgroup this group's own directory lives in (its
    /// parent) — the rollback half of a bare-pid adoption (`ProcessGroup.AdoptByPid`) whose identity
    /// re-read found the number had changed hands ACROSS the `cgroup.procs` write above. The write has
    /// already happened by then, and cgroup v2 membership is exclusive, so the stranger is in this
    /// group's cgroup and would be killed by its teardown; putting it in the parent takes it back out of
    /// reach of `cgroup.kill` and of the caps this cgroup carries.
    ///
    /// What it deliberately does NOT claim: this is not a restore. The kernel does not report which
    /// cgroup a task came from, so the process lands in the parent, not wherever it was before — and a
    /// refused move-out (a delegated hierarchy that will not accept the write, a parent that may not
    /// hold processes because its `cgroup.subtree_control` is populated) leaves it a member of this
    /// group. The caller says which of the two happened in its typed error rather than reporting a clean
    /// undo either way.
    let releaseFromCgroup (cgroupPath: string) (pid: int) : Result<unit, string> =
        match Path.GetDirectoryName cgroupPath with
        | null -> Error $"could not determine the parent cgroup of {cgroupPath} to move pid {pid} back out"
        | parent ->
            let procs = Path.Combine(parent, "cgroup.procs")
            let fd = openWrite (procs, O_WRONLY)

            if fd < 0 then
                let errno = Marshal.GetLastWin32Error()
                Error $"could not open {procs} to move pid {pid} back out (errno {errno})"
            else
                try
                    let payload = System.Text.Encoding.ASCII.GetBytes(string pid)
                    let written = writeAll (fd, payload, nativeint payload.Length)

                    if written >= 0n && written = nativeint payload.Length then
                        Ok()
                    elif written >= 0n then
                        Error $"short write moving pid {pid} out to {procs} ({written} of {payload.Length} bytes)"
                    else
                        let errno = Marshal.GetLastWin32Error()

                        if errno = ESRCH then
                            // The process is gone, so it is no longer a member of anything: there is
                            // nothing left in this cgroup for teardown to reach, which is what the
                            // move-out was for.
                            Ok()
                        else
                            Error $"writing pid {pid} to {procs} to move it back out failed (errno {errno})"
                finally
                    closeFd fd |> ignore

    /// The live member pids of a cgroup (`cgroup.procs`), distinguishing "read, and it's empty" from
    /// "the read itself failed" (EACCES/EIO, a race with teardown removing the directory, …). Folding
    /// both into `[]` (the previous behaviour) made a transient read failure indistinguishable from a
    /// genuinely drained group — every fail-safe decision below (`cgroupAlive`, the `killCgroup` sweep,
    /// `signalCgroup`, `CgroupBackend.Members`/`Stats`) depends on telling them apart.
    let cgroupMembers (cgroupPath: string) : Result<int list, string> =
        try
            File.ReadAllLines(Path.Combine(cgroupPath, "cgroup.procs"))
            |> Array.choose (fun line ->
                match Int32.TryParse(line.Trim()) with
                | true, pid -> Some pid
                | _ -> None)
            |> List.ofArray
            |> Ok
        with ex ->
            Error ex.Message

    /// "Not yet drained" for the graceful-teardown poll loop (`GracefulKillTree`) and the legacy sweep
    /// below: a read failure is UNKNOWN membership, not an empty group, so it must report `true` (still
    /// alive) — never let an unreadable `cgroup.procs` look like the tree already drained and cut the
    /// teardown short.
    let cgroupAlive (cgroupPath: string) =
        match cgroupMembers cgroupPath with
        | Ok members -> not (List.isEmpty members)
        | Error _ -> true

    /// What one emptiness probe of a cgroup concluded — the input to the bounded drain wait teardown runs
    /// between the hard kill and the `rmdir` (`releaseCgroupUsing`). A probe that FAILED is `Unknown`,
    /// never `Empty`: an unreadable membership is unknown, and reading it as a drained group is exactly
    /// how a still-live tree would be declared gone (the same fail-safe rule `cgroupAlive` follows for the
    /// legacy sweep).
    [<RequireQualifiedAccess; NoComparison>]
    type Drain =
        /// Nothing is left to wait for: the kernel reported `populated 0`, `cgroup.procs` read back empty,
        /// or there is no cgroup directory at all any more.
        | Empty
        /// The kernel says the cgroup (or a descendant of it) still holds live members.
        | Populated
        /// The probe itself failed, so the membership is UNKNOWN — which is never "empty".
        | Unknown of Message: string

    // The kernel's own "is anything still alive in here" flag — the `populated` line of `cgroup.events` —
    // which is the very condition `rmdir` answers `EBUSY` to, and which (unlike `cgroup.procs`) also
    // counts a descendant cgroup's members. `None` when the file is unavailable or carries something other
    // than the documented `0`/`1` (a pre-4.14 kernel, a hierarchy that does not expose it, a cgroup being
    // torn down): not an answer of its own, so the caller falls back to the honest `cgroup.procs` read
    // rather than guessing from the absence.
    let private populatedFlag (cgroupPath: string) : bool option =
        try
            File.ReadAllLines(Path.Combine(cgroupPath, "cgroup.events"))
            |> Array.tryPick (fun line ->
                match line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
                | [| "populated"; "0" |] -> Some false
                | [| "populated"; "1" |] -> Some true
                | _ -> None)
        with _ ->
            // No readable `cgroup.events` (absent on this kernel, or the cgroup is gone) — the
            // `cgroup.procs` fallback below is what then decides drained / populated / unknown.
            None

    /// Is the cgroup empty *right now*? The question the bounded drain wait asks between a hard kill and
    /// the directory removal, because `cgroup.kill` (and the legacy sweep) only START the members leaving:
    /// the kernel drops a member from the cgroup when it EXITS, so membership can still be non-empty after
    /// the kill write returns, and `rmdir` refuses a cgroup that still holds anyone.
    ///
    /// Crucially this is a MEMBERSHIP question, never a reap one — a process leaves `cgroup.procs` on exit,
    /// before anybody reaps it — so it is answerable for an ADOPTED member (which we must never `waitpid`)
    /// exactly as it is for one of our own children.
    ///
    /// `cgroup.events`' `populated` flag is preferred (the kernel's own aggregate, covering a descendant
    /// cgroup's members too) with the `cgroup.procs` read as the fallback. A failed read is `Unknown`; only
    /// a positively empty answer — or a cgroup directory that is not there at all — is `Empty`. Note what
    /// `Empty` does and does not claim: it decides WHEN to try the `rmdir` (`Directory.Exists` also answers
    /// false for a path this process can no longer even stat), while the authority on whether the cgroup
    /// really drained is the kernel's own answer to that removal.
    let cgroupDrainState (cgroupPath: string) : Drain =
        match populatedFlag cgroupPath with
        | Some true -> Drain.Populated
        | Some false -> Drain.Empty
        | None ->
            match cgroupMembers cgroupPath with
            | Ok [] -> Drain.Empty
            | Ok _ -> Drain.Populated
            | Error message ->
                // A cgroup whose directory is gone holds nothing and has nothing left to remove — the one
                // absent-versus-unreadable distinction worth making here. Anything else is an unreadable
                // membership: unknown, and never reported as drained.
                if Directory.Exists cgroupPath then
                    Drain.Unknown message
                else
                    Drain.Empty

    // errno: the kernel does not implement the syscall — a pre-5.3 kernel lacking `pidfd_open`, a pre-5.1
    // kernel lacking `pidfd_send_signal`, or a seccomp filter blocking either. Turned into an honest
    // fail-safe error below rather than a racy raw-kill fallback.
    [<Literal>]
    let private ENOSYS = 38

    /// The classified outcome of one identity-safe per-member delivery attempt (see `deliverIdentitySafe`).
    /// Distinct from `SignalDelivery`: "the pinned target is gone" and "the pid left the cgroup, so it was
    /// deliberately skipped" are both broadcast success, yet must be told apart from a real failure that
    /// has to surface.
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type Delivery =
        /// The signal reached the confirmed member, or a benign exit race made it a no-op — either the
        /// target exited before it could be pinned, or the *pinned* task exited before the send (an ESRCH
        /// the pidfd guarantees is the target's own exit, never a signal leaked to a recycled pid).
        | Delivered
        /// The pinned pid was no longer a member when reconfirmed: its number may have been recycled by a
        /// process *outside* the cgroup, so it was refused a signal. Nothing was sent.
        | Skipped
        /// A real failure to surface: EPERM (a member that changed uid, or a seccomp/container policy), an
        /// unreadable membership (fail-safe: never signal when membership cannot be confirmed), or a kernel
        /// lacking pidfd (fail-safe: refuse to downgrade to a racy raw kill).
        | Failed of Errno: int * Message: string

    // The honest failure when the kernel lacks pidfd, so per-member signalling refuses to fall back to a
    // racy `kill(pid, ...)` that could hit a pid recycled by a process outside the cgroup. Carries ENOSYS
    // so `SignalDelivery.DeliveryFailed` still reports a real errno.
    let private pidfdUnsupported () : Delivery =
        Delivery.Failed(
            ENOSYS,
            "identity-safe per-member signalling needs pidfd (pidfd_open/pidfd_send_signal, Linux >= 5.3); "
            + "this kernel lacks it, so ProcessKit refuses to fall back to a racy kill(pid, ...) that could "
            + "hit a pid recycled by a process outside the cgroup — use SIGKILL teardown (the atomic "
            + "cgroup.kill) or run on a >= 5.3 kernel"
        )

    /// The identity-safe per-member signal primitive, factored over its syscall seam so the pid-reuse race
    /// is testable without real pidfd syscalls. Three steps, and their ORDER is what makes it race-free:
    ///
    /// 1. `openPin pid` **pins** the exact task currently running as `pid` (a pidfd in production). From
    ///    here the send in step 3 can only ever reach *that* task — never a later process that recycles the
    ///    number.
    /// 2. `stillMember pid` **reconfirms** membership, read *after* the pin. If the pin captured a process
    ///    that had already recycled `pid` (the original member exited in the snapshot->pin window), that
    ///    impostor is not a member of our cgroup, so this reports `false` and delivery is skipped.
    /// 3. `send handle sig` delivers through the pinned handle.
    ///
    /// Why a *live* process outside the cgroup is never signalled: a delivery reaches a live process only
    /// if the pinned task is still alive at step 3, in which case it has held `pid` continuously since the
    /// pin (a live process keeps its pid), so it *is* the process step 2 read at `pid` — and step 2 only
    /// let us proceed if that process was a member. If the pinned task instead exited, the send is a benign
    /// ESRCH, never a hit on whoever recycled the number.
    ///
    /// Generic over the pin handle so a test can pin with a token instead of a real fd; `closePin` releases
    /// the handle exactly once (the pidfd's `close` in production, a no-op for a test token).
    let deliverIdentitySafe
        (pid: int)
        (signalNum: int)
        (openPin: int -> Result<'H, int>)
        (stillMember: int -> Result<bool, string>)
        (send: 'H -> int -> Result<unit, int>)
        (closePin: 'H -> unit)
        : Delivery =
        // 1. Pin the exact task currently at `pid`.
        match openPin pid with
        // Already gone before it could be pinned — the intended end state (gone) already holds. Benign,
        // exactly like an ESRCH from the old raw `kill`; membership is not even consulted.
        | Error errno when errno = ESRCH -> Delivery.Delivered
        // No pidfd on this kernel (< 5.3) or a seccomp block: fail safe, never a racy raw-kill downgrade.
        | Error errno when errno = ENOSYS -> pidfdUnsupported ()
        | Error errno -> Delivery.Failed(errno, System.ComponentModel.Win32Exception(errno).Message)
        | Ok handle ->
            try
                // 2. Reconfirm membership *after* pinning.
                match stillMember pid with
                // The pinned pid left the cgroup — its number may have been recycled by a process outside
                // our tree. Refuse to signal it.
                | Ok false -> Delivery.Skipped
                // Membership unknown (an unreadable cgroup.procs): never signal when it cannot be confirmed.
                | Error message -> Delivery.Failed(0, message)
                | Ok true ->
                    // 3. Deliver through the pinned handle — the pinned task or nothing.
                    match send handle signalNum with
                    | Ok() -> Delivery.Delivered
                    // The pinned target exited between the reconfirm and the send. The pidfd guarantees this
                    // ESRCH is *our* target's exit, never a signal leaked to a recycled pid — benign.
                    | Error errno when errno = ESRCH -> Delivery.Delivered
                    | Error errno when errno = ENOSYS -> pidfdUnsupported ()
                    // A real delivery failure (EPERM, ...): surface it, never read as success.
                    | Error errno -> Delivery.Failed(errno, System.ComponentModel.Win32Exception(errno).Message)
            finally
                closePin handle

    /// Deliver `signalNum` to every pid of an already-read membership snapshot through the identity-safe
    /// choke above, reconfirming membership against `cgroupPath` *after* each pin (step 2 of
    /// `deliverIdentitySafe`). Shared by the per-member broadcast (`signalCgroup`) and the legacy teardown
    /// sweep (`killCgroup`), so both deliver through the one pin -> reconfirm -> send mechanism instead of
    /// two implementations of it.
    ///
    /// A benign race never cuts the pass short — a member gone before its pin, and a pid that left the
    /// cgroup (its number may have been recycled by a process outside the tree, so it is skipped
    /// unsignalled), both leave every remaining member its own chance. The FIRST genuine failure is
    /// returned as `Some (errno, message)` once each member has had that chance; `None` means no member
    /// reported one. Factored over the syscall seam for the same reason `deliverIdentitySafe` is.
    let private deliverToMembers
        (cgroupPath: string)
        (signalNum: int)
        (members: int list)
        (openPin: int -> Result<'H, int>)
        (send: 'H -> int -> Result<unit, int>)
        (closePin: 'H -> unit)
        : (int * string) option =
        // Reconfirm membership *after* each pidfd pins its pid: re-read cgroup.procs and ask whether the
        // pinned pid is still listed. If it left, the pidfd may now point at a process outside the cgroup
        // that recycled the number, so `deliverIdentitySafe` refuses to send.
        let stillMember (pid: int) : Result<bool, string> =
            match cgroupMembers cgroupPath with
            | Ok current -> Ok(List.contains pid current)
            | Error message -> Error message

        let mutable firstFailure: (int * string) option = None

        for pid in members do
            match deliverIdentitySafe pid signalNum openPin stillMember send closePin with
            | Delivery.Delivered
            | Delivery.Skipped -> ()
            | Delivery.Failed(errno, message) ->
                if firstFailure.IsNone then
                    firstFailure <- Some(errno, message)

        firstFailure

    [<Literal>]
    let private MaxThawAttempts = 3

    [<Literal>]
    let private ThawRetryDelayMilliseconds = 2

    /// Test-only seam for the cgroup kill path's control-file writes. Production leaves it unset;
    /// fault-injection tests use it to model a kernel/delegation write refusal without depending on
    /// the test runner's effective uid or a real delegated cgroup hierarchy.
    let mutable internal killCgroupWriteTestHook: (string -> string -> unit) option =
        None

    let private writeKillCgroupFile (file: string) (content: string) =
        killCgroupWriteTestHook |> Option.iter (fun hook -> hook file content)
        File.WriteAllText(file, content)

    /// Thaw a hard-killed reusable cgroup and verify the kernel-visible state. A successful
    /// write is not enough: cgroup.freeze is asynchronous, and a refused write can leave a previously
    /// frozen cgroup unchanged. A missing freezer means the cgroup was removed or the filesystem no
    /// longer exposes the control, so there is no reusable frozen group left to protect. Any other
    /// unreadable or unexpected state remains an error rather than a false success.
    let private thawCgroupAfterKill (cgroupPath: string) : Result<unit, string> =
        let freezeFile = Path.Combine(cgroupPath, "cgroup.freeze")
        let mutable attempt = 0
        let mutable thawed = false
        let mutable lastFailure = "cgroup.freeze did not report an unfrozen state"

        while attempt < MaxThawAttempts && not thawed do
            try
                writeKillCgroupFile freezeFile "0"
            with ex ->
                lastFailure <- $"could not write {freezeFile} to thaw the cgroup: {ex.Message}"

            try
                let state = File.ReadAllText freezeFile |> fun value -> value.Trim()

                if state = "0" then
                    thawed <- true
                elif state = "1" then
                    lastFailure <- $"{freezeFile} still reports frozen state (1) after thaw attempt"
                else
                    lastFailure <- $"{freezeFile} returned unexpected state '{state}' while verifying thaw"
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException ->
                // A removed cgroup cannot remain as a frozen reusable group; teardown may continue.
                thawed <- true
            | ex -> lastFailure <- $"could not read {freezeFile} while verifying thaw: {ex.Message}"

            attempt <- attempt + 1

            if not thawed && attempt < MaxThawAttempts then
                System.Threading.Thread.Sleep ThawRetryDelayMilliseconds

        if thawed then Ok() else Error lastFailure

    /// Hard-kill the whole subtree via `cgroup.kill` (kernel >= 5.14) — the atomic, race-free whole-subtree
    /// SIGKILL that also catches a process forked after any membership snapshot. On older kernels (< 5.14,
    /// no `cgroup.kill`) fall back to freezing the tree and running a bounded per-member SIGKILL sweep.
    /// Both paths explicitly thaw and verify the reusable cgroup before reporting success: `cgroup.kill`
    /// terminates members inside a frozen cgroup but does not reset `cgroup.freeze` itself.
    ///
    /// That fallback sweep is **identity-safe against pid recycling**: every SIGKILL goes through the same
    /// pin -> reconfirm-membership -> send choke `signalCgroup` uses (`deliverIdentitySafe`), never a raw
    /// `kill(pid, SIGKILL)` on a number snapshotted from `cgroup.procs`. The freeze does not close that
    /// window on its own — it stops members forking, not exiting, and pid numbers are recycled globally —
    /// so between the snapshot and the syscall a raw kill could land on an unrelated process outside the
    /// cgroup. Pinning the task first and re-reading membership after the pin is what confines each SIGKILL
    /// to a confirmed member.
    ///
    /// A pid that is gone, or that left the cgroup before its membership could be reconfirmed, is skipped
    /// rather than signalled, and that skip is not a failure by itself: the drain check driving this loop
    /// is the authority on whether the tree is dead. A pin/send failure is remembered and reported when the
    /// cgroup is still populated (or its membership unreadable) once the sweep ends, so a teardown that did
    /// not do its job can never read as success. On a kernel without pidfd (< 5.3) the sweep stops on the
    /// spot and returns that honest error instead of downgrading to the racy raw kill.
    ///
    /// Factored over the pidfd syscall seam exactly like `deliverIdentitySafe`, so the sweep's pid-reuse
    /// behaviour is testable without a real kernel; `killCgroup` wires the production primitives.
    let killCgroupUsing
        (openPin: int -> Result<'H, int>)
        (send: 'H -> int -> Result<unit, int>)
        (closePin: 'H -> unit)
        (cgroupPath: string)
        : Result<unit, string> =
        let viaKillFile =
            try
                writeKillCgroupFile (Path.Combine(cgroupPath, "cgroup.kill")) "1"
                true
            with _ ->
                false

        if not viaKillFile then
            (try
                writeKillCgroupFile (Path.Combine(cgroupPath, "cgroup.freeze")) "1"
             with _ ->
                 // Best-effort freeze to stop members forking faster than the sweep can kill them; if
                 // the freeze controller is unavailable we still SIGKILL the members below.
                 ())

            let mutable sweep = 0
            // The first genuine pin/send failure seen across all sweeps, so one that leaves the cgroup
            // populated is reported instead of masked by a success return.
            let mutable firstFailure: (int * string) option = None
            // A kernel that does not implement pidfd cannot be retried into implementing it: stop on the
            // first ENOSYS instead of spending the whole budget re-failing, and surface it below.
            let mutable pidfdMissing = false

            // `cgroupAlive` reports a read failure as "still alive," so a persistent (or transient)
            // `cgroup.procs` read failure keeps this loop running for its full iteration budget instead
            // of stopping on the first failed read — self-healing if the failure clears within the
            // budget, and otherwise leaving the caller correctly unsure the tree is fully dead rather
            // than falsely told it drained.
            while cgroupAlive cgroupPath && sweep < 50 && not pidfdMissing do
                match cgroupMembers cgroupPath with
                | Ok members ->
                    // Identity-safe sweep (see the docstring): each member is pinned, reconfirmed as a
                    // member, and only then SIGKILLed through the pinned handle, so a number recycled
                    // between this snapshot and the syscall is skipped instead of killed.
                    match deliverToMembers cgroupPath SIGKILL members openPin send closePin with
                    | None -> ()
                    | Some(errno, message) ->
                        if firstFailure.IsNone then
                            firstFailure <- Some(errno, message)

                        if errno = ENOSYS then
                            pidfdMissing <- true
                | Error _ ->
                    // Unknown membership this iteration — nothing safe to target; the loop condition
                    // above already keeps sweeping rather than treating this as drained.
                    ()

                System.Threading.Thread.Sleep 2
                sweep <- sweep + 1

            let thawed = thawCgroupAfterKill cgroupPath

            // The drain check is authoritative for "the tree is gone": a skipped (gone or recycled) pid, or
            // even a failed delivery, still ends in success when nothing is left in the cgroup. A recorded
            // failure with the group still populated — or unreadable, which `cgroupAlive` fail-safes to
            // "alive" — is a teardown that did not do its job, and is reported rather than thawed away.
            match firstFailure with
            | Some(errno, message) when cgroupAlive cgroupPath ->
                Error $"the identity-safe SIGKILL sweep left the cgroup populated: {message} (errno {errno})"
            | _ -> thawed
        else
            thawCgroupAfterKill cgroupPath

    /// `killCgroupUsing` wired to the production pidfd primitives.
    let killCgroup (cgroupPath: string) : Result<unit, string> =
        killCgroupUsing pidfdOpenChecked pidfdSendSignalChecked closePidfd cgroupPath

    /// Broadcast `signalNum` to every current member through the identity-safe pidfd primitive
    /// (`deliverIdentitySafe`, applied member by member by `deliverToMembers`), aggregating the per-member
    /// outcomes into one `SignalDelivery`: a benign race (a member gone, or a pid that left the cgroup)
    /// never aborts the broadcast — every member still gets its chance — while the first genuine delivery
    /// failure is what the aggregate reports. An unreadable member list is itself a delivery failure (never
    /// a false "delivered to nobody" success).
    let private broadcastIdentitySafe (cgroupPath: string) (signalNum: int) : SignalDelivery =
        match cgroupMembers cgroupPath with
        | Error message ->
            SignalDelivery.DeliveryFailed(0, $"could not read cgroup.procs to broadcast the signal: {message}")
        | Ok members ->
            match deliverToMembers cgroupPath signalNum members pidfdOpenChecked pidfdSendSignalChecked closePidfd with
            | Some(errno, message) -> SignalDelivery.DeliveryFailed(errno, message)
            | None -> SignalDelivery.Delivered

    /// Broadcast a raw signal to every member of a cgroup, aggregating the per-pid outcomes: a member
    /// that already exited (or whose pid left the cgroup) does not abort the broadcast — every member
    /// still gets the signal — but the first genuine delivery failure is what the aggregated result
    /// reports. An unreadable member list is reported as a delivery failure too (never a false "delivered
    /// to nobody" success) — signalling nobody must not look like a successful broadcast to an unknown
    /// group. Each delivery is **identity-safe** against pid recycling (see `deliverIdentitySafe`): the old
    /// raw `kill(pid, sig)` could hit a pid recycled by an unrelated process between the `cgroup.procs`
    /// snapshot and the syscall; pinning with a pidfd and reconfirming membership closes that TOCTOU
    /// window. On a kernel without pidfd this fails safe with an honest error rather than downgrading to
    /// the racy raw kill.
    let signalCgroup (cgroupPath: string) (signalNum: int) : SignalDelivery =
        broadcastIdentitySafe cgroupPath signalNum

    /// Freeze (`true`) or thaw (`false`) a cgroup (`cgroup.freeze`).
    let freezeCgroup (cgroupPath: string) (frozen: bool) : Result<unit, string> =
        try
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.freeze"), (if frozen then "1" else "0"))
            Ok()
        with ex ->
            Error ex.Message

    let private cgroupIoCounters (cgroupPath: string) : ProcessIoCounters option =
        try
            let mutable readBytes = 0L
            let mutable writeBytes = 0L
            let mutable readOperations = 0L
            let mutable writeOperations = 0L
            let mutable malformed = false

            let addSaturated current value =
                if value > Int64.MaxValue - current then
                    Int64.MaxValue
                else
                    current + value

            let content = File.ReadAllText(Path.Combine(cgroupPath, "io.stat"))
            let mutable cursor = 0

            while cursor < content.Length do
                while cursor < content.Length && Char.IsWhiteSpace content[cursor] do
                    cursor <- cursor + 1

                let tokenStart = cursor

                while cursor < content.Length && not (Char.IsWhiteSpace content[cursor]) do
                    cursor <- cursor + 1

                let mutable separator = tokenStart

                while separator < cursor && content[separator] <> '=' do
                    separator <- separator + 1

                if separator > tokenStart && separator < cursor then
                    let name = content.AsSpan(tokenStart, separator - tokenStart)
                    let valueText = content.AsSpan(separator + 1, cursor - separator - 1)

                    match Int64.TryParse valueText with
                    | true, value when value >= 0L ->
                        if name.SequenceEqual "rbytes" then
                            readBytes <- addSaturated readBytes value
                        elif name.SequenceEqual "wbytes" then
                            writeBytes <- addSaturated writeBytes value
                        elif name.SequenceEqual "rios" then
                            readOperations <- addSaturated readOperations value
                        elif name.SequenceEqual "wios" then
                            writeOperations <- addSaturated writeOperations value
                    | _ when
                        name.SequenceEqual "rbytes"
                        || name.SequenceEqual "wbytes"
                        || name.SequenceEqual "rios"
                        || name.SequenceEqual "wios"
                        ->
                        malformed <- true
                    | _ -> ()

            if malformed then
                None
            else
                Some
                    { ReadBytes = readBytes
                      WriteBytes = writeBytes
                      ReadOperations = readOperations
                      WriteOperations = writeOperations }
        with _ ->
            // io.stat is an optional controller file and can also disappear with a concurrently removed
            // cgroup; either case means the I/O metric is unavailable, while the other stats stay valid.
            None

    /// cgroup accounting for `stats`: cumulative CPU (`cpu.stat` `usage_usec`), peak memory
    /// (`memory.peak`), peak task count (`pids.peak`), and aggregate block I/O (`io.stat`), each `None`
    /// when its file is unavailable.
    let cgroupStats (cgroupPath: string) : TimeSpan option * int64 option * int64 option * ProcessIoCounters option =
        let cpu =
            try
                File.ReadAllLines(Path.Combine(cgroupPath, "cpu.stat"))
                |> Array.tryPick (fun line ->
                    if line.StartsWith "usage_usec" then
                        match Int64.TryParse(line.Substring("usage_usec".Length).Trim()) with
                        | true, usec -> Some(TimeSpan.FromTicks(usec * 10L)) // 1 microsecond = 10 ticks
                        | _ -> None
                    else
                        None)
            with _ ->
                None

        let memory =
            try
                match Int64.TryParse((File.ReadAllText(Path.Combine(cgroupPath, "memory.peak"))).Trim()) with
                | true, peak -> Some peak
                | _ -> None
            with _ ->
                None

        let processCount =
            try
                match Int64.TryParse((File.ReadAllText(Path.Combine(cgroupPath, "pids.peak"))).Trim()) with
                | true, peak when peak >= 0L -> Some peak
                | _ -> None
            with _ ->
                // pids.peak is optional and can disappear during concurrent cgroup teardown; an unreadable
                // native counter is unavailable, never a fabricated zero or the current membership count.
                None

        cpu, memory, processCount, cgroupIoCounters cgroupPath

    /// A `flat_keyed` cgroup v2 file's value for `key` — one `<key> <value>\n` line per key, matching
    /// `/sys/fs/cgroup/**/memory.events`/`pids.events`/`cpu.stat`'s own documented format. Whole-token
    /// matching only (a `key` of `"oom"` is never satisfied by an `"oom_kill"` line), and an unparsable,
    /// negative, or truncated value is an honest miss (`None`), never a fabricated reading.
    let private flatKeyedValue (text: string) (key: string) : int64 option =
        text.Split '\n'
        |> Array.tryPick (fun line ->
            let fields = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

            if fields.Length >= 2 && fields[0] = key then
                match Int64.TryParse fields[1] with
                | true, value when value >= 0L -> Some value
                | _ -> None
            else
                None)

    /// One `LimitEvidence` axis: `files` in preference order (a caller-scoped `.local` file, when this
    /// kernel has one, before the subtree total), and the `key` this axis reads from whichever of them
    /// reads first. Never capped on this axis → `NotTripped` with no read at all — nothing was capped, so
    /// nothing could have fired, and the cost of evidence stays off a group that asked for no caps.
    /// Capped → the FIRST file that reads successfully decides the verdict (a present-but-zero counter is
    /// an authoritative "did not fire", not a reason to fall through to the next file); a value present
    /// and non-zero is `Tripped`, present and zero is `NotTripped`, and a file that reads but lacks the
    /// key — or every listed file failing to read at all (an older kernel, a controller without that
    /// accounting, a cgroup already gone) — is the honest `Unknown` gap.
    let private axisVerdict (cgroupPath: string) (isCapped: bool) (files: string list) (key: string) : LimitVerdict =
        if not isCapped then
            LimitVerdict.NotTripped
        else
            let rec tryFiles files =
                match files with
                | [] -> LimitVerdict.Unknown
                | file :: rest ->
                    try
                        let text = File.ReadAllText(Path.Combine(cgroupPath, file))

                        match flatKeyedValue text key with
                        | Some 0L -> LimitVerdict.NotTripped
                        | Some _ -> LimitVerdict.Tripped
                        | None -> LimitVerdict.Unknown
                    with _ ->
                        // The file itself could not be read (missing on this kernel, a controller never
                        // enabled, a cgroup already removed by a racing teardown) — fall through to the
                        // next candidate rather than treating an absent file as decisive; the recursive
                        // base case above is the final honest `Unknown` once every candidate is exhausted.
                        tryFiles rest

            tryFiles files

    /// Post-run, per-axis `LimitEvidence` for a cgroup v2 group: the ONLY containment backend with real
    /// evidence to read (see `ProcessGroup.LimitEvidence`'s own doc comment for exactly what a Windows Job
    /// Object and the POSIX process-group fallback answer instead, and why). Reads straight from the
    /// kernel's own per-axis counters — never re-derived from the `ResourceLimits` that requested the cap
    /// — so a configured cap that never fired reads `NotTripped`, one that did reads `Tripped`, and one
    /// this cgroup cannot honestly attribute (an older kernel, a controller this hierarchy never enabled,
    /// a cgroup that is already gone by the time this runs) reads `Unknown`.
    ///
    /// | Axis | Counter | Key |
    /// |---|---|---|
    /// | Memory | `memory.events.local` (preferred) / `memory.events` | `oom` — this cgroup hit **its own** cap and had to OOM. Deliberately not `oom_kill`, which also counts a *global* host OOM kill of a member, misattributing a system-wide event to this cap. |
    /// | Processes | `pids.events.local` (preferred) / `pids.events` | `max` — a fork inside the cgroup was refused by `pids.max`. |
    /// | CPU | `cpu.stat` | `nr_throttled` — the tree was throttled by `cpu.max`'s quota at least once. |
    ///
    /// `Cpu`'s raw verdict from `nr_throttled` is about `cpu.max`/`CpuQuota` alone; it is passed through
    /// `CappedAxes.GuardCpuVerdict` before it reaches the returned `LimitEvidence`, so a group that ALSO
    /// carries a `CpuTimeMax` (per-child `RLIMIT_CPU`, which `cpu.stat` cannot see at all) never reports a
    /// fabricated `NotTripped` for it — including when `CpuQuota` itself was never set, so `nr_throttled`
    /// is never even read (T-381/R-01).
    let limitEvidence (cgroupPath: string) (capped: CappedAxes) : LimitEvidence =
        LimitEvidence(
            axisVerdict cgroupPath capped.Memory [ "memory.events.local"; "memory.events" ] "oom",
            axisVerdict cgroupPath capped.Processes [ "pids.events.local"; "pids.events" ] "max",
            capped.GuardCpuVerdict(axisVerdict cgroupPath capped.Cpu [ "cpu.stat" ] "nr_throttled")
        )

    /// How one attempt to remove the cgroup directory ended.
    [<RequireQualifiedAccess; NoComparison>]
    type Removal =
        /// The directory is gone: the `rmdir` succeeded, or it was already absent.
        | Removed
        /// The kernel refused because something is still in there — `EBUSY` while the cgroup holds members
        /// that have not finished leaving, `ENOTEMPTY` while it holds a child cgroup. Both surface as an
        /// `IOException`, and both are worth retrying inside the drain budget rather than swallowing.
        | Busy of Detail: string
        /// A refusal no amount of waiting fixes (a revoked delegation, a read-only mount): reported at once.
        | Failed of Detail: string

    /// What reclaiming a cgroup directory concluded, once the bounded wait for its tree to leave is over.
    [<RequireQualifiedAccess; NoComparison>]
    type Release =
        /// The directory is gone — the only verdict the kernel itself confirms (cgroupfs refuses to remove
        /// a cgroup that still holds members, so a successful `rmdir` IS the drain proof).
        | Removed
        /// The budget was spent and the directory is still there, with why: still populated, never
        /// confirmed drained (an unreadable membership), or a removal the kernel refused.
        | Retained of Detail: string

    /// How long teardown waits for a hard-killed cgroup to actually empty before it gives up on reclaiming
    /// the directory. The ordinary cgroup is already empty by the time teardown asks — its members were
    /// SIGKILLed, and the ones that are our own children reaped, before this runs — so this is a ceiling
    /// for the straggler case, not latency anyone pays, and it is deliberately short because
    /// `CgroupBackend.HardRelease` runs under the owning `ProcessGroup`'s lifecycle lock. Matches the
    /// ProcessKit-rs prototype's own bounded drain wait (50 × 2 ms) before its `rmdir`.
    let DefaultDrainBudget = TimeSpan.FromMilliseconds 100.0

    [<Literal>]
    let private DrainPollIntervalMilliseconds = 2.0

    /// Test seam: production NEVER assigns this, so the budget is `DefaultDrainBudget` everywhere. A
    /// regression that must not pay the real wait sets it (and restores it) around the call, exactly like
    /// `killCgroupWriteTestHook` and `PostKillReap.budgetOverrideForTests`.
    ///
    /// NOT thread-safe, the same convention `migrateWriteTestHook` documents: it is a process-wide,
    /// unsynchronized mutable, so it relies on the tests that set it running sequentially (no
    /// `[<Parallelizable>]` in this suite) and always restoring it in a `finally`.
    let mutable internal drainBudgetOverrideForTests: TimeSpan option = None

    let private drainBudget () =
        match drainBudgetOverrideForTests with
        | Some value -> value
        | None -> DefaultDrainBudget

    // A teardown that could not reclaim its cgroup directory has nowhere to REPORT that: the backend's
    // `HardRelease` is `unit` by contract (`IContainmentBackend`) and holds no `ILogger`. So the classified
    // verdict is recorded here rather than dropped on the floor — how many directories this process failed
    // to reclaim, and the most recent one with its reason — which is what makes an accumulating cgroup
    // hierarchy diagnosable (and assertable in a regression test) instead of silently invisible. Behind a
    // small lock so the count and the detail can never disagree about the same teardown.
    let private diagnosticGate = obj ()
    let mutable private retainedCgroups = 0
    let mutable private lastRetainedCgroup: string option = None

    /// Record a cgroup directory a teardown could not reclaim, with the reason it gave.
    let noteRetainedCgroup (cgroupPath: string) (detail: string) =
        lock diagnosticGate (fun () ->
            retainedCgroups <- retainedCgroups + 1
            lastRetainedCgroup <- Some $"{cgroupPath}: {detail}")

    /// How many cgroup directories this process has failed to reclaim at teardown.
    let retainedCgroupCount () =
        lock diagnosticGate (fun () -> retainedCgroups)

    /// The most recent cgroup directory teardown left behind, and why — `None` while there is none.
    let lastRetainedCgroupDetail () =
        lock diagnosticGate (fun () -> lastRetainedCgroup)

    /// Wait — BOUNDED — for a hard-killed cgroup to actually empty, then remove its directory, with the
    /// clock, the sleep, the emptiness probe and the removal all injected so the whole sequence is testable
    /// without a real kernel (the `killCgroupUsing`/`GracefulTeardown.pollUsing` pattern).
    ///
    /// The shape, and why each part is there:
    ///
    ///  * **Bounded, always.** Every pass re-reads the clock; once `budget` is spent the loop ends on that
    ///    pass, whatever the cgroup is doing. No path here sleeps unbounded, and each sleep is additionally
    ///    clamped to what is left of the budget.
    ///  * **No wait on the ordinary path.** A cgroup that already reads empty is removed on the FIRST pass,
    ///    before any sleep — the bounded wait must not become a fixed teardown latency.
    ///  * **A transient `EBUSY` is retried, not swallowed.** The kernel refusing the removal is its own
    ///    statement that a member has not finished leaving, so it re-enters the same wait (and the same
    ///    budget) instead of being dropped after one attempt.
    ///  * **An unreadable membership is never "drained".** `Drain.Unknown` keeps polling for the whole
    ///    budget exactly as `Drain.Populated` does, and can only end in `Retained` — never in a `Removed`
    ///    this function made up. What it does NOT do is refuse to try: once the budget is spent the removal
    ///    is attempted anyway, because cgroupfs will not remove a cgroup that still holds members, so that
    ///    attempt can reclaim a directory whose emptiness could not be READ while being unable to take away
    ///    one still in use. `Removed` therefore always rests on the kernel's answer, never on this loop's.
    let releaseCgroupUsing
        (probe: unit -> Drain)
        (remove: unit -> Removal)
        (elapsed: unit -> TimeSpan)
        (sleep: TimeSpan -> unit)
        (budget: TimeSpan)
        : Release =
        let pollInterval = TimeSpan.FromMilliseconds DrainPollIntervalMilliseconds

        // What a retained directory would be blamed on if the budget ran out right now, kept as its two
        // independent halves so neither can hide the other: the STATE the last probe found (which is where
        // "still populated" and "never confirmed drained" stay distinguishable), and the kernel's own last
        // refusal, if it got as far as one. Both are rewritten per pass, so the verdict reports what
        // actually ended the wait rather than a generic message.
        let mutable drainDetail =
            "the cgroup did not drain before the teardown budget ran out"

        let mutable refusal: string option = None

        let retained () =
            match refusal with
            | Some detail -> $"{drainDetail}; {detail}"
            | None -> drainDetail

        let rec waitForRelease () =
            let remaining = budget - elapsed ()
            let expired = remaining <= TimeSpan.Zero

            let attemptRemoval =
                match probe () with
                | Drain.Empty ->
                    // Nothing left to wait for: remove now, so an already-drained cgroup — the ordinary
                    // case — pays no part of the budget at all.
                    drainDetail <- "the cgroup read empty"
                    true
                | Drain.Populated ->
                    drainDetail <- "the cgroup was still populated when the teardown budget ran out"
                    expired
                | Drain.Unknown message ->
                    drainDetail <- $"the cgroup was never confirmed drained: {message}"
                    expired

            let continueWaiting () =
                if expired then
                    Release.Retained(retained ())
                else
                    sleep (min pollInterval remaining)
                    waitForRelease ()

            if attemptRemoval then
                match remove () with
                | Removal.Removed -> Release.Removed
                | Removal.Failed detail ->
                    Release.Retained $"{drainDetail}; the directory could not be removed: {detail}"
                | Removal.Busy detail ->
                    // The kernel's own verdict that the cgroup is not drained after all (a member is still
                    // on its way out, or a child cgroup remains). Keep waiting inside the same budget.
                    refusal <- Some $"the kernel refused to remove the directory: {detail}"
                    continueWaiting ()
            else
                continueWaiting ()

        waitForRelease ()

    // One `rmdir` of the cgroup directory, classified for the loop above. Deliberately NON-recursive: a
    // cgroup is reclaimed by removing its directory, never by deleting anything inside it.
    let private removeCgroupDirectory (cgroupPath: string) : Removal =
        try
            Directory.Delete cgroupPath
            Removal.Removed
        with
        | :? DirectoryNotFoundException ->
            // Already gone — the end state this call wanted, whoever reached it first. Matched ahead of
            // `IOException`, which it derives from.
            Removal.Removed
        | :? IOException as ex ->
            // `EBUSY` (members still leaving) or `ENOTEMPTY` (a child cgroup): something is still in there,
            // which is the retryable case.
            Removal.Busy ex.Message
        | ex ->
            // A refusal waiting cannot fix — `UnauthorizedAccessException` on a revoked delegation or a
            // read-only mount, an argument the runtime rejects outright.
            Removal.Failed ex.Message

    /// Reclaim a hard-killed cgroup's directory: `releaseCgroupUsing` wired to the real `cgroup.events`/
    /// `cgroup.procs` probe, the real `rmdir`, a real clock and a real sleep.
    ///
    /// This is teardown's LAST step and it is why the wait exists: `cgroup.kill` is asynchronous, so the
    /// members can still be leaving when it returns, and removing the directory right then fails with
    /// `EBUSY` — an error that used to be swallowed whole, leaving an empty but permanent cgroup behind on
    /// every teardown until the hierarchy filled up with them. The wait is bounded (`DefaultDrainBudget`)
    /// and needs no reap: membership is what it reads, and a process leaves the cgroup when it exits.
    let releaseCgroup (cgroupPath: string) : Release =
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()

        releaseCgroupUsing
            (fun () -> cgroupDrainState cgroupPath)
            (fun () -> removeCgroupDirectory cgroupPath)
            (fun () -> stopwatch.Elapsed)
            (fun (duration: TimeSpan) -> System.Threading.Thread.Sleep duration)
            (drainBudget ())
