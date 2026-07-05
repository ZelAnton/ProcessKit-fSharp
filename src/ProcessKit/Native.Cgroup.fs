namespace ProcessKit.Native

open ProcessKit
open System
open System.IO
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

    /// Migrate a process into the cgroup (a single write to `cgroup.procs`). Returns the write's
    /// failure detail on `Error` instead of swallowing it: a failure would otherwise leave the child
    /// running in the parent cgroup, entirely outside the requested limits. The caller
    /// (`CgroupBackend.Track`) turns that `Error` into an honest spawn failure — killing and reaping
    /// the child — rather than silently downgrading to an unconstrained run.
    let migrateToCgroup (cgroupPath: string) (pid: int) : Result<unit, string> =
        try
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.procs"), string pid)
            Ok()
        with ex ->
            Error ex.Message

    /// The live member pids of a cgroup (`cgroup.procs`).
    let cgroupMembers (cgroupPath: string) : int list =
        try
            File.ReadAllLines(Path.Combine(cgroupPath, "cgroup.procs"))
            |> Array.choose (fun line ->
                match Int32.TryParse(line.Trim()) with
                | true, pid -> Some pid
                | _ -> None)
            |> List.ofArray
        with _ ->
            []

    let cgroupAlive (cgroupPath: string) =
        not (List.isEmpty (cgroupMembers cgroupPath))

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

            while cgroupAlive cgroupPath && sweep < 50 do
                for pid in cgroupMembers cgroupPath do
                    kill (pid, SIGKILL) |> ignore

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
        for pid in cgroupMembers cgroupPath do
            kill (pid, SIGTERM) |> ignore

    /// Broadcast a raw signal to every member of a cgroup, aggregating the per-pid outcomes: a member
    /// that already exited (ESRCH) does not abort the broadcast — every member still gets the signal —
    /// but the first genuine delivery failure is what the aggregated result reports.
    let signalCgroup (cgroupPath: string) (signalNum: int) : SignalDelivery =
        let mutable firstFailure: SignalDelivery option = None

        for pid in cgroupMembers cgroupPath do
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
