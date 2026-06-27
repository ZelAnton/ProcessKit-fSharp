namespace ProcessKit

open System
open System.ComponentModel
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Text
open System.Threading
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
        let parts = command.Program :: command.Config.Args
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
    let private CREATE_NO_WINDOW = 0x08000000u

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

    /// A `WaitHandle` over an already-owned `SafeWaitHandle`. Subclassing avoids `new ManualResetEvent()`
    /// — which allocates a throwaway kernel event that assigning `SafeWaitHandle` would orphan until GC.
    type private OwnedProcessWait(handle: SafeWaitHandle) =
        inherit WaitHandle()
        do base.SafeWaitHandle <- handle

    /// Wait for a Windows process to exit and read its exit code — asynchronously, via a thread-pool
    /// *registered wait* (one pool wait thread serves ~63 handles) instead of parking a dedicated
    /// thread per child for its whole lifetime. The process handle is itself a waitable object that
    /// signals on exit.
    let waitWindows (hProcess: nativeint) : Task<Outcome> =
        let tcs =
            TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously)

        // Wait on our OWN duplicate of the process handle, not the backend's: the backend may close
        // its handle on reap/teardown while this wait is still pending, and a registered wait on a
        // closed handle is undefined (and shares its pool wait thread with other handles, so the blast
        // radius is wider than a dedicated thread). The duplicate signals on the same process exit and
        // is closed when the wait completes.
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if not (DuplicateHandle(current, hProcess, current, &duplicate, 0u, false, DUPLICATE_SAME_ACCESS)) then
            // The source handle is already gone — the process is unobservable; report a clean exit.
            tcs.SetResult(Outcome.Exited 0)
        else
            let waitHandle =
                new OwnedProcessWait(new SafeWaitHandle(duplicate, ownsHandle = true))

            let callback =
                WaitOrTimerCallback(fun _ _ ->
                    let mutable code = 0u
                    // We own `duplicate` for the wait's lifetime, so this reads the real exit code;
                    // tolerate any residual hiccup by reporting `Exited 0` rather than faulting a verb.
                    GetExitCodeProcess(duplicate, &code) |> ignore
                    tcs.TrySetResult(Outcome.Exited(int code)) |> ignore)

            // -1 = infinite, executeOnlyOnce = true. The registration is published before the
            // continuation that uses it is attached, so unregistering there is race-free even if the
            // wait was already satisfied when registered.
            let registration =
                ThreadPool.RegisterWaitForSingleObject(waitHandle, callback, null, -1, true)

            tcs.Task.ContinueWith(fun (_: Task<Outcome>) ->
                registration.Unregister null |> ignore
                waitHandle.Dispose()) // disposes the SafeWaitHandle -> closes our duplicate
            |> ignore

        tcs.Task

    /// Hard-kill one Windows process (not its descendants — for that, terminate the whole Job).
    let terminateWindowsProcess (hProcess: nativeint) =
        TerminateProcess(hProcess, 1u) |> ignore

    // Tree introspection / suspend-resume for the `process-control` surface.
    [<Literal>]
    let private JobObjectBasicProcessIdList = 3

    [<Literal>]
    let private PROCESS_SUSPEND_RESUME = 0x0800u

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private QueryInformationJobObject(
        nativeint hJob,
        int infoClass,
        nativeint lpInfo,
        uint32 cbInfo,
        uint32& returnLength
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private OpenProcess(uint32 dwDesiredAccess, bool bInheritHandle, uint32 dwProcessId)

    // NtSuspendProcess/NtResumeProcess freeze/thaw every thread of a process in one call. They are
    // undocumented ntdll entry points but stable and the standard way to suspend a whole process;
    // the documented alternative (snapshot every thread + SuspendThread) is far more code.
    [<DllImport("ntdll.dll")>]
    extern int private NtSuspendProcess(nativeint hProcess)

    [<DllImport("ntdll.dll")>]
    extern int private NtResumeProcess(nativeint hProcess)

    /// Snapshot the pids assigned to a Job Object (the whole contained tree). A point-in-time view;
    /// a process can exit immediately after. Caps at 1024 members (ample for a diagnostic snapshot).
    let membersWindows (job: nativeint) : int list =
        let capacity = 1024
        let headerSize = 8 // two DWORDs: NumberOfAssignedProcesses, NumberOfProcessIdsInList
        let size = headerSize + capacity * IntPtr.Size
        let buffer = Marshal.AllocHGlobal size

        try
            let mutable returnLength = 0u

            if QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, uint32 size, &returnLength) then
                let count = min (Marshal.ReadInt32(buffer, 4)) capacity

                [ for i in 0 .. count - 1 -> int (Marshal.ReadIntPtr(buffer, headerSize + i * IntPtr.Size)) ]
            else
                []
        finally
            Marshal.FreeHGlobal buffer

    // Suspend / resume every member process of a Job. Best-effort and not atomic: a process can
    // spawn between the snapshot and the suspend; Windows keeps per-thread suspend counts, so nested
    // suspends stack and need matching resumes (unlike the level-triggered POSIX SIGSTOP/SIGCONT).
    let private forEachMemberHandle (job: nativeint) (action: nativeint -> unit) =
        for pid in membersWindows job do
            let handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, uint32 pid)

            if handle <> IntPtr.Zero then
                action handle
                CloseHandle handle |> ignore

    let suspendWindows (job: nativeint) =
        forEachMemberHandle job (fun handle -> NtSuspendProcess handle |> ignore)

    let resumeWindows (job: nativeint) =
        forEachMemberHandle job (fun handle -> NtResumeProcess handle |> ignore)

    // Job-Object accounting for `stats`: cumulative CPU + active count (basic accounting) and peak
    // committed memory (extended limit info).
    [<Literal>]
    let private JobObjectBasicAccountingInformation = 1

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_BASIC_ACCOUNTING_INFORMATION =
        struct
            val mutable TotalUserTime: int64
            val mutable TotalKernelTime: int64
            val mutable ThisPeriodTotalUserTime: int64
            val mutable ThisPeriodTotalKernelTime: int64
            val mutable TotalPageFaultCount: uint32
            val mutable TotalProcesses: uint32
            val mutable ActiveProcesses: uint32
            val mutable TotalTerminatedProcesses: uint32
        end

    /// Snapshot a Job's accounting: `(activeProcesses, totalCpuTime, peakCommittedBytes)`. `None` if
    /// either query fails (e.g. the job handle was closed). CPU is user + kernel (100ns units, the
    /// same as a `TimeSpan` tick).
    let jobStatsWindows (job: nativeint) : (int * TimeSpan * uint64) option =
        let accSize = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>()
        let extSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
        let accBuffer = Marshal.AllocHGlobal accSize
        let extBuffer = Marshal.AllocHGlobal extSize

        try
            let mutable returnLength = 0u

            let okAcc =
                QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformation,
                    accBuffer,
                    uint32 accSize,
                    &returnLength
                )

            let okExt =
                QueryInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    extBuffer,
                    uint32 extSize,
                    &returnLength
                )

            if okAcc && okExt then
                let acc =
                    Marshal.PtrToStructure(accBuffer, typeof<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>)
                    :?> JOBOBJECT_BASIC_ACCOUNTING_INFORMATION

                let ext =
                    Marshal.PtrToStructure(extBuffer, typeof<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>)
                    :?> JOBOBJECT_EXTENDED_LIMIT_INFORMATION

                let cpu = TimeSpan.FromTicks(acc.TotalUserTime + acc.TotalKernelTime)
                Some(int acc.ActiveProcesses, cpu, uint64 ext.PeakJobMemoryUsed)
            else
                None
        finally
            Marshal.FreeHGlobal accBuffer
            Marshal.FreeHGlobal extBuffer

    // Job-Object resource limits (the `limits` backend on Windows).
    [<Literal>]
    let private JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008u

    [<Literal>]
    let private JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200u

    [<Literal>]
    let private JobObjectCpuRateControlInformation = 15

    [<Literal>]
    let private JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1u

    [<Literal>]
    let private JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4u

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_CPU_RATE_CONTROL_INFORMATION =
        struct
            val mutable ControlFlags: uint32
            // Union member: the hard-cap rate as 1/100ths of a percent of total system CPU (1..10000).
            val mutable CpuRate: uint32
        end

    /// Apply resource limits to a Job: a memory cap (`JobMemoryLimit`), an active-process cap, and a
    /// CPU hard cap (a fraction of *total* system CPU, so per-core quota is approximate). Preserves
    /// `KILL_ON_JOB_CLOSE`. Returns an error message on failure.
    let applyWindowsJobLimits (job: nativeint) (limits: ResourceLimits) : Result<unit, string> =
        let mutable info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        let mutable flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

        match limits.MaxProcesses with
        | Some n ->
            flags <- flags ||| JOB_OBJECT_LIMIT_ACTIVE_PROCESS
            info.BasicLimitInformation.ActiveProcessLimit <- uint32 (max 0 n)
        | None -> ()

        match limits.MemoryMax with
        | Some bytes ->
            flags <- flags ||| JOB_OBJECT_LIMIT_JOB_MEMORY
            info.JobMemoryLimit <- unativeint (uint64 bytes)
        | None -> ()

        info.BasicLimitInformation.LimitFlags <- flags
        let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
        let buffer = Marshal.AllocHGlobal size

        try
            Marshal.StructureToPtr(info, buffer, false)

            if not (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size)) then
                Error(Win32Exception(Marshal.GetLastWin32Error()).Message)
            else
                match limits.CpuQuota with
                | None -> Ok()
                | Some cores ->
                    let fraction = min 1.0 (cores / float Environment.ProcessorCount)
                    let rate = uint32 (max 1.0 (Math.Round(fraction * 10000.0)))
                    let mutable cpuInfo = JOBOBJECT_CPU_RATE_CONTROL_INFORMATION()

                    cpuInfo.ControlFlags <- JOB_OBJECT_CPU_RATE_CONTROL_ENABLE ||| JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP

                    cpuInfo.CpuRate <- rate
                    let cpuSize = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()
                    let cpuBuffer = Marshal.AllocHGlobal cpuSize

                    try
                        Marshal.StructureToPtr(cpuInfo, cpuBuffer, false)

                        if
                            SetInformationJobObject(job, JobObjectCpuRateControlInformation, cpuBuffer, uint32 cpuSize)
                        then
                            Ok()
                        else
                            Error(Win32Exception(Marshal.GetLastWin32Error()).Message)
                    finally
                        Marshal.FreeHGlobal cpuBuffer
        finally
            Marshal.FreeHGlobal buffer

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
            ||| (if config.CreateNoWindow then CREATE_NO_WINDOW else 0u)

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

    // Non-variadic close-on-exec, used instead of fcntl (which a fixed-signature P/Invoke cannot
    // call under the AArch64 variadic ABI): Linux gets O_CLOEXEC via pipe2/open; macOS gets
    // POSIX_SPAWN_CLOEXEC_DEFAULT, which closes every non-dup2 fd in the child at exec.
    [<Literal>]
    let private O_CLOEXEC = 0x80000

    [<Literal>]
    let private POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000s

    [<Literal>]
    let private SIGKILL = 9

    [<Literal>]
    let private SIGTERM = 15

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

    /// Broadcast a raw signal to a POSIX process group; `true` if the send was accepted.
    let signalProcessGroup (pgid: int) (signalNum: int) : bool = killpg (pgid, signalNum) = 0

    /// Freeze a POSIX process group (SIGSTOP).
    let suspendProcessGroup (pgid: int) = killpg (pgid, sigStop) |> ignore

    /// Thaw a POSIX process group (SIGCONT).
    let resumeProcessGroup (pgid: int) = killpg (pgid, sigCont) |> ignore

    // ----------------------------------------------------------------------------------
    // Linux cgroup v2 (the `limits` backend) — all plain file I/O over /sys/fs/cgroup
    // ----------------------------------------------------------------------------------

    [<DllImport("libc", SetLastError = true)>]
    extern int private kill(int pid, int signalNumber)

    let private cgroupRoot = "/sys/fs/cgroup"

    /// True when cgroup v2's unified hierarchy is mounted (its root exposes `cgroup.controllers`).
    let cgroupV2Available () =
        try
            File.Exists(Path.Combine(cgroupRoot, "cgroup.controllers"))
        with _ ->
            false

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
        try
            let rel = (selfCgroupRelative ()).TrimStart('/')
            let parent = Path.Combine(cgroupRoot, rel)
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

    /// Migrate a process into the cgroup (a single write to `cgroup.procs`). Best-effort: a failure
    /// leaves the child in the parent cgroup (unbounded), which the run's outcome reflects.
    let migrateToCgroup (cgroupPath: string) (pid: int) =
        try
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.procs"), string pid)
        with _ ->
            ()

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
                 ())

    /// SIGTERM every member (graceful); the caller polls then escalates with `killCgroup`.
    let terminateCgroup (cgroupPath: string) =
        for pid in cgroupMembers cgroupPath do
            kill (pid, SIGTERM) |> ignore

    /// Broadcast a raw signal to every member of a cgroup.
    let signalCgroup (cgroupPath: string) (signalNum: int) =
        for pid in cgroupMembers cgroupPath do
            kill (pid, signalNum) |> ignore

    /// Freeze (`true`) or thaw (`false`) a cgroup (`cgroup.freeze`).
    let freezeCgroup (cgroupPath: string) (frozen: bool) =
        try
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.freeze"), (if frozen then "1" else "0"))
        with _ ->
            ()

    /// cgroup accounting for `stats`: cumulative CPU (cpu.stat `usage_usec`) and peak memory
    /// (`memory.peak`), each `None` when the file is absent.
    let cgroupStats (cgroupPath: string) : TimeSpan option * uint64 option =
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
                | true, peak -> Some(uint64 peak)
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

    /// Reap a POSIX child and report how it concluded.
    let waitPosix (pid: nativeint) : Task<Outcome> =
        Task.Run(fun () ->
            let mutable status = 0
            // `waitpid` can return -1/EINTR when a signal interrupts the blocking wait — notably the
            // .NET runtime's own thread-suspension signal on Linux. Retry so we don't misread the
            // untouched status as `Exited 0` (and leak the child as a zombie).
            let mutable result = waitpid (int pid, &status, 0)

            while result < 0 && Marshal.GetLastWin32Error() = EINTR do
                result <- waitpid (int pid, &status, 0)

            decodeWaitStatus status)

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
            let argv = command.Program :: command.Config.Args

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
