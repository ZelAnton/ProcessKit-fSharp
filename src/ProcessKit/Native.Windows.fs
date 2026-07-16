namespace ProcessKit.Native

// FS3265 fires when the generic `Marshal.PtrToStructure<'T>` — the AOT-safe overload Microsoft recommends
// over the `[<RequiresDynamicCode>]` non-generic `PtrToStructure(ptr, Type)` — is instantiated with a value
// type: F# forms a `'T | null` return whose nullness it can't track precisely. A struct can never be null,
// so the lost precision is harmless; suppress the false positive here (the only value-type reads via that
// overload in this file are the Job-Object accounting/limit structs below).
#nowarn "3265"

open ProcessKit
open System
open System.ComponentModel
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Security.Principal
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open ProcessKit.Native.Common

/// Windows kill-on-drop containment: a Job Object with `KILL_ON_JOB_CLOSE`, `CreateProcessW` with
/// `CREATE_SUSPENDED` → assign-to-job → resume, plus the Job-Object accounting/limits and the
/// async named-pipe stdio. All Win32 `DllImport`s live here; call sites are guarded by
/// `RuntimeInformation.IsOSPlatform` so a kernel32/ntdll entry point is only invoked on Windows.
/// Independent of the POSIX/cgroup layers — depends only on `Native.Common`.
module internal Windows =

    // ----------------------------------------------------------------------------------
    // Windows: command-line / argv helpers
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
        let parts = command.Program :: List.ofSeq command.Config.Args
        parts |> List.map quoteWindowsArg |> String.concat " "

    // ----------------------------------------------------------------------------------
    // Windows: Job Object + CREATE_SUSPENDED → assign → resume
    // ----------------------------------------------------------------------------------

    [<Literal>]
    let private CREATE_SUSPENDED = 0x00000004u

    [<Literal>]
    let private CREATE_UNICODE_ENVIRONMENT = 0x00000400u

    [<Literal>]
    let private CREATE_NO_WINDOW = 0x08000000u

    // Spawn the child as the root of a NEW console process group (its group id = its pid), so a
    // `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` can be targeted at just that group. It also
    // disables the child's default CTRL+C handling, which is why the soft signal is CTRL+BREAK.
    [<Literal>]
    let private CREATE_NEW_PROCESS_GROUP = 0x00000200u

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

    // Test seam: `ResumeThread`, overridable so a fault-injection test can force the `(DWORD)-1`
    // (`UInt32.MaxValue`) error sentinel deterministically — a genuinely failing `ResumeThread` cannot
    // be provoked on demand. Production always runs the real entry point; only the (sequential) tests
    // reassign it, and restore it in a `finally`.
    let mutable resumeThreadHook: nativeint -> uint32 = ResumeThread

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetExitCodeProcess(nativeint hProcess, uint32& lpExitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private WaitForSingleObject(nativeint hHandle, uint32 dwMilliseconds)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private CloseHandle(nativeint hObject)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private GetProcessId(nativeint hProcess)

    // Test seam: `GetProcessId`, overridable so a fault-injection test can force its documented zero
    // failure sentinel deterministically. Production always runs the real entry point; only the
    // (sequential) tests reassign it, and restore it in a `finally`.
    let mutable getProcessIdHook: nativeint -> uint32 = GetProcessId

    /// The OS process id behind a Windows process handle, when the native query succeeds.
    let processIdWindows (hProcess: nativeint) : int option =
        match getProcessIdHook hProcess with
        | 0u -> None
        | processId -> Some(int processId)

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

    /// `true` for a real, usable Win32 handle — `false` for the two "nothing here" sentinels
    /// (`IntPtr.Zero`/`INVALID_HANDLE_VALUE`) that `GetStdHandle`/`CreateFileW`/`DuplicateHandle`
    /// return on failure or "no such handle". Shared by the handle validation at the
    /// `STARTUPINFO` boundary and by every place that closes one of these handles, so a cleanup
    /// path can never call `CloseHandle` on a sentinel.
    let private isValidHandle (handle: nativeint) : bool =
        handle <> IntPtr.Zero && handle <> nativeint INVALID_HANDLE_VALUE

    let private closeHandleIfValid (handle: nativeint) =
        if isValidHandle handle then
            CloseHandle handle |> ignore

    /// An inheritable handle to the null device, for `StdioMode.Null`.
    let private inheritableNul (access: uint32) : nativeint =
        let handle =
            CreateFileW("NUL", access, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0u, IntPtr.Zero)

        if isValidHandle handle then
            SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) |> ignore

        handle

    /// An inheritable duplicate of one of the parent's std handles, for `StdioMode.Inherit`.
    let private inheritableStdHandle (stdHandleId: int) : nativeint =
        let source = GetStdHandle stdHandleId
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if
            // `GetStdHandle` returns `INVALID_HANDLE_VALUE` (`-1`) on failure and `NULL` for "no such
            // handle"; both must be rejected here. A bare `source <> IntPtr.Zero` lets `-1` through, and
            // for `DuplicateHandle` the pseudo-handle `-1` means "the current process" — it would happily
            // duplicate the parent's own process handle (full access) and hand it to the child as a std
            // handle, instead of failing. `isValidHandle` rejects both sentinels, so a broken `GetStdHandle`
            // reaches the honest `ProcessError.Spawn` path in `spawnWindowsCore` rather than being masked.
            isValidHandle source
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
                // Explicit generic overload — the non-generic `StructureToPtr(object, ...)` is
                // `[<RequiresDynamicCode>]` (AOT-unfriendly). The concrete struct type keeps it trim/AOT-clean.
                Marshal.StructureToPtr<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(info, buffer, false)

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
            // The source handle is already gone/unusable — the process's real exit status is not
            // observable through it. Honest failure, not a fabricated clean exit.
            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
            tcs.SetResult(Outcome.Unobserved $"DuplicateHandle failed: {message}")
        else
            let waitHandle =
                new OwnedProcessWait(new SafeWaitHandle(duplicate, ownsHandle = true))

            let callback =
                WaitOrTimerCallback(fun _ _ ->
                    let mutable code = 0u
                    // We own `duplicate` for the wait's lifetime, so this call should always succeed;
                    // an honest `Unobserved` outcome on the rare hiccup instead of fabricating a clean exit.
                    if GetExitCodeProcess(duplicate, &code) then
                        tcs.TrySetResult(Outcome.Exited(int code)) |> ignore
                    else
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message

                        tcs.TrySetResult(Outcome.Unobserved $"GetExitCodeProcess failed: {message}")
                        |> ignore)

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

    // Console control events — the best-effort SOFT stop for a console child spawned with
    // `CREATE_NEW_PROCESS_GROUP`. `CTRL_BREAK_EVENT` is used rather than `CTRL_C_EVENT` because only
    // CTRL+BREAK can be targeted at a specific process group; CTRL+C can only be broadcast to the whole
    // console (group id 0), which would also hit the CALLER — so CTRL+BREAK to the child's own group id
    // is the only way to reach the child without signalling ourselves.
    [<Literal>]
    let private CTRL_BREAK_EVENT = 1u

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GenerateConsoleCtrlEvent(uint32 dwCtrlEvent, uint32 dwProcessGroupId)

    // Test seam: `GenerateConsoleCtrlEvent`, so validation tests can prove an invalid group id does not
    // reach the native API. Production always runs the real entry point; only the (sequential) tests
    // reassign it, and restore it in a `finally`.
    let mutable generateConsoleCtrlEventHook: uint32 * uint32 -> bool =
        GenerateConsoleCtrlEvent

    /// Best-effort soft stop for a console child: generate a CTRL+BREAK for the process group
    /// `processGroupId` (a child spawned with `CREATE_NEW_PROCESS_GROUP`, whose group id is its pid).
    /// The event is targeted at that SPECIFIC group — never group 0 — so the caller's own console group
    /// is never signalled. `Ok` on a successful generate; `Error` carries the Win32 message when the API
    /// itself fails (e.g. the caller has no console). A success means the event was generated for the
    /// group, not that any child actually handled it: it reaches only console children sharing the
    /// caller's console, and a child may install its own handler.
    let sendConsoleCtrlBreakWindows (processGroupId: int) : Result<unit, string> =
        if processGroupId <= 0 then
            Error "process group id must be positive"
        elif generateConsoleCtrlEventHook (CTRL_BREAK_EVENT, uint32 processGroupId) then
            Ok()
        else
            Error(Win32Exception(Marshal.GetLastWin32Error()).Message)

    // Tree introspection / suspend-resume for the `process-control` surface.
    [<Literal>]
    let private JobObjectBasicProcessIdList = 3

    [<Literal>]
    let private PROCESS_SUSPEND_RESUME = 0x0800u

    // Least-privilege addition to the suspend/resume handle so the same handle can also be used
    // for the `IsProcessInJob` re-check below, without a second `OpenProcess` call.
    [<Literal>]
    let private PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

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

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private IsProcessInJob(nativeint processHandle, nativeint jobHandle, bool& result)

    // NtSuspendProcess/NtResumeProcess freeze/thaw every thread of a process in one call. They are
    // undocumented ntdll entry points but stable and the standard way to suspend a whole process;
    // the documented alternative (snapshot every thread + SuspendThread) is far more code.
    [<DllImport("ntdll.dll")>]
    extern int private NtSuspendProcess(nativeint hProcess)

    [<DllImport("ntdll.dll")>]
    extern int private NtResumeProcess(nativeint hProcess)

    // `QueryInformationJobObject` signals "your buffer was too small" for `JobObjectBasicProcessIdList`
    // by returning FALSE with this last-error (as well as, on some paths, returning TRUE but reporting a
    // `NumberOfAssignedProcesses` larger than the list it could fit); both are handled by growing.
    [<Literal>]
    let private ERROR_MORE_DATA = 234

    // Grow-and-retry rounds `membersWindows` will attempt. Growth jumps straight to the reported assigned
    // count (or doubles on a bare ERROR_MORE_DATA), so a real job fits within one or two rounds; this is
    // only a defensive cap against a pathological job that keeps signalling overflow without ever fitting.
    [<Literal>]
    let private maxQueryAttempts = 24

    // Two DWORDs — NumberOfAssignedProcesses, NumberOfProcessIdsInList — then the pid array (8-aligned, so
    // it starts right at offset 8 on 64-bit; the header itself is already 8 bytes).
    [<Literal>]
    let private processIdListHeaderSize = 8

    // Test seam: one `QueryInformationJobObject` call — the caller passes a buffer of `bufferSize` bytes,
    // the call writes the process-id list into it and returns `struct (succeeded, lastError)`. The real
    // seam captures `GetLastWin32Error` right after the P/Invoke so the classification below works off a
    // returned value rather than thread-global state; a fault-injection test reassigns it to simulate a
    // job with more members than the initial buffer (driving grow-and-retry) and a genuine query failure,
    // without spawning thousands of real processes. Only the members path is routed through it.
    let mutable queryInformationJobObjectHook: nativeint -> int -> nativeint -> uint32 -> struct (bool * int) =
        fun job infoClass buffer bufferSize ->
            let mutable returnLength = 0u

            let ok =
                QueryInformationJobObject(job, infoClass, buffer, bufferSize, &returnLength)

            struct (ok, (if ok then 0 else Marshal.GetLastWin32Error()))

    /// Snapshot the pids assigned to a Job Object (the whole contained tree). A point-in-time view;
    /// a process can exit immediately after. The buffer grows to fit however many processes the job
    /// holds (starting at 1024, then re-querying at the reported assigned count), so no member is
    /// silently dropped; a genuine query failure (as opposed to a too-small buffer) is returned as
    /// `ProcessError.Io` rather than an empty list, so `Members`/`Suspend`/`Resume` can never quietly
    /// report success without having touched the real job.
    let membersWindows (job: nativeint) : Result<int list, ProcessError> =
        let rec loop (capacity: int) (attempt: int) : Result<int list, ProcessError> =
            let size = processIdListHeaderSize + capacity * IntPtr.Size
            let buffer = Marshal.AllocHGlobal size

            // Decide inside the `try` (the buffer must still be alive to be read), then act after freeing
            // it: `Choice1Of2 newCapacity` = grow and retry, `Choice2Of2 result` = done.
            let decision =
                try
                    let struct (ok, lastError) =
                        queryInformationJobObjectHook job JobObjectBasicProcessIdList buffer (uint32 size)

                    if ok then
                        let assigned = Marshal.ReadInt32(buffer, 0)

                        if assigned > capacity && attempt < maxQueryAttempts then
                            // More members than this buffer can hold: grow straight to the reported count
                            // (plus headroom for members that may appear before the retry) and re-query.
                            Choice1Of2(assigned + assigned / 2 + 16)
                        else
                            let count = min (Marshal.ReadInt32(buffer, 4)) capacity

                            Choice2Of2(
                                Ok
                                    [ for i in 0 .. count - 1 ->
                                          int (Marshal.ReadIntPtr(buffer, processIdListHeaderSize + i * IntPtr.Size)) ]
                            )
                    elif lastError = ERROR_MORE_DATA && attempt < maxQueryAttempts then
                        // Overflow signalled as a failure rather than a truncated success — grow and retry.
                        Choice1Of2(capacity * 2)
                    else
                        // A genuine query failure (not a size problem): surface it honestly rather than
                        // reporting an empty group and letting Members/Suspend/Resume claim a false success.
                        Choice2Of2(
                            Error(
                                ProcessError.Io
                                    $"could not enumerate job members (QueryInformationJobObject failed): {Win32Exception(lastError).Message}"
                            )
                        )
                finally
                    Marshal.FreeHGlobal buffer

            match decision with
            | Choice1Of2 newCapacity -> loop newCapacity (attempt + 1)
            | Choice2Of2 result -> result

        loop 1024 1

    // Suspend / resume every member process of a Job over the COMPLETE `membersWindows` snapshot (the
    // buffer grows to fit the whole job, so no member is dropped by an artificial cap). Best-effort and
    // not atomic: a process can still spawn between the snapshot and the suspend — the only documented
    // race, now scoped to genuinely later arrivals rather than a truncated list; Windows keeps per-thread
    // suspend counts, so nested suspends stack and need matching resumes (unlike the level-triggered POSIX
    // SIGSTOP/SIGCONT). A genuine members-query failure propagates as `ProcessError.Io` instead of being
    // silently treated as an empty group, so `Suspend`/`Resume` can never report success without having
    // touched the real job.
    //
    // Recycle-safe: the member pid list is a snapshot, so a member (typically a handle-less
    // grandchild) can exit and its pid be reused by an unrelated process between the snapshot and
    // `OpenProcess` below. Re-verify with `IsProcessInJob` that the just-opened handle is STILL a
    // member of THIS job before invoking `action` (`NtSuspendProcess`/`NtResumeProcess`), so a
    // recycled pid can never divert a suspend/resume onto a foreign process. Fail-safe: any failure
    // to open the process or query its membership is treated as "not our member" — an uncertain
    // result never touches the process.
    let private forEachMemberHandle (job: nativeint) (action: nativeint -> unit) : Result<unit, ProcessError> =
        match membersWindows job with
        | Error error -> Error error
        | Ok pids ->
            for pid in pids do
                let handle =
                    OpenProcess(PROCESS_SUSPEND_RESUME ||| PROCESS_QUERY_LIMITED_INFORMATION, false, uint32 pid)

                if handle <> IntPtr.Zero then
                    let mutable stillMember = false

                    if IsProcessInJob(handle, job, &stillMember) && stillMember then
                        action handle

                    CloseHandle handle |> ignore

            Ok()

    let suspendWindows (job: nativeint) : Result<unit, ProcessError> =
        forEachMemberHandle job (fun handle -> NtSuspendProcess handle |> ignore)

    let resumeWindows (job: nativeint) : Result<unit, ProcessError> =
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
    let jobStatsWindows (job: nativeint) : (int * TimeSpan * int64) option =
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
                // Generic `PtrToStructure<'T>` (not the non-generic `PtrToStructure(ptr, Type)` overload,
                // which is `[<RequiresDynamicCode>]` — its marshalling stub for an arbitrary runtime Type
                // can't be generated ahead of time, so it warns under NativeAOT). The generic form has the
                // concrete struct baked in at compile time and is trim/AOT-clean.
                let acc = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION> accBuffer

                let ext = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION> extBuffer

                let cpu = TimeSpan.FromTicks(acc.TotalUserTime + acc.TotalKernelTime)
                Some(int acc.ActiveProcesses, cpu, int64 ext.PeakJobMemoryUsed)
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
            Marshal.StructureToPtr<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(info, buffer, false)

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
                        Marshal.StructureToPtr<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(cpuInfo, cpuBuffer, false)

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
        if not command.Config.ClearEnv && command.Config.EnvOverrides.IsEmpty then
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

    // The named pipe's OS-level buffer, on each side. The 5-arg `NamedPipeServerStream` constructor
    // defaults this to 0, which lets the OS pick a minimal buffer — too small to hold even a couple of
    // short lines. An anonymous pipe (what this replaces) gets a much more generous OS default, so a
    // child that outpaces its (perhaps already-faulted, no-longer-draining) reader could still finish
    // writing its output and exit; reproduced by a throwing `OnStdoutLine` handler that abandons the
    // stdout pump after line 1 — the child then blocked forever writing line 2 into a too-small pipe,
    // hanging `FinishAsync` (and, unnoticed by that specific test, leaking the child until the group's
    // kill-on-drop reaped it). 64 KiB comfortably covers ordinary line-buffered output.
    [<Literal>]
    let private asyncPipeBufferSize = 65536

    // A connected named-pipe pair for one piped stdio stream: the parent's async-capable server end
    // (`PipeOptions.Asynchronous` — real overlapped `ReadAsync`/`WriteAsync`, completed via IOCP, no
    // thread-pool-parking sync fallback) and an inheritable client end the child inherits as its std
    // handle. `AnonymousPipeServerStream` (what this replaces) has no `PipeOptions` overload at all —
    // it is unconditionally synchronous — so an async-capable pipe on Windows has to be a *named* one.
    // `serverDirection` is from the PARENT's perspective (`In` for stdout/stderr, `Out` for stdin); the
    // client uses the opposite direction. A unique per-call pipe name (a GUID) keeps concurrent spawns
    // from colliding; the actual cross-inherit hazard (ANY inheritable handle open at `CreateProcessW`
    // time) is still guarded by `windowsSpawnLock` below, exactly as for the anonymous pipes this
    // replaces — switching pipe kinds does not touch that invariant.
    /// Wraps a raw Win32 handle for the pipe-setup unwind list (`createdPipes` in `spawnWindowsCore`):
    /// `Dispose` closes it, guarded by `closeHandleIfValid` — the list is a rescue mechanism run from
    /// an exception handler, so it must never call `CloseHandle` on a sentinel that was never really
    /// opened.
    let private disposableHandle (handle: nativeint) : IDisposable =
        { new IDisposable with
            member _.Dispose() = closeHandleIfValid handle }

    let private createAsyncPipePair (serverDirection: PipeDirection) : NamedPipeServerStream * NamedPipeClientStream =
        let pipeName = "ProcessKit-" + Guid.NewGuid().ToString("N")

        let clientDirection =
            if serverDirection = PipeDirection.In then
                PipeDirection.Out
            else
                PipeDirection.In

        let server =
            new NamedPipeServerStream(
                pipeName,
                serverDirection,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                asyncPipeBufferSize,
                asyncPipeBufferSize
            )

        try
            let client =
                new NamedPipeClientStream(
                    ".",
                    pipeName,
                    clientDirection,
                    PipeOptions.None,
                    TokenImpersonationLevel.None,
                    HandleInheritability.Inheritable
                )

            try
                // Purely local + same-process: the server instance already exists (constructed just
                // above), so this connects and completes near-instantly. Bounded rather than infinite
                // so a pathological OS/security-software failure can't hang a spawn forever.
                client.Connect 5000
                server.WaitForConnection()
                server, client
            with _ ->
                client.Dispose()
                reraise ()
        with _ ->
            server.Dispose()
            reraise ()

    /// Spawn `command` suspended, assign it to `job` while still suspended (so no
    /// grandchild can escape the container), then resume it. Returns the process handle and
    /// managed read streams for stdout/stderr.
    let private spawnWindowsCore (job: nativeint) (command: Command) : Result<Spawned, ProcessError> =
        let config = command.Config
        // `Command.InheritStdin` hands the child the PARENT's own standard-input handle directly (no
        // pipe, no feeder) — the interactive/console case. Every other configuration goes through a
        // pipe so we control EOF; its parent-side write end is retained only when there is a feeder
        // source to pump or `KeepStdinOpen` kept it open for interactive writing.
        let stdinInherit = Stdin.isInherit config.StdinSource

        let stdinPipeKept =
            (config.StdinSource.IsSome && not stdinInherit) || config.KeepStdinOpen

        // Every pipe end created so far, torn down (best-effort, reverse order) if pipe setup fails
        // partway through — before `CreateProcessW` is even reached, so nothing has been handed to a
        // child yet. `AnonymousPipeServerStream` construction essentially never threw in practice; a
        // named pipe's `Connect` genuinely can (e.g. under resource exhaustion), which is a new failure
        // mode this replacement introduces, so it gets a real unwind-and-report instead of leaking
        // handles or letting a BCL exception escape this `Result`-returning function.
        let createdPipes = ResizeArray<IDisposable>()

        let disposeCreatedPipes () =
            for i in createdPipes.Count - 1 .. -1 .. 0 do
                try
                    createdPipes[i].Dispose()
                with _ ->
                    // Best-effort unwind after an earlier failure; that original failure is what we
                    // report, not a secondary problem tearing down an already-broken pipe.
                    ()

        try
            // stdin: with `InheritStdin` the child is handed a duplicated inheritable copy of the
            // parent's own STD_INPUT_HANDLE directly (no pipe, no feeder). Otherwise it is always a
            // pipe so we control EOF; the write end is kept (feeder/interactive) or closed. `stdinChild`
            // is what goes to `STARTUPINFO.hStdInput`; `inStreams` is `Some (server, client)` only for
            // the pipe path (the parent write end + the child read end); both paths register their
            // handles in `createdPipes` for the exception unwind and drop the child's copy after spawn.
            let stdinChild, inStreams =
                if stdinInherit then
                    let handle = inheritableStdHandle STD_INPUT_HANDLE

                    if not (isValidHandle handle) then
                        // Same rationale as `setupOut`'s Inherit branch: a failed `GetStdHandle`/
                        // `DuplicateHandle` (e.g. no console and stdin not redirected) must fail the
                        // spawn, not silently hand the child a broken std input handle.
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message

                        failwith
                            $"could not duplicate an inheritable copy of the parent's standard input handle: {message}"

                    createdPipes.Add(disposableHandle handle)
                    handle, None
                else
                    let inServer, inClient = createAsyncPipePair PipeDirection.Out
                    createdPipes.Add inServer
                    createdPipes.Add inClient
                    inClient.SafePipeHandle.DangerousGetHandle(), Some(inServer, inClient)

            // For an output stream: the inheritable child-side handle, the parent read stream
            // (`Some` only when piped), and a cleanup that drops the parent's copy of the child handle
            // after spawn (the child has its own inherited copy by then).
            let setupOut (mode: StdioMode) (stdHandleId: int) : nativeint * Stream option * (unit -> unit) =
                match mode with
                | StdioMode.Piped ->
                    let server, client = createAsyncPipePair PipeDirection.In
                    createdPipes.Add server
                    createdPipes.Add client
                    client.SafePipeHandle.DangerousGetHandle(), Some(server :> Stream), (fun () -> client.Dispose())
                | StdioMode.Null ->
                    let handle = inheritableNul GENERIC_WRITE

                    if not (isValidHandle handle) then
                        // Validated at the source, before this ever reaches `STARTUPINFO.hStdOutput`/
                        // `hStdError` — a NUL-device handle is not the sort of thing that should be
                        // handed to the child silently broken. Caught by the outer `with` below, which
                        // turns it into an honest `ProcessError.Spawn` instead of a fabricated success.
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                        failwith $"could not open an inheritable handle to the NUL device: {message}"

                    // Registered in the unwind list immediately: if the NEXT step in this same spawn
                    // (the `setupOut` call for stderr, when this was stdout's) throws afterwards, this
                    // handle has not been handed to a child yet and must not leak.
                    createdPipes.Add(disposableHandle handle)
                    handle, None, (fun () -> closeHandleIfValid handle)
                | StdioMode.Inherit ->
                    let handle = inheritableStdHandle stdHandleId

                    if not (isValidHandle handle) then
                        // Same rationale as the `Null` branch above: `GetStdHandle`/`DuplicateHandle`
                        // failing (e.g. no console and this stream not redirected) must fail the spawn,
                        // not silently hand the child a broken std handle.
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                        failwith $"could not duplicate an inheritable copy of the parent's std handle: {message}"

                    createdPipes.Add(disposableHandle handle)
                    handle, None, (fun () -> closeHandleIfValid handle)

            let outChild, outStream, outCleanup = setupOut config.StdoutMode STD_OUTPUT_HANDLE

            let errChild, errStream, errCleanup =
                if config.MergeStderr then
                    // `Command.MergeStderr` (2>&1): the child's stderr shares the SAME inherited handle as
                    // its stdout (`hStdError` = `hStdOutput` below), so both write into the one stdout
                    // destination (pipe / NUL / inherited) and interleave honestly. No separate stderr
                    // pipe is set up, so there is no separate parent stream (`errStream = None`) and
                    // nothing extra to close — `outCleanup` already drops the parent's copy of `outChild`,
                    // so `errCleanup` is a no-op (a second `CloseHandle` on that same handle would be a
                    // double-close).
                    outChild, None, (fun () -> ())
                else
                    setupOut config.StderrMode STD_ERROR_HANDLE

            let mutable startup = STARTUPINFO()
            startup.cb <- Marshal.SizeOf<STARTUPINFO>()
            startup.dwFlags <- STARTF_USESTDHANDLES
            startup.hStdInput <- stdinChild
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
                // Opt-in: make the child the root of its own console process group so a later
                // `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` can soft-signal it (and the tree it
                // shares a console with) without touching the caller's own group. `Spawned.WindowsCtrlGroup`
                // records this so `ProcessGroup.Signal` knows which children can receive the event.
                ||| (if config.WindowsCtrlSignals then
                         CREATE_NEW_PROCESS_GROUP
                     else
                         0u)
                // The requested CPU priority becomes a priority-class creation flag on the direct child,
                // set atomically at creation (unlike the POSIX post-spawn nudge), so no window. It is
                // honored on the immediate child for every level, but Windows only *inherits* a class to
                // grandchildren when it is lowered: Idle/BelowNormal (and Normal) reach the whole tree,
                // while a grandchild spawned with no flag defaults to NORMAL unless its creator is
                // idle/below-normal — so grandchildren of an AboveNormal/High child run at Normal. This is
                // the honest divergence documented on `Priority`; a job-wide class is not used here because
                // the Job Object is a per-group container shared across commands, not per-command.
                ||| (match config.Priority with
                     | Some priority -> PriorityMapping.windowsCreationFlag priority
                     | None -> 0u)

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

                match inStreams with
                | Some(inServer, inClient) ->
                    inClient.Dispose()
                    inServer.Dispose()
                | None ->
                    // Inherit: no pipe — just close the inheritable duplicate of the parent's std input.
                    closeHandleIfValid stdinChild

            if not created then
                releaseStdio ()

                if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                    Error(notFoundFromSpawnFailure command.Program)
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
            elif resumeThreadHook info.hThread = UInt32.MaxValue then
                // `ResumeThread` returned its `(DWORD)-1` failure sentinel: the child is assigned to the
                // job but still SUSPENDED and will never run. Leaving it would masquerade as a healthy
                // spawn while the child hangs forever, so terminate it inside the job, release every
                // handle and stream, and report an honest `ProcessError.Spawn` — the same shape as the
                // `AssignProcessToJobObject` failure just above.
                let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                TerminateProcess(info.hProcess, 1u) |> ignore
                CloseHandle info.hThread |> ignore
                CloseHandle info.hProcess |> ignore
                releaseStdio ()
                Error(ProcessError.Spawn(command.Program, $"could not resume the suspended child: {message}"))
            else
                CloseHandle info.hThread |> ignore
                // Drop the parent's copies of the child-side handles now that the child has inherited
                // them, so reads see EOF when the child exits.
                outCleanup ()
                errCleanup ()

                let stdinStream =
                    match inStreams with
                    | Some(inServer, inClient) ->
                        // Drop the parent's copy of the child's read end, then keep the write end only
                        // for a feeder/interactive stdin; otherwise close it so the child sees EOF.
                        inClient.Dispose()

                        if stdinPipeKept then
                            Some(inServer :> Stream)
                        else
                            inServer.Dispose() // close stdin write end -> child sees EOF
                            None
                    | None ->
                        // Inherit: the child now has its own inherited copy of the parent's std input, so
                        // drop the parent's inheritable duplicate. There is no parent-side stdin stream.
                        closeHandleIfValid stdinChild
                        None

                Ok
                    { Handle = info.hProcess
                      Stdout = outStream
                      Stderr = errStream
                      Stdin = stdinStream
                      WindowsCtrlGroup = config.WindowsCtrlSignals }
        with ex ->
            disposeCreatedPipes ()
            Error(ProcessError.Spawn(command.Program, ex.Message))

    let spawnWindows (job: nativeint) (command: Command) : Result<Spawned, ProcessError> =
        // `Command.Umask`/`Uid`/`Gid`/`Groups`/`Setsid` are Unix-only primitives with no Windows
        // equivalent (a file-mode creation mask, `setuid`/`setgid`/supplementary-group privilege drop,
        // and a `setsid()` session detach). Honour each request honestly as `ProcessError.Unsupported`
        // BEFORE any spawn work, rather than silently ignoring it — symmetric to the port's other
        // Unix-only gates (e.g. every non-`Kill` `Signal` on Windows → `Unsupported` in `Backend.fs`).
        // Reported one at a time; the first requested-but-unsupported knob names the failure.
        let config = command.Config

        match config.Umask, config.Uid, config.Gid, config.Setsid, config.Groups with
        | Some _, _, _, _, _ -> Error(ProcessError.Unsupported "umask")
        | _, Some _, _, _, _ -> Error(ProcessError.Unsupported "uid")
        | _, _, Some _, _, _ -> Error(ProcessError.Unsupported "gid")
        | _, _, _, true, _ -> Error(ProcessError.Unsupported "setsid")
        | _, _, _, _, Some _ -> Error(ProcessError.Unsupported "groups")
        | None, None, None, false, None -> lock windowsSpawnLock (fun () -> spawnWindowsCore job command)
