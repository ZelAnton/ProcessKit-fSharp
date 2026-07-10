namespace ProcessKit.Native

open ProcessKit
open System
open System.IO
open System.Runtime.InteropServices
open ProcessKit.Native.Common
open ProcessKit.Native.Posix

/// Linux cgroup v2 — the `limits` backend and cgroup-scoped tree control. All plain file I/O over
/// /sys/fs/cgroup, plus per-member signal sweeps. Depends on `Native.Common` (`SignalDelivery`) and
/// `Native.Posix` (the single-pid `kill` and `signalPid`, and the `SIGKILL`/`SIGTERM` numbers), so
/// it compiles after both.
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
        let period = 100000.0
        let quota = max 1.0 (Math.Round(cores * period))
        $"{int64 quota} {int64 period}"

    // Enable the controllers the requested limits need (only the missing ones) in the parent's
    // `cgroup.subtree_control`, then write the caps into the child cgroup. Raises on failure (notably
    // EBUSY writing subtree_control when this process is not at the real cgroup root).
    let private applyCgroupLimits (parent: string) (cgroupPath: string) (limits: ResourceLimits) =
        let needed =
            [ if limits.MemoryMax.IsSome then
                  "memory"
              if limits.MaxProcesses.IsSome then
                  "pids"
              if limits.CpuQuota.IsSome then
                  "cpu" ]

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

        match limits.MemoryMax with
        | Some bytes -> File.WriteAllText(Path.Combine(cgroupPath, "memory.max"), string bytes)
        | None -> ()

        match limits.MaxProcesses with
        | Some n -> File.WriteAllText(Path.Combine(cgroupPath, "pids.max"), string n)
        | None -> ()

        match limits.CpuQuota with
        | Some cores -> File.WriteAllText(Path.Combine(cgroupPath, "cpu.max"), cpuMaxValue cores)
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
                let written = writeAll (fd, payload, nativeint payload.Length)

                if written >= 0n then
                    Ok()
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

    /// The live member pids of a cgroup (`cgroup.procs`), distinguishing "read, and it's empty" from
    /// "the read itself failed" (EACCES/EIO, a race with teardown removing the directory, …). Folding
    /// both into `[]` (the previous behaviour) made a transient read failure indistinguishable from a
    /// genuinely drained group — every fail-safe decision below (`cgroupAlive`, the `killCgroup` sweep,
    /// `terminateCgroup`, `signalCgroup`, `CgroupBackend.Members`/`Stats`) depends on telling them apart.
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

    /// Hard-kill the whole subtree via `cgroup.kill` (kernel >= 5.14); on older kernels, freeze then
    /// run a bounded per-pid SIGKILL sweep.
    let killCgroup (cgroupPath: string) =
        let viaKillFile =
            try
                File.WriteAllText(Path.Combine(cgroupPath, "cgroup.kill"), "1")
                true
            with _ ->
                false

        if not viaKillFile then
            (try
                File.WriteAllText(Path.Combine(cgroupPath, "cgroup.freeze"), "1")
             with _ ->
                 // Best-effort freeze to stop members forking faster than the sweep can kill them; if
                 // the freeze controller is unavailable we still SIGKILL the members below.
                 ())

            let mutable sweep = 0

            // `cgroupAlive` reports a read failure as "still alive," so a persistent (or transient)
            // `cgroup.procs` read failure keeps this loop running for its full iteration budget instead
            // of stopping on the first failed read — self-healing if the failure clears within the
            // budget, and otherwise leaving the caller correctly unsure the tree is fully dead rather
            // than falsely told it drained.
            while cgroupAlive cgroupPath && sweep < 50 do
                match cgroupMembers cgroupPath with
                | Ok members ->
                    for pid in members do
                        kill (pid, SIGKILL) |> ignore
                | Error _ ->
                    // Unknown membership this iteration — nothing safe to target; the loop condition
                    // above already keeps sweeping rather than treating this as drained.
                    ()

                System.Threading.Thread.Sleep 2
                sweep <- sweep + 1

            (try
                File.WriteAllText(Path.Combine(cgroupPath, "cgroup.freeze"), "0")
             with _ ->
                 // Best-effort thaw so a survivor of the sweep isn't left frozen; the cgroup is being
                 // torn down regardless, so a failure here is not actionable.
                 ())

    /// SIGTERM every member (graceful); the caller polls then escalates with `killCgroup`.
    let terminateCgroup (cgroupPath: string) =
        match cgroupMembers cgroupPath with
        | Ok members ->
            for pid in members do
                kill (pid, SIGTERM) |> ignore
        | Error _ ->
            // Unknown membership: there is nobody safe to signal this call, but silently doing nothing
            // must not look like "the group is already empty" — `GracefulKillTree`'s poll loop that
            // follows relies on `cgroupAlive` (not this call's return) to treat the same read failure as
            // "not drained," so the teardown still escalates to `killCgroup`'s retrying sweep instead of
            // quietly completing.
            ()

    /// Broadcast a raw signal to every member of a cgroup, aggregating the per-pid outcomes: a member
    /// that already exited (ESRCH) does not abort the broadcast — every member still gets the signal —
    /// but the first genuine delivery failure is what the aggregated result reports. An unreadable
    /// member list is reported as a delivery failure too (never a false "delivered to nobody" success)
    /// — signalling nobody must not look like a successful broadcast to an unknown group.
    let signalCgroup (cgroupPath: string) (signalNum: int) : SignalDelivery =
        match cgroupMembers cgroupPath with
        | Error message ->
            SignalDelivery.DeliveryFailed(0, $"could not read cgroup.procs to broadcast the signal: {message}")
        | Ok members ->
            let mutable firstFailure: SignalDelivery option = None

            for pid in members do
                match signalPid pid signalNum with
                | SignalDelivery.Delivered
                | SignalDelivery.TargetGone -> ()
                | SignalDelivery.DeliveryFailed _ as failure ->
                    if firstFailure.IsNone then
                        firstFailure <- Some failure

            match firstFailure with
            | Some failure -> failure
            | None -> SignalDelivery.Delivered

    /// Freeze (`true`) or thaw (`false`) a cgroup (`cgroup.freeze`).
    let freezeCgroup (cgroupPath: string) (frozen: bool) =
        try
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.freeze"), (if frozen then "1" else "0"))
        with _ ->
            // Suspend/Resume of a cgroup is best-effort: the freeze controller may be absent, or the
            // write may race the group's teardown. Leave the tree in its current state on failure.
            ()

    /// cgroup accounting for `stats`: cumulative CPU (cpu.stat `usage_usec`) and peak memory
    /// (`memory.peak`), each `None` when the file is absent.
    let cgroupStats (cgroupPath: string) : TimeSpan option * int64 option =
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

        cpu, memory

    /// Remove a (drained) cgroup directory. Best-effort cleanup.
    let removeCgroup (cgroupPath: string) =
        try
            Directory.Delete cgroupPath
        with _ ->
            ()
