namespace ProcessKit

open System
open System.ComponentModel
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles

/// Low-level, platform-specific process spawning and containment.
///
/// Internal: the public surface is `ProcessGroup` / `JobRunner`. P/Invoke declarations
/// here compile on every platform (they are just `extern` signatures); call sites are
/// guarded by `RuntimeInformation.IsOSPlatform` so a libc/kernel32 entry point is only
/// invoked on the OS that has it.
module internal Native =

    /// A freshly spawned, contained child: the OS process handle/pid the platform layer waits
    /// on, plus managed streams for whichever of stdin/stdout/stderr are connected to a pipe.
    type Spawned =
        {
            /// The OS process handle (Windows) or pid (Unix), as a native integer.
            Handle: nativeint
            /// Parent read stream for the child's stdout — `Some` only in `Piped` mode.
            Stdout: Stream option
            /// Parent read stream for the child's stderr — `Some` only in `Piped` mode.
            Stderr: Stream option
            /// Parent write stream for the child's stdin — `Some` only when a stdin pipe was created.
            Stdin: Stream option
        }

    // ----------------------------------------------------------------------------------
    // Shared: command-line / argv helpers
    // ----------------------------------------------------------------------------------

    /// Quote a single argument per the Windows `CommandLineToArgvW` rules.
    let private quoteWindowsArg (arg: string) =
        let needsQuoting =
            arg.Length = 0
            || arg
               |> Seq.exists (fun c -> c = ' ' || c = '\t' || c = '\n' || c = '\v' || c = '"')

        if not needsQuoting then
            arg
        else
            let sb = StringBuilder()
            sb.Append('"') |> ignore
            let mutable i = 0

            while i < arg.Length do
                let mutable backslashes = 0

                while i < arg.Length && arg[i] = '\\' do
                    backslashes <- backslashes + 1
                    i <- i + 1

                if i = arg.Length then
                    sb.Append('\\', backslashes * 2) |> ignore
                elif arg[i] = '"' then
                    sb.Append('\\', backslashes * 2 + 1).Append('"') |> ignore
                    i <- i + 1
                else
                    sb.Append('\\', backslashes).Append(arg[i]) |> ignore
                    i <- i + 1

            sb.Append('"').ToString()

    let private buildWindowsCommandLine (command: Command) =
        let parts = command.Program :: List.ofSeq command.Arguments
        parts |> List.map quoteWindowsArg |> String.concat " "

    /// The effective environment for the child: the inherited set (unless cleared) with the
    /// command's overrides applied (`Some` sets, `None` removes).
    let private effectiveEnvironment (command: Command) =
        // Windows environment names are case-insensitive; POSIX names are case-sensitive.
        let comparer =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        let env = System.Collections.Generic.Dictionary<string, string>(comparer)

        if not command.Config.ClearEnv then
            for entry in
                Environment.GetEnvironmentVariables()
                |> Seq.cast<System.Collections.DictionaryEntry> do
                env[string entry.Key] <- string entry.Value

        for key, value in command.Config.EnvOverrides do
            match value with
            | Some v -> env[key] <- v
            | None -> env.Remove key |> ignore

        env

    // ----------------------------------------------------------------------------------
    // Windows: Job Object + CREATE_SUSPENDED → assign → resume
    // ----------------------------------------------------------------------------------

    [<Literal>]
    let private CREATE_SUSPENDED = 0x00000004u

    [<Literal>]
    let private CREATE_UNICODE_ENVIRONMENT = 0x00000400u

    [<Literal>]
    let private STARTF_USESTDHANDLES = 0x00000100u

    [<Literal>]
    let private JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000u

    [<Literal>]
    let private JobObjectExtendedLimitInformation = 9

    [<Literal>]
    let private INFINITE = 0xFFFFFFFFu

    [<Literal>]
    let private ERROR_FILE_NOT_FOUND = 2

    [<Literal>]
    let private ERROR_PATH_NOT_FOUND = 3

    [<StructLayout(LayoutKind.Sequential)>]
    type private STARTUPINFO =
        struct
            val mutable cb: int
            val mutable lpReserved: nativeint
            val mutable lpDesktop: nativeint
            val mutable lpTitle: nativeint
            val mutable dwX: int
            val mutable dwY: int
            val mutable dwXSize: int
            val mutable dwYSize: int
            val mutable dwXCountChars: int
            val mutable dwYCountChars: int
            val mutable dwFillAttribute: int
            val mutable dwFlags: uint32
            val mutable wShowWindow: uint16
            val mutable cbReserved2: uint16
            val mutable lpReserved2: nativeint
            val mutable hStdInput: nativeint
            val mutable hStdOutput: nativeint
            val mutable hStdError: nativeint
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private PROCESS_INFORMATION =
        struct
            val mutable hProcess: nativeint
            val mutable hThread: nativeint
            val mutable dwProcessId: uint32
            val mutable dwThreadId: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_BASIC_LIMIT_INFORMATION =
        struct
            val mutable PerProcessUserTimeLimit: int64
            val mutable PerJobUserTimeLimit: int64
            val mutable LimitFlags: uint32
            val mutable MinimumWorkingSetSize: unativeint
            val mutable MaximumWorkingSetSize: unativeint
            val mutable ActiveProcessLimit: uint32
            val mutable Affinity: unativeint
            val mutable PriorityClass: uint32
            val mutable SchedulingClass: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private IO_COUNTERS =
        struct
            val mutable ReadOperationCount: uint64
            val mutable WriteOperationCount: uint64
            val mutable OtherOperationCount: uint64
            val mutable ReadTransferCount: uint64
            val mutable WriteTransferCount: uint64
            val mutable OtherTransferCount: uint64
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_EXTENDED_LIMIT_INFORMATION =
        struct
            val mutable BasicLimitInformation: JOBOBJECT_BASIC_LIMIT_INFORMATION
            val mutable IoInfo: IO_COUNTERS
            val mutable ProcessMemoryLimit: unativeint
            val mutable JobMemoryLimit: unativeint
            val mutable PeakProcessMemoryUsed: unativeint
            val mutable PeakJobMemoryUsed: unativeint
        end

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private CreateJobObjectW(nativeint lpJobAttributes, nativeint lpName)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetInformationJobObject(nativeint hJob, int infoClass, nativeint lpInfo, uint32 cbInfo)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private AssignProcessToJobObject(nativeint hJob, nativeint hProcess)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private TerminateJobObject(nativeint hJob, uint32 uExitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private TerminateProcess(nativeint hProcess, uint32 uExitCode)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private CreateProcessW(
        nativeint lpApplicationName,
        string lpCommandLine,
        nativeint lpProcessAttributes,
        nativeint lpThreadAttributes,
        bool bInheritHandles,
        uint32 dwCreationFlags,
        nativeint lpEnvironment,
        string lpCurrentDirectory,
        STARTUPINFO& lpStartupInfo,
        PROCESS_INFORMATION& lpProcessInformation
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private ResumeThread(nativeint hThread)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetExitCodeProcess(nativeint hProcess, uint32& lpExitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private WaitForSingleObject(nativeint hHandle, uint32 dwMilliseconds)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private CloseHandle(nativeint hObject)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private GetProcessId(nativeint hProcess)

    /// The OS process id behind a Windows process handle.
    let processIdWindows (hProcess: nativeint) : int = int (GetProcessId hProcess)

    // Std-handle ids and flags for the Inherit / Null stdio modes.
    [<Literal>]
    let private STD_INPUT_HANDLE = -10

    [<Literal>]
    let private STD_OUTPUT_HANDLE = -11

    [<Literal>]
    let private STD_ERROR_HANDLE = -12

    [<Literal>]
    let private GENERIC_READ = 0x80000000u

    [<Literal>]
    let private GENERIC_WRITE = 0x40000000u

    [<Literal>]
    let private FILE_SHARE_RW = 0x00000003u

    [<Literal>]
    let private OPEN_EXISTING = 3u

    [<Literal>]
    let private HANDLE_FLAG_INHERIT = 1u

    [<Literal>]
    let private DUPLICATE_SAME_ACCESS = 2u

    [<Literal>]
    let private INVALID_HANDLE_VALUE = -1

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private GetStdHandle(int nStdHandle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private GetCurrentProcess()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetHandleInformation(nativeint hObject, uint32 dwMask, uint32 dwFlags)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private DuplicateHandle(
        nativeint hSourceProcess,
        nativeint hSource,
        nativeint hTargetProcess,
        nativeint& lpTargetHandle,
        uint32 dwDesiredAccess,
        bool bInheritHandle,
        uint32 dwOptions
    )

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern nativeint private CreateFileW(
        string lpFileName,
        uint32 dwDesiredAccess,
        uint32 dwShareMode,
        nativeint lpSecurityAttributes,
        uint32 dwCreationDisposition,
        uint32 dwFlagsAndAttributes,
        nativeint hTemplateFile
    )

    /// An inheritable handle to the null device, for `StdioMode.Null`.
    let private inheritableNul (access: uint32) : nativeint =
        let handle =
            CreateFileW("NUL", access, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0u, IntPtr.Zero)

        if handle <> IntPtr.Zero && handle <> nativeint INVALID_HANDLE_VALUE then
            SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) |> ignore

        handle

    /// An inheritable duplicate of one of the parent's std handles, for `StdioMode.Inherit`.
    let private inheritableStdHandle (stdHandleId: int) : nativeint =
        let source = GetStdHandle stdHandleId
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if
            source <> IntPtr.Zero
            && DuplicateHandle(current, source, current, &duplicate, 0u, true, DUPLICATE_SAME_ACCESS)
        then
            duplicate
        else
            IntPtr.Zero

    /// Create a Job Object that kills its whole process tree when its last handle closes
    /// (`KILL_ON_JOB_CLOSE`). This is how kill-on-drop maps to .NET: the owning
    /// `ProcessGroup` holds the only handle, and disposing it (or GC finalizing it) reaps
    /// the tree.
    let createWindowsJob () : Result<nativeint, ProcessError> =
        let job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero)

        if job = IntPtr.Zero then
            Error(ProcessError.Spawn("<job>", Win32Exception(Marshal.GetLastWin32Error()).Message))
        else
            let mutable info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
            info.BasicLimitInformation.LimitFlags <- JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
            let buffer = Marshal.AllocHGlobal size

            try
                Marshal.StructureToPtr(info, buffer, false)

                if SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size) then
                    Ok job
                else
                    let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                    CloseHandle job |> ignore
                    Error(ProcessError.Spawn("<job>", message))
            finally
                Marshal.FreeHGlobal buffer

    let terminateWindowsJob (job: nativeint) = TerminateJobObject(job, 1u) |> ignore

    let closeWindowsHandle (handle: nativeint) = CloseHandle handle |> ignore

    /// Wait for a Windows process to exit and read its exit code. Blocks a thread-pool
    /// thread for the child's lifetime (acceptable for the run-to-completion verbs).
    let waitWindows (hProcess: nativeint) : Task<Outcome> =
        Task.Run(fun () ->
            WaitForSingleObject(hProcess, INFINITE) |> ignore
            let mutable code = 0u
            GetExitCodeProcess(hProcess, &code) |> ignore
            Outcome.Exited(int code))

    let private buildWindowsEnvironment (command: Command) : nativeint =
        if not command.Config.ClearEnv && List.isEmpty command.Config.EnvOverrides then
            IntPtr.Zero
        else
            let env = effectiveEnvironment command
            let sb = StringBuilder()

            for entry in
                env
                |> Seq.sortWith (fun a b -> String.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)) do
                sb.Append(entry.Key).Append('=').Append(entry.Value).Append(char 0) |> ignore

            // The block is "name=value\0…" terminated by one more null; an empty block must
            // still be a double null, or CreateProcess reads past it.
            if env.Count = 0 then
                sb.Append(char 0) |> ignore

            sb.Append(char 0) |> ignore
            Marshal.StringToHGlobalUni(sb.ToString())

    // CreateProcess(bInheritHandles = true) snapshots EVERY inheritable handle in the
    // process, so two overlapping spawns could cross-inherit each other's pipe ends and a
    // run's read would never see EOF. Serialize the spawn window (pipe setup → CreateProcess
    // → drop the inheritable copies); reads happen afterwards, off the lock.
    let private windowsSpawnLock = obj ()

    /// Spawn `command` suspended, assign it to `job` while still suspended (so no
    /// grandchild can escape the container), then resume it. Returns the process handle and
    /// managed read streams for stdout/stderr.
    let private spawnWindowsCore (job: nativeint) (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        let stdinWanted = config.StdinSource.IsSome || config.KeepStdinOpen

        // stdin is always a pipe so we control EOF; the write end is kept (interactive) or closed.
        let inPipe =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)

        // For an output stream: the inheritable child-side handle, the parent read stream
        // (`Some` only when piped), and a cleanup that closes the child-side handle after spawn.
        let setupOut (mode: StdioMode) (stdHandleId: int) : nativeint * Stream option * (unit -> unit) =
            match mode with
            | StdioMode.Piped ->
                let pipe =
                    new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

                pipe.ClientSafePipeHandle.DangerousGetHandle(),
                Some(pipe :> Stream),
                (fun () -> pipe.DisposeLocalCopyOfClientHandle())
            | StdioMode.Null ->
                let handle = inheritableNul GENERIC_WRITE
                handle, None, (fun () -> CloseHandle handle |> ignore)
            | StdioMode.Inherit ->
                let handle = inheritableStdHandle stdHandleId

                handle,
                None,
                (fun () ->
                    if handle <> IntPtr.Zero then
                        CloseHandle handle |> ignore)

        let outChild, outStream, outCleanup = setupOut config.StdoutMode STD_OUTPUT_HANDLE
        let errChild, errStream, errCleanup = setupOut config.StderrMode STD_ERROR_HANDLE

        let mutable startup = STARTUPINFO()
        startup.cb <- Marshal.SizeOf<STARTUPINFO>()
        startup.dwFlags <- STARTF_USESTDHANDLES
        startup.hStdInput <- inPipe.ClientSafePipeHandle.DangerousGetHandle()
        startup.hStdOutput <- outChild
        startup.hStdError <- errChild

        let mutable info = PROCESS_INFORMATION()
        let commandLine = buildWindowsCommandLine command

        let workingDirectory =
            config.WorkingDirectory |> Option.defaultWith Directory.GetCurrentDirectory

        let environment = buildWindowsEnvironment command

        let flags =
            CREATE_SUSPENDED
            ||| (if environment = IntPtr.Zero then
                     0u
                 else
                     CREATE_UNICODE_ENVIRONMENT)

        let created =
            CreateProcessW(
                IntPtr.Zero,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                flags,
                environment,
                workingDirectory,
                &startup,
                &info
            )

        let lastError = Marshal.GetLastWin32Error()

        if environment <> IntPtr.Zero then
            Marshal.FreeHGlobal environment

        let releaseStdio () =
            outCleanup ()
            errCleanup ()
            outStream |> Option.iter (fun s -> s.Dispose())
            errStream |> Option.iter (fun s -> s.Dispose())
            inPipe.Dispose()

        if not created then
            releaseStdio ()

            if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                Error(ProcessError.NotFound(command.Program, None))
            else
                Error(ProcessError.Spawn(command.Program, Win32Exception(lastError).Message))
        elif not (AssignProcessToJobObject(job, info.hProcess)) then
            // Suspended but uncontained — kill it rather than let it run free.
            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
            TerminateProcess(info.hProcess, 1u) |> ignore
            CloseHandle info.hThread |> ignore
            CloseHandle info.hProcess |> ignore
            releaseStdio ()
            Error(ProcessError.Spawn(command.Program, $"could not assign process to job object: {message}"))
        else
            ResumeThread info.hThread |> ignore
            CloseHandle info.hThread |> ignore
            // Drop the parent's copies of the child-side handles now that the child has inherited
            // them, so reads see EOF when the child exits.
            inPipe.DisposeLocalCopyOfClientHandle()
            outCleanup ()
            errCleanup ()

            let stdinStream =
                if stdinWanted then
                    Some(inPipe :> Stream)
                else
                    inPipe.Dispose() // close stdin write end -> child sees EOF
                    None

            Ok
                { Handle = info.hProcess
                  Stdout = outStream
                  Stderr = errStream
                  Stdin = stdinStream }

    let spawnWindows (job: nativeint) (command: Command) : Result<Spawned, ProcessError> =
        lock windowsSpawnLock (fun () -> spawnWindowsCore job command)

    // ----------------------------------------------------------------------------------
    // POSIX: posix_spawn into a new process group (Linux / macOS)
    // ----------------------------------------------------------------------------------

    [<Literal>]
    let private POSIX_SPAWN_SETPGROUP = 0x02s

    [<Literal>]
    let private O_RDONLY = 0

    [<Literal>]
    let private O_WRONLY = 1

    [<Literal>]
    let private SIGKILL = 9

    [<Literal>]
    let private SIGTERM = 15

    [<Literal>]
    let private ENOENT = 2

    [<Literal>]
    let private F_SETFD = 2

    [<Literal>]
    let private FD_CLOEXEC = 1

    [<DllImport("libc", SetLastError = true)>]
    extern int private pipe(int[] fds)

    [<DllImport("libc", SetLastError = true)>]
    extern int private fcntl(int fd, int cmd, int arg)

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
    let private decodeWaitStatus (status: int) : Outcome =
        if status &&& 0x7f = 0 then
            Outcome.Exited((status >>> 8) &&& 0xff)
        elif status &&& 0x7f <> 0x7f then
            Outcome.Signalled(Some(status &&& 0x7f))
        else
            Outcome.Exited 0

    /// Kill an entire POSIX process group (teardown / cancellation).
    let killProcessGroup (pgid: int) = killpg (pgid, SIGKILL) |> ignore

    /// Ask an entire POSIX process group to terminate gracefully (SIGTERM).
    let terminateProcessGroup (pgid: int) = killpg (pgid, SIGTERM) |> ignore

    /// True while any process remains in the group (signal 0 probes existence).
    let processGroupAlive (pgid: int) = killpg (pgid, 0) = 0

    // Mark a fd close-on-exec so a *different* concurrent spawn does not inherit this run's
    // pipe ends (which outlive the spawn). Our own child still gets fds 0/1/2 because dup2
    // clears CLOEXEC on its target.
    let private setCloseOnExec (fd: int) =
        if fd >= 0 then
            fcntl (fd, F_SETFD, FD_CLOEXEC) |> ignore

    /// Reap a POSIX child and report how it concluded.
    let waitPosix (pid: nativeint) : Task<Outcome> =
        Task.Run(fun () ->
            let mutable status = 0
            waitpid (int pid, &status, 0) |> ignore
            decodeWaitStatus status)

    /// Spawn `command` into a brand-new process group (`POSIX_SPAWN_SETPGROUP`, so pgid = the
    /// child's pid) and capture its stdout/stderr. The whole group can later be reaped with
    /// `killProcessGroup`.
    let spawnPosix (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        let stdinWanted = config.StdinSource.IsSome || config.KeepStdinOpen
        // Child-side fds the parent closes after spawn (the child gets its own via dup2).
        let childSideFds = System.Collections.Generic.List<int>()

        let openNul (flags: int) =
            let fd = openFile ("/dev/null", flags)

            if fd >= 0 then
                setCloseOnExec fd
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

            if pipe fds <> 0 then
                failure <- Some $"pipe() failed for {label}"
                None
            else
                setCloseOnExec fds[0]
                setCloseOnExec fds[1]
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
            | StdioMode.Inherit -> stdoutChildFd <- -1

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
            | StdioMode.Inherit -> stderrChildFd <- -1

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
            let argv = command.Program :: List.ofSeq command.Arguments

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

                match config.WorkingDirectory with
                | Some directory -> posix_spawn_file_actions_addchdir_np (fileActions, directory) |> ignore
                | None -> ()

                posix_spawnattr_setflags (attributes, POSIX_SPAWN_SETPGROUP) |> ignore
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
