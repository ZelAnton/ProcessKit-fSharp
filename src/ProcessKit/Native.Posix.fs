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

    // Read/write, and "do not acquire this terminal as MY controlling tty on open" — used opening a pty
    // master/slave pair (`openPtyPair`): the master is O_RDWR (the single bidirectional merged stream),
    // and both ends carry O_NOCTTY so the PARENT never accidentally adopts the slave as its controlling
    // terminal (the CHILD acquires it deliberately, via `setsid --ctty`). Same numeric values on Linux.
    [<Literal>]
    let private O_RDWR = 2

    [<Literal>]
    let private O_NOCTTY = 0x100

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

    // `killpg(pgid, 0)` reports this only when the target process group no longer exists. A failed
    // probe with EPERM instead proves that the group exists but the caller may not signal it.
    [<Literal>]
    let private ESRCH = 3

    // errno EINVAL — "invalid argument", the kernel's verdict on an out-of-range signal *number*. Used
    // for the synthetic classification of a non-deliverable number (signal 0 / a negative) that
    // `signalProcessGroup` refuses *before* the syscall, so a probe never masquerades as a delivered
    // signal. Signal 0 is not itself an EINVAL to `killpg` (it is a liveness probe that returns success),
    // which is exactly why it must be refused here rather than run.
    [<Literal>]
    let private EINVAL = 22

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

    // POSIX pty (pseudo-terminal) master/slave allocation for `Command.Pty` (see `openPtyPair`).
    // `posix_openpt`/`grantpt`/`unlockpt`/`ptsname` are ALL in libc — portable across glibc versions and
    // back to old glibc where `openpty(3)` lived only in `libutil` — and `posix_openpt` accepts O_CLOEXEC,
    // so the master fd is close-on-exec ATOMICALLY (no `fcntl`, which this port avoids on the AArch64
    // variadic ABI). That is the pty analogue of the socketpair's SOCK_CLOEXEC, and the exact reason the
    // PTY ADR ratifies `posix_openpt` as the mechanism a bare `openpty` cannot match here.
    [<DllImport("libc", SetLastError = true)>]
    extern int private posix_openpt(int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int private grantpt(int fd)

    [<DllImport("libc", SetLastError = true)>]
    extern int private unlockpt(int fd)

    // Returns a pointer to a static buffer holding the slave device path (e.g. "/dev/pts/3"); marshalled
    // with `Marshal.PtrToStringAnsi`. NULL on failure.
    [<DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern nativeint private ptsname(int fd)

    // `struct winsize` — the terminal window size the kernel keeps per pty pair. Only the row/column
    // fields are meaningful to a program reading TIOCGWINSZ; the pixel fields are unused and left 0.
    // Sequential layout is the C ABI (four `unsigned short`s, 8 bytes), passed by-ref to `ioctl`.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private Winsize =
        val mutable Row: uint16
        val mutable Col: uint16
        val mutable XPixel: uint16
        val mutable YPixel: uint16

    // ioctl(master, TIOCSWINSZ, &winsize) applies the INITIAL terminal geometry (PtyConfig.Cols/Rows) to
    // a freshly opened pty, so a full-screen TUI (vim/top/less) sees the requested size instead of the
    // kernel's 0x0 default — parity with the Windows CreatePseudoConsole geometry, never a silent
    // cross-platform downgrade of a validated user field. `ioctl` is variadic in C, but the pty path is
    // Linux-only (the ctty helper is util-linux `setsid --ctty`; macOS/BSD take the honest Unsupported
    // path BEFORE any pty is opened), and on Linux the AArch64 ABI passes this single fixed pointer
    // argument in a register exactly like a named arg — so a fixed-signature P/Invoke is correct here and
    // the macOS variadic-ABI hazard that bans `fcntl` never applies. `request` is C `unsigned long`
    // (`unativeint`); TIOCSWINSZ is 0x5414 across the Linux architectures this port targets
    // (x86/x86_64/arm/arm64 share the asm-generic ioctl numbers).
    [<Literal>]
    let private TIOCSWINSZ = 0x5414

    [<DllImport("libc", SetLastError = true)>]
    extern int private ioctl(int fd, unativeint request, Winsize& winsize)

    // termios (line-discipline) get/set for `PtyConfig.Echo`. The whole `struct termios` is blitted
    // through an opaque unmanaged buffer (a raw `nativeint`), NOT a modelled struct: that way every field
    // — c_cc[NCCS] control chars, the input/output speeds — round-trips from `tcgetattr` back to
    // `tcsetattr` unchanged, and only `c_lflag` (the 4th 32-bit flag word, offset 12 on Linux) is edited
    // to clear the ECHO bit. Modelling the struct partially (an explicit `Size` over-pad, or a half-filled
    // field list) would let the marshaller zero the untouched tail on the write-back and corrupt the
    // terminal's control chars/baud — so the buffer is passed verbatim. POSIX pty is Linux-only here (the
    // ctty helper is util-linux `setsid --ctty`; macOS/BSD take the honest Unsupported path before any pty
    // opens), so the Linux `c_lflag` offset and `ECHO`/`TCSANOW` values apply.
    [<DllImport("libc", SetLastError = true)>]
    extern int private tcgetattr(int fd, nativeint termios)

    [<DllImport("libc", SetLastError = true)>]
    extern int private tcsetattr(int fd, int optionalActions, nativeint termios)

    // termios c_lflag ECHO bit (Linux): a terminal echoes typed input back into its OUTPUT by default
    // (cooked mode). Cleared for `PtyConfig.Echo = false` so a credential fed to the child's stdin through
    // the pty is not echoed into the captured merged stream — the ADR's secret-safety mitigation.
    [<Literal>]
    let private ECHO = 0x8

    // Byte offset of `c_lflag` inside `struct termios` (the 4th `tcflag_t`, each a 4-byte `unsigned int`).
    [<Literal>]
    let private TERMIOS_LFLAG_OFFSET = 12

    // tcsetattr optional_actions: apply the change immediately (POSIX `TCSANOW` = 0 on Linux).
    [<Literal>]
    let private TCSANOW = 0

    // SIGWINCH — "window size changed" (Linux value 28, shared across the arches this port targets).
    // Delivered to the pty child after a resize so a running TUI re-queries its geometry (ADR D6).
    [<Literal>]
    let private SIGWINCH = 28

    // read/write on a pty master fd. Unlike a socketpair end (a `Socket`/`NetworkStream` on the runtime's
    // epoll/kqueue engine), a pty master is a character device that engine cannot host, so `PtyStream`
    // does its I/O with these directly (offloaded to the thread pool for async by the base `Stream`).
    // `nativeint` is `ssize_t` — a negative return signals an error whose errno decides EOF vs a fault.
    [<DllImport("libc", SetLastError = true, EntryPoint = "read")>]
    extern nativeint private ptyReadRaw(int fd, nativeint buffer, nativeint count)

    [<DllImport("libc", SetLastError = true, EntryPoint = "write")>]
    extern nativeint private ptyWriteRaw(int fd, nativeint buffer, nativeint count)

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

    // ----------------------------------------------------------------------------------
    // Fault-injection seams (internal, test-only). Production leaves all three `None` and runs the real
    // path; sequential tests set one, run a single spawn, and reset it in a `finally`. Each makes a
    // specific step of the spawn throw or fail on demand — an OOM part-way through marshalling
    // argv/envp, a privilege-refused `setpriority`, or a `Socket`/`NetworkStream` ctor failure while
    // wrapping a retained parent-side stream — none of which can be provoked reliably on a healthy host,
    // so the leak-free unwind (free the partially built argv/envp; kill + reap the just-spawned child;
    // close every parent/child fd exactly once) stays covered by explicit tests. Mirrors the existing
    // `resumeThreadHook` fault seam in Native.Windows.
    // ----------------------------------------------------------------------------------

    /// Test seam (internal, not public API): invoked before each unmanaged-string allocation inside
    /// `marshalCStringArray`, with the number already allocated. A test throws once at least one has been
    /// allocated, to exercise the partial-free path (every earlier block freed before the throw escapes).
    let mutable marshalCStringFaultForTests: (int -> unit) option = None

    /// Test seam (internal, not public API): overrides the post-spawn `setpriority` when set, so a test
    /// can force its privilege-refused (non-zero) return deterministically — regardless of whether it
    /// runs as root — and exercise the kill + reap of the just-spawned leader on a priority failure.
    /// Arguments mirror `setpriority(which, who, prio)`.
    let mutable setpriorityForTests: (int -> int -> int -> int) option = None

    /// Test seam (internal, not public API): invoked when wrapping a retained parent-side fd
    /// ("stdout"/"stderr"/"stdin") into its owning `Socket`/`NetworkStream`. A test throws for a chosen
    /// slot to exercise the post-spawn kill + reap and the exactly-once release of every parent/child fd
    /// when a stream ctor fails.
    let mutable streamWrapFaultForTests: (string -> unit) option = None

    /// Test seam (internal, not public API): overrides the by-number process-group liveness probe
    /// (`killpg(pgid, 0)`), so a pid/pgid-reuse scenario — a recycled number that still probes alive —
    /// can be driven deterministically without racing a real OS pid recycle. Production leaves it `None`
    /// and runs the real probe.
    let mutable processGroupAliveForTests: (int -> bool) option = None

    /// Test seam (internal, not public API): overrides `readProcessIdentity`, so a test can present a
    /// captured-then-changed start-time token deterministically (the recycled-number case) rather than a
    /// real process whose start time it cannot control. Production leaves it `None`.
    let mutable readProcessIdentityForTests: (int -> uint64 option) option = None

    /// Test seam (internal, not public API): invoked with the target pgid/pid by every process-group
    /// delivery primitive (`killProcessGroup` / `terminateProcessGroup` / `signalProcessGroup` /
    /// `suspendProcessGroup` / `resumeProcessGroup`) and the per-child raw kill (`killProcess`, the cgroup
    /// backend's `KillChild`) just before its syscall, so a test can record which pgids/pids a path
    /// actually delivered to — proving a recycled number is pruned and NEVER signalled/killed, and a
    /// matching one still is. Production leaves it `None`.
    let mutable groupDeliveryObserverForTests: (int -> unit) option = None

    /// Test seam (internal, not public API): overrides the `setsid --ctty` controlling-terminal helper
    /// availability probe, so a test can force the "no ctty helper" host (macOS/BSD, an old util-linux)
    /// deterministically ON a host that DOES carry `setsid` — exercising the honest typed
    /// `ProcessError.Unsupported` a PTY returns where a controlling terminal cannot be established (D9),
    /// without a socketpair ever pretending to be a tty. Production leaves it `None` and runs the real
    /// `resolveProgram "setsid"` PATH probe.
    let mutable ptyCttyHelperAvailableForTests: (unit -> bool) option = None

    // Fire the group-delivery test observer (if installed) with the target pgid BEFORE the syscall, so a
    // test can record exactly which pgids a signal/kill path delivered to — and so it can never perturb
    // the errno a delivery classification reads immediately after the call. A single `Option` check in
    // production (observer `None`).
    let private observeGroupDelivery (pgid: int) =
        groupDeliveryObserverForTests |> Option.iter (fun observe -> observe pgid)

    /// Marshal a list of strings into a NULL-terminated `char* []`. Returns the array pointer and the
    /// individual string allocations to free afterwards (via `freeCStringArray`).
    ///
    /// Exception/OOM-safe: the per-string `StringToCoTaskMemUTF8` allocations and the pointer-array
    /// `AllocHGlobal` are made one at a time, and if ANY of them throws — an `OutOfMemoryException`
    /// part-way through, or the `marshalCStringFaultForTests` seam — every block allocated so far is
    /// freed before the exception propagates. The invariant is therefore total: either it returns a
    /// fully built (array, pointers) the caller owns and must free, or it throws having freed everything
    /// it allocated — a partially built argv/envp never strands unmanaged memory (the leak this closes).
    let private marshalCStringArray (items: string list) : nativeint * nativeint list =
        let stringPointers = System.Collections.Generic.List<nativeint>()
        let mutable array = IntPtr.Zero

        try
            for item in items do
                marshalCStringFaultForTests
                |> Option.iter (fun fault -> fault stringPointers.Count)

                stringPointers.Add(Marshal.StringToCoTaskMemUTF8 item)

            array <- Marshal.AllocHGlobal((stringPointers.Count + 1) * IntPtr.Size)

            for i in 0 .. stringPointers.Count - 1 do
                Marshal.WriteIntPtr(array, i * IntPtr.Size, stringPointers[i])

            Marshal.WriteIntPtr(array, stringPointers.Count * IntPtr.Size, IntPtr.Zero)
            array, List.ofSeq stringPointers
        with _ ->
            // Free every block allocated so far before re-raising, so a failure mid-build (an OOM or the
            // test fault seam) leaks nothing — the pointer array itself only if it was already allocated.
            // Not a swallowing handler: `reraise ()` propagates the original fault to the caller, which
            // reports it as an honest `ProcessError.Spawn`.
            for pointer in stringPointers do
                Marshal.FreeCoTaskMem pointer

            if array <> IntPtr.Zero then
                Marshal.FreeHGlobal array

            reraise ()

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
    let killProcessGroup (pgid: int) =
        observeGroupDelivery pgid
        killpg (pgid, SIGKILL) |> ignore

    /// Ask an entire POSIX process group to terminate gracefully (SIGTERM).
    let terminateProcessGroup (pgid: int) =
        observeGroupDelivery pgid
        killpg (pgid, SIGTERM) |> ignore

    /// True while any process remains in the group (signal 0 probes existence). `ESRCH` means the group
    /// is gone; `EPERM` and unknown probe failures conservatively keep it tracked because they do not
    /// prove its absence. The test seam overrides the by-number verdict so liveness outcomes can be
    /// driven deterministically.
    let processGroupAlive (pgid: int) =
        match processGroupAliveForTests with
        | Some hook -> hook pgid
        | None ->
            let returnCode = killpg (pgid, 0)

            if returnCode = 0 then
                true
            else
                // Read errno before another P/Invoke can overwrite the per-thread value. Unlike ESRCH,
                // EPERM proves the group exists; unknown failures are likewise not evidence it is gone.
                Marshal.GetLastWin32Error() <> ESRCH

    // ----------------------------------------------------------------------------------
    // Start-time identity token: pid/pgid-reuse safety (T-084)
    // ----------------------------------------------------------------------------------
    //
    // `killpg(pgid, 0)` / `kill(pid, 0)` probe a NUMBER, not a specific process: once a tracked group
    // drains (or a solo child is reaped) the OS can recycle the number for an unrelated process, and a
    // by-number liveness probe then reports the recycled number "alive" with no intervening `ESRCH` to
    // catch it. Signalling / killing on that verdict would deliver to the stranger — a wrong-target kill
    // that breaks the kill-on-drop tree guarantee. Bind each tracked id to a stable start-time identity
    // token captured at track time and re-read on every probe: a live number whose CURRENT identity
    // differs from the captured one is a recycled stranger (`processGroupStillTracked` reports it gone,
    // so callers prune it and never signal it). An unknown token on either side — a non-Linux/macOS
    // POSIX with no reader, a process already gone, or a leader reaped while descendants keep the pgid
    // alive — defers to the by-number liveness verdict, so no platform loses coverage.

    // proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, buffer, buffersize) fills a `proc_bsdinfo` (macOS/Apple
    // only) whose pbi_start_tvsec/pbi_start_tvusec is the process creation time (stable across `exec`,
    // distinct for a recycled pid). Marshalled by raw offset (pbi_start_tvsec at 120, pbi_start_tvusec
    // at 128 in the 136-byte struct) rather than a `[<StructLayout>]` type. Lives in libproc, not libc.
    [<Literal>]
    let private PROC_PIDTBSDINFO = 3

    [<Literal>]
    let private procBsdInfoSize = 136

    [<DllImport("libproc", SetLastError = true)>]
    extern int private proc_pidinfo(int pid, int flavor, uint64 arg, nativeint buffer, int buffersize)

    /// Parse the start-time identity from a `/proc/<pid>/stat` line: field 22 (starttime, in clock ticks
    /// since boot). The comm field (2) may contain spaces and ')', so parse AFTER the final ')' — past
    /// it, the whitespace-split index 0 is field 3 (state), so starttime (field 22) is index 19.
    /// Internal (not private) so the parsing is unit-testable with synthetic `stat` lines.
    let parseLinuxStartTime (stat: string) : uint64 option =
        let closeParen = stat.LastIndexOf ')'

        if closeParen < 0 then
            None
        else
            let fields =
                stat.Substring(closeParen + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

            if fields.Length > 19 then
                match UInt64.TryParse fields.[19] with
                | true, value -> Some value
                | _ -> None
            else
                None

    let private readLinuxStartTime (pid: int) : uint64 option =
        try
            parseLinuxStartTime (File.ReadAllText $"/proc/{pid}/stat")
        with
        | :? IOException ->
            // The /proc entry vanished (the process is gone) or could not be read — no usable token;
            // the caller then defers to the by-number liveness verdict.
            None
        | :? UnauthorizedAccessException ->
            // Denied by permissions — likewise no usable token; defer to the liveness verdict.
            None

    let private readMacStartTime (pid: int) : uint64 option =
        let buffer = Marshal.AllocHGlobal procBsdInfoSize

        try
            let got = proc_pidinfo (pid, PROC_PIDTBSDINFO, 0UL, buffer, procBsdInfoSize)

            if got = procBsdInfoSize then
                // pbi_start_tvsec @120, pbi_start_tvusec @128; fold into microseconds since the epoch.
                let tvsec = uint64 (Marshal.ReadInt64(buffer, 120))
                let tvusec = uint64 (Marshal.ReadInt64(buffer, 128))
                Some(tvsec * 1_000_000UL + tvusec)
            else
                // 0 / -1 (gone, EPERM) or a short read — not a usable identity; defer to liveness.
                None
        finally
            Marshal.FreeHGlobal buffer

    /// Read a stable start-time identity token for `pid`, or `None` when unreadable/unavailable (a
    /// non-Linux/macOS POSIX with no reader, a process already gone, or a denied read). See the section
    /// comment above: an unknown token is never proof of anything — the caller defers to the by-number
    /// liveness verdict, so no platform is weakened.
    let readProcessIdentity (pid: int) : uint64 option =
        match readProcessIdentityForTests with
        | Some hook -> hook pid
        | None ->
            if RuntimeInformation.IsOSPlatform OSPlatform.Linux then
                readLinuxStartTime pid
            elif isMacOs then
                readMacStartTime pid
            else
                None

    // Positive proof a tracked number was recycled: the identity captured at track time and the one read
    // now are BOTH known and they differ. A `None` on either side is never proof (the caller then defers
    // to the liveness verdict), so a target without an identity reader (the BSDs) is not weakened.
    let private isRecycled (tracked: uint64 option) (current: uint64 option) : bool =
        match tracked, current with
        | Some a, Some b -> a <> b
        | _ -> false

    /// The single liveness + identity probe for a tracked pgid/pid: is `id` still the SAME live process
    /// (group) the caller captured `identity` for? The by-number liveness probe (`processGroupAlive`)
    /// decides existence; when the number still answers alive, the current identity is re-read and, ONLY
    /// if both the captured and the current token are known AND they differ, the id is reported gone
    /// (recycled by a stranger). A matching identity — or an unknown token on either side (a leader
    /// reaped while descendants keep the pgid alive, or a platform without a reader) — falls back to the
    /// by-number verdict, so no platform loses coverage. This is the one choke every probe/signal/kill
    /// path funnels through, so the reuse check is never duplicated per call site.
    let processGroupStillTracked (id: int) (identity: uint64 option) : bool =
        if not (processGroupAlive id) then
            false
        elif identity.IsNone then
            // No captured token to compare against — defer to the liveness verdict without an identity
            // read (the BSDs, or a track-time read that failed).
            true
        else
            not (isRecycled identity (readProcessIdentity id))

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

    /// Whether `signalNum` is a signal that can actually be *delivered*. A real signal is `>= 1`; the
    /// number 0 is a liveness *probe* (`kill`/`killpg` with a zero number only checks the target's
    /// existence/permissions and delivers nothing, yet returns success), and a negative number is not a
    /// signal at all — neither may ever be mistaken for a delivered signal.
    let isDeliverableSignal (signalNum: int) : bool = signalNum >= 1

    /// Reject a non-deliverable signal number at the API boundary, *before* any native delivery, as the
    /// typed error the signal verbs return in their `Result` channel — never an exception, and never the
    /// false `Ok()` that running `killpg(pgid, 0)` (a probe that reports success while delivering nothing)
    /// used to produce. Both POSIX backends (`ProcessGroupBackend.Signal` / `CgroupBackend.Signal`) funnel
    /// through this, so signal 0 (and negatives) fail honestly regardless of whether the group currently
    /// has members — the per-pgid / per-member delivery loops would otherwise skip a probe entirely on an
    /// empty group and report a vacuous success. `signalPid` deliberately keeps signal 0 usable as a bare
    /// liveness probe (its callers ask for existence, not delivery), so it is *not* routed through here.
    let ensureDeliverable (signalNum: int) : Result<unit, ProcessError> =
        if isDeliverableSignal signalNum then
            Ok()
        else
            Error(
                ProcessError.Unsupported(
                    $"signal {signalNum} is a liveness probe, not a deliverable signal: kill/killpg with signal 0 only "
                    + "checks that the target exists and delivers nothing (and a negative number is not a signal at all) "
                    + "— pass Signal.Other with a real signal number (>= 1)"
                )
            )

    /// Broadcast a raw signal to a POSIX process group via `killpg`, classifying the outcome (see
    /// `SignalDelivery`) instead of collapsing every non-zero errno into "not delivered". A non-deliverable
    /// number (signal 0 — a liveness probe — or a negative) is refused *before* the syscall, so the
    /// primitive itself can never report a probe as `Delivered` the way `killpg(pgid, 0)`'s success return
    /// otherwise would. The API boundary (`ensureDeliverable`, called by both POSIX backends) already turns
    /// that into a typed error; this is the low-level safety net for any direct caller.
    let signalProcessGroup (pgid: int) (signalNum: int) : SignalDelivery =
        if not (isDeliverableSignal signalNum) then
            SignalDelivery.DeliveryFailed(
                EINVAL,
                $"signal {signalNum} is a liveness probe / non-signal, not a deliverable signal — refused before killpg"
            )
        else
            observeGroupDelivery pgid
            classifySignalDelivery (killpg (pgid, signalNum))

    /// Freeze a POSIX process group (SIGSTOP), classifying the delivery outcome.
    let suspendProcessGroup (pgid: int) : SignalDelivery =
        observeGroupDelivery pgid
        classifySignalDelivery (killpg (pgid, sigStop))

    /// Thaw a POSIX process group (SIGCONT), classifying the delivery outcome.
    let resumeProcessGroup (pgid: int) : SignalDelivery =
        observeGroupDelivery pgid
        classifySignalDelivery (killpg (pgid, sigCont))

    /// Send a raw signal to a single POSIX pid via `kill`, classified the same way as
    /// `signalProcessGroup` (see `SignalDelivery`).
    let signalPid (pid: int) (signalNum: int) : SignalDelivery =
        classifySignalDelivery (kill (pid, signalNum))

    /// Hard-kill a single POSIX process by pid (SIGKILL). Fires the delivery observer first (like the
    /// group primitives) so a test can confirm the cgroup backend's identity-gated `KillChild` delivers to
    /// a matching pid and prunes — never SIGKILLs — a recycled one.
    let killProcess (pid: int) =
        observeGroupDelivery pid
        kill (pid, SIGKILL) |> ignore

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

    // errno for an I/O error, and for a broken pipe. Once the child has closed the LAST slave fd (the pty
    // "hangup"), a Linux `read`/`write` on the master returns EIO where a pipe/socket would give a clean
    // 0-length EOF / EPIPE. `PtyStream` maps a read-side EIO to EOF and a write-side EIO/EPIPE to a
    // broken-pipe `IOException`; macOS/BSD give a normal EOF instead (harmless — the EIO branch is Linux).
    let private EIO = 5
    let private EPIPE = 32

    // ----------------------------------------------------------------------------------
    // POSIX pty (pseudo-terminal) master: fd allocation + an async Stream wrapper (Command.Pty)
    // ----------------------------------------------------------------------------------

    /// A `Stream` over a POSIX pty MASTER fd — the single merged terminal output (D3). When an
    /// interactive/fed stdin is also wanted, the SAME master is wrapped a second time as a NON-owning
    /// `PtyStream` for the write side (a pty master is bidirectional; read and write on it are
    /// independent), so there is one master fd, closed exactly once by the owning read stream — no `dup`,
    /// hence no non-CLOEXEC duplicate to leak. Unlike a socketpair end (a `Socket`/`NetworkStream` driven
    /// by the runtime's epoll/kqueue `SocketAsyncEngine`),
    /// a pty master is a character device that engine cannot host, so I/O goes through libc `read`/`write`;
    /// the base `Stream` offloads the synchronous `Read`/`Write` to the thread pool for its async verbs, so
    /// the `Pump`'s `ReadAsync` still completes on data with no busy-poll (it parks a pool thread for the
    /// read's duration — the pty analogue accepted here in place of the socketpair's epoll path). Two
    /// pty-specific wrinkles it hides from the `Pump`:
    ///   * **Hangup EIO → EOF.** On Linux, once the child closes the last slave fd, a master `read` returns
    ///     EIO rather than a clean 0-length EOF; this maps it to EOF so the merged-capture path ends
    ///     normally (macOS/BSD already give a real EOF). A write-side EIO/EPIPE becomes a broken-pipe
    ///     `IOException`, matching the socketpair path's write-after-peer-close.
    ///   * **Single-owner fd.** The fd is held by a `SafeFileHandle`; the merged-output (read) stream owns
    ///     it (closed exactly once on `Dispose`, finalizer as the GC-time safety net) — the same contract
    ///     the socketpair `SafeSocketHandle` gives — while the optional stdin (write) view over the same fd
    ///     is constructed non-owning, so it never closes the fd. Reads/writes take a
    ///     `DangerousAddRef`/`Release` around the syscall so a concurrent `Dispose` cannot close the fd
    ///     mid-call.
    type internal PtyStream(handle: Microsoft.Win32.SafeHandles.SafeFileHandle) =
        inherit Stream()

        // Run one libc read/write (`op`) against the owned fd with the caller's buffer pinned at `offset`,
        // holding a ref on the SafeHandle so `Dispose` cannot close the fd underneath the syscall.
        let withPinnedFd (buffer: byte[]) (offset: int) (op: int -> nativeint -> nativeint) : nativeint =
            let mutable refAdded = false

            try
                handle.DangerousAddRef &refAdded
                let fd = int (handle.DangerousGetHandle())
                let pin = GCHandle.Alloc(buffer, GCHandleType.Pinned)

                try
                    op fd (pin.AddrOfPinnedObject() + nativeint offset)
                finally
                    pin.Free()
            finally
                if refAdded then
                    handle.DangerousRelease()

        override _.CanRead = true
        override _.CanWrite = true
        override _.CanSeek = false
        override _.Flush() = ()
        override _.Length = raise (NotSupportedException "a pty master stream has no length")

        override _.Position
            with get () = raise (NotSupportedException "a pty master stream is not seekable")
            and set _ = raise (NotSupportedException "a pty master stream is not seekable")

        override _.Seek(_, _) =
            raise (NotSupportedException "a pty master stream is not seekable")

        override _.SetLength _ =
            raise (NotSupportedException "a pty master stream has no length")

        override _.Read(buffer, offset, count) =
            if count = 0 then
                0
            else
                let rec attempt () =
                    let n =
                        withPinnedFd buffer offset (fun fd ptr -> ptyReadRaw (fd, ptr, nativeint count))

                    if n >= 0n then
                        int n
                    else
                        let errno = Marshal.GetLastWin32Error()

                        if errno = EINTR then
                            attempt () // interrupted before any byte arrived; retry the read
                        elif errno = EIO then
                            0 // pty hangup: the child closed the last slave fd — an ordinary end-of-stream
                        else
                            raise (IOException $"read from the pty master failed (errno {errno})")

                attempt ()

        override _.Write(buffer, offset, count) =
            let mutable written = 0

            while written < count do
                let remaining = count - written

                let n =
                    withPinnedFd buffer (offset + written) (fun fd ptr -> ptyWriteRaw (fd, ptr, nativeint remaining))

                if n >= 0n then
                    written <- written + int n
                else
                    let errno = Marshal.GetLastWin32Error()

                    if errno = EINTR then
                        () // interrupted before any byte was written; retry the remaining bytes
                    elif errno = EIO || errno = EPIPE then
                        raise (
                            IOException $"write to the pty master failed: the child closed the terminal (errno {errno})"
                        )
                    else
                        raise (IOException $"write to the pty master failed (errno {errno})")

        override _.Dispose(disposing) =
            if disposing then
                handle.Dispose()

            base.Dispose disposing

    /// Clear the pty's cooked-mode ECHO bit on `fd` (the slave, before the child adopts it) so typed input
    /// is NOT echoed into the terminal's output — the `PtyConfig.Echo = false` secret-safety mitigation.
    /// The whole `struct termios` is read into an opaque unmanaged buffer and written back verbatim with
    /// only `c_lflag`'s ECHO bit cleared, so no other line-discipline setting (control chars, speeds) is
    /// disturbed. A generous 128-byte buffer over-covers glibc's ~60-byte `struct termios`. A `tcgetattr`/
    /// `tcsetattr` failure is a real per-invocation error the caller turns into a failed spawn — silently
    /// leaving echo ON when the caller explicitly asked for it off is the exact secret leak this guards.
    let private disableEcho (fd: int) : Result<unit, string> =
        let buffer = Marshal.AllocHGlobal 128

        try
            // Zero the buffer so a short `tcgetattr` write can never leave stack garbage in the tail that a
            // subsequent `tcsetattr` would then apply.
            for i in 0..127 do
                Marshal.WriteByte(buffer, i, 0uy)

            if tcgetattr (fd, buffer) <> 0 then
                Error $"tcgetattr failed (errno {Marshal.GetLastWin32Error()})"
            else
                let lflag = Marshal.ReadInt32(buffer, TERMIOS_LFLAG_OFFSET)
                Marshal.WriteInt32(buffer, TERMIOS_LFLAG_OFFSET, lflag &&& ~~~ECHO)

                if tcsetattr (fd, TCSANOW, buffer) <> 0 then
                    Error $"tcsetattr(TCSANOW) failed (errno {Marshal.GetLastWin32Error()})"
                else
                    Ok()
        finally
            Marshal.FreeHGlobal buffer

    /// Open a POSIX pty master/slave pair via the all-libc `posix_openpt` sequence (portable across glibc
    /// versions, and — unlike a bare `openpty` — able to set O_CLOEXEC without `fcntl`, per the PTY ADR).
    /// Both ends are opened O_CLOEXEC so a DIFFERENT concurrent spawn never inherits them (the pty analogue
    /// of the socketpair's SOCK_CLOEXEC), and O_NOCTTY so opening the slave in the PARENT never steals it
    /// as the parent's controlling terminal — the CHILD adopts it deliberately via `setsid --ctty`. The
    /// initial window size (`cols`/`rows` from `PtyConfig`) is applied to the pair via `ioctl(TIOCSWINSZ)`
    /// so a full-screen TUI sees the requested geometry instead of the kernel's 0x0 default — parity with
    /// the Windows `CreatePseudoConsole` path (`bare openpty` would take a `winsize` argument directly; the
    /// `posix_openpt` sequence sets it explicitly after allocation). When `echo` is false the slave's
    /// cooked-mode ECHO is cleared via `termios` before the child adopts it (`PtyConfig.Echo` — the
    /// secret-safety mitigation). Returns the master and slave fds (the caller owns and must close both),
    /// or an errno-tagged message. Each `Marshal.GetLastWin32Error()` is read immediately after its failing
    /// P/Invoke, before any other one.
    let private openPtyPair (cols: int) (rows: int) (echo: bool) : Result<int * int, string> =
        let master = posix_openpt (O_RDWR ||| O_NOCTTY ||| O_CLOEXEC)

        if master < 0 then
            Error $"posix_openpt failed (errno {Marshal.GetLastWin32Error()})"
        else
            let grantRc = grantpt master

            if grantRc <> 0 then
                let errno = Marshal.GetLastWin32Error()
                close master |> ignore
                Error $"grantpt failed (errno {errno})"
            else
                let unlockRc = unlockpt master

                if unlockRc <> 0 then
                    let errno = Marshal.GetLastWin32Error()
                    close master |> ignore
                    Error $"unlockpt failed (errno {errno})"
                else
                    let namePtr = ptsname master

                    if namePtr = IntPtr.Zero then
                        let errno = Marshal.GetLastWin32Error()
                        close master |> ignore
                        Error $"ptsname failed (errno {errno})"
                    else
                        match Marshal.PtrToStringAnsi namePtr with
                        | null ->
                            close master |> ignore
                            Error "ptsname returned an empty slave device path"
                        | slavePath ->
                            let slave = openFile (slavePath, O_RDWR ||| O_NOCTTY ||| O_CLOEXEC)

                            if slave < 0 then
                                let errno = Marshal.GetLastWin32Error()
                                close master |> ignore
                                Error $"open({slavePath}) failed (errno {errno})"
                            else
                                // Apply the initial geometry. A pty pair shares one `winsize`, so setting it
                                // on the master is reflected on the slave the child reads. A failure here is a
                                // real per-invocation error (not a capability gap): close BOTH ends and report
                                // it rather than silently leave the child at the kernel's 0x0 default.
                                let mutable ws = Winsize()
                                ws.Row <- uint16 rows
                                ws.Col <- uint16 cols

                                if ioctl (master, unativeint TIOCSWINSZ, &ws) <> 0 then
                                    let errno = Marshal.GetLastWin32Error()
                                    close slave |> ignore
                                    close master |> ignore
                                    Error $"ioctl(TIOCSWINSZ) failed (errno {errno})"
                                elif not echo then
                                    // `PtyConfig.Echo = false`: clear ECHO on the SLAVE (the child's side)
                                    // before it is dup'd onto the child's 0/1/2, so no typed credential is
                                    // echoed into the captured merged output. A failure fails the spawn
                                    // (both ends closed) — never a silent fallback to echo-on (the secret leak).
                                    match disableEcho slave with
                                    | Ok() -> Ok(master, slave)
                                    | Error message ->
                                        close slave |> ignore
                                        close master |> ignore
                                        Error message
                                else
                                    Ok(master, slave)

    /// Resize the pty behind `masterFd` (retained from spawn as `Spawned.PtyControl`) to `cols` x `rows`
    /// and notify the child `pid` — the POSIX arm of `RunningProcess.ResizeAsync` (Stage 4 / D6). Applies
    /// the new geometry via `ioctl(TIOCSWINSZ)` on the master (mirroring the initial-geometry call in
    /// `openPtyPair`, KB K-028 — the pair shares one `winsize`, so the slave the child reads sees it),
    /// then delivers SIGWINCH so a running TUI re-queries its size. An ioctl failure is a typed
    /// `ProcessError.Io`; the SIGWINCH is best-effort — a child that already exited is a harmless ESRCH,
    /// not a resize failure. Geometry is validated positive and SHORT-bounded by the caller
    /// (`RunningProcess.ResizeAsync`), matching the `Command.Pty` builder's `validatePtyConfig`.
    let resizePty (masterFd: nativeint) (pid: nativeint) (cols: int) (rows: int) : Result<unit, ProcessError> =
        let mutable ws = Winsize()
        ws.Row <- uint16 rows
        ws.Col <- uint16 cols

        if ioctl (int masterFd, unativeint TIOCSWINSZ, &ws) <> 0 then
            Error(ProcessError.Io $"ioctl(TIOCSWINSZ) failed (errno {Marshal.GetLastWin32Error()})")
        else
            // Best-effort SIGWINCH to the child; a race with the child's own exit (ESRCH) is not a failure.
            kill (int pid, SIGWINCH) |> ignore
            Ok()

    // `waitpid` option: return immediately (0) if the child has not yet changed state, rather than
    // blocking. Same value on Linux and macOS.
    let private WNOHANG = 1

    // The set of event-driven `waitPosix` waits in flight for ONE pid. Every waiter for the same pid
    // shares a single group — and therefore a single OS reap (`waitpid`/`waitid`) — so registering a
    // second wait for a pid never triggers a second reap: the one decoded status is fanned out to every
    // waiter (reap-once, K-016). `Waiters` doubles as the group's lock object. `Outcome` caches the decoded
    // result once the child has been reaped, so a wait that registers AFTER the reap (the double-
    // registration race) still observes the real status instead of losing an ECHILD race.
    [<NoComparison; NoEquality>]
    type private PendingWait =
        { Waiters: ResizeArray<TaskCompletionSource<Outcome>>
          mutable Outcome: Outcome voption }

    // Every event-driven POSIX wait currently in flight — or just-reaped and briefly lingering with its
    // cached outcome — process-wide, keyed by pid. `waitPosix` adds a waiter to the pid's group (creating
    // it on first use); whichever of `reapAllPending` (SIGCHLD-triggered), the pidfd reaper, or `reapLeader`
    // (teardown) actually reaps a given child resolves the group and fans the ONE real status out to every
    // waiter — the OS status is consumed exactly once, since only one `waitpid`/`waitid` can get there.
    let private pendingWaits = ConcurrentDictionary<int, PendingWait>()

    // How long a resolved group lingers in `pendingWaits` after its child was reaped, so a second
    // `waitPosix` for the same pid that registers just AFTER the reap still finds the cached outcome
    // instead of re-reaping an already-consumed child (ECHILD) and inventing an `Unobserved`. Kept short:
    // far below any possible pid-number wraparound (which needs millions of spawns), so the cache can never
    // hand a reused pid a stale generation's status — and the reap itself stays pidfd-identity-safe
    // regardless of this window.
    [<Literal>]
    let private resolvedGroupLingerMs = 200

    // True once the group's child has been reaped and its outcome cached. Read under the group lock so the
    // two-word `voption` struct is never observed torn.
    let private groupResolved (group: PendingWait) : bool =
        lock group.Waiters (fun () ->
            match group.Outcome with
            | ValueSome _ -> true
            | ValueNone -> false)

    // Resolve `group` (for `pid`) with the ONE decoded `outcome` and fan it out to every waiter registered
    // so far. Atomic against a concurrent `waitPosix` join (both take the group lock): a waiter enlisted
    // before this call is completed here; one that races in after sees `Outcome` already set and completes
    // itself from the cache — so no waiter is ever stranded. The first resolution wins and schedules the
    // group's eventual eviction; later reap attempts for the same child (a losing SIGCHLD/pidfd ECHILD
    // race, or `reapLeader`) find it already resolved and are no-ops, preserving reap-once (K-016).
    let private resolveGroup (pid: int) (group: PendingWait) (outcome: Outcome) =
        let waiters, firstResolution =
            lock group.Waiters (fun () ->
                match group.Outcome with
                | ValueSome _ -> Array.empty<TaskCompletionSource<Outcome>>, false
                | ValueNone ->
                    group.Outcome <- ValueSome outcome
                    let snapshot = group.Waiters.ToArray()
                    group.Waiters.Clear()
                    snapshot, true)

        for waiter in waiters do
            waiter.TrySetResult outcome |> ignore

        if firstResolution then
            // Evict after a brief linger (see `resolvedGroupLingerMs`), removing only if THIS exact group is
            // still the mapped value, so a newer generation's group for a reused pid is never dropped. Fire-
            // and-forget on the thread pool; nothing inside can throw, so it needs no fault observer.
            task {
                do! Task.Delay resolvedGroupLingerMs

                pendingWaits.TryRemove(System.Collections.Generic.KeyValuePair(pid, group))
                |> ignore
            }
            |> ignore

    // Resolve whatever wait (if any) is registered for `pid` with `outcome`. Shared by the SIGCHLD-triggered
    // reap path below and by `reapLeader`'s teardown reap, so whichever of the two actually reaps a given
    // child still delivers the real decoded status to everything awaiting it, instead of leaving the wait to
    // notice only `ECHILD`. A no-op if nothing is pending for `pid` (no `waitPosix` call is in flight).
    let private completePending (pid: int) (outcome: Outcome) =
        match pendingWaits.TryGetValue pid with
        | true, group -> resolveGroup pid group outcome
        | false, _ -> ()

    // Best-effort, non-blocking reap of one pending pid. Returns `true` once nothing further needs to
    // be done SYNCHRONOUSLY for it (reaped by us; nothing was ever pending; already reaped and only
    // lingering with its cached outcome; or an `ECHILD` race whose resolution has been handed off to an
    // async grace-then-fallback on the thread pool — see below); `false` if it is still alive (left
    // pending for the next trigger). Never blocks.
    let private tryReapPending (pid: int) : bool =
        match pendingWaits.TryGetValue pid with
        | false, _ -> true
        | true, group when groupResolved group ->
            // Already reaped; the group is only lingering so late registrants can read the cached outcome.
            // Re-probing would just hit ECHILD, so leave the consumed child alone (reap-once, K-016).
            true
        | true, group ->
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
                // We won the reap race — this is the real, decoded status; fan it out to every waiter.
                resolveGroup pid group (decodeWaitStatus status)
                true
            else
                // `ECHILD`: some concurrent caller (most plausibly `reapLeader` tearing down an
                // abandoned run, or another `tryReapPending` invocation that already won this exact
                // race) has already reaped this pid and holds the REAL status; we have nothing to
                // report ourselves. `resolveGroup` completes only the FIRST resolution, so whichever side
                // resolves the group *first* decides the outcome — not whichever side won the `waitpid`
                // race — so give the genuine winner a brief grace period to land its real result before
                // falling back.
                //
                // This grace-then-fallback runs on the thread pool (fire-and-forget), NOT spun in-line:
                // this function is called from the shared SIGCHLD callback, so blocking it here would
                // stall the runtime's whole signal-dispatch path and delay reaping every other pending
                // child. Nothing inside the task can throw (`groupResolved`/`resolveGroup` don't), so
                // it needs no fault observer.
                task {
                    let mutable spins = 0

                    while not (groupResolved group) && spins < 20 do
                        do! Task.Delay 1
                        spins <- spins + 1

                    // Still unresolved after the grace period: nobody ever reported this pid's real status
                    // (the genuine winner errored out before resolving the group, or something outside
                    // ProcessKit's own reap machinery reaped it). Resolve honestly instead of leaving the
                    // waiters hanging forever or inventing a clean exit — a no-op if the winner just landed.
                    resolveGroup
                        pid
                        group
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

    // SYS_pidfd_send_signal — same syscall number on x86_64/arm64/x86/arm/riscv (Linux 5.1+). Delivers a
    // signal to the exact task a pidfd PINS (not a pid number), so it can never reach a process that later
    // recycled the number — the primitive the cgroup per-member signal path uses for pid-reuse-safe
    // delivery (see `pidfdSendSignalChecked` below and `Native.Cgroup.deliverIdentitySafe`).
    [<Literal>]
    let private SYS_pidfd_send_signal = 424

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

    // pidfd_send_signal(pidfd, sig, siginfo*, flags) via syscall(2). A second fixed-signature P/Invoke
    // into glibc's `syscall` (`syscall3` above is the pidfd_open one): `syscall`'s glibc stub reads its
    // arguments from REGISTERS (hand-written asm, not a C variadic), so a fixed-signature P/Invoke passes
    // them correctly even on AArch64 — the same reasoning that makes the pidfd_open path safe. Distinct
    // managed name, same native `syscall` entry point, wider signature.
    [<DllImport("libc", SetLastError = true, EntryPoint = "syscall")>]
    extern nativeint private syscall5(nativeint number, int arg1, int arg2, nativeint arg3, uint arg4)

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

    // pidfd_send_signal(pidfd, sig, NULL, 0): deliver `sig` to the exact task `pidfd` pins — never a
    // process that later recycled the pid number. A NULL `siginfo` pointer with 0 flags is the documented
    // "behave like kill(2)" form. Via `syscall` for the same register-passing reason as `pidfdOpen`. A
    // negative return signals failure (errno in `Marshal.GetLastWin32Error`).
    let private pidfdSendSignal (pidfd: int) (signalNum: int) : int =
        int (syscall5 (nativeint SYS_pidfd_send_signal, pidfd, signalNum, IntPtr.Zero, 0u))

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

    /// Pin the exact task currently running as `pid` via `pidfd_open(2)` (Linux >= 5.3), for the cgroup
    /// per-member signal path (`Native.Cgroup.deliverIdentitySafe`). `Ok fd` owns a fresh pidfd the caller
    /// MUST release with `closePidfd`; `Error errno` carries the raw errno — `ESRCH` if the target already
    /// exited, `ENOSYS` on a kernel without the syscall (or a seccomp block). The fd never refers to a
    /// later process that recycles the number, which is the identity anchor pid-reuse-safe delivery relies
    /// on. Reuses the existing `pidfdOpen`; the errno is read immediately, before any other P/Invoke.
    let pidfdOpenChecked (pid: int) : Result<int, int> =
        let fd = pidfdOpen pid

        if fd < 0 then Error(Marshal.GetLastWin32Error()) else Ok fd

    /// Deliver `sig` to the task pinned by pidfd `fd` via `pidfd_send_signal(2)` (Linux >= 5.1). Because
    /// the fd names a specific task, the signal can only ever reach it — never a process that later reused
    /// its pid. `Ok ()` on delivery; `Error errno` carries the raw errno — `ESRCH` if the pinned task has
    /// since exited (benign: its own exit, never a recycled pid), `ENOSYS` on a kernel without the syscall,
    /// `EPERM` for a refused delivery. The errno is read immediately, before any other P/Invoke.
    let pidfdSendSignalChecked (fd: int) (signalNum: int) : Result<unit, int> =
        let rc = pidfdSendSignal fd signalNum

        if rc < 0 then Error(Marshal.GetLastWin32Error()) else Ok()

    /// Release a pidfd opened by `pidfdOpenChecked`. Best-effort — closing a pidfd cannot meaningfully
    /// fail for the caller, and each is single-use per delivery.
    let closePidfd (fd: int) : unit = close fd |> ignore

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

    // Resolve exactly the group THIS pidfd registration owns, fanning the outcome out to every waiter on
    // it. `resolveGroup` operates on the group carried by reference (never re-looked-up by pid) and evicts
    // only when it still maps to this exact registration, so a stale pidfd for a reused pid can't resolve
    // or evict a NEWER generation's group. Symmetric to the SIGCHLD path's `completePending`, but keyed by
    // registration identity instead of by pid — the pid-reuse-safe reap the pidfd path exists for.
    let private completePidfdReg (reg: PidfdReg) (outcome: Outcome) =
        resolveGroup reg.Pid reg.Pending outcome

    // `waitid` came back ECHILD (or a spurious wake): the genuine winner — `reapLeader` racing this
    // child's teardown — holds the real status and is about to deliver it via `completePending`. Give it
    // the same brief grace the SIGCHLD path's ECHILD branch gives, then resolve OUR group honestly. Keyed
    // by registration identity throughout (`groupResolved`/`resolveGroup` act on the group by reference),
    // so a reused pid is never crossed. Runs on the thread pool so it never blocks the shared reaper thread.
    let private graceThenResolve (reg: PidfdReg) : Task =
        task {
            let mutable spins = 0

            while not (groupResolved reg.Pending) && spins < 20 do
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
    /// Idempotent per pid: any number of concurrent OR sequential waits for the same pid resolve to the
    /// SAME real decoded outcome. They share ONE group — and thus ONE OS reap (`waitpid`/`waitid`, reap-
    /// once, K-016) — with the status fanned out to every waiter; a second wait never opens a second pidfd
    /// or triggers a second reap. A wait that registers just AFTER the child was reaped (the double-
    /// registration race the flaky idempotency test exercises) still gets the real status from the group's
    /// briefly-cached outcome, instead of losing an ECHILD race and inventing an `Unobserved`.
    let rec private waitPosixCore (usePidfd: bool) (pid: nativeint) : Task<Outcome> =
        let intPid = int pid

        let tcs =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        match pendingWaits.TryGetValue intPid with
        | true, existing ->
            // A wait for this pid is already registered — or was just reaped and is lingering with its
            // cached outcome. Join it under the group lock: take the already-decoded outcome if it is
            // resolved, else enlist as one more waiter the single shared reap will fan the status out to.
            // Never a second pidfd / second reap for the same pid.
            lock existing.Waiters (fun () ->
                match existing.Outcome with
                | ValueSome outcome -> tcs.TrySetResult outcome |> ignore
                | ValueNone -> existing.Waiters.Add tcs)

            tcs.Task
        | false, _ ->
            let waiters = ResizeArray<TaskCompletionSource<Outcome>>()
            waiters.Add tcs

            let pending =
                { Waiters = waiters
                  Outcome = ValueNone }

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
                // `TryGetValue` miss and this `TryAdd`. Retry so we join the winner's group instead.
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

                // If event-driven `waitPosix` waits are still pending for this pid (an abandoned run
                // being torn down concurrently with, or just ahead of, its own exit), hand EVERY waiter on
                // the group the real decoded status now that we have it, rather than leaving them to notice
                // only `ECHILD` whenever they next get a chance to look. `completePending` resolves the
                // whole group at once, so all waiters agree on this one status.
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
    // correctly), and handle the child's supplementary groups. By default (or an explicit empty
    // `Command.Groups []`) the parent's set is cleared with `--clear-groups` so the child never keeps
    // root's; a non-empty `Command.Groups gids` instead sets EXACTLY those groups with `--groups`
    // (a comma-separated numeric list — `setpriv` applies them verbatim, no `/etc/group` lookup). The
    // group flag is mutually exclusive on the wire, so at most one of `--clear-groups`/`--groups` is
    // emitted. `Command.Groups` only reaches here alongside a uid/gid drop (`spawnPosix`'s up-front
    // `groupsRequireDropError` guard refuses it without one), so it always composes with a real drop.
    let private setprivFlags (config: CommandConfig) : string list =
        let regid =
            match config.Gid with
            | Some g -> [ $"--regid={g}" ]
            | None -> []

        let reuid =
            match config.Uid with
            | Some u -> [ $"--reuid={u}" ]
            | None -> []

        let groups =
            match config.Groups with
            | Some(_ :: _ as gids) -> [ "--groups=" + String.concat "," (gids |> List.map string) ]
            | _ -> [ "--clear-groups" ]

        regid @ reuid @ groups

    /// A `Command.Groups` (supplementary groups) request is applied only along the `setpriv`
    /// privilege-drop path, which is entered exclusively by a `Uid`/`Gid` drop. Set WITHOUT either it
    /// would otherwise fall through to a plain `posix_spawn` and be silently dropped — the very silent
    /// downgrade the project forbids. Refuse it up front with a typed `ProcessError.Spawn`, mirroring the
    /// honest-failure contract of the uid/gid drop itself. `None` = clear to go.
    let private groupsRequireDropError (command: Command) : ProcessError option =
        let config = command.Config

        if config.Groups.IsSome && config.Uid.IsNone && config.Gid.IsNone then
            Some(
                ProcessError.Spawn(
                    command.Program,
                    "Command.Groups (supplementary groups) is applied by the setpriv privilege-drop helper and needs a Uid or Gid drop to take effect; it was set without either"
                )
            )
        else
            None

    /// Rewrite `command` to run through the `setpriv` helper: `setpriv <flags> <program> <args...>`. The
    /// uid/gid/groups are cleared on the rewritten command (so the `spawnPosix` dispatcher does not
    /// recurse), and every other knob — stdio, env, CurrentDir, Priority, Umask, Setsid — is preserved and
    /// applied by `posix_spawn` to the `setpriv` process, which inherits them across its `exec` of the
    /// real program.
    let private setprivCommand (command: Command) : Command =
        let config = command.Config
        let prefix = setprivFlags config @ [ config.Program ]

        Command(
            { config with
                Program = "setpriv"
                Args = config.Args.InsertRange(0, prefix)
                Uid = None
                Gid = None
                Groups = None }
        )

    /// Spawn `command` into a brand-new process group (`POSIX_SPAWN_SETPGROUP`, so pgid = the
    /// child's pid) and capture its stdout/stderr. The whole group can later be reaped with
    /// `killProcessGroup`. This is the one real POSIX spawn path; `spawnPosix` routes a command that
    /// requests `Uid`/`Gid` here too, after rewriting it to run through the `setpriv` helper.
    let private spawnPosixViaSpawn (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        // `Command.InheritStdin` hands the child the PARENT's own standard input directly (no
        // socketpair, no feeder) — the interactive/console case. Every other configuration goes
        // through a socketpair; its parent-side write end is retained only for a feeder source or
        // `KeepStdinOpen`.
        let stdinInherit = Stdin.isInherit config.StdinSource

        let stdinWanted =
            (config.StdinSource.IsSome && not stdinInherit) || config.KeepStdinOpen
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
        // Under a PTY with an interactive/fed stdin, the write side is the SAME single master fd the
        // parent keeps for the merged output (a pty master is bidirectional; read and write on it are
        // independent). `ptyMasterFd` carries that fd number to the stream-wrapping step, where it is
        // wrapped as a NON-owning stdin view; `ptyStdinOverMaster` gates whether to build it. There is
        // deliberately NO `dup` of the master — a `dup` would drop its O_CLOEXEC and leak a writable
        // master into a concurrent spawn, keeping the pty-child from ever seeing its stdin EOF (R-02).
        let mutable ptyMasterFd = -1
        let mutable ptyStdinOverMaster = false

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

        // `Command.StdoutToFile`/`StderrToFile`: open the redirect file ON THE PARENT and hand its fd to
        // the child as a child-side fd (dup2'd onto the child's slot by a `posix_spawn` file action, then
        // closed in the child; the parent's copy closed after spawn), exactly like the `/dev/null` fd from
        // `openNul`. Both the create/truncate and the append opens go through .NET's own `open(2)`, which
        // sets O_CLOEXEC portably across Linux and macOS (no variadic `open(…, mode)` P/Invoke — the
        // AArch64 variadic-ABI hazard this port avoids for `fcntl`), so the fd reaches the child with NO
        // `dup(2)` of a second logical copy that would drop O_CLOEXEC (K-029). Ownership is then released
        // from the SafeFileHandle (`SetHandleAsInvalid` — it does NOT close the fd) so the raw fd is
        // managed by the SAME single-close child-side machinery. `append = false` creates/truncates via
        // `File.OpenHandle(FileMode.Create)`; `append = true` opens a `FileStream(FileMode.Append)`, whose
        // Unix open sets the kernel O_APPEND flag AT OPEN TIME (the classic multi-writer shared-log idiom
        // .NET guarantees on Unix — unlike the lower-level `File.OpenHandle`) so every raw child write lands
        // atomically at EOF on Linux, Alpine, and macOS alike, with no custom `fcntl(F_SETFL)` and no
        // per-platform O_APPEND numeric value — closing the whole variadic-ABI risk class the two earlier
        // fcntl-based fixes were exposed to. A bad path/permission fails the spawn honestly.
        let openRedirectFile (label: string) (path: string) (append: bool) : int =
            try
                let fd =
                    if append then
                        // Kernel O_APPEND is set at OPEN TIME by `FileMode.Append`'s Unix `open(2)`. The raw
                        // fd is detached from the FileStream (`SetHandleAsInvalid` marks the handle closed
                        // WITHOUT calling close(2)) BEFORE `fs` is disposed at end of this branch's scope, so
                        // the FileStream's own Dispose — with an empty write buffer (nothing was written) —
                        // is a no-op on the real fd, which stays ours to hand to the child.
                        use fs =
                            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)

                        let handle = fs.SafeFileHandle
                        let raw = int (handle.DangerousGetHandle())
                        handle.SetHandleAsInvalid()
                        raw
                    else
                        // Create/truncate: no append concern, so the lighter `File.OpenHandle` primitive is
                        // kept. Release .NET's ownership WITHOUT closing the fd, exactly as before.
                        let handle =
                            File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)

                        let raw = int (handle.DangerousGetHandle())
                        handle.SetHandleAsInvalid()
                        raw

                // The raw fd is now ours to hand to the child and to close exactly once via `childSideFds`
                // (parent) + a child-side addclose.
                childSideFds.Add fd
                fd
            with ex ->
                // A bad redirect path / denied access is an honest per-invocation spawn failure — symmetric
                // to a failed `open(/dev/null)` (`openNul`) or `socketpair()` (`makeStdioChannel`) — never a
                // silent downgrade to the parent's own stdout/stderr.
                failure <- Some $"could not open the redirect file '{path}' for {label}: {ex.Message}"
                -1

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

        // stdin / stdout / stderr. A PTY replaces the whole socketpair wiring (D3/D7): one pty master/
        // slave pair, the slave dup'd onto the child's 0/1/2 and the parent keeping the master as the
        // single merged terminal stream — handled as the FIRST branch here, so the ordinary per-stream
        // stdout/stderr setup below is skipped (`config.Pty.IsNone`).
        match config.Pty with
        | Some pty ->
            // The slave becomes the child's controlling terminal (via the rewritten `setsid --ctty`
            // Program); it is dup'd onto 0/1/2 and its original closed by the file actions further down.
            // The parent keeps the SINGLE MASTER (`stdoutParentRead`) as the merged output. When an
            // interactive/fed stdin is wanted, that SAME master is the write side too (its read and write
            // are independent) — wrapped as a NON-owning stdin view below, so one master fd is closed
            // exactly once by the owning stdout stream and there is no non-CLOEXEC `dup` to leak (R-02).
            // There is no separate stderr (`stderrParentRead` stays `None`, D3) and no `/dev/null` or
            // socketpair on this path.
            match openPtyPair pty.Cols pty.Rows pty.Echo with
            | Error message -> failure <- Some message
            | Ok(master, slave) ->
                childSideFds.Add slave
                stdinChildFd <- slave
                stdoutChildFd <- slave
                stderrChildFd <- slave
                stdoutParentRead <- Some master
                ptyMasterFd <- master
                ptyStdinOverMaster <- stdinWanted
        | None ->
            if stdinInherit then
                // `InheritStdin`: hand the child the PARENT's own standard input directly (no socketpair,
                // no feeder). On macOS, POSIX_SPAWN_CLOEXEC_DEFAULT closes every fd not named by a file
                // action at exec, so register a self-dup2 (0 -> 0) to keep fd 0 open in the child; on Linux
                // fd 0 is inherited naturally, so no dup2 is needed. Symmetric to the stdout/stderr
                // `StdioMode.Inherit` branches below. `stdinParentWrite` stays `None`, so `Spawned.Stdin` is
                // `None` and no feeder is ever started.
                stdinChildFd <- if isMacOs then 0 else -1
            elif stdinWanted then
                match makeStdioChannel "stdin" with
                | Some(readFd, writeFd) ->
                    stdinChildFd <- readFd
                    childSideFds.Add readFd
                    stdinParentWrite <- Some writeFd
                | None -> ()
            else
                stdinChildFd <- openNul "stdin" O_RDONLY

        // stdout (skipped under a PTY — its stdout is the merged pty master, wired above). A file redirect
        // (`Command.StdoutToFile`) takes precedence over `StdoutMode`: the child's fd 1 is the file, so
        // there is no parent read stream (`stdoutParentRead` stays `None` -> `Spawned.Stdout = None`), and
        // the file outlives the parent (no pump). The builder rejected combining it with the parent-side
        // stdout observation knobs / MergeStderr / Pty.
        if failure.IsNone && config.Pty.IsNone && config.StdoutFile.IsSome then
            match config.StdoutFile with
            | Some(path, append) -> stdoutChildFd <- openRedirectFile "stdout" path append
            | None -> ()
        elif failure.IsNone && config.Pty.IsNone then
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

        // stderr (skipped under a PTY — a tty is one device, so stderr is physically merged into the
        // master; `stderrParentRead` stays `None`, D3)
        if failure.IsNone && config.Pty.IsNone then
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
            elif config.StderrFile.IsSome then
                // `Command.StderrToFile`: the child's fd 2 is the file, so there is no parent read stream
                // (`stderrParentRead` stays `None` -> `Spawned.Stderr = None`) and the file outlives the
                // parent. Rejected at the builder alongside MergeStderr, so this is never reached under it.
                match config.StderrFile with
                | Some(path, append) -> stderrChildFd <- openRedirectFile "stderr" path append
                | None -> ()
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
                    //
                    // A PTY (`config.Pty`) sets NEITHER flag: the rewritten `setsid --ctty` helper performs
                    // `setsid()` ITSELF post-`exec` (to become a session leader before `TIOCSCTTY`), and
                    // `setsid()` fails for a process that is already a group/session leader — so pre-setting
                    // `SETPGROUP`/`SETSID` here would break it (`SETPGROUP` makes `setsid --ctty` fork the
                    // real target into a DIFFERENT, untracked pgid; `SETSID` makes its `setsid()` EPERM).
                    // Left without either flag the helper inherits the parent's group (never a leader, since
                    // its fresh pid cannot equal the parent's pgid), its `setsid()` succeeds, and the target
                    // ends up at pgid == pid == sid IN PLACE — the same invariant `SETSID` gives, so
                    // `killProcessGroup pid` still reaps the whole session-group. The one cost is a
                    // sub-millisecond window before that `setsid()` where `killpg pid` does not yet target
                    // the child; `postSpawnTeardown` closes it with a direct single-pid kill (below).
                    let groupFlag =
                        if config.Pty.IsSome then 0s
                        elif config.Setsid then posixSpawnSetsid
                        else POSIX_SPAWN_SETPGROUP

                    let spawnFlags =
                        if isMacOs then
                            groupFlag ||| POSIX_SPAWN_CLOEXEC_DEFAULT
                        else
                            groupFlag

                    register "posix_spawnattr_setflags" (fun () -> posix_spawnattr_setflags (attributes, spawnFlags))

                    // Only set an explicit pgroup when NOT detaching a session and NOT under a PTY;
                    // `POSIX_SPAWN_SETSID` establishes the child's own group itself, and the pty helper's
                    // own `setsid()` does the same — a `setpgroup` alongside either is redundant (and, for
                    // the pty case, would re-introduce the group-leader-before-`setsid()` breakage above).
                    if not config.Setsid && config.Pty.IsNone then
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
                                Error(notFoundFromSpawnFailure command.Program)
                            else
                                Error(ProcessError.Spawn(command.Program, $"posix_spawn failed ({rc})"))
                        else
                            // posix_spawnp succeeded: the child is running with pid `pid` (== its own pgid).
                            // From here on ANY failure in the parent-side managed initialization — a refused
                            // `setpriority`, or a `Socket`/`NetworkStream` ctor that throws while wrapping a
                            // retained stream end — must not strand the live child. `postSpawnTeardown` kills the
                            // process group (the child is its own leader), reaps the leader, disposes every stream
                            // already wrapped, and closes whatever parent-side fds were not yet handed to a stream —
                            // each fd released exactly once (wrapping clears its option as ownership transfers).
                            // The ORIGINAL error is then returned; the teardown is best-effort and never masks it,
                            // nor escapes as a raw exception. Without this, a throw while wrapping a stream would
                            // unwind to the outer handler, which closes fds but never kills the already-running
                            // child (the orphan/zombie leak this fixes); with it, the outer handler only ever sees
                            // pre-spawn faults, where no child exists yet.
                            let createdStreams = System.Collections.Generic.List<Stream>()

                            let postSpawnTeardown () =
                                killProcessGroup pid

                                // A PTY child is spawned with no `SETPGROUP` (its `setsid --ctty` helper does
                                // `setsid()` itself, post-`exec`), so for the sub-millisecond window before that
                                // call `killpg pid` above may not yet target it. A direct single-pid SIGKILL on
                                // this fault-path teardown closes that window so a just-spawned pty child — one
                                // whose stream ctor threw, say — is never stranded. Redundant with `killpg` once
                                // `setsid()` has run, and harmless (an already-reaped pid is `ESRCH`).
                                if config.Pty.IsSome then
                                    kill (pid, SIGKILL) |> ignore

                                reapLeader pid

                                for stream in createdStreams do
                                    try
                                        stream.Dispose()
                                    with _ ->
                                        // Best-effort teardown of a just-built stream: a Dispose fault here must
                                        // not mask the primary spawn error nor escape as a raw exception.
                                        ()

                                closeParentEnds ()

                            try
                                // Apply the requested CPU priority to the freshly spawned leader (pgid = its pid).
                                // `posix_spawn` has no nice attribute, so this is a post-spawn `setpriority` from the
                                // parent; the nice inherits across `fork`, so every descendant the leader spawns runs
                                // at it (whole-tree, modulo the sub-millisecond window before this call lands — see
                                // `Priority`). Lowering nice (raising priority) can be refused for lack of privilege:
                                // rather than silently running the child at a lower-than-requested priority, kill and
                                // reap it and fail the spawn honestly — matching the contract that priority is never
                                // downgraded silently.
                                let priorityRc =
                                    match config.Priority with
                                    | None -> 0
                                    | Some priority ->
                                        match setpriorityForTests with
                                        | Some hook -> hook PRIO_PROCESS pid (PriorityMapping.niceValue priority)
                                        | None -> setpriority (PRIO_PROCESS, pid, PriorityMapping.niceValue priority)

                                if priorityRc <> 0 then
                                    // The child is already running but must not run at an unintended priority: tear
                                    // it down (kill + reap + drop the parent pipe ends), then report the honest
                                    // priority failure — the ORIGINAL error, never the cleanup's.
                                    let errno = Marshal.GetLastWin32Error()
                                    postSpawnTeardown ()

                                    Error(
                                        ProcessError.Spawn(
                                            command.Program,
                                            $"could not set process priority via setpriority (errno {errno}); raising priority may require privilege (CAP_SYS_NICE)"
                                        )
                                    )
                                else
                                    // Wrap a retained parent-side socketpair end in a `Socket` +
                                    // `NetworkStream`: its ReadAsync/WriteAsync complete through the runtime's
                                    // epoll/kqueue `SocketAsyncEngine`, so a piped POSIX child no longer parks a
                                    // thread-pool thread in a blocking `FileStream` read/write for the stream's
                                    // whole lifetime (the linear thread-pool pressure this closes). `ownsHandle`/
                                    // `ownsSocket` give the stream sole ownership, so disposing it closes the fd
                                    // exactly once, with the SafeSocketHandle finalizer as the GC-time safety net
                                    // — the same single-owner contract the FileStream + SafeFileHandle had. A
                                    // socketpair end is bidirectional, but each stream is only ever read
                                    // (stdout/stderr) xor written (stdin) by its one consumer. A ctor that throws
                                    // is caught below: the streams already built are tracked in `createdStreams`,
                                    // so the teardown disposes them and closes the not-yet-wrapped ends — each fd
                                    // exactly once — after killing and reaping the child.
                                    // Under a PTY the retained parent end is the SINGLE pty MASTER fd (the merged
                                    // output) — a character device the `SocketAsyncEngine` cannot host, so a
                                    // `Socket` ctor would reject the non-socket fd. It wraps in `PtyStream` (libc
                                    // read/write, hangup-EIO → EOF) over a `SafeFileHandle` instead — the same
                                    // single-owner + finalizer contract, and the same `createdStreams` bookkeeping
                                    // so the teardown closes it exactly once. An interactive/fed stdin under a PTY
                                    // wraps that SAME master a second time as a NON-owning view (`owns = false`):
                                    // the one master is closed exactly once by the owning stdout stream, and there
                                    // is no `dup` — hence no non-CLOEXEC duplicate to leak into a concurrent spawn
                                    // (R-02). `owns` is ignored on the socketpair path (always sole-owning there).
                                    let pipeStream (label: string) (owns: bool) fd =
                                        streamWrapFaultForTests |> Option.iter (fun fault -> fault label)

                                        let stream =
                                            if config.Pty.IsSome then
                                                let owned =
                                                    new Microsoft.Win32.SafeHandles.SafeFileHandle(
                                                        nativeint fd,
                                                        ownsHandle = owns
                                                    )

                                                new PtyStream(owned) :> Stream
                                            else
                                                let socket =
                                                    new Socket(new SafeSocketHandle(nativeint fd, ownsHandle = true))

                                                new NetworkStream(socket, ownsSocket = true) :> Stream

                                        createdStreams.Add stream
                                        stream

                                    // Wrap each retained parent end into an owning stream, clearing its fd
                                    // tracker as ownership transfers so that if a later step (or the teardown)
                                    // runs, it never double-closes an fd the stream now owns.
                                    let stdoutStream =
                                        match stdoutParentRead with
                                        | Some fd ->
                                            let stream = pipeStream "stdout" true fd
                                            stdoutParentRead <- None
                                            Some stream
                                        | None -> None

                                    let stderrStream =
                                        match stderrParentRead with
                                        | Some fd ->
                                            let stream = pipeStream "stderr" true fd
                                            stderrParentRead <- None
                                            Some stream
                                        | None -> None

                                    let stdinStream =
                                        match stdinParentWrite with
                                        | Some fd ->
                                            // Socketpair path: the parent-write end is its own owning stream.
                                            let stream = pipeStream "stdin" true fd
                                            stdinParentWrite <- None
                                            Some stream
                                        | None when ptyStdinOverMaster && stdoutStream.IsSome ->
                                            // PTY interactive/fed stdin: a NON-owning second view over the SAME
                                            // master fd the stdout stream already owns (no `dup`, so nothing extra
                                            // to close and nothing to leak — R-02). The stdout owner closes the one
                                            // master exactly once; disposing this view is a no-op on the fd.
                                            Some(pipeStream "stdin" false ptyMasterFd)
                                        | None -> None

                                    Ok
                                        { Handle = nativeint pid
                                          Stdout = stdoutStream
                                          Stderr = stderrStream
                                          Stdin = stdinStream
                                          // POSIX signals the child's process group directly (killpg); the
                                          // Windows console-ctrl-group flag has no bearing here.
                                          WindowsCtrlGroup = false
                                          // Retain the pty MASTER fd for `RunningProcess.ResizeAsync`
                                          // (ioctl(TIOCSWINSZ) target, D6); `None` for a non-PTY run.
                                          // `ptyMasterFd` is set (>= 0) only on the pty branch above; the one
                                          // owning stdout stream closes it, so the raw fd stays valid for the
                                          // child's whole running lifetime (no `dup`, no extra owner).
                                          PtyControl =
                                            (if ptyMasterFd >= 0 then
                                                 Some(nativeint ptyMasterFd)
                                             else
                                                 None) }
                            with ex ->
                                // A stream ctor (or the priority hook) threw after the child was spawned: tear the
                                // child down and release every parent/child fd exactly once, then report the
                                // original fault as an honest Spawn error — never a raw exception, and the cleanup
                                // never replaces the primary cause.
                                postSpawnTeardown ()
                                Error(ProcessError.Spawn(command.Program, ex.Message))
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
                // Reached only for a fault BEFORE `posix_spawnp` returns a live child — an OOM in
                // `AllocHGlobal`/`marshalCStringArray` (whose own unwind already freed its partial
                // argv/envp), or an `EntryPointNotFoundException` from a posix_spawn* import while wiring
                // the file actions/attributes. No child has been spawned yet, so there is nothing to
                // kill: close whatever fds are still open and report an honest Spawn error (symmetric to
                // `spawnWindowsCore`'s `with ex -> ...`). A fault AFTER a successful `posix_spawnp` is
                // handled by the post-spawn block's own inner try/with, which additionally kills and
                // reaps the running child. The `finally` above has already released the native scratch on
                // this same unwind.
                closeChildSideFds ()
                closeParentEnds ()
                Error(ProcessError.Spawn(command.Program, ex.Message))

    // ----------------------------------------------------------------------------------
    // POSIX pty (Command.Pty): the `setsid --ctty` controlling-terminal helper + host gating
    // ----------------------------------------------------------------------------------
    //
    // A pty needs a controlling terminal, which is a pre-`exec` step `posix_spawn` attributes cannot
    // express — `setsid()` → `ioctl(slave, TIOCSCTTY)` → the slave on fd 0/1/2 (the `login_tty(3)`
    // sequence). As with `setpriv`/cgroup, no managed code may run in a forked child, so this rides the
    // helper-launcher pattern: `setsid --ctty <program> <args…>` (util-linux) does the `setsid()` +
    // `TIOCSCTTY` and `exec`s the target IN PLACE (same pid — so `Spawned.Handle`, the pgid, and
    // kill-on-drop are unchanged). The pty stdio (master/slave) is wired by `spawnPosixViaSpawn` above
    // when `config.Pty` is set. A host lacking that helper (macOS/BSD, an old util-linux) — or the pty
    // devfs — gets a typed `ProcessError.Unsupported`, never a socketpair pretending to be a tty (D9).

    /// Whether the `setsid --ctty` controlling-terminal helper is available on this host (util-linux
    /// `setsid`, resolved on PATH). The test seam forces the verdict so the honest-`Unsupported` path can
    /// be exercised on a host that DOES carry `setsid`.
    let private cttyHelperAvailable () : bool =
        match ptyCttyHelperAvailableForTests with
        | Some hook -> hook ()
        | None ->
            match resolveProgram "setsid" with
            | Ok _ -> true
            | Error _ -> false

    /// The typed `ProcessError.Unsupported` for a host that cannot honor a PTY, or `None` when it can: the
    /// `setsid --ctty` ctty helper must be present (absent on macOS/BSD, an old util-linux), and the pty
    /// devfs (`/dev/ptmx`) must exist. Checked at dispatch, BEFORE any spawn, so a PTY on an unsupported
    /// host is an honest up-front failure rather than a socketpair silently standing in for a tty (D9). A
    /// transient runtime pty-open failure (EMFILE, a devfs race) surfaces later as `ProcessError.Spawn`
    /// from `openPtyPair`, which is correct — that is a per-invocation failure, not a capability gap.
    let private ptyHostUnsupported () : ProcessError option =
        if not (cttyHelperAvailable ()) then
            Some(
                ProcessError.Unsupported
                    "Pty (needs the 'setsid --ctty' controlling-terminal helper from util-linux on PATH; not found here — absent on macOS/BSD and old util-linux)"
            )
        elif not (File.Exists "/dev/ptmx") then
            Some(ProcessError.Unsupported "Pty (host has no /dev/ptmx pseudo-terminal support)")
        else
            None

    /// Rewrite `command` to run under the `setsid --ctty` controlling-terminal helper:
    /// `setsid --ctty <program> <args…>`. `setsid` performs `setsid()` + `TIOCSCTTY` on its stdin — which
    /// the pty stdio wiring points at the slave — making the pty the child's controlling terminal, then
    /// `exec`s the real program IN PLACE. A requested `Uid`/`Gid` drop is nested INSIDE
    /// (`setsid --ctty setpriv <flags> <program> …`), so the (privilege-free) controlling-tty setup runs
    /// before the drop, mirroring how the cgroup launcher nests `setpriv`. `Pty` is KEPT so
    /// `spawnPosixViaSpawn` wires the pty stdio; `Uid`/`Gid`/`Groups` are cleared so it does not ALSO wrap
    /// this in a second `setpriv` layer. Every other knob rides `posix_spawn` and is inherited across the
    /// `exec`s.
    let private cttyShimCommand (command: Command) : Command =
        let config = command.Config

        let target =
            if config.Uid.IsSome || config.Gid.IsSome then
                "setpriv" :: (setprivFlags config @ (config.Program :: List.ofSeq config.Args))
            else
                config.Program :: List.ofSeq config.Args

        Command(
            { config with
                Program = "setsid"
                Args = System.Collections.Immutable.ImmutableList.CreateRange("--ctty" :: target)
                Uid = None
                Gid = None
                Groups = None }
        )

    /// Spawn `command` (which requests `Command.Pty`) as a contained POSIX child with a real controlling
    /// pseudo-terminal. Gates an unsupported host up front (`ptyHostUnsupported`), refuses a supplementary
    /// `Groups` without a drop and a non-root uid/gid drop exactly as the plain path does, then spawns the
    /// `setsid --ctty` shim (with any drop nested inside). A `NotFound` for the just-checked `setsid`
    /// (a PATH race between the availability check and the spawn) maps back to a typed `Unsupported`.
    let private spawnPosixPty (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config

        match ptyHostUnsupported () with
        | Some error -> Error error
        | None ->
            match groupsRequireDropError command with
            | Some error -> Error error
            | None ->
                let proceed () =
                    match spawnPosixViaSpawn (cttyShimCommand command) with
                    | Error(ProcessError.NotFound _) ->
                        Error(
                            ProcessError.Unsupported
                                "Pty (the 'setsid' controlling-terminal helper was not found by the spawn; util-linux setsid must be on PATH)"
                        )
                    | other -> other

                if config.Uid.IsSome || config.Gid.IsSome then
                    match privilegeDropPrecheck command with
                    | Some error -> Error error
                    | None -> proceed ()
                else
                    proceed ()

    /// Spawn `command` as a contained POSIX child. A command requesting a privilege drop (`Uid`/`Gid`) is
    /// rewritten to run through the `setpriv` helper and spawned on the ordinary `posix_spawn` path — the
    /// drop runs in `setpriv` before it `exec`s the real program, so no managed code runs in a forked
    /// child (see the section comment above). Everything else — including a lone `Setsid` — spawns
    /// directly. A refused drop by a non-root caller is rejected up front; a missing `setpriv` helper
    /// becomes a typed `ProcessError.Spawn`, never a silently un-dropped child.
    let spawnPosix (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config

        if config.Pty.IsSome then
            // A PTY gives the child a real controlling pseudo-terminal via the `setsid --ctty` helper (see
            // `spawnPosixPty`); an unsupported host is a typed `Unsupported`, never a silent pipe (D9).
            spawnPosixPty command
        else

            match groupsRequireDropError command with
            | Some error -> Error error
            | None ->
                if config.Uid.IsSome || config.Gid.IsSome then
                    match privilegeDropPrecheck command with
                    | Some error -> Error error
                    | None ->
                        match spawnPosixViaSpawn (setprivCommand command) with
                        | Error(ProcessError.NotFound _) ->
                            // `posix_spawn` could not find `setpriv` on PATH — report it against the ORIGINAL
                            // program (not the helper), so a caller who never mentioned `setpriv` gets a
                            // message that explains what a `Uid`/`Gid` drop needs on this host.
                            Error(
                                ProcessError.Spawn(
                                    command.Program,
                                    "a Uid/Gid privilege drop needs the 'setpriv' helper (util-linux) on PATH; it was not found (available on mainstream Linux; absent on macOS/BSD)"
                                )
                            )
                        | other -> other
                else
                    spawnPosixViaSpawn command

    // ----------------------------------------------------------------------------------
    // Linux cgroup v2: place the child INSIDE its cgroup atomically with its own execution
    // ----------------------------------------------------------------------------------
    //
    // `posix_spawn` starts the child running immediately (there is no portable "start stopped"), so the
    // parent-side `CgroupBackend.Track` write of the child's pid to `cgroup.procs` necessarily lands
    // AFTER the child has already executed some instructions. A user target that forks in that first
    // instant would create a descendant in the PARENT cgroup — outside the requested memory/pids/cpu
    // caps — a real enforcement bypass, not merely a cleanup risk.
    //
    // The fix places the target inside its cgroup BEFORE it runs a single instruction, using the SAME
    // safe helper-process pattern as the `setpriv` uid/gid drop (no managed .NET code runs in an unsafe
    // post-fork child — the obvious `fork()`-in-child port is unsafe on CoreCLR, as the `setpriv` note
    // above explains). A tiny `/bin/sh` launcher writes its OWN pid (`$$`) into the target cgroup's
    // `cgroup.procs` — joining the cgroup — and only THEN `exec`s the real program IN PLACE ($2...). An
    // `exec` keeps the pid, so the target's very first instruction runs with its pid already a cgroup
    // member, and every descendant it forks inherits that cgroup from the kernel. A failed self-migrate
    // (missing / unwritable cgroup) `exit`s WITHOUT exec, so the target never runs outside the cgroup —
    // no escapee. Because the launcher is a separate program reached through the ordinary `posix_spawn`
    // path, everything else composes exactly as it does for `setpriv`: stdio / cwd / env via posix_spawn
    // file actions, `Setsid` via `POSIX_SPAWN_SETSID`, `Umask` via the parent-side set/restore,
    // `Priority` via the post-spawn `setpriority` on the leader (pgid == the launcher's pid == the
    // target's pid after exec), and kill-on-drop via that unchanged pgid. A requested `Uid`/`Gid` drop
    // is nested INSIDE the launcher (`exec setpriv ... program ...`), so the privileged cgroup join runs
    // BEFORE the drop and is never blocked by the child's lowered credentials (cgroup membership is
    // independent of uid and survives the drop). The migration write is by `$$` (the launcher's own,
    // definitionally-live pid), so it can only fail at OPEN (missing/denied cgroup) — never ESRCH — which
    // is what makes the parent-side confirmation (`Native.Cgroup.migrateToCgroup`) able to tell a genuine
    // failure apart from a fast target that has already exited.

    // The self-migrating cgroup launcher script (POSIX sh). `$1` is the target cgroup's `cgroup.procs`
    // path; the program and its args are $2.. and are exec'd verbatim through `"$@"` (passed as separate
    // positional parameters, never interpolated into the script — so no shell parsing of user data). A
    // failed self-migrate exits 127 without exec, so the program never runs outside the cgroup.
    [<Literal>]
    let private cgroupLauncherScript =
        "echo $$ > \"$1\" || exit 127; shift; exec \"$@\""

    /// Spawn `command` so that its pid is placed into the cgroup whose `cgroup.procs` path is
    /// `cgroupProcs` BEFORE the program executes a single instruction, closing the spawn-to-migrate
    /// window (see the section comment above). Linux cgroup-backend only. A requested `Uid`/`Gid` drop is
    /// honoured (nested inside the launcher, after the privileged migration) with the same up-front
    /// non-root rejection as `spawnPosix`. `/bin/sh` missing (pathological on Linux) surfaces as an honest
    /// `ProcessError.ResourceLimit`, never a silent unconstrained run.
    let spawnPosixIntoCgroup (command: Command) (cgroupProcs: string) : Result<Spawned, ProcessError> =
        let config = command.Config

        let launch () =
            // The argv the launcher `exec`s after migrating: the real program (and its args), or — when a
            // uid/gid drop is requested — the `setpriv` helper wrapping it, so the drop happens AFTER the
            // privileged cgroup join. Mirrors `setprivCommand`'s `setpriv <flags> <program> <args...>`.
            let dropOrPlain =
                if config.Uid.IsSome || config.Gid.IsSome then
                    "setpriv" :: (setprivFlags config @ (config.Program :: List.ofSeq config.Args))
                else
                    config.Program :: List.ofSeq config.Args

            let innerArgv =
                if config.Pty.IsSome then
                    // PTY under a cgroup: the launcher joins `cgroup.procs`, then `exec`s the
                    // `setsid --ctty` controlling-terminal shim (with any privilege drop nested inside it,
                    // after the privileged join). The cgroup membership is by the launcher's `$$` — which is
                    // still the target's pid after the whole exec chain and after the helper's `setsid()` —
                    // so limits and the controlling pty compose; `Mechanism.CgroupV2` is reported unchanged.
                    "setsid" :: "--ctty" :: dropOrPlain
                else
                    dropOrPlain

            let launcherArgs = "-c" :: cgroupLauncherScript :: "sh" :: cgroupProcs :: innerArgv

            // Every other knob (stdio, cwd, env, Priority, Umask, Setsid) is preserved and applied by
            // posix_spawn to the launcher, then inherited across its `exec`(s). `Uid`/`Gid`/`Groups` are
            // cleared so `spawnPosixViaSpawn` does not ALSO wrap the launcher in a second `setpriv` layer
            // (the drop, and its `--groups`/`--clear-groups`, is already nested in `innerArgv` above).
            let launcherConfig =
                { config with
                    Program = "/bin/sh"
                    Args = System.Collections.Immutable.ImmutableList.CreateRange launcherArgs
                    Uid = None
                    Gid = None
                    Groups = None }

            match spawnPosixViaSpawn (Command launcherConfig) with
            | Error(ProcessError.NotFound _) ->
                // Only `/bin/sh` itself can be NotFound here (a missing target program instead makes the
                // launcher's `exec` fail and the shell exit 127 — an honest non-zero run, not a spawn
                // NotFound). Placing the child in its cgroup atomically needs the launcher, so report an
                // honest limit-enforcement failure rather than a silent unconstrained run.
                Error(
                    ProcessError.ResourceLimit
                        "placing the process into its cgroup atomically needs /bin/sh (the migration launcher), which was not found on this host"
                )
            | other -> other

        // A PTY gates the same unsupported-host check as `spawnPosixPty` before the cgroup launch; the rest
        // of the dispatch (Groups-need-a-drop, non-root drop precheck) is shared with the non-pty path.
        let ptyGate = if config.Pty.IsSome then ptyHostUnsupported () else None

        match ptyGate with
        | Some error -> Error error
        | None ->
            match groupsRequireDropError command with
            | Some error -> Error error
            | None ->
                if config.Uid.IsSome || config.Gid.IsSome then
                    match privilegeDropPrecheck command with
                    | Some error -> Error error
                    | None -> launch ()
                else
                    launch ()
