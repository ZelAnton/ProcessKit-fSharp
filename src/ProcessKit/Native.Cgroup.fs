namespace ProcessKit.Native

open ProcessKit
open System
open System.IO
open System.Runtime.InteropServices
open ProcessKit.Native.Common
open ProcessKit.Native.Posix

/// Linux cgroup v2 — the `limits` backend and cgroup-scoped tree control. All plain file I/O over
/// /sys/fs/cgroup, plus per-member signal delivery. Depends on `Native.Common` (`SignalDelivery`) and
/// `Native.Posix` (the raw `kill` and the `SIGKILL`/`SIGTERM` numbers used by the best-effort teardown
/// sweep in `killCgroup`, and the pidfd primitives `pidfdOpenChecked`/`pidfdSendSignalChecked`/
/// `closePidfd` that make per-member SIGTERM / arbitrary-signal delivery identity-safe against pid
/// recycling), so it compiles after both.
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
        let quota = CgroupCpuMax.calculateQuota cores
        CgroupCpuMax.formatCpuMax quota

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

    /// Hard-kill the whole subtree via `cgroup.kill` (kernel >= 5.14) — the atomic, race-free whole-subtree
    /// SIGKILL that also catches a process forked after any membership snapshot. On older kernels (< 5.14,
    /// no `cgroup.kill`) fall back to freezing the tree and running a bounded per-pid `kill(pid, SIGKILL)`
    /// sweep.
    ///
    /// That fallback sweep is deliberately **best-effort, not identity-safe against pid recycling** — it is
    /// NOT presented as TOCTOU-safe. Unlike `signalCgroup`/`terminateCgroup` (which pin each pid with a
    /// pidfd before delivering, see `deliverIdentitySafe`), it signals raw pid numbers snapshotted from
    /// `cgroup.procs`, so in the tiny window between the snapshot and the `kill` a member could exit and its
    /// number be recycled by an unrelated process that then receives the SIGKILL. This is an accepted
    /// trade-off confined to teardown on ancient kernels: the freeze halts new forks while the sweep runs,
    /// SIGKILL is the least-harmful signal to misdirect, and a kernel without `cgroup.kill` (< 5.14) is old
    /// enough that pinning is not the point of this legacy path. The atomic `cgroup.kill` above is the
    /// race-free path used on every current kernel.
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
                    // Best-effort raw sweep (see the docstring): NOT identity-safe against pid recycling,
                    // unlike the pidfd-pinned `signalCgroup`/`terminateCgroup` path. Confined to teardown on
                    // kernels < 5.14 that lack the atomic `cgroup.kill`; the freeze above halts new forks
                    // while it runs, and SIGKILL is the least-harmful signal to misdirect in the tiny
                    // snapshot->kill window.
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

    /// Broadcast `signalNum` to every current member through the identity-safe pidfd primitive
    /// (`deliverIdentitySafe`), aggregating the per-member outcomes into one `SignalDelivery`: a benign
    /// race (a member gone, or a pid that left the cgroup) never aborts the broadcast — every member still
    /// gets its chance — while the first genuine delivery failure is what the aggregate reports. An
    /// unreadable member list is itself a delivery failure (never a false "delivered to nobody" success).
    let private broadcastIdentitySafe (cgroupPath: string) (signalNum: int) : SignalDelivery =
        match cgroupMembers cgroupPath with
        | Error message ->
            SignalDelivery.DeliveryFailed(0, $"could not read cgroup.procs to broadcast the signal: {message}")
        | Ok members ->
            // Reconfirm membership *after* each pidfd pins its pid: re-read cgroup.procs and ask whether the
            // pinned pid is still listed. If it left, the pidfd may now point at a process outside the
            // cgroup that recycled the number, so `deliverIdentitySafe` refuses to send.
            let stillMember (pid: int) : Result<bool, string> =
                match cgroupMembers cgroupPath with
                | Ok current -> Ok(List.contains pid current)
                | Error message -> Error message

            let mutable firstFailure: SignalDelivery option = None

            for pid in members do
                match
                    deliverIdentitySafe pid signalNum pidfdOpenChecked stillMember pidfdSendSignalChecked closePidfd
                with
                | Delivery.Delivered
                | Delivery.Skipped -> ()
                | Delivery.Failed(errno, message) ->
                    if firstFailure.IsNone then
                        firstFailure <- Some(SignalDelivery.DeliveryFailed(errno, message))

            match firstFailure with
            | Some failure -> failure
            | None -> SignalDelivery.Delivered

    /// SIGTERM every member (graceful); the caller polls then escalates with `killCgroup`. Each delivery is
    /// identity-safe (see `broadcastIdentitySafe`/`deliverIdentitySafe`): a pid recycled by a process
    /// outside the cgroup is never SIGTERM'd. The aggregated outcome is deliberately discarded — this is
    /// the graceful attempt tier, and `GracefulKillTree`'s poll loop relies on `cgroupAlive` (not this
    /// call's return) to decide whether to escalate. That preserves the old fail-safe: an unreadable
    /// membership signals nobody but must not look like "the group is already empty," and the poll loop's
    /// `cgroupAlive` (a read failure reads as "not drained") still drives escalation to `killCgroup`. On a
    /// kernel without pidfd every delivery fails safe (no raw kill), so nothing is signalled and the poll
    /// loop escalates to `killCgroup`'s atomic teardown — an honest degradation to a hard kill, never a
    /// misdirected SIGTERM.
    let terminateCgroup (cgroupPath: string) =
        broadcastIdentitySafe cgroupPath SIGTERM |> ignore

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
