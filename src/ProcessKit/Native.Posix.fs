namespace ProcessKit.Native

open ProcessKit
open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
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
    // call under the AArch64 variadic ABI): Linux gets O_CLOEXEC via pipe2/open; macOS gets
    // POSIX_SPAWN_CLOEXEC_DEFAULT, which closes every non-dup2 fd in the child at exec.
    [<Literal>]
    let private O_CLOEXEC = 0x80000

    [<Literal>]
    let private POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000s

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
    extern int private pipe(int[] fds)

    [<DllImport("libc", SetLastError = true)>]
    extern int private pipe2(int[] fds, int flags)

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

    // Create a pipe whose ends are close-on-exec so a *different* concurrent spawn does not
    // inherit this run's pipe ends (which outlive the spawn). Linux sets it atomically with
    // pipe2(O_CLOEXEC); macOS lacks pipe2, and relies on POSIX_SPAWN_CLOEXEC_DEFAULT instead.
    let private createPipe (fds: int[]) : int =
        if isMacOs then pipe fds else pipe2 (fds, O_CLOEXEC)

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

    /// Reap a POSIX child and report how it concluded — event-driven: a shared, process-wide SIGCHLD
    /// registration (not a thread parked per child) re-checks every outstanding wait when any child
    /// changes state, so a piped POSIX child no longer holds a dedicated thread-pool thread blocked in
    /// `waitpid` for its whole lifetime.
    ///
    /// Idempotent per pid: a second call while a wait for the same pid is already in flight reuses the
    /// existing registration's task instead of overwriting it in `pendingWaits` — an unconditional
    /// overwrite would strand the earlier `TaskCompletionSource` forever (nothing would ever complete
    /// it, since `completePending`'s `TryRemove` only ever observes the newer entry). Both callers
    /// observe the same eventual outcome.
    let rec waitPosix (pid: nativeint) : Task<Outcome> =
        ensureSigchldRegistration ()
        let intPid = int pid

        match pendingWaits.TryGetValue intPid with
        | true, existing -> existing.Tcs.Task
        | false, _ ->
            let tcs =
                TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            let pending = { Tcs = tcs }

            if pendingWaits.TryAdd(intPid, pending) then
                // The child may already have exited — even before this call started — so probe once
                // immediately rather than waiting on a SIGCHLD that may already have been delivered.
                tryReapPending intPid |> ignore
                tcs.Task
            else
                // Lost the race to register first — a concurrent `waitPosix` call for the same pid won
                // in between our `TryGetValue` miss and this `TryAdd`. Reuse the winner's entry instead.
                waitPosix pid

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

    /// Spawn `command` into a brand-new process group (`POSIX_SPAWN_SETPGROUP`, so pgid = the
    /// child's pid) and capture its stdout/stderr. The whole group can later be reaped with
    /// `killProcessGroup`.
    let spawnPosix (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        let stdinWanted = config.StdinSource.IsSome || config.KeepStdinOpen
        // Child-side fds the parent closes after spawn (the child gets its own via dup2).
        let childSideFds = System.Collections.Generic.List<int>()

        let openNul (flags: int) =
            let fd = openDevNull flags

            if fd >= 0 then
                childSideFds.Add fd

            fd

        // Per stream: the fd the child should see at the target slot (-1 = inherit, no dup2), and
        // the fd the parent keeps as a stream (read for stdout/stderr, write for stdin).
        let mutable stdinChildFd = -1
        let mutable stdinParentWrite: int option = None
        let mutable stdoutChildFd = -1
        let mutable stdoutParentRead: int option = None
        let mutable stderrChildFd = -1
        let mutable stderrParentRead: int option = None
        let mutable failure: string option = None

        let makePipe (label: string) =
            let fds = Array.zeroCreate<int> 2

            if createPipe fds <> 0 then
                failure <- Some $"pipe() failed for {label}"
                None
            else
                Some(fds[0], fds[1])

        // stdin
        if stdinWanted then
            match makePipe "stdin" with
            | Some(readFd, writeFd) ->
                stdinChildFd <- readFd
                childSideFds.Add readFd
                stdinParentWrite <- Some writeFd
            | None -> ()
        else
            stdinChildFd <- openNul O_RDONLY

        // stdout
        if failure.IsNone then
            match config.StdoutMode with
            | StdioMode.Piped ->
                match makePipe "stdout" with
                | Some(readFd, writeFd) ->
                    stdoutParentRead <- Some readFd
                    stdoutChildFd <- writeFd
                    childSideFds.Add writeFd
                | None -> ()
            | StdioMode.Null -> stdoutChildFd <- openNul O_WRONLY
            | StdioMode.Inherit ->
                // Inherit the parent's stdout. On macOS, POSIX_SPAWN_CLOEXEC_DEFAULT closes every fd
                // not named by a file action at exec, so register a self-dup2 (1 -> 1) to keep fd 1
                // open in the child; on Linux the fd is inherited naturally, so no dup2 is needed.
                stdoutChildFd <- if isMacOs then 1 else -1

        // stderr
        if failure.IsNone then
            match config.StderrMode with
            | StdioMode.Piped ->
                match makePipe "stderr" with
                | Some(readFd, writeFd) ->
                    stderrParentRead <- Some readFd
                    stderrChildFd <- writeFd
                    childSideFds.Add writeFd
                | None -> ()
            | StdioMode.Null -> stderrChildFd <- openNul O_WRONLY
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
            // posix_spawn_file_actions_t / posix_spawnattr_t are opaque; a generous zeroed buffer
            // holds either platform's representation (glibc structs or macOS pointers).
            let fileActions = Marshal.AllocHGlobal 1024
            let attributes = Marshal.AllocHGlobal 1024
            let argv = command.Program :: List.ofSeq command.Config.Args

            let envp =
                effectiveEnvironment command
                |> Seq.map (fun entry -> $"{entry.Key}={entry.Value}")
                |> List.ofSeq

            let argvPointer, argvAllocations = marshalCStringArray argv
            let envpPointer, envpAllocations = marshalCStringArray envp

            try
                posix_spawn_file_actions_init fileActions |> ignore
                posix_spawnattr_init attributes |> ignore

                if stdinChildFd >= 0 then
                    posix_spawn_file_actions_adddup2 (fileActions, stdinChildFd, 0) |> ignore

                if stdoutChildFd >= 0 then
                    posix_spawn_file_actions_adddup2 (fileActions, stdoutChildFd, 1) |> ignore

                if stderrChildFd >= 0 then
                    posix_spawn_file_actions_adddup2 (fileActions, stderrChildFd, 2) |> ignore

                // After dup2, close the original child-side fds so only 0/1/2 remain in the child.
                for fd in childSideFds do
                    posix_spawn_file_actions_addclose (fileActions, fd) |> ignore

                // Also close the parent-kept ends in the child. This is what guarantees EOF: a
                // child must never inherit a writer to its own stdin. We do it explicitly rather
                // than rely on FD_CLOEXEC, whose `fcntl` is variadic and is mis-passed by a
                // fixed-signature P/Invoke on the AArch64 variadic ABI (Apple Silicon), so CLOEXEC
                // never takes effect there and the child would block forever waiting for stdin.
                stdinParentWrite
                |> Option.iter (fun fd -> posix_spawn_file_actions_addclose (fileActions, fd) |> ignore)

                stdoutParentRead
                |> Option.iter (fun fd -> posix_spawn_file_actions_addclose (fileActions, fd) |> ignore)

                stderrParentRead
                |> Option.iter (fun fd -> posix_spawn_file_actions_addclose (fileActions, fd) |> ignore)

                match config.WorkingDirectory with
                | Some directory -> posix_spawn_file_actions_addchdir_np (fileActions, directory) |> ignore
                | None -> ()

                let spawnFlags =
                    if isMacOs then
                        POSIX_SPAWN_SETPGROUP ||| POSIX_SPAWN_CLOEXEC_DEFAULT
                    else
                        POSIX_SPAWN_SETPGROUP

                posix_spawnattr_setflags (attributes, spawnFlags) |> ignore
                posix_spawnattr_setpgroup (attributes, 0) |> ignore

                let mutable pid = 0

                let rc =
                    posix_spawnp (&pid, command.Program, fileActions, attributes, argvPointer, envpPointer)

                // The parent never needs the child-side fds.
                for fd in childSideFds do
                    closeFd fd

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
                        let readStream fd =
                            new FileStream(new SafeFileHandle(nativeint fd, true), FileAccess.Read) :> Stream

                        let writeStream fd =
                            new FileStream(new SafeFileHandle(nativeint fd, true), FileAccess.Write) :> Stream

                        Ok
                            { Handle = nativeint pid
                              Stdout = stdoutParentRead |> Option.map readStream
                              Stderr = stderrParentRead |> Option.map readStream
                              Stdin = stdinParentWrite |> Option.map writeStream }
            finally
                posix_spawn_file_actions_destroy fileActions |> ignore
                posix_spawnattr_destroy attributes |> ignore
                Marshal.FreeHGlobal fileActions
                Marshal.FreeHGlobal attributes
                freeCStringArray argvPointer argvAllocations
                freeCStringArray envpPointer envpAllocations
