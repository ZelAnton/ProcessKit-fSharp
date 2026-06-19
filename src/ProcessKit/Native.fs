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

    /// A freshly spawned, contained child: managed read streams for stdout/stderr and the
    /// OS process handle/pid the platform layer waits on.
    type Spawned =
        {
            /// The OS process handle (Windows) or pid (Unix), as a native integer.
            Handle: nativeint
            Stdout: Stream
            Stderr: Stream
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

        if not command.ClearEnv then
            for entry in
                Environment.GetEnvironmentVariables()
                |> Seq.cast<System.Collections.DictionaryEntry> do
                env[string entry.Key] <- string entry.Value

        for key, value in command.EnvOverrides do
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
        if not command.ClearEnv && List.isEmpty command.EnvOverrides then
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
        let outPipe =
            new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

        let errPipe =
            new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

        let inPipe =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)

        let mutable startup = STARTUPINFO()
        startup.cb <- Marshal.SizeOf<STARTUPINFO>()
        startup.dwFlags <- STARTF_USESTDHANDLES
        startup.hStdInput <- inPipe.ClientSafePipeHandle.DangerousGetHandle()
        startup.hStdOutput <- outPipe.ClientSafePipeHandle.DangerousGetHandle()
        startup.hStdError <- errPipe.ClientSafePipeHandle.DangerousGetHandle()

        let mutable info = PROCESS_INFORMATION()
        let commandLine = buildWindowsCommandLine command

        let workingDirectory =
            command.WorkingDirectory |> Option.defaultWith Directory.GetCurrentDirectory

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

        if not created then
            outPipe.Dispose()
            errPipe.Dispose()
            inPipe.Dispose()

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
            outPipe.Dispose()
            errPipe.Dispose()
            inPipe.Dispose()
            Error(ProcessError.Spawn(command.Program, $"could not assign process to job object: {message}"))
        else
            ResumeThread info.hThread |> ignore
            CloseHandle info.hThread |> ignore
            // Drop the parent's copies of the child's handle ends so reads see EOF when the
            // child exits; closing the stdin write end gives the child an empty stdin.
            outPipe.DisposeLocalCopyOfClientHandle()
            errPipe.DisposeLocalCopyOfClientHandle()
            inPipe.DisposeLocalCopyOfClientHandle()
            inPipe.Dispose()

            Ok
                { Handle = info.hProcess
                  Stdout = outPipe
                  Stderr = errPipe }

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
        let outFds = Array.zeroCreate<int> 2
        let errFds = Array.zeroCreate<int> 2

        if pipe outFds <> 0 then
            Error(ProcessError.Spawn(command.Program, "pipe() failed for stdout"))
        elif pipe errFds <> 0 then
            close outFds[0] |> ignore
            close outFds[1] |> ignore
            Error(ProcessError.Spawn(command.Program, "pipe() failed for stderr"))
        else
            let outRead, outWrite = outFds[0], outFds[1]
            let errRead, errWrite = errFds[0], errFds[1]
            let devNull = openFile ("/dev/null", O_RDONLY)
            // Keep these fds out of any other concurrent spawn's child.
            setCloseOnExec outRead
            setCloseOnExec outWrite
            setCloseOnExec errRead
            setCloseOnExec errWrite
            setCloseOnExec devNull
            // posix_spawn_file_actions_t / posix_spawnattr_t are opaque; a generous zeroed
            // buffer holds either platform's representation (glibc structs or macOS pointers).
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
                posix_spawn_file_actions_adddup2 (fileActions, outWrite, 1) |> ignore
                posix_spawn_file_actions_adddup2 (fileActions, errWrite, 2) |> ignore

                if devNull >= 0 then
                    posix_spawn_file_actions_adddup2 (fileActions, devNull, 0) |> ignore

                posix_spawn_file_actions_addclose (fileActions, outRead) |> ignore
                posix_spawn_file_actions_addclose (fileActions, outWrite) |> ignore
                posix_spawn_file_actions_addclose (fileActions, errRead) |> ignore
                posix_spawn_file_actions_addclose (fileActions, errWrite) |> ignore

                if devNull >= 0 then
                    posix_spawn_file_actions_addclose (fileActions, devNull) |> ignore

                match command.WorkingDirectory with
                | Some directory -> posix_spawn_file_actions_addchdir_np (fileActions, directory) |> ignore
                | None -> ()

                posix_spawnattr_setflags (attributes, POSIX_SPAWN_SETPGROUP) |> ignore
                posix_spawnattr_setpgroup (attributes, 0) |> ignore

                let mutable pid = 0

                let rc =
                    posix_spawnp (&pid, command.Program, fileActions, attributes, argvPointer, envpPointer)

                // The parent no longer needs the child's pipe ends or /dev/null.
                close outWrite |> ignore
                close errWrite |> ignore

                if devNull >= 0 then
                    close devNull |> ignore

                if rc <> 0 then
                    close outRead |> ignore
                    close errRead |> ignore

                    if rc = ENOENT then
                        Error(ProcessError.NotFound(command.Program, None))
                    else
                        Error(ProcessError.Spawn(command.Program, $"posix_spawn failed ({rc})"))
                else
                    let stdout =
                        new FileStream(new SafeFileHandle(nativeint outRead, true), FileAccess.Read)

                    let stderr =
                        new FileStream(new SafeFileHandle(nativeint errRead, true), FileAccess.Read)

                    Ok
                        { Handle = nativeint pid
                          Stdout = stdout
                          Stderr = stderr }
            finally
                posix_spawn_file_actions_destroy fileActions |> ignore
                posix_spawnattr_destroy attributes |> ignore
                Marshal.FreeHGlobal fileActions
                Marshal.FreeHGlobal attributes
                freeCStringArray argvPointer argvAllocations
                freeCStringArray envpPointer envpAllocations
