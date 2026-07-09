namespace ProcessKit.Native

open ProcessKit
open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open ProcessKit.Native.Common

/// POSIX process-group containment: `posix_spawn` into a fresh process group, event-driven
/// `waitpid` reaping via a shared SIGCHLD registration, and `killpg`/`kill` signal delivery.
/// All libc `DllImport`s for this layer live here (including the single-pid `kill`, which the
/// cgroup layer also uses); call sites are guarded by `RuntimeInformation.IsOSPlatform` so a libc
/// entry point is only invoked on a POSIX host. Depends only on `Native.Common`.
module internal Posix =

    // ----------------------------------------------------------------------------------
    // POSIX: posix_spawn into a new process group (Linux / macOS)
    // ----------------------------------------------------------------------------------

    [<Literal>]
    let private POSIX_SPAWN_SETPGROUP = 0x02s

    [<Literal>]
    let private O_RDONLY = 0

    [<Literal>]
    let private O_WRONLY = 1

    // Non-variadic close-on-exec, used instead of fcntl (which a fixed-signature P/Invoke cannot
    // call under the AArch64 variadic ABI): Linux gets O_CLOEXEC on open (and the sibling SOCK_CLOEXEC
    // on the stdio socketpairs); macOS gets POSIX_SPAWN_CLOEXEC_DEFAULT, which closes every non-dup2 fd
    // in the child at exec.
    [<Literal>]
    let private O_CLOEXEC = 0x80000

    [<Literal>]
    let private POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000s

    // The stdio channels are AF_UNIX SOCK_STREAM socket pairs (see `createSocketPair`), not bare
    // pipes: a socket end can be wrapped in a .NET `Socket`/`NetworkStream` for genuine async I/O.
    // AF_UNIX and SOCK_STREAM are both 1 on Linux and macOS. SOCK_CLOEXEC (Linux only) shares
    // O_CLOEXEC's numeric value; OR'd into the socket type it makes socketpair(2) set close-on-exec
    // atomically on both ends.
    [<Literal>]
    let private AF_UNIX = 1

    [<Literal>]
    let private SOCK_STREAM = 1

    [<Literal>]
    let private SOCK_CLOEXEC = 0x80000

    // SIGKILL / SIGTERM are shared with the cgroup layer (`Native.Cgroup` sweeps members with these
    // raw numbers), so they are module-internal rather than private.
    [<Literal>]
    let SIGKILL = 9

    [<Literal>]
    let SIGTERM = 15

    [<Literal>]
    let private ENOENT = 2

    let private isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    [<DllImport("libc", SetLastError = true)>]
    extern int private socketpair(int domain, int socketType, int protocol, int[] fds)

    [<DllImport("libc", SetLastError = true)>]
    extern int private close(int fd)

    [<DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int private waitpid(int pid, int& status, int options)

    [<DllImport("libc", SetLastError = true)>]
    extern int private killpg(int pgrp, int signalNumber)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawn_file_actions_init(nativeint fileActions)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawn_file_actions_destroy(nativeint fileActions)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawn_file_actions_adddup2(nativeint fileActions, int fd, int newFd)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawn_file_actions_addclose(nativeint fileActions, int fd)

    [<DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern int private posix_spawn_file_actions_addchdir_np(nativeint fileActions, string path)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawnattr_init(nativeint attr)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawnattr_destroy(nativeint attr)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawnattr_setflags(nativeint attr, int16 flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_spawnattr_setpgroup(nativeint attr, int pgroup)

    [<DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern int private posix_spawnp(
        int& pid,
        string file,
        nativeint fileActions,
        nativeint attr,
        nativeint argv,
        nativeint envp
    )

    // Single-pid signal delivery. Shared with the cgroup layer (`Native.Cgroup` sweeps individual
    // member pids), so it is module-internal rather than private.
    [<DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signalNumber)

    // setpriority(PRIO_PROCESS, pid, nice) sets a process's absolute nice value (its CPU-scheduling
    // priority). `posix_spawn` has no attribute for nice, so `Command.Priority` applies it to the
    // spawned leader from the parent right after the spawn returns (see `spawnPosix`).
    [<Literal>]
    let private PRIO_PROCESS = 0

    [<DllImport("libc", SetLastError = true)>]
    extern int private setpriority(int which, int who, int prio)

    // umask(2) sets the calling PROCESS's file-mode creation mask and returns the previous one. It is a
    // whole-process attribute (not per-thread, not per-spawn), and `posix_spawn` has no umask attribute
    // of its own (unlike its file-action / flag attributes), so `Command.Umask` is applied by setting
    // the process umask right before `posix_spawnp` and restoring it right after, under `umaskSpawnLock`
    // (see `spawnPosix`). Only the low permission bits are meaningful; the return of any garbage high
    // bits on platforms with a narrow `mode_t` is harmless — umask ignores non-permission bits.
    [<DllImport("libc", SetLastError = true)>]
    extern int private umask(int mask)

    // Serializes the umask set/spawn/restore critical section against EVERY concurrent `posix_spawnp` in
    // this process — including one that requested no mask, which would otherwise be able to inherit
    // another spawn's temporarily-set umask during that window. This is the port's deliberate, typed
    // divergence from ProcessKit-rs: there `umask(2)` is applied in a `pre_exec` hook that runs in the
    // already-forked child, so it races neither the parent nor any sibling spawn; `posix_spawn` exposes
    // no such user hook, so the mask must be set in the PARENT and the whole window serialized. Mirrors
    // the existing `windowsSpawnLock` (Native.Windows) and `sigchldInitLock` module-level `lock`s.
    let private umaskSpawnLock = obj ()

    /// Marshal a list of strings into a NULL-terminated `char* []`. Returns the array pointer
    /// and the individual string allocations to free afterwards.
    let private marshalCStringArray (items: string list) : nativeint * nativeint list =
        let stringPointers = items |> List.map Marshal.StringToCoTaskMemUTF8
        let array = Marshal.AllocHGlobal((List.length stringPointers + 1) * IntPtr.Size)

        stringPointers
        |> List.iteri (fun i pointer -> Marshal.WriteIntPtr(array, i * IntPtr.Size, pointer))

        Marshal.WriteIntPtr(array, List.length stringPointers * IntPtr.Size, IntPtr.Zero)
        array, stringPointers

    let private freeCStringArray (array: nativeint) (stringPointers: nativeint list) =
        for pointer in stringPointers do
            Marshal.FreeCoTaskMem pointer

        Marshal.FreeHGlobal array

    /// Decode a `waitpid` status word into an `Outcome` (the encoding is shared by Linux and macOS).
    /// The final branch (`WIFSTOPPED`) should be unreachable: every `waitpid` call in this file passes
    /// only `WNOHANG` — never `WUNTRACED`/`WCONTINUED` — so a stopped/continued child never surfaces a
    /// status here. Decoded honestly rather than assumed impossible, in case some future call site (or
    /// an unexpected kernel/runtime interaction) ever reaches it.
    let private decodeWaitStatus (status: int) : Outcome =
        if status &&& 0x7f = 0 then
            Outcome.Exited((status >>> 8) &&& 0xff)
        elif status &&& 0x7f <> 0x7f then
            Outcome.Signalled(Some(status &&& 0x7f))
        else
            Outcome.Unobserved $"unexpected wait status 0x{status:x} (neither exited nor signalled)"

    /// Kill an entire POSIX process group (teardown / cancellation).
    let killProcessGroup (pgid: int) = killpg (pgid, SIGKILL) |> ignore

    /// Ask an entire POSIX process group to terminate gracefully (SIGTERM).
    let terminateProcessGroup (pgid: int) = killpg (pgid, SIGTERM) |> ignore

    /// True while any process remains in the group (signal 0 probes existence).
    let processGroupAlive (pgid: int) = killpg (pgid, 0) = 0

    // SIGSTOP / SIGCONT numbers differ between Linux and the BSD/macOS table (so do SIGUSR1/2);
    // resolve them per-platform.
    let private sigStop = if isMacOs then 17 else 19
    let private sigCont = if isMacOs then 19 else 18

    /// The raw POSIX signal number for a portable `Signal`, resolved for the current platform.
    let signalNumber (signal: Signal) : int =
        match signal with
        | Signal.Term -> SIGTERM
        | Signal.Kill -> SIGKILL
        | Signal.Int -> 2
        | Signal.Hup -> 1
        | Signal.Quit -> 3
        | Signal.Usr1 -> if isMacOs then 30 else 10
        | Signal.Usr2 -> if isMacOs then 31 else 12
        | Signal.Other n -> n

    /// Broadcast a raw signal to a POSIX process group via `killpg`, classifying the outcome (see
    /// `SignalDelivery`) instead of collapsing every non-zero errno into "not delivered".
    let signalProcessGroup (pgid: int) (signalNum: int) : SignalDelivery =
        classifySignalDelivery (killpg (pgid, signalNum))

    /// Freeze a POSIX process group (SIGSTOP).
    let suspendProcessGroup (pgid: int) = killpg (pgid, sigStop) |> ignore

    /// Thaw a POSIX process group (SIGCONT).
    let resumeProcessGroup (pgid: int) = killpg (pgid, sigCont) |> ignore

    /// Send a raw signal to a single POSIX pid via `kill`, classified the same way as
    /// `signalProcessGroup` (see `SignalDelivery`).
    let signalPid (pid: int) (signalNum: int) : SignalDelivery =
        classifySignalDelivery (kill (pid, signalNum))

    /// Hard-kill a single POSIX process by pid (SIGKILL).
    let killProcess (pid: int) = kill (pid, SIGKILL) |> ignore

    // A connected AF_UNIX SOCK_STREAM socket pair for one piped stdio channel, used instead of a bare
    // pipe: its parent-kept end can be wrapped in a .NET `Socket`/`NetworkStream`, whose async reads and
    // writes complete through the runtime's epoll/kqueue `SocketAsyncEngine` rather than parking a
    // thread-pool thread in a blocking `FileStream` call for the stream's whole lifetime — the entire
    // point of this path. SOCK_STREAM (never SOCK_DGRAM) keeps it a byte-exact, boundary-free stream, so
    // the parent captures the same bytes a pipe delivered. Close-on-exec so a *different* concurrent
    // spawn never inherits this run's parent-kept end: Linux sets it atomically with SOCK_CLOEXEC; macOS
    // lacks it and relies on POSIX_SPAWN_CLOEXEC_DEFAULT, exactly as the pipe path did.
    let private createSocketPair (fds: int[]) : int =
        let socketType =
            if isMacOs then
                SOCK_STREAM
            else
                SOCK_STREAM ||| SOCK_CLOEXEC

        socketpair (AF_UNIX, socketType, 0, fds)

    let private openDevNull (flags: int) : int =
        let flags = if isMacOs then flags else flags ||| O_CLOEXEC
        openFile ("/dev/null", flags)

    // errno value for a syscall interrupted by a signal (same number on Linux and macOS).
    let private EINTR = 4

    // `waitpid` option: return immediately (0) if the child has not yet changed state, rather than
    // blocking. Same value on Linux and macOS.
    let private WNOHANG = 1

    // An event-driven `waitPosix` wait in flight for one pid: the completion source the eventual
    // `waitpid` result feeds. Keyed by pid in `pendingWaits` below.
    [<NoComparison; NoEquality>]
    type private PendingWait = { Tcs: TaskCompletionSource<Outcome> }

    // Every event-driven POSIX wait currently in flight, process-wide. `waitPosix` adds an entry;
    // whichever of `reapAllPending` (SIGCHLD-triggered) or `reapLeader` (teardown) actually reaps a
    // given child removes it and completes its `Tcs` — only one of them ever can, since a child's
    // exit status is consumed exactly once by whichever `waitpid` call gets there first.
    let private pendingWaits = ConcurrentDictionary<int, PendingWait>()

    // Complete a pending event-driven wait for `pid` (if `waitPosix` registered one) with `outcome`.
    // Shared by the SIGCHLD-triggered reap path below and by `reapLeader`'s teardown reap, so
    // whichever of the two actually reaps a given child still delivers the real decoded status to
    // anything awaiting it, instead of leaving the wait to notice only `ECHILD`. A no-op if nothing
    // is pending for `pid` (no `waitPosix` call is in flight for it).
    let private completePending (pid: int) (outcome: Outcome) =
        match pendingWaits.TryRemove pid with
        | true, pending -> pending.Tcs.TrySetResult outcome |> ignore
        | false, _ -> ()

    // Best-effort, non-blocking reap of one pending pid. Returns `true` once nothing further needs to
    // be done SYNCHRONOUSLY for it (reaped by us; nothing was ever pending; or an `ECHILD` race whose
    // resolution has been handed off to an async grace-then-fallback on the thread pool — see below);
    // `false` if it is still alive (left pending for the next trigger). Never blocks.
    let private tryReapPending (pid: int) : bool =
        if not (pendingWaits.ContainsKey pid) then
            true
        else
            let mutable status = 0
            let mutable result = waitpid (pid, &status, WNOHANG)

            // `WNOHANG` returns near-instantly (it never blocks), so retrying `EINTR` immediately here
            // costs nothing — unlike the blocking `waitpid` this replaces, an "unbounded" retry loop
            // can't wedge a thread. Retrying immediately (rather than deferring to the next SIGCHLD,
            // which may never come) matters because this call can be the *only* look a pid ever gets:
            // the one immediate probe right after `waitPosix` registers it, for a child that had
            // already exited before that — no *new* SIGCHLD is generated for an event that already
            // happened.
            while result < 0 && Marshal.GetLastWin32Error() = EINTR do
                result <- waitpid (pid, &status, WNOHANG)

            if result = 0 then
                false // still alive
            elif result = pid then
                // We won the reap race — this is the real, decoded status.
                completePending pid (decodeWaitStatus status)
                true
            else
                // `ECHILD`: some concurrent caller (most plausibly `reapLeader` tearing down an
                // abandoned run, or another `tryReapPending` invocation that already won this exact
                // race) has already reaped this pid and holds the REAL status; we have nothing to
                // report ourselves. `completePending` is a `TryRemove`, so whichever side calls it
                // *first* decides the outcome — not whichever side won the `waitpid` race — so give the
                // genuine winner a brief grace period to land its real result before falling back.
                //
                // This grace-then-fallback runs on the thread pool (fire-and-forget), NOT spun in-line:
                // this function is called from the shared SIGCHLD callback, so blocking it here would
                // stall the runtime's whole signal-dispatch path and delay reaping every other pending
                // child. Nothing inside the task can throw (`ContainsKey`/`completePending` don't), so
                // it needs no fault observer.
                task {
                    let mutable spins = 0

                    while pendingWaits.ContainsKey pid && spins < 20 do
                        do! Task.Delay 1
                        spins <- spins + 1

                    // Still pending after the grace period: nobody ever reported this pid's real status
                    // (the genuine winner errored out before calling `completePending`, or something
                    // outside ProcessKit's own reap machinery reaped it). Resolve honestly instead of
                    // leaving the wait hanging forever or inventing a clean exit.
                    completePending
                        pid
                        (Outcome.Unobserved "the process's exit status could not be observed (ECHILD race)")
                }
                |> ignore

                true

    // Re-scan every still-pending pid — triggered by SIGCHLD (some child changed state; POSIX signals
    // are not queued, so a burst of near-simultaneous exits can coalesce into one delivery, and we
    // cannot assume it was any *specific* one of ours — re-probing all of them handles that
    // uniformly) and once eagerly right after registering a new wait, in case that child had already
    // exited before we started listening for it.
    let private reapAllPending () =
        for pid in pendingWaits.Keys |> Seq.toArray do
            tryReapPending pid |> ignore

    let mutable private sigchldRegistration: PosixSignalRegistration option = None
    let private sigchldInitLock = obj ()

    // Lazily install ONE process-wide SIGCHLD handler (not one thread, and not one per child) the
    // first time a POSIX wait is needed. `PosixSignalRegistration` dispatches through the runtime's
    // own signal-handling machinery — no dedicated blocking thread of our own, and it coexists safely
    // alongside any other SIGCHLD registration in the process (unlike installing a raw `sigaction`
    // handler, which would clobber one).
    let private ensureSigchldRegistration () =
        if sigchldRegistration.IsNone then
            lock sigchldInitLock (fun () ->
                if sigchldRegistration.IsNone then
                    sigchldRegistration <-
                        Some(PosixSignalRegistration.Create(PosixSignal.SIGCHLD, (fun _ -> reapAllPending ()))))

    // ----------------------------------------------------------------------------------
    // Linux >= 5.4: per-child pidfd + one shared epoll reaper (the pid-reuse-safe fast path)
    // ----------------------------------------------------------------------------------
    //
    // The SIGCHLD path above is the portable fallback: one process-wide signal handler that, on every
    // child state change, re-scans EVERY pending pid with `waitpid(pid, WNOHANG)`. Two costs are
    // inherent to that model. (1) Dispatch is O(pending) per SIGCHLD — POSIX signals do not queue, so a
    // burst of near-simultaneous exits coalesces into one delivery that carries no "which child", and
    // the only correct response is to re-probe them all. (2) The reap is by *pid number*: between a
    // child becoming a zombie and our `waitpid` landing, if anything else reaps it (classically a
    // double-forked grandchild reaped by init) the pid can be recycled, and a `waitpid(pid)` on a
    // recycled number is the pid-reuse window the ROADMAP calls out as this model's limitation.
    //
    // On Linux >= 5.4 we replace both. Each child gets its own `pidfd_open(pid)` handle, registered once
    // with a single shared `epoll` instance served by one background thread. When the child exits its
    // pidfd becomes readable, so dispatch is O(1) for THAT child (no rescan of the others), and the reap
    // is `waitid(P_PIDFD, pidfd, WEXITED)` — which refers to the exact process the pidfd was opened on,
    // NOT a pid number, so it is immune to pid reuse. The reuse window on this path is therefore narrowed
    // to essentially nothing: the only remaining `waitpid(pid)` on a live child is `reapLeader`
    // (teardown), coordinated with this path exactly as the SIGCHLD path already is — through the shared
    // `pendingWaits` registry — so a child is still reaped exactly once and no status is lost.
    //
    // Which mechanism is used is decided ONCE, at first use (`pidfdSupported`), never per-run: Linux with
    // a working `pidfd_open` + `waitid(P_PIDFD)` uses this path for the whole process lifetime; macOS, a
    // non-Linux POSIX, or a kernel too old (`ENOSYS`/`EINVAL`) uses the SIGCHLD fallback for the whole
    // process lifetime. The two never run side by side for the same child in production, so no new
    // double-reap window is introduced. (Coordination via `pendingWaits` is nonetheless race-safe even
    // if both are active — see `completePidfdReg` — which is what the test-only fallback seam relies on.)

    [<Literal>]
    let private SYS_pidfd_open = 434 // same syscall number on x86_64/arm64/x86/arm/riscv (Linux 5.3+)

    // waitid idtype: wait on the process a pidfd refers to (P_ALL=0, P_PID=1, P_PGID=2, P_PIDFD=3).
    [<Literal>]
    let private P_PIDFD = 3

    // waitid option: report a child that has TERMINATED (as opposed to stopped/continued).
    [<Literal>]
    let private WEXITED = 4

    // errno: no (more) child of ours matches — someone else already reaped it.
    [<Literal>]
    let private ECHILD = 10

    // siginfo si_code values for a terminated child (asm-generic; shared across the Linux archs we run).
    [<Literal>]
    let private CLD_EXITED = 1

    [<Literal>]
    let private CLD_KILLED = 2

    [<Literal>]
    let private CLD_DUMPED = 3

    // epoll: interest in readability, add/delete ops, and close-on-exec for the epoll fd itself.
    [<Literal>]
    let private EPOLLIN = 0x001

    [<Literal>]
    let private EPOLL_CTL_ADD = 1

    [<Literal>]
    let private EPOLL_CTL_DEL = 2

    [<Literal>]
    let private EPOLL_CLOEXEC = 0x80000

    // How many ready pidfds one `epoll_wait` may harvest at once; the rest wait for the next call
    // (level-triggered), so this only bounds a batch — it never drops an event.
    [<Literal>]
    let private maxEpollEvents = 64

    // `struct epoll_event { uint32 events; epoll_data (u64); }` is `__attribute__((packed))` ONLY on
    // x86_64 (12 bytes, data at offset 4); every other arch aligns the u64 to 8 (16 bytes, data at
    // offset 8). x86 keeps the u64 4-aligned, so it matches x86_64's offset. We marshal the struct by
    // raw offset rather than a `[<StructLayout>]` type to get this arch split exactly right.
    let private epollDataOffset =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.X64
        | Architecture.X86 -> 4
        | _ -> 8

    let private epollEventSize = epollDataOffset + 8

    // siginfo_t field offsets. si_signo/si_errno/si_code are the first three ints (si_code at 8); the
    // _sigchld union follows, 8-byte aligned on LP64 (an int `__pad0` sits at 12 and the union starts at
    // 16: si_pid@16, si_status@24) and 4-byte aligned on 32-bit (union at 12: si_pid@12, si_status@20).
    let private siPidOffset = if IntPtr.Size = 8 then 16 else 12
    let private siStatusOffset = if IntPtr.Size = 8 then 24 else 20

    [<DllImport("libc", SetLastError = true, EntryPoint = "syscall")>]
    extern nativeint private syscall3(nativeint number, int arg1, uint arg2)

    // waitid(idtype, id, siginfo*, options). The libc wrapper is a fixed-signature function (not
    // variadic), so a plain P/Invoke is correct on every ABI including AArch64.
    [<DllImport("libc", SetLastError = true)>]
    extern int private waitid(int idtype, uint id, nativeint infop, int options)

    [<DllImport("libc", SetLastError = true)>]
    extern int private epoll_create1(int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int private epoll_ctl(int epfd, int op, int fd, nativeint event)

    [<DllImport("libc", SetLastError = true)>]
    extern int private epoll_wait(int epfd, nativeint events, int maxevents, int timeout)

    // pidfd_open(pid, flags) via syscall(2). There is no fixed-signature libc wrapper before glibc 2.36,
    // so we go through `syscall`, whose glibc stub reads its arguments from REGISTERS (hand-written asm,
    // not a C variadic) — so a fixed-signature P/Invoke passes them correctly even on AArch64, unlike the
    // genuinely-variadic `fcntl` this file deliberately avoids for exactly that reason.
    let private pidfdOpen (pid: int) : int =
        int (syscall3 (nativeint SYS_pidfd_open, pid, 0u))

    // Decide ONCE whether the pidfd fast path is usable, for the whole process lifetime. `pidfd_open`
    // arrived in Linux 5.3 but `waitid(P_PIDFD)` only in 5.4, so we confirm BOTH: open a pidfd on our own
    // (always-live) process, then probe `waitid(P_PIDFD)` on it. A kernel that understands P_PIDFD
    // answers ECHILD (we are not our own child); a 5.3 kernel rejects the idtype with EINVAL, and a
    // pre-5.3 kernel already failed `pidfd_open` with ENOSYS. Any non-Linux host (macOS, other POSIX)
    // skips the probe and uses the SIGCHLD fallback.
    let private detectPidfdSupport () : bool =
        if not (RuntimeInformation.IsOSPlatform OSPlatform.Linux) then
            false
        else
            let fd = pidfdOpen (Environment.ProcessId)

            if fd < 0 then
                false
            else
                let siginfo = Marshal.AllocHGlobal 128

                try
                    let rc = waitid (P_PIDFD, uint fd, siginfo, WEXITED ||| WNOHANG)
                    let errno = Marshal.GetLastWin32Error()
                    // rc = 0 (nothing to report for a live non-child) or rc < 0 with ECHILD both mean the
                    // kernel UNDERSTANDS P_PIDFD; EINVAL (old kernel) means it does not.
                    rc = 0 || errno = ECHILD
                finally
                    Marshal.FreeHGlobal siginfo
                    close fd |> ignore

    let private pidfdSupported = detectPidfdSupport ()

    /// Internal diagnostic (not public API): the POSIX exit-wait mechanism this process selected — `true`
    /// = the Linux pidfd fast path, `false` = the shared SIGCHLD fallback. For tests / observability.
    let pidfdActive = pidfdSupported

    /// Decode a `waitid` siginfo buffer into an `Outcome`. Unlike a `waitpid` status word, waitid hands
    /// back already-decoded fields: CLD_EXITED carries the exit code directly in si_status;
    /// CLD_KILLED / CLD_DUMPED carry the terminating signal number.
    let private decodeSiginfo (siginfo: nativeint) : Outcome =
        let siCode = Marshal.ReadInt32(siginfo, 8)
        let siStatus = Marshal.ReadInt32(siginfo, siStatusOffset)

        if siCode = CLD_EXITED then
            Outcome.Exited siStatus
        elif siCode = CLD_KILLED || siCode = CLD_DUMPED then
            Outcome.Signalled(Some siStatus)
        else
            Outcome.Unobserved $"unexpected waitid si_code {siCode} (neither exited nor signalled)"

    // One in-flight pidfd wait: the pid and the exact `pendingWaits` entry (by reference) this pidfd
    // owns. Keyed by pidfd in `pidfdRegs`. Carrying the entry by reference — rather than re-looking it up
    // by pid — is what keeps the reaper pid-reuse-safe: a stale pidfd whose pid has since been recycled
    // can never resolve a NEWER generation's wait for the same pid number (see `completePidfdReg`).
    [<NoComparison; NoEquality>]
    type private PidfdReg = { Pid: int; Pending: PendingWait }

    let private pidfdRegs = ConcurrentDictionary<int, PidfdReg>()

    let mutable private epollFd = -1
    let private epollInitLock = obj ()

    // Resolve exactly the wait THIS pidfd registration owns. Remove the shared `pendingWaits` entry only
    // when it still maps to this exact registration (reference equality via the value comparer), so a
    // stale pidfd for a reused pid can't evict a newer generation's entry; then set our own TCS
    // (idempotent). Symmetric to the SIGCHLD path's `completePending`, but keyed by registration identity
    // instead of by pid — the pid-reuse-safe reap the pidfd path exists for.
    let private completePidfdReg (reg: PidfdReg) (outcome: Outcome) =
        pendingWaits.TryRemove(System.Collections.Generic.KeyValuePair(reg.Pid, reg.Pending))
        |> ignore

        reg.Pending.Tcs.TrySetResult outcome |> ignore

    // `waitid` came back ECHILD (or a spurious wake): the genuine winner — `reapLeader` racing this
    // child's teardown — holds the real status and is about to deliver it via `completePending`. Give it
    // the same brief grace the SIGCHLD path's ECHILD branch gives, then resolve OUR registration
    // honestly. Keyed by registration identity throughout, so a reused pid is never crossed. Runs on the
    // thread pool so it never blocks the shared reaper thread.
    let private graceThenResolve (reg: PidfdReg) : Task =
        task {
            let mutable spins = 0

            while (match pendingWaits.TryGetValue reg.Pid with
                   | true, current -> obj.ReferenceEquals(current, reg.Pending)
                   | false, _ -> false)
                  && spins < 20 do
                do! Task.Delay 1
                spins <- spins + 1

            completePidfdReg reg (Outcome.Unobserved "the process's exit status could not be observed (ECHILD race)")
        }
        :> Task

    // Handle one pidfd the reaper thread found readable: the child it refers to has terminated. Reap it
    // pid-reuse-safely with `waitid(P_PIDFD)`, resolve the owning wait, then unregister and close the
    // pidfd (a dead process's pidfd stays readable, so it MUST be removed or epoll would report it
    // forever). Runs only on the single reaper thread.
    let private reapReadyPidfd (pidfd: int) (siginfo: nativeint) =
        match pidfdRegs.TryGetValue pidfd with
        | false, _ ->
            // Already processed and removed (no event should arrive after the DEL below): nothing to do.
            ()
        | true, reg ->
            Marshal.WriteInt32(siginfo, siPidOffset, 0)
            let mutable rc = waitid (P_PIDFD, uint pidfd, siginfo, WEXITED ||| WNOHANG)

            while rc < 0 && Marshal.GetLastWin32Error() = EINTR do
                Marshal.WriteInt32(siginfo, siPidOffset, 0)
                rc <- waitid (P_PIDFD, uint pidfd, siginfo, WEXITED ||| WNOHANG)

            let siPid = Marshal.ReadInt32(siginfo, siPidOffset)

            if rc = 0 && siPid <> 0 then
                // We won the reap — this is the real, pid-reuse-safe status.
                completePidfdReg reg (decodeSiginfo siginfo)
            else
                // ECHILD (or a spurious wake): `reapLeader` (teardown) reaped this child first and holds
                // the real status. Give it a brief grace to land it, then resolve honestly.
                graceThenResolve reg |> ignore

            // Unregister from `pidfdRegs` BEFORE closing, so a concurrent `pidfdOpen` that reuses this fd
            // number (only possible after the close) never lands on the stale registration; then DEL
            // (needs the fd still open) and close.
            pidfdRegs.TryRemove pidfd |> ignore
            epoll_ctl (epollFd, EPOLL_CTL_DEL, pidfd, IntPtr.Zero) |> ignore
            close pidfd |> ignore

    // The single shared reaper: one thread for the whole process, blocking in `epoll_wait` and
    // dispatching each ready pidfd. Never returns; a background thread so it does not hold process exit
    // open. Its scratch buffers live for the thread's lifetime (intentionally never freed).
    let private epollReaperLoop () =
        let eventsBuf = Marshal.AllocHGlobal(maxEpollEvents * epollEventSize)
        let siginfo = Marshal.AllocHGlobal 128

        while true do
            let n = epoll_wait (epollFd, eventsBuf, maxEpollEvents, -1)

            if n < 0 then
                // EINTR is routine (a signal interrupted the wait); anything else is unexpected — a brief
                // pause avoids a hot spin before retrying, since epollFd is never closed.
                if Marshal.GetLastWin32Error() <> EINTR then
                    Thread.Sleep 1
            else
                for i in 0 .. n - 1 do
                    let pidfd = int (Marshal.ReadInt64(eventsBuf, i * epollEventSize + epollDataOffset))

                    reapReadyPidfd pidfd siginfo

    // Lazily create the one epoll instance + reaper thread, the first time the pidfd path is used.
    let private ensureEpoll () =
        if epollFd < 0 then
            lock epollInitLock (fun () ->
                if epollFd < 0 then
                    let fd = epoll_create1 EPOLL_CLOEXEC

                    if fd >= 0 then
                        // Publish the fd BEFORE starting the thread so the loop reads a valid epollFd.
                        epollFd <- fd
                        let thread = Thread(ThreadStart(epollReaperLoop))
                        thread.IsBackground <- true
                        thread.Name <- "ProcessKit-pidfd-reaper"
                        thread.Start())

    // Register a pidfd with the shared epoll for readability (level-triggered, so an already-dead child
    // is reported at once). Returns false if `epoll_ctl` rejects it (pathological on a pidfd-capable
    // kernel), so the caller can fall back for this child.
    let private armEpoll (pidfd: int) : bool =
        let ev = Marshal.AllocHGlobal epollEventSize

        try
            Marshal.WriteInt32(ev, 0, EPOLLIN)

            if epollDataOffset <> 4 then
                // Zero the 4-byte alignment gap on the unpacked layout; harmless but tidy.
                Marshal.WriteInt32(ev, 4, 0)

            Marshal.WriteInt64(ev, epollDataOffset, int64 pidfd)
            epoll_ctl (epollFd, EPOLL_CTL_ADD, pidfd, ev) = 0
        finally
            Marshal.FreeHGlobal ev

    // Last-resort per-child fallback when `pidfd_open`/`epoll_ctl` can't be used for THIS child (the
    // child was already reaped — ESRCH — or the fd table is momentarily exhausted). Confined to this rare
    // corner so the global pidfd path stays park-free: a single blocking `waitpid(pid, 0)` on a pool
    // thread, resolving the owning registration so it coordinates with `reapLeader` exactly like the
    // epoll path. This is the only place the pidfd path parks a thread, and only under fd exhaustion or an
    // already-gone child.
    let private blockingReapFallback (pending: PendingWait) (pid: int) =
        let reg = { Pid = pid; Pending = pending }

        let work () : Task =
            task {
                let mutable status = 0
                let mutable result = waitpid (pid, &status, 0)

                while result < 0 && Marshal.GetLastWin32Error() = EINTR do
                    result <- waitpid (pid, &status, 0)

                if result = pid then
                    completePidfdReg reg (decodeWaitStatus status)
                else
                    // ECHILD (already reaped, most likely by `reapLeader`): grace-then-resolve.
                    do! graceThenResolve reg
            }
            :> Task

        Task.Run(work) |> ignore

    // Begin a pidfd-based wait for one freshly-registered child: open its pidfd and arm epoll; on any
    // per-child failure, degrade just this child via `blockingReapFallback`. The reap itself happens
    // later on the reaper thread when the pidfd signals readiness.
    let private beginPidfdWait (pid: int) (pending: PendingWait) =
        ensureEpoll ()

        if epollFd < 0 then
            // The epoll instance could not be created (pathological): degrade this child.
            blockingReapFallback pending pid
        else
            let pidfd = pidfdOpen pid

            if pidfd < 0 then
                blockingReapFallback pending pid
            else
                pidfdRegs[pidfd] <- { Pid = pid; Pending = pending }

                if not (armEpoll pidfd) then
                    // Could not arm epoll for it: undo and degrade this child.
                    pidfdRegs.TryRemove pidfd |> ignore
                    close pidfd |> ignore
                    blockingReapFallback pending pid

    /// Reap a POSIX child and report how it concluded, without parking a thread per child. On Linux
    /// >= 5.4 each child is awaited through its own `pidfd` on one shared epoll reaper (O(1) dispatch,
    /// pid-reuse-safe `waitid(P_PIDFD)` reap); elsewhere — macOS, an old kernel — through the shared
    /// process-wide SIGCHLD registration. Which one is chosen is fixed for the process at first use; the
    /// public contract (the decoded `Outcome`, zombie-free teardown, the `nativeint` pid handle) is
    /// identical either way.
    ///
    /// Idempotent per pid: a second call while a wait for the same pid is already in flight reuses the
    /// existing registration's task instead of opening a second pidfd or overwriting `pendingWaits` — an
    /// unconditional overwrite would strand the earlier `TaskCompletionSource` forever (nothing would
    /// complete it, since the `TryRemove` handoffs only ever observe the newer entry). Both callers
    /// observe the same eventual outcome.
    let rec private waitPosixCore (usePidfd: bool) (pid: nativeint) : Task<Outcome> =
        let intPid = int pid

        match pendingWaits.TryGetValue intPid with
        | true, existing -> existing.Tcs.Task
        | false, _ ->
            let tcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let pending = { Tcs = tcs }

            if pendingWaits.TryAdd(intPid, pending) then
                if usePidfd && pidfdSupported then
                    beginPidfdWait intPid pending
                else
                    ensureSigchldRegistration ()
                    // The child may already have exited — even before this call started — so probe once
                    // immediately rather than waiting on a SIGCHLD that may already have been delivered.
                    tryReapPending intPid |> ignore

                tcs.Task
            else
                // Lost the race to register first — a concurrent call for the same pid won in between our
                // `TryGetValue` miss and this `TryAdd`. Reuse the winner's entry instead.
                waitPosixCore usePidfd pid

    /// Reap a POSIX child on the mechanism selected once for this process (pidfd fast path where
    /// available, else the shared SIGCHLD reaper). See `waitPosixCore`.
    let waitPosix (pid: nativeint) : Task<Outcome> = waitPosixCore true pid

    /// Test seam (internal, not public API): force the shared-SIGCHLD fallback path regardless of pidfd
    /// support, so the fallback stays covered by an explicit test even on a pidfd-capable Linux host. In
    /// production this is never called — `waitPosix` is the only entry — so the pidfd path never has the
    /// SIGCHLD registration installed alongside it.
    let waitPosixViaSigchldForTests (pid: nativeint) : Task<Outcome> = waitPosixCore false pid

    /// Best-effort synchronous reap of a POSIX child we own (a group leader, whose pid == its pgid)
    /// during teardown — it was just SIGKILLed, so it becomes a zombie within a moment. Uses a
    /// *non-blocking* `WNOHANG` wait in a short bounded loop, deliberately NOT a blocking `waitpid`:
    /// a blocking wait would (a) stall the disposing/finalizer thread indefinitely on a child wedged
    /// in uninterruptible (D-state) sleep, where SIGKILL is deferred, and (b) compete for the reap a
    /// run verb may be blocking on. A child already reaped elsewhere yields ECHILD — the harmless
    /// no-op we want. A still-wedged child is left for the OS to reap at host exit (the prior, rarer
    /// failure mode), rather than wedging teardown.
    let reapLeader (pid: int) : unit =
        let mutable status = 0
        let mutable attempts = 0
        let mutable finished = false

        // ~200 ms ceiling: the common just-SIGKILLed child is reaped in the first iteration or two;
        // the bound only matters for a wedged child, which we must not let stall teardown.
        while not finished && attempts < 200 do
            attempts <- attempts + 1
            let result = waitpid (pid, &status, WNOHANG)

            if result = 0 then
                // Still alive — SIGKILL not yet reflected; wait a brief, bounded moment and retry.
                System.Threading.Thread.Sleep 1
            elif result < 0 && Marshal.GetLastWin32Error() = EINTR then
                () // interrupted before we learned anything; loop and retry
            else
                finished <- true // reaped (result = pid), or ECHILD / other error — nothing more to do

                // If an event-driven `waitPosix` wait is still pending for this pid (an abandoned run
                // being torn down concurrently with, or just ahead of, its own exit), hand it the real
                // decoded status now that we have it, rather than leaving it to notice only `ECHILD`
                // whenever it next gets a chance to look.
                if result = pid then
                    completePending pid (decodeWaitStatus status)

    // ----------------------------------------------------------------------------------
    // POSIX privilege drop / session detach (Command.Uid / Gid / Setsid)
    // ----------------------------------------------------------------------------------
    //
    // `Setsid` is a native `posix_spawn` attribute (`POSIX_SPAWN_SETSID`, applied by `spawnPosixViaSpawn`
    // above), so it needs nothing extra here.
    //
    // A uid/gid drop, however, cannot be a `posix_spawn` attribute — `posix_spawn` has no `setuid`/
    // `setgid` hook, and dropping privileges can only happen in the child before `exec`. The obvious port
    // of the Rust source's `pre_exec` closure — `fork()` and run the drop in the forked child — is NOT
    // viable on .NET: a forked CoreCLR child that executes ANY managed code (even a single P/Invoke) can
    // fault or hang, because the runtime's GC / JIT / finalizer threads do not survive the fork yet their
    // locks and half-states are copied into the child (this was observed here as a hard test-host crash).
    // So instead of forking, a command that requests `Uid`/`Gid` spawns a small privilege-dropping HELPER
    // through the SAME safe `posix_spawn` path used by every other command: `setpriv` (util-linux) sets
    // the gid/uid and clears the supplementary groups, then `exec`s the real program IN PLACE — same pid,
    // so `Spawned.Handle`, the pgid, and kill-on-drop containment are all unchanged. Every other builder
    // composes because it is applied to the `setpriv` process and inherited across its `exec`: stdio /
    // CurrentDir via `posix_spawn` file actions, `Setsid` via `POSIX_SPAWN_SETSID`, `Umask` via the
    // parent-side set/restore, `Priority` via the post-spawn `setpriority` on the pgid leader (the nice
    // survives the `exec`), and — for the cgroup backend — the parent migrates the `setpriv` pid (which
    // IS the target's pid after `exec`) into `cgroup.procs` from the still-privileged parent, so resource
    // limits apply regardless of the child's dropped uid. This is the "helper process" option the task
    // calls out, chosen over `fork` because it is the only mechanism that is reliable on .NET.
    //
    // Availability & honesty. `setpriv` ships in util-linux, a required package on mainstream Linux
    // (Debian/Ubuntu — the CI image and GitHub runners), so the drop is real and tested there; where it
    // is absent (macOS/BSD, a minimal musl image) the spawn fails with a typed `ProcessError.Spawn` that
    // names the missing helper, never a silently un-dropped child. A drop the OS *refuses* surfaces as
    // `setpriv` exiting non-zero (carrying its own stderr); the common "not privileged" case is caught
    // up-front as `ProcessError.Spawn` by `privilegeDropPrecheck` (a non-root caller cannot change to a
    // different uid/gid).

    // `POSIX_SPAWN_SETSID` differs per libc: 0x0400 on macOS, 0x80 on Linux glibc/musl. Not a `[<Literal>]`
    // because it is resolved from `isMacOs` at load. Used by `spawnPosixViaSpawn`'s flag block above.
    let private posixSpawnSetsid = if isMacOs then 0x0400s else 0x80s

    [<DllImport("libc", SetLastError = true)>]
    extern int private geteuid()

    [<DllImport("libc", SetLastError = true)>]
    extern int private getegid()

    /// Up-front guard for a uid/gid drop: a caller that is not root (euid 0) is refused a DIFFERENT
    /// uid/gid here with an honest `ProcessError.Spawn`, before spawning the helper (rather than letting
    /// `setpriv` fail later as a non-zero exit). A no-op request — the target id already equals the
    /// current one — needs no privilege and is allowed through. `None` = clear to go.
    ///
    /// This is DELIBERATELY a coarse, root-only gate, not a capability probe. A non-root caller that
    /// holds `CAP_SETUID`/`CAP_SETGID` (a rootless container / sandbox) could in principle perform the
    /// drop, but is conservatively refused here rather than probed: the precheck exists only to turn the
    /// common not-privileged case into a clean up-front error, and `setpriv` itself remains the true
    /// arbiter of a drop the kernel actually permits (bounding set, no-new-privs, LSM/seccomp can all make
    /// a `CapEff`-positive drop still fail — the very non-zero exit this guard avoids). Root-gating keeps
    /// the check simple and testable and never ships an untested, more-permissive security path; if a
    /// real rootless-container need arises, loosening this to capability-aware is a strictly non-breaking
    /// change. The public docs (Command.Uid, CHANGELOG, docs/commands.md) state this root-only contract.
    let private privilegeDropPrecheck (command: Command) : ProcessError option =
        let config = command.Config

        if geteuid () = 0 then
            None
        else
            let euid = geteuid ()
            let egid = getegid ()

            let wantsDifferentUid =
                match config.Uid with
                | Some u -> u <> euid
                | None -> false

            let wantsDifferentGid =
                match config.Gid with
                | Some g -> g <> egid
                | None -> false

            if wantsDifferentUid || wantsDifferentGid then
                Some(ProcessError.Spawn(command.Program, "dropping to a different Uid/Gid needs root (euid 0)"))
            else
                None

    // The `setpriv` flags for the requested drop: set the gid and the uid (setpriv sequences them
    // correctly), and clear the parent's supplementary groups so the child never keeps them.
    let private setprivFlags (config: CommandConfig) : string list =
        let regid =
            match config.Gid with
            | Some g -> [ $"--regid={g}" ]
            | None -> []

        let reuid =
            match config.Uid with
            | Some u -> [ $"--reuid={u}" ]
            | None -> []

        regid @ reuid @ [ "--clear-groups" ]

    /// Rewrite `command` to run through the `setpriv` helper: `setpriv <flags> <program> <args...>`. The
    /// uid/gid are cleared on the rewritten command (so the `spawnPosix` dispatcher does not recurse), and
    /// every other knob — stdio, env, CurrentDir, Priority, Umask, Setsid — is preserved and applied by
    /// `posix_spawn` to the `setpriv` process, which inherits them across its `exec` of the real program.
    let private setprivCommand (command: Command) : Command =
        let config = command.Config
        let prefix = setprivFlags config @ [ config.Program ]

        Command(
            { config with
                Program = "setpriv"
                Args = config.Args.InsertRange(0, prefix)
                Uid = None
                Gid = None }
        )

    /// Spawn `command` into a brand-new process group (`POSIX_SPAWN_SETPGROUP`, so pgid = the
    /// child's pid) and capture its stdout/stderr. The whole group can later be reaped with
    /// `killProcessGroup`. This is the one real POSIX spawn path; `spawnPosix` routes a command that
    /// requests `Uid`/`Gid` here too, after rewriting it to run through the `setpriv` helper.
    let private spawnPosixViaSpawn (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        let stdinWanted = config.StdinSource.IsSome || config.KeepStdinOpen
        // Child-side fds the parent closes after spawn (the child gets its own via dup2).
        let childSideFds = System.Collections.Generic.List<int>()

        // Per stream: the fd the child should see at the target slot (-1 = inherit, no dup2), and
        // the fd the parent keeps as a stream (read for stdout/stderr, write for stdin).
        let mutable stdinChildFd = -1
        let mutable stdinParentWrite: int option = None
        let mutable stdoutChildFd = -1
        let mutable stdoutParentRead: int option = None
        let mutable stderrChildFd = -1
        let mutable stderrParentRead: int option = None
        let mutable failure: string option = None

        // A failed open("/dev/null") (fd < 0, e.g. EMFILE/ENFILE) must fail the spawn honestly,
        // exactly like a failed socketpair() below (`makeStdioChannel`) already does — NOT leave the slot's
        // *ChildFd at -1, which `posix_spawn_file_actions_adddup2` below reads as "no dup2 —
        // inherit the parent's stream", the silent downgrade this guards against (for stdin,
        // handing the child the parent's real stdin/terminal instead of an immediate EOF).
        let openNul (label: string) (flags: int) =
            let fd = openDevNull flags

            if fd >= 0 then
                childSideFds.Add fd
            else
                let errno = Marshal.GetLastWin32Error()
                failure <- Some $"open(/dev/null) failed for {label} (errno {errno})"

            fd

        // Both socketpair ends are bidirectional and interchangeable; the tuple keeps the pipe-era
        // (readFd, writeFd) = (fds[0], fds[1]) shape purely so the per-stream role assignment below
        // (child-read/parent-write for stdin, parent-read/child-write for stdout+stderr) reads the same.
        let makeStdioChannel (label: string) =
            let fds = Array.zeroCreate<int> 2

            if createSocketPair fds <> 0 then
                failure <- Some $"socketpair() failed for {label}"
                None
            else
                Some(fds[0], fds[1])

        // stdin
        if stdinWanted then
            match makeStdioChannel "stdin" with
            | Some(readFd, writeFd) ->
                stdinChildFd <- readFd
                childSideFds.Add readFd
                stdinParentWrite <- Some writeFd
            | None -> ()
        else
            stdinChildFd <- openNul "stdin" O_RDONLY

        // stdout
        if failure.IsNone then
            match config.StdoutMode with
            | StdioMode.Piped ->
                match makeStdioChannel "stdout" with
                | Some(readFd, writeFd) ->
                    stdoutParentRead <- Some readFd
                    stdoutChildFd <- writeFd
                    childSideFds.Add writeFd
                | None -> ()
            | StdioMode.Null -> stdoutChildFd <- openNul "stdout" O_WRONLY
            | StdioMode.Inherit ->
                // Inherit the parent's stdout. On macOS, POSIX_SPAWN_CLOEXEC_DEFAULT closes every fd
                // not named by a file action at exec, so register a self-dup2 (1 -> 1) to keep fd 1
                // open in the child; on Linux the fd is inherited naturally, so no dup2 is needed.
                stdoutChildFd <- if isMacOs then 1 else -1

        // stderr
        if failure.IsNone then
            if config.MergeStderr then
                // `Command.MergeStderr` (2>&1): route the child's stderr at the SAME destination as its
                // stdout, at the OS level, by dup2'ing fd 2 onto stdout's child-side fd below — so both
                // fd 1 and fd 2 reference the one stdout target (the pipe write end, the /dev/null fd, or
                // the inherited fd 1) and the child's stdout+stderr interleave honestly on the single
                // stdout stream. There is NO separate stderr channel: `stderrParentRead` stays `None`
                // (`Spawned.Stderr = None`). Reuse `stdoutChildFd` WITHOUT re-adding it to `childSideFds`
                // (it is registered and closed exactly once by the stdout branch above); for an inherited
                // stdout on Linux (`stdoutChildFd = -1`, no dup2) point fd 2 at fd 1 explicitly so stderr
                // still follows the inherited stdout.
                stderrChildFd <- if stdoutChildFd >= 0 then stdoutChildFd else 1
            else
                match config.StderrMode with
                | StdioMode.Piped ->
                    match makeStdioChannel "stderr" with
                    | Some(readFd, writeFd) ->
                        stderrParentRead <- Some readFd
                        stderrChildFd <- writeFd
                        childSideFds.Add writeFd
                    | None -> ()
                | StdioMode.Null -> stderrChildFd <- openNul "stderr" O_WRONLY
                | StdioMode.Inherit ->
                    // See the stdout note: a self-dup2 (2 -> 2) keeps fd 2 alive under macOS
                    // POSIX_SPAWN_CLOEXEC_DEFAULT; Linux inherits it without a file action.
                    stderrChildFd <- if isMacOs then 2 else -1

        let closeFd fd = close fd |> ignore

        let closeParentEnds () =
            stdinParentWrite |> Option.iter closeFd
            stdoutParentRead |> Option.iter closeFd
            stderrParentRead |> Option.iter closeFd

        match failure with
        | Some message ->
            for fd in childSideFds do
                closeFd fd

            closeParentEnds ()
            Error(ProcessError.Spawn(command.Program, message))
        | None ->
            // Native scratch for the opaque posix_spawn_file_actions_t / posix_spawnattr_t (a generous
            // zeroed buffer holds either platform's representation — glibc structs or macOS pointers)
            // plus the marshalled argv/envp. Held in mutables seeded with a null/empty sentinel so the
            // `finally` and the exception handler below release exactly what was actually allocated: an
            // OOM in `AllocHGlobal`/`marshalCStringArray`, or any unexpected native fault, must free the
            // earlier allocations and close any fds already opened, never escape this Result-returning
            // function (symmetric to `spawnWindowsCore`'s unwind list in Native.Windows).
            let mutable fileActions = IntPtr.Zero
            let mutable attributes = IntPtr.Zero
            let mutable fileActionsReady = false
            let mutable attributesReady = false
            let mutable argvPointer = IntPtr.Zero
            let mutable argvAllocations: nativeint list = []
            let mutable envpPointer = IntPtr.Zero
            let mutable envpAllocations: nativeint list = []

            // The parent closes the child-side fds right after the spawn attempt. This flag lets the
            // error/exception paths close them if an exception fired before that point, without
            // double-closing them once the normal flow already has (an fd number can be reused after
            // close, so a stray second `close` could hit an unrelated fd).
            let mutable childFdsOpen = true

            let closeChildSideFds () =
                if childFdsOpen then
                    for fd in childSideFds do
                        closeFd fd

                    childFdsOpen <- false

            try
                try
                    fileActions <- Marshal.AllocHGlobal 1024
                    attributes <- Marshal.AllocHGlobal 1024
                    let argv = command.Program :: List.ofSeq command.Config.Args

                    let envp =
                        effectiveEnvironment command
                        |> Seq.map (fun entry -> $"{entry.Key}={entry.Value}")
                        |> List.ofSeq

                    let argvArray, argvStrings = marshalCStringArray argv
                    argvPointer <- argvArray
                    argvAllocations <- argvStrings
                    let envpArray, envpStrings = marshalCStringArray envp
                    envpPointer <- envpArray
                    envpAllocations <- envpStrings

                    // Every posix_spawn_file_actions_* / posix_spawnattr_* helper returns an errno-style
                    // rc; a non-zero one is an honest spawn failure (a mis-wired stdio dup2/close, or a
                    // failed attr set that would silently drop SETPGROUP/CLOEXEC), NOT something to
                    // `|> ignore`. Record the FIRST failure and stop invoking further helpers — never
                    // operate a helper on a half-initialized struct.
                    let mutable err: ProcessError option = None

                    let register (helper: string) (call: unit -> int) =
                        if err.IsNone then
                            let rc = call ()

                            if rc <> 0 then
                                err <- Some(ProcessError.Spawn(command.Program, $"{helper} failed (rc {rc})"))

                    register "posix_spawn_file_actions_init" (fun () -> posix_spawn_file_actions_init fileActions)

                    // Only a successfully initialized struct may be destroyed (POSIX leaves a failed
                    // init's struct undefined) — the `finally` keys its destroy calls off these flags.
                    if err.IsNone then
                        fileActionsReady <- true

                    register "posix_spawnattr_init" (fun () -> posix_spawnattr_init attributes)

                    if err.IsNone then
                        attributesReady <- true

                    if stdinChildFd >= 0 then
                        register "posix_spawn_file_actions_adddup2 (stdin)" (fun () ->
                            posix_spawn_file_actions_adddup2 (fileActions, stdinChildFd, 0))

                    if stdoutChildFd >= 0 then
                        register "posix_spawn_file_actions_adddup2 (stdout)" (fun () ->
                            posix_spawn_file_actions_adddup2 (fileActions, stdoutChildFd, 1))

                    if stderrChildFd >= 0 then
                        register "posix_spawn_file_actions_adddup2 (stderr)" (fun () ->
                            posix_spawn_file_actions_adddup2 (fileActions, stderrChildFd, 2))

                    // After dup2, close the original child-side fds so only 0/1/2 remain in the child.
                    for fd in childSideFds do
                        register "posix_spawn_file_actions_addclose (child-side fd)" (fun () ->
                            posix_spawn_file_actions_addclose (fileActions, fd))

                    // Also close the parent-kept ends in the child. This is what guarantees EOF: a
                    // child must never inherit a writer to its own stdin. We do it explicitly rather
                    // than rely on FD_CLOEXEC, whose `fcntl` is variadic and is mis-passed by a
                    // fixed-signature P/Invoke on the AArch64 variadic ABI (Apple Silicon), so CLOEXEC
                    // never takes effect there and the child would block forever waiting for stdin.
                    stdinParentWrite
                    |> Option.iter (fun fd ->
                        register "posix_spawn_file_actions_addclose (stdin parent end)" (fun () ->
                            posix_spawn_file_actions_addclose (fileActions, fd)))

                    stdoutParentRead
                    |> Option.iter (fun fd ->
                        register "posix_spawn_file_actions_addclose (stdout parent end)" (fun () ->
                            posix_spawn_file_actions_addclose (fileActions, fd)))

                    stderrParentRead
                    |> Option.iter (fun fd ->
                        register "posix_spawn_file_actions_addclose (stderr parent end)" (fun () ->
                            posix_spawn_file_actions_addclose (fileActions, fd)))

                    // CurrentDir → a child-side chdir. A non-zero rc from addchdir_np is an honest spawn
                    // error; its ABSENCE (EntryPointNotFoundException — the entry point arrived in glibc
                    // 2.29 / macOS 10.15) means this platform cannot honor CurrentDir at all. We report
                    // that as a typed `Unsupported` rather than silently running the child in the PARENT's
                    // working directory (a silent CurrentDir downgrade — the exact bug this fixes). Chosen
                    // over folding it into `Spawn` because it is a fixed platform capability gap, not a
                    // per-invocation failure — mirroring the port's other Unix-only gates (e.g. Windows
                    // `umask` → `Unsupported`), so a caller can branch on it with `err.IsUnsupported`.
                    match config.WorkingDirectory with
                    | Some directory when err.IsNone ->
                        try
                            let rc = posix_spawn_file_actions_addchdir_np (fileActions, directory)

                            if rc <> 0 then
                                err <-
                                    Some(
                                        ProcessError.Spawn(
                                            command.Program,
                                            $"posix_spawn_file_actions_addchdir_np failed for CurrentDir (rc {rc})"
                                        )
                                    )
                        with :? EntryPointNotFoundException ->
                            // libc predates posix_spawn_file_actions_addchdir_np; CurrentDir genuinely
                            // cannot be applied here. Honest typed failure, never a silent parent-cwd run.
                            err <-
                                Some(
                                    ProcessError.Unsupported
                                        "CurrentDir on this platform (needs posix_spawn_file_actions_addchdir_np: glibc >= 2.29 or macOS >= 10.15)"
                                )
                    | _ -> ()

                    // `Command.Setsid` detaches the child into a new session via `POSIX_SPAWN_SETSID`
                    // INSTEAD of forming a new process group with `POSIX_SPAWN_SETPGROUP`. This is the
                    // deliberate coordination with the pgid containment model: `setsid()` already makes
                    // the child a session AND new-process-group leader (pgid == pid == sid), so the two
                    // flags are mutually exclusive — combining them (or leaving `SETPGROUP` on) would set
                    // a pgroup the kernel then overrides. `Spawned.Handle` is still the child pid, and the
                    // pgid equals it either way, so `killProcessGroup` (killpg on the pid) reaps the whole
                    // session-group in teardown exactly as it does for a `SETPGROUP` child — containment is
                    // preserved; the only difference is the detached session and dropped controlling tty.
                    let groupFlag =
                        if config.Setsid then
                            posixSpawnSetsid
                        else
                            POSIX_SPAWN_SETPGROUP

                    let spawnFlags =
                        if isMacOs then
                            groupFlag ||| POSIX_SPAWN_CLOEXEC_DEFAULT
                        else
                            groupFlag

                    register "posix_spawnattr_setflags" (fun () -> posix_spawnattr_setflags (attributes, spawnFlags))

                    // Only set an explicit pgroup when NOT detaching a session; `POSIX_SPAWN_SETSID`
                    // establishes the child's own group itself, and a `setpgroup` alongside it is
                    // redundant (and rejected on some libc).
                    if not config.Setsid then
                        register "posix_spawnattr_setpgroup" (fun () -> posix_spawnattr_setpgroup (attributes, 0))

                    match err with
                    | Some error ->
                        // A helper failed (or CurrentDir is unsupported here): nothing has been spawned.
                        // Close the child-side fds and the parent-kept ends before reporting — the
                        // `finally` still frees the native buffers/marshalling on this same path.
                        closeChildSideFds ()
                        closeParentEnds ()
                        Error error
                    | None ->
                        // Do the actual `posix_spawnp` (returning its rc and the pid it wrote) under
                        // `umaskSpawnLock`. `localPid` is declared INSIDE the closure so `&localPid` is a
                        // plain addressable local, not a captured mutable (which F# forbids taking the
                        // address of).
                        let spawnUnderLock () =
                            let mutable localPid = 0

                            let rc =
                                posix_spawnp (
                                    &localPid,
                                    command.Program,
                                    fileActions,
                                    attributes,
                                    argvPointer,
                                    envpPointer
                                )

                            rc, localPid

                        // umask(2) is a whole-process attribute with no `posix_spawn` attribute, so a
                        // requested mask is set on the parent right before the spawn and restored right
                        // after — and EVERY spawn (mask or not) takes `umaskSpawnLock`, so a concurrent
                        // no-mask spawn can never observe another spawn's temporarily-set umask. See
                        // `umaskSpawnLock` for why this parent-side set/spawn/restore replaces the Rust
                        // source's child-side `pre_exec` hook.
                        let rc, pid =
                            lock umaskSpawnLock (fun () ->
                                match config.Umask with
                                | None -> spawnUnderLock ()
                                | Some mask ->
                                    let previous = umask mask

                                    try
                                        spawnUnderLock ()
                                    finally
                                        umask previous |> ignore)

                        // The parent never needs the child-side fds.
                        closeChildSideFds ()

                        if rc <> 0 then
                            closeParentEnds ()

                            if rc = ENOENT then
                                Error(ProcessError.NotFound(command.Program, None))
                            else
                                Error(ProcessError.Spawn(command.Program, $"posix_spawn failed ({rc})"))
                        else
                            // Apply the requested CPU priority to the freshly spawned leader (pgid = its pid).
                            // `posix_spawn` has no nice attribute, so this is a post-spawn `setpriority` from the
                            // parent; the nice inherits across `fork`, so every descendant the leader spawns runs
                            // at it (whole-tree, modulo the sub-millisecond window before this call lands — see
                            // `Priority`). Lowering nice (raising priority) can be refused for lack of privilege:
                            // rather than silently running the child at a lower-than-requested priority, kill and
                            // reap it and fail the spawn honestly — matching the contract that priority is never
                            // downgraded silently.
                            let priorityApplied =
                                match config.Priority with
                                | None -> Ok()
                                | Some priority ->
                                    if setpriority (PRIO_PROCESS, pid, PriorityMapping.niceValue priority) = 0 then
                                        Ok()
                                    else
                                        let errno = Marshal.GetLastWin32Error()

                                        Error(
                                            ProcessError.Spawn(
                                                command.Program,
                                                $"could not set process priority via setpriority (errno {errno}); raising priority may require privilege (CAP_SYS_NICE)"
                                            )
                                        )

                            match priorityApplied with
                            | Error error ->
                                // The child is already running but must not run at an unintended priority: killpg
                                // its group (it is its own leader) and reap the leader, then drop the parent pipe
                                // ends before reporting the failure — the same kill+reap+cleanup a failed spawn does.
                                killProcessGroup pid
                                reapLeader pid
                                closeParentEnds ()
                                Error error
                            | Ok() ->
                                // Wrap a retained parent-side socketpair end in a `Socket` +
                                // `NetworkStream`: its ReadAsync/WriteAsync complete through the runtime's
                                // epoll/kqueue `SocketAsyncEngine`, so a piped POSIX child no longer parks a
                                // thread-pool thread in a blocking `FileStream` read/write for the stream's
                                // whole lifetime (the linear thread-pool pressure this closes). `ownsHandle`/
                                // `ownsSocket` give the stream sole ownership, so disposing it closes the fd
                                // exactly once, with the SafeSocketHandle finalizer as the GC-time safety net
                                // — the same single-owner contract the FileStream + SafeFileHandle had. A
                                // socketpair end is bidirectional, but each stream is only ever read
                                // (stdout/stderr) xor written (stdin) by its one consumer.
                                let pipeStream fd =
                                    let socket = new Socket(new SafeSocketHandle(nativeint fd, ownsHandle = true))

                                    new NetworkStream(socket, ownsSocket = true) :> Stream

                                // Wrap each retained parent end into an owning stream, clearing its fd
                                // tracker as ownership transfers so that if a later step (or the outer
                                // handler) runs, it never double-closes an fd the stream now owns.
                                let stdoutStream =
                                    match stdoutParentRead with
                                    | Some fd ->
                                        let stream = pipeStream fd
                                        stdoutParentRead <- None
                                        Some stream
                                    | None -> None

                                let stderrStream =
                                    match stderrParentRead with
                                    | Some fd ->
                                        let stream = pipeStream fd
                                        stderrParentRead <- None
                                        Some stream
                                    | None -> None

                                let stdinStream =
                                    match stdinParentWrite with
                                    | Some fd ->
                                        let stream = pipeStream fd
                                        stdinParentWrite <- None
                                        Some stream
                                    | None -> None

                                Ok
                                    { Handle = nativeint pid
                                      Stdout = stdoutStream
                                      Stderr = stderrStream
                                      Stdin = stdinStream
                                      // POSIX signals the child's process group directly (killpg); the
                                      // Windows console-ctrl-group flag has no bearing here.
                                      WindowsCtrlGroup = false }
                finally
                    // Release the native scratch on every path (success, honest helper failure, or an
                    // exception unwinding through here) — destroying only the structs that initialized
                    // cleanly and freeing only what was actually allocated (a null/empty sentinel is a
                    // documented no-op for FreeHGlobal / FreeCoTaskMem, so partial allocation is safe).
                    if fileActionsReady then
                        posix_spawn_file_actions_destroy fileActions |> ignore

                    if attributesReady then
                        posix_spawnattr_destroy attributes |> ignore

                    if fileActions <> IntPtr.Zero then
                        Marshal.FreeHGlobal fileActions

                    if attributes <> IntPtr.Zero then
                        Marshal.FreeHGlobal attributes

                    freeCStringArray argvPointer argvAllocations
                    freeCStringArray envpPointer envpAllocations
            with ex ->
                // An OOM in AllocHGlobal/marshalCStringArray, or an EntryPointNotFoundException from any
                // other posix_spawn* import, must not escape this Result-returning function: close
                // whatever fds are still open and report an honest Spawn error (symmetric to
                // `spawnWindowsCore`'s `with ex -> ...`). The `finally` above has already released the
                // native scratch on this same unwind.
                closeChildSideFds ()
                closeParentEnds ()
                Error(ProcessError.Spawn(command.Program, ex.Message))

    /// Spawn `command` as a contained POSIX child. A command requesting a privilege drop (`Uid`/`Gid`) is
    /// rewritten to run through the `setpriv` helper and spawned on the ordinary `posix_spawn` path — the
    /// drop runs in `setpriv` before it `exec`s the real program, so no managed code runs in a forked
    /// child (see the section comment above). Everything else — including a lone `Setsid` — spawns
    /// directly. A refused drop by a non-root caller is rejected up front; a missing `setpriv` helper
    /// becomes a typed `ProcessError.Spawn`, never a silently un-dropped child.
    let spawnPosix (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config

        if config.Uid.IsSome || config.Gid.IsSome then
            match privilegeDropPrecheck command with
            | Some error -> Error error
            | None ->
                match spawnPosixViaSpawn (setprivCommand command) with
                | Error(ProcessError.NotFound _) ->
                    // `posix_spawn` could not find `setpriv` on PATH — report it against the ORIGINAL
                    // program (not the helper), so a caller who never mentioned `setpriv` gets a message
                    // that explains what a `Uid`/`Gid` drop needs on this host.
                    Error(
                        ProcessError.Spawn(
                            command.Program,
                            "a Uid/Gid privilege drop needs the 'setpriv' helper (util-linux) on PATH; it was not found (available on mainstream Linux; absent on macOS/BSD)"
                        )
                    )
                | other -> other
        else
            spawnPosixViaSpawn command
