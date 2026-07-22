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

    // ----------------------------------------------------------------------------------
    // Windows: cmd.exe (.cmd/.bat) launch wrapping — BatBadBut / CVE-2024-24576-safe quoting
    // ----------------------------------------------------------------------------------
    //
    // A `.cmd`/`.bat` is not a PE image, so `CreateProcessW` cannot launch it directly; it must run
    // THROUGH `cmd.exe /d /c`. But `cmd.exe` re-parses its command tail with its OWN grammar before the
    // batch script's `%*`/`%1` reconstruction ever sees it — two parsing layers, not one. Quoting an
    // argument by the ordinary `CommandLineToArgvW` rules (`quoteWindowsArg`) is correct for the batch's
    // argv reconstruction but NOT for cmd's command parser: an unescaped metacharacter (`&`, `|`, `<`,
    // `>`, `(`, `)`) or `"` in an argument can break out and run attacker-chosen commands — the
    // "BatBadBut" class, CVE-2024-24576. So each argument is escaped for BOTH layers: first the ordinary
    // argv quoting (so the batch recovers the exact argument), then every cmd metacharacter in that
    // result is caret-escaped (`^x`) so cmd's command parser consumes the caret and passes the literal
    // character straight to the script instead of acting on it.
    //
    // `%` (environment expansion), `!` (delayed expansion), and CR/LF cannot be represented safely on a
    // cmd command line at all — percent/`!` expansion runs regardless of carets or quoting, and CR/LF
    // truncate the line — so an argument (or a resolved script path) carrying one is an honest typed
    // refusal (`ProcessError.Spawn`), never a "launch it anyway". The whole command is wrapped in one
    // extra outer quote pair so cmd's `/c` quote-stripping (which removes the first and last quote of the
    // tail) peels exactly that pair, leaving the real-quoted script path and the caret-escaped arguments
    // to be re-parsed verbatim. `/d` disables AutoRun so a per-user registry command can't run first.

    /// The absolute path to the system `cmd.exe`, taken from the Windows system directory rather than
    /// `PATH`/`%ComSpec%` so a `cmd.exe` planted earlier on `PATH` can never become the shell for a batch
    /// wrapper — this is a security fix, so the shell itself must not be hijackable.
    let private systemCmdExe =
        lazy (Path.Combine(Environment.SystemDirectory, "cmd.exe"))

    /// The `cmd.exe` command-parser metacharacters neutralized by a leading caret when NOT inside cmd's
    /// own quotes: the quote itself, the caret, command chaining, redirection, and grouping.
    let private isCmdMetacharacter (c: char) =
        c = '"'
        || c = '^'
        || c = '&'
        || c = '|'
        || c = '<'
        || c = '>'
        || c = '('
        || c = ')'

    /// Characters that cannot be safely represented on a `cmd.exe` command line at all (see the section
    /// comment): environment/delayed-variable expansion (`%`/`!`) runs regardless of any escaping, and
    /// CR/LF truncate the command line.
    let private isCmdUnescapable (c: char) =
        c = '%' || c = '!' || c = '\r' || c = '\n'

    /// Quote one argument for a `cmd.exe /d /c` batch wrapper (see the section comment). `Ok` carries the
    /// doubly-escaped token — the ordinary argv quoting the batch's own reconstruction expects, then a
    /// caret before every cmd metacharacter so cmd's command parser passes each through literally. `Error`
    /// is the honest refusal (its reason) when the argument holds a character cmd.exe cannot escape.
    let private quoteCmdArgument (arg: string) : Result<string, string> =
        if arg |> Seq.exists isCmdUnescapable then
            Error
                "it contains a percent sign, an exclamation mark, or a line break, none of which cmd.exe can escape without risking command injection"
        else
            // First the ordinary argv quoting so the batch script's `%*`/`%1` reconstruction recovers the
            // exact argument; then caret-escape every cmd metacharacter in that result (including the
            // quotes it just added) so cmd's command parser passes each through literally.
            let argv = quoteWindowsArg arg
            let sb = StringBuilder(argv.Length + 8)

            for c in argv do
                if isCmdMetacharacter c then
                    sb.Append('^') |> ignore

                sb.Append(c) |> ignore

            Ok(sb.ToString())

    /// Build the full `cmd.exe /d /c "…"` command line that launches the resolved `.cmd`/`.bat` at
    /// `script` with `args`, with BatBadBut-safe quoting (see the section comment). `program` is the
    /// original bare name, carried only for a refusal error's identity. The script path is placed in a
    /// REAL quote pair (Windows file names cannot contain `"`, and inside cmd's quotes `&|<>()^` are
    /// literal, so cmd locates the program correctly); a path carrying `%`/`!`/`"`/CR/LF is refused, since
    /// those would still expand or truncate even quoted.
    let private buildBatchCommandLine
        (program: string)
        (script: string)
        (args: string list)
        : Result<string, ProcessError> =
        if script |> Seq.exists (fun c -> isCmdUnescapable c || c = '"') then
            Error(
                ProcessError.Spawn(
                    program,
                    "the resolved batch file path contains a character that cannot be safely passed to cmd.exe (a percent sign, exclamation mark, quote, or line break)"
                )
            )
        else
            let rec quoteAll acc remaining =
                match remaining with
                | [] -> Ok(List.rev acc)
                | arg :: rest ->
                    match quoteCmdArgument arg with
                    | Ok quoted -> quoteAll (quoted :: acc) rest
                    | Error reason ->
                        Error(
                            ProcessError.Spawn(
                                program,
                                $"argument '{arg}' cannot be safely quoted for the cmd.exe batch wrapper: {reason}"
                            )
                        )

            match quoteAll [] args with
            | Error error -> Error error
            | Ok quotedArgs ->
                let sb = StringBuilder()
                // The (absolute, non-`PATH`) system cmd.exe as the program token, then `/d /c` and the
                // outer opening quote cmd's `/c` parsing strips together with the final closing quote.
                sb.Append('"').Append(systemCmdExe.Value).Append('"') |> ignore
                sb.Append(" /d /c \"") |> ignore
                // The real-quoted script path (cmd uses these quotes to find the program), then each
                // caret-escaped argument.
                sb.Append('"').Append(script).Append('"') |> ignore

                for quoted in quotedArgs do
                    sb.Append(' ').Append(quoted) |> ignore

                sb.Append('"') |> ignore
                Ok(sb.ToString())

    /// The `CreateProcessW` command line for `command`, honouring the Windows PATHEXT launch substitution
    /// (T-181) and the prefer-local search (T-182). A bare name whose only match under our own
    /// PATHEXT-aware resolver (`Common.resolveProgram`/`probeDir`, reused via `resolveWindowsLaunch` — no
    /// second copy) carries a non-`.exe` extension is launched via that resolved absolute path instead of
    /// the bare name, because the OS's own bare-name `PATH` search appends only `.exe` and would miss it —
    /// the `which`-vs-spawn divergence this closes. A prefer-local match (`Command.PreferLocal`) is
    /// searched first and is likewise substituted by absolute path (even a `.exe`, since the OS never
    /// searches those directories). A `.cmd`/`.bat` match — on `PATH` or prefer-local — additionally
    /// routes through `cmd.exe /d /c` with BatBadBut-safe quoting. A `PATH` `.exe` match, a path-form
    /// program, and a name that resolves to nothing are all left verbatim (the OS resolves them exactly as
    /// before). Fails only when a batch-wrapper argument (or script path) cannot be safely quoted for
    /// cmd.exe.
    let private buildWindowsCommandLine (command: Command) : Result<string, ProcessError> =
        match resolveWindowsLaunch command with
        | WindowsLaunch.AsIs ->
            let parts = command.Program :: List.ofSeq command.Config.Args
            Ok(parts |> List.map quoteWindowsArg |> String.concat " ")
        | WindowsLaunch.DirectPath resolved ->
            let parts = resolved :: List.ofSeq command.Config.Args
            Ok(parts |> List.map quoteWindowsArg |> String.concat " ")
        | WindowsLaunch.BatchWrapper resolved ->
            buildBatchCommandLine command.Program resolved (List.ofSeq command.Config.Args)

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

    // Returned by SetInformationJobObject when asked to DISABLE CPU rate control (ControlFlags = 0) on a
    // Job that has none enabled — there is nothing to turn off. Treated as the desired "no CPU cap" end
    // state on the limit-replace path rather than a real failure.
    [<Literal>]
    let private ERROR_INVALID_PARAMETER = 87

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

    // Creation dispositions and the append-only access right for `Command.StdoutToFile`/`StderrToFile`.
    // CREATE_ALWAYS creates a new file, truncating an existing one (the `append = false` semantics);
    // OPEN_ALWAYS opens an existing file or creates it without truncating (the `append = true` base).
    // FILE_APPEND_DATA (WITHOUT FILE_WRITE_DATA) is Windows's O_APPEND analogue: the OS moves the file
    // pointer to end-of-file before every write on that handle, so the child's stdout/stderr genuinely
    // appends rather than overwriting from offset 0.
    [<Literal>]
    let private CREATE_ALWAYS = 2u

    [<Literal>]
    let private OPEN_ALWAYS = 4u

    [<Literal>]
    let private FILE_APPEND_DATA = 0x00000004u

    [<Literal>]
    let private FILE_ATTRIBUTE_NORMAL = 0x00000080u

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
            // If `SetHandleInformation` fails, the handle stays non-inheritable: `CreateProcessW`
            // (`bInheritHandles=true`) would silently not copy it, and the child would receive a
            // std handle that is invalid in its own process — writes to it fail silently instead of
            // reaching the null device. Close it and hand back a sentinel so the `isValidHandle`
            // gate in `setupOut` fails the spawn honestly (same pattern as `inheritableStdHandle`
            // above), rather than let a broken handle through as if it were inheritable.
            if not (SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) then
                CloseHandle handle |> ignore
                IntPtr.Zero
            else
                handle
        else
            handle

    /// An inheritable handle to the file at `path`, for `Command.StdoutToFile`/`StderrToFile` — opened on
    /// the PARENT and handed to the child straight through `STARTUPINFO` on the spawn (the child inherits
    /// its own copy; the parent's copy is dropped right after `CreateProcessW`, exactly like the pipe/NUL
    /// child handles), so the child writes the file directly with no parent pump. `append = true` opens the
    /// file append-only (`FILE_APPEND_DATA`, the O_APPEND analogue — every write goes to EOF) and creates
    /// it if absent (`OPEN_ALWAYS`); `append = false` creates a fresh file, truncating an existing one
    /// (`GENERIC_WRITE` + `CREATE_ALWAYS`). `FILE_SHARE_RW` lets a `tail -f`-style reader open it too.
    /// Returns a sentinel (`IntPtr.Zero`/`INVALID_HANDLE_VALUE`) on failure — a bad path or denied access
    /// — which the `isValidHandle` gate in `setupOut` turns into an honest `ProcessError.Spawn`, never a
    /// child handed a broken std handle (same contract as `inheritableNul`).
    let private inheritableFile (path: string) (append: bool) : nativeint =
        let access = if append then FILE_APPEND_DATA else GENERIC_WRITE

        let disposition = if append then OPEN_ALWAYS else CREATE_ALWAYS

        let handle =
            CreateFileW(path, access, FILE_SHARE_RW, IntPtr.Zero, disposition, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero)

        if isValidHandle handle then
            // As in `inheritableNul`: if `SetHandleInformation` fails the handle stays non-inheritable, so
            // `CreateProcessW` would not copy it into the child and the child's std handle would be invalid
            // in its own process. Close it and hand back a sentinel so the spawn fails honestly instead.
            if not (SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) then
                CloseHandle handle |> ignore
                IntPtr.Zero
            else
                handle
        else
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

    /// Duplicate a Job Object handle into a second, caller-owned handle to the SAME underlying Job. The
    /// Windows graceful-teardown poll (`JobObjectBackend.GracefulKillTree`) runs its liveness query and
    /// final force-kill on THIS duplicate rather than the backend's `jobHandle`, so a concurrent
    /// `HardRelease` that closes `jobHandle` mid-poll can never turn those calls into a use-after-close —
    /// nor a wrong-target `TerminateJobObject` on a handle value the OS has since recycled to an unrelated
    /// object (T-162). It mirrors the self-owned-duplicate pattern `waitWindows` already uses for a
    /// child's process handle: the duplicate refers to the same kernel object, keeps that Job alive for
    /// the bounded grace window even if the backend closes its own handle underneath the poll, and is
    /// closed when the poll concludes (at which point `KILL_ON_JOB_CLOSE` fires as the final backstop if
    /// it was the last handle). `None` when duplication fails, i.e. the source handle is already unusable.
    let duplicateJobHandle (job: nativeint) : nativeint option =
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if DuplicateHandle(current, job, current, &duplicate, 0u, false, DUPLICATE_SAME_ACCESS) then
            Some duplicate
        else
            None

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

    // ----------------------------------------------------------------------------------
    // Windows: enriched member snapshot (ProcessGroup.MembersInfo) — parent pid + image name
    // ----------------------------------------------------------------------------------

    // The ToolHelp process-snapshot flag (`CreateToolhelp32Snapshot` returns the shared module-level
    // `INVALID_HANDLE_VALUE` sentinel on failure).
    [<Literal>]
    let private TH32CS_SNAPPROCESS = 0x00000002u

    // PROCESSENTRY32W — the ToolHelp per-process record. Only `th32ProcessID`, `th32ParentProcessID`, and
    // the base image name `szExeFile` are consumed; the command line and environment are NOT part of this
    // structure at all, so the member snapshot cannot leak them. `szExeFile` is a fixed MAX_PATH (260)
    // WCHAR buffer marshalled by value; the `CharSet.Unicode` layout drives the `W` entry points below.
    [<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type private ProcessEntry32 =
        struct
            val mutable dwSize: uint32
            val mutable cntUsage: uint32
            val mutable th32ProcessID: uint32
            val mutable th32DefaultHeapID: unativeint
            val mutable th32ModuleID: uint32
            val mutable cntThreads: uint32
            val mutable th32ParentProcessID: uint32
            val mutable pcPriClassBase: int32
            val mutable dwFlags: uint32

            [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)>]
            val mutable szExeFile: string
        end

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private CreateToolhelp32Snapshot(uint32 dwFlags, uint32 th32ProcessID)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private Process32FirstW(nativeint hSnapshot, nativeint lppe)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private Process32NextW(nativeint hSnapshot, nativeint lppe)

    /// A single whole-system process snapshot as a `pid -> (parentPid, imageName)` map, or `None` when the
    /// snapshot could not be taken. One `CreateToolhelp32Snapshot` walk backs the enrichment of every
    /// member, so a group of N members costs one snapshot, not N. Never throws.
    let private snapshotProcesses () : System.Collections.Generic.Dictionary<int, int * string option> option =
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0u)

        if snapshot = nativeint INVALID_HANDLE_VALUE then
            None
        else
            let size = Marshal.SizeOf<ProcessEntry32>()
            let buffer = Marshal.AllocHGlobal size

            try
                // dwSize must be set before the first `Process32FirstW`; the API reads it and never
                // overwrites it, so it stays valid for the subsequent `Process32NextW` calls on the buffer.
                Marshal.WriteInt32(buffer, 0, size)
                let map = System.Collections.Generic.Dictionary<int, int * string option>()
                let mutable more = Process32FirstW(snapshot, buffer)

                while more do
                    let entry = Marshal.PtrToStructure<ProcessEntry32> buffer

                    // `szExeFile` is a non-null marshalled string (empty at worst); report a real image
                    // name, `None` for the empty case — never argv, which this record does not carry.
                    let exeName =
                        if String.IsNullOrEmpty entry.szExeFile then
                            None
                        else
                            Some entry.szExeFile

                    map[int entry.th32ProcessID] <- (int entry.th32ParentProcessID, exeName)
                    more <- Process32NextW(snapshot, buffer)

                Some map
            finally
                Marshal.FreeHGlobal buffer
                closeWindowsHandle snapshot

    /// Enrich the Job's member `pids` with each member's parent pid and executable image name from ONE
    /// system process snapshot, plus its OS-reported start time. A member absent from the snapshot has
    /// exited between the group's enumeration and this read and is OMITTED — never fabricated. The
    /// member's command line and environment are never read on any path (the snapshot structure does not
    /// carry them). If the snapshot itself is unavailable (a rare query failure), every member is still
    /// reported with a best-effort start time and `None` parent/image rather than dropping the whole
    /// group. Never throws.
    let readMembersInfo (pids: int list) : MemberInfo list =
        match snapshotProcesses () with
        | None ->
            // No system snapshot: parent pid and image name are honestly unavailable; keep each member
            // with a best-effort start time rather than emptying the group over a transient query failure.
            pids
            |> List.map (fun pid -> MemberInfo(pid, None, None, readProcessStartTime pid))
        | Some byPid ->
            pids
            |> List.choose (fun pid ->
                match byPid.TryGetValue pid with
                | true, (ppid, exeName) -> Some(MemberInfo(pid, Some ppid, exeName, readProcessStartTime pid))
                | false, _ ->
                    // Enumerated as a group member but not present in the whole-system snapshot: it exited
                    // between the two reads — omit it, never fabricate its metadata.
                    None)

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

    /// Whether the Job still contains any live process — the liveness predicate for the Windows
    /// graceful-teardown poll (`JobObjectBackend.GracefulKillTree`). Reads the Job accounting's
    /// active-process count. A failed query is treated as "still alive" (fail-safe): a transient
    /// accounting hiccup can then never let the poll skip its unconditional hard kill and leave a
    /// tree running.
    let jobTreeAliveWindows (job: nativeint) : bool =
        match jobStatsWindows job with
        | Some(active, _, _) -> active > 0
        | None -> true

    // ----------------------------------------------------------------------------------
    // Windows: best-effort WM_CLOSE soft close for GUI children (Electron/desktop tools)
    // ----------------------------------------------------------------------------------
    //
    // The SOFT phase of a graceful stop for a WINDOWED child: post `WM_CLOSE` to every top-level window
    // owned by a member of the Job — the standard graceful close a GUI app turns into its own shutdown
    // (a form's close handler, an app's "are you sure?" is bypassed by a plain WM_CLOSE, an Electron
    // `before-quit`), exactly what `taskkill` (without `/F`) does. It is complementary to the console
    // CTRL+BREAK path (`sendConsoleCtrlBreakWindows`), which reaches only console children started with
    // `Command.WindowsCtrlSignals()`: a GUI child has no console to CTRL+BREAK, and a console child has
    // no top-level window to WM_CLOSE, so the two mechanisms cover disjoint child classes.
    //
    // Targeted strictly by pid via `GetWindowThreadProcessId`, so — unlike a console CTRL event — it can
    // never reach a window outside the Job (no `CREATE_NEW_PROCESS_GROUP` requirement, no risk of hitting
    // the caller's own console group). That is why it is an UNCONDITIONAL addition to the soft phase for
    // every child, not a new opt-in builder: a child with no top-level window is simply a no-op, never a
    // regression. Honest and best-effort: a window may prompt/veto the close (WM_CLOSE is a request), and
    // the unconditional `TerminateJobObject` after the grace window remains the deterministic guarantee.

    [<Literal>]
    let private WM_CLOSE = 0x0010u

    // The `EnumWindows` callback (`WNDENUMPROC`) — invoked once per top-level window on the caller's
    // desktop, on the calling thread, synchronously for the duration of the `EnumWindows` call. `Winapi`
    // calling convention matches the Win32 `CALLBACK`/`__stdcall` contract. Passing a managed delegate as
    // a native callback is a standard, trim/NativeAOT-safe marshalling scenario (unlike
    // `Marshal.GetDelegateForFunctionPointer` over an arbitrary runtime type).
    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type private EnumWindowsProc = delegate of nativeint * nativeint -> bool

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool private EnumWindows(EnumWindowsProc lpEnumFunc, nativeint lParam)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern uint32 private GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool private PostMessageW(nativeint hWnd, uint32 Msg, nativeint wParam, nativeint lParam)

    /// Best-effort soft close for a Windows GUI tree: enumerate the caller's desktop top-level windows
    /// ONCE, keep those owned by a process currently in `job`, then `PostMessage(WM_CLOSE)` to each.
    /// Returns the number of windows posted to — `0` means the tree has no top-level window (a no-op,
    /// NOT an error), matching how `sendConsoleCtrlBreakWindows`/`membersWindows` honestly distinguish
    /// "nothing to signal" from "the request failed". NEVER throws: a failed member query, a failed
    /// enumeration (e.g. a session with no interactive desktop), or a failed post is just reported as
    /// zero-or-fewer windows closed, never an exception that could derail the graceful-kill path that
    /// calls it. Windows are collected first and posted to afterwards, so posting can never perturb the
    /// enumeration in flight.
    let postCloseToJobWindows (job: nativeint) : int =
        try
            let memberPids =
                match membersWindows job with
                | Ok pids -> Set.ofList pids
                | Error _ -> Set.empty

            if Set.isEmpty memberPids then
                0
            else
                let targets = ResizeArray<nativeint>()

                let collect =
                    EnumWindowsProc(fun hWnd _ ->
                        let mutable owningPid = 0u
                        GetWindowThreadProcessId(hWnd, &owningPid) |> ignore
                        // `owningPid = 0` is the documented "could not determine the owner" result — never a
                        // real pid, so it can't spuriously match a member and is skipped.
                        if owningPid <> 0u && Set.contains (int owningPid) memberPids then
                            targets.Add hWnd

                        true) // keep enumerating every remaining top-level window

                // EnumWindows walks the desktop synchronously, so `collect` is alive for the whole call;
                // `GC.KeepAlive` pins it against an over-eager collection, and its `bool` result is ignored
                // (FALSE here only signals an early stop / empty desktop — neither an error for this pass).
                EnumWindows(collect, IntPtr.Zero) |> ignore
                GC.KeepAlive collect

                for hWnd in targets do
                    // A REQUEST, not a guarantee: the window's own close handler may prompt or veto. That
                    // is why the post-grace `TerminateJobObject` is the unconditional backstop.
                    PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero) |> ignore

                targets.Count
        with _ ->
            // Best-effort by contract (see the section comment): enumeration/post failing on a host with
            // no usable desktop must never throw into the graceful-kill path — report "nothing closed" and
            // let the unconditional hard kill proceed.
            0

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
    ///
    /// This cleanly REPLACES the caps in force, so it serves both `ProcessGroup.Create` (a fresh Job)
    /// and `ProcessGroup.UpdateLimits` (a live Job): `SetInformationJobObject` overwrites the whole
    /// extended-limit block, so a dimension left `None` (its flag not set) is written back as unbounded.
    /// The CPU rate control is enabled with the hard cap when a quota is set; when it is `None` the cap
    /// is explicitly DISABLED (`ControlFlags = 0`) so an update that drops the CPU quota removes a
    /// previously-applied cap rather than silently leaving it in force — and disabling on a Job that had
    /// no CPU cap (a fresh Job, or a `None`→`None` update) reports `ERROR_INVALID_PARAMETER`, which is
    /// exactly the desired "no CPU cap" end state and so is treated as success.
    let applyWindowsJobLimits (job: nativeint) (limits: ResourceLimits) : Result<unit, string> =
        // (Re)write the Job's CPU rate control block. `controlFlags = 0` disables CPU rate control (the
        // replace-semantics "no CPU cap" state); the enable+hard-cap flags with a rate arm the cap. The
        // raw Win32 errno is returned on failure so the caller can classify it (see the `None` branch).
        let writeCpuRate (controlFlags: uint32) (rate: uint32) : Result<unit, int> =
            let mutable cpuInfo = JOBOBJECT_CPU_RATE_CONTROL_INFORMATION()
            cpuInfo.ControlFlags <- controlFlags
            cpuInfo.CpuRate <- rate
            let cpuSize = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()
            let cpuBuffer = Marshal.AllocHGlobal cpuSize

            try
                Marshal.StructureToPtr<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(cpuInfo, cpuBuffer, false)

                if SetInformationJobObject(job, JobObjectCpuRateControlInformation, cpuBuffer, uint32 cpuSize) then
                    Ok()
                else
                    // Captured inline, before the `finally` runs any further P/Invoke that could reset it.
                    Error(Marshal.GetLastWin32Error())
            finally
                Marshal.FreeHGlobal cpuBuffer

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
                | Some cores ->
                    let fraction = min 1.0 (cores / float Environment.ProcessorCount)
                    let rate = uint32 (max 1.0 (Math.Round(fraction * 10000.0)))

                    writeCpuRate (JOB_OBJECT_CPU_RATE_CONTROL_ENABLE ||| JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP) rate
                    |> Result.mapError (fun errno -> Win32Exception(errno).Message)
                | None ->
                    // Replace semantics: no CPU quota now, so disable any rate cap a prior apply set.
                    // Disabling on a Job that has none enabled is rejected with ERROR_INVALID_PARAMETER —
                    // the "no CPU cap" state already holds, so treat that as success; surface anything else.
                    match writeCpuRate 0u 0u with
                    | Ok() -> Ok()
                    | Error errno when errno = ERROR_INVALID_PARAMETER -> Ok()
                    | Error errno -> Error(Win32Exception(errno).Message)
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

    // ----------------------------------------------------------------------------------
    // Windows: ConPTY (pseudoconsole) — the opt-in `Command.Pty` mechanism
    // ----------------------------------------------------------------------------------
    //
    // Under `Command.Pty` the child's stdio is REPLACED, not extended: instead of inheriting pipe handles
    // it is attached to a pseudoconsole (a real terminal — `isatty` true) whose single MERGED output
    // stream we capture (there is no separate stderr under a tty — D3). `CreatePseudoConsole` spins up a
    // headless conhost/OpenConsole sidecar (an I/O helper process OUTSIDE the Job — an honest, documented
    // containment divergence) bound to the pseudoconsole handle; closing that handle tears the sidecar
    // down. Kill-on-dispose containment is unchanged: the child is spawned CREATE_SUSPENDED, assigned to
    // the Job while still suspended, then resumed — the proven `spawnWindowsCore` dance. (The ADR (D7)
    // preferred a PROC_THREAD_ATTRIBUTE_JOB_LIST attribute in the same list as the pseudoconsole, but that
    // empirically leaves the child on the PARENT's console rather than the pseudoconsole; the suspended->
    // assign->resume flow is D7's permitted fallback and is what `spawnWindowsPtyCore` uses.) Needs Windows
    // 10 1809 (build 17763); older hosts return a typed `ProcessError.Unsupported` (D9), probed via the
    // kernel32 export table (below) rather than a blind call that would throw `EntryPointNotFoundException`.

    [<Literal>]
    let private EXTENDED_STARTUPINFO_PRESENT = 0x00080000u

    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE is a DWORD_PTR-sized attribute id (not `[<Literal>]`-able as nativeint).
    let private PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: nativeint = nativeint 0x00020016

    [<StructLayout(LayoutKind.Sequential)>]
    type private STARTUPINFOEX =
        struct
            val mutable StartupInfo: STARTUPINFO
            val mutable lpAttributeList: nativeint
        end

    // `CreatePseudoConsole`/`ResizePseudoConsole` take a `COORD` by value. A `COORD` is two `SHORT`s — 4
    // bytes, ABI-passed exactly like a 32-bit integer — so it is marshalled as a packed `uint32` (X = cols
    // in the low word, Y = rows in the high word), sidestepping struct-by-value marshalling entirely.
    let private packCoord (cols: int) (rows: int) : uint32 =
        (uint32 (uint16 rows) <<< 16) ||| uint32 (uint16 cols)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int private CreatePseudoConsole(
        uint32 size,
        nativeint hInput,
        nativeint hOutput,
        uint32 dwFlags,
        nativeint& phPC
    )

    // The resize verb's native primitive (Stage 4 — `RunningProcess.ResizeAsync`), wrapped by
    // `resizePseudoConsole` below; takes a `COORD` by value, marshalled as the packed `uint32` (see `packCoord`).
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int private ResizePseudoConsole(nativeint hPC, uint32 size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern void private ClosePseudoConsole(nativeint hPC)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private InitializeProcThreadAttributeList(
        nativeint lpAttributeList,
        uint32 dwAttributeCount,
        uint32 dwFlags,
        nativeint& lpSize
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private UpdateProcThreadAttribute(
        nativeint lpAttributeList,
        uint32 dwFlags,
        nativeint attribute,
        nativeint lpValue,
        nativeint cbSize,
        nativeint lpPreviousValue,
        nativeint lpReturnSize
    )

    [<DllImport("kernel32.dll")>]
    extern void private DeleteProcThreadAttributeList(nativeint lpAttributeList)

    // A second binding of `CreateProcessW` whose `lpStartupInfo` is a `STARTUPINFOEX&` (for
    // EXTENDED_STARTUPINFO_PRESENT + the attribute list). Distinct F# name, same entry point.
    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")>]
    extern bool private CreateProcessExtended(
        nativeint lpApplicationName,
        string lpCommandLine,
        nativeint lpProcessAttributes,
        nativeint lpThreadAttributes,
        bool bInheritHandles,
        uint32 dwCreationFlags,
        nativeint lpEnvironment,
        string lpCurrentDirectory,
        STARTUPINFOEX& lpStartupInfo,
        PROCESS_INFORMATION& lpProcessInformation
    )

    /// Whether this Windows build exposes ConPTY (`CreatePseudoConsole` arrived in Windows 10 1809 / build
    /// 17763). Probed once via the kernel32 export table — never a blind call that would throw
    /// `EntryPointNotFoundException` on a pre-1809 host — so `Command.Pty` there fails with a typed
    /// `ProcessError.Unsupported` (D9), never a silent pipe fallback.
    let private conptyAvailability =
        lazy
            (try
                let handle = NativeLibrary.Load "kernel32.dll"
                let mutable export = IntPtr.Zero
                NativeLibrary.TryGetExport(handle, "CreatePseudoConsole", &export)
             with _ ->
                 // Failing to even load kernel32 / probe its exports is treated as "no ConPTY".
                 false)

    let conptyAvailable () : bool = conptyAvailability.Value

    /// Close the pseudoconsole `hPC` — tearing down the conhost sidecar (which lives OUTSIDE the Job) —
    /// once the child `hProcess` exits. Closing the pseudoconsole also closes the merged-output pipe's
    /// write end, so the parent's read reaches EOF and the capture/streaming pumps conclude (they never
    /// would while conhost holds that write handle open). Waits on our OWN duplicate of the process handle
    /// (the backend may close its copy on reap) via a thread-pool registered wait — one pool wait thread
    /// serves ~63 handles, so no dedicated thread is parked per PTY child. `hPC` is closed exactly once, by
    /// this one-shot wait — never elsewhere (no double-close). Returns `false` only if the initial handle
    /// duplication fails (near-impossible for a just-created process).
    let private closePseudoConsoleOnChildExit (hProcess: nativeint) (hPC: nativeint) : bool =
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if not (DuplicateHandle(current, hProcess, current, &duplicate, 0u, false, DUPLICATE_SAME_ACCESS)) then
            false
        else
            let waitHandle =
                new OwnedProcessWait(new SafeWaitHandle(duplicate, ownsHandle = true))

            let tcs = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            // Fires once, when the child exits: close the pseudoconsole (conhost flushes its final output
            // to the pipe, then exits), then let the continuation unregister the wait and release the
            // duplicate. Publishing the registration before attaching the continuation makes the unregister
            // race-free even if the child had already exited when we registered (mirrors `waitWindows`).
            let callback =
                WaitOrTimerCallback(fun _ _ ->
                    ClosePseudoConsole hPC
                    tcs.TrySetResult() |> ignore)

            let registration =
                ThreadPool.RegisterWaitForSingleObject(waitHandle, callback, null, -1, true)

            tcs.Task.ContinueWith(fun (_: Task) ->
                registration.Unregister null |> ignore
                waitHandle.Dispose()) // disposes the SafeWaitHandle -> closes our duplicate
            |> ignore

            true

    /// Resize the pseudoconsole `hPC` (retained from spawn as `Spawned.PtyControl`) to `cols` x `rows` —
    /// the Windows arm of `RunningProcess.ResizeAsync` (Stage 4 / D6). Reuses the SAME packed-`COORD`
    /// encoding `CreatePseudoConsole` already uses (X = cols in the low word, Y = rows in the high word),
    /// so the resize geometry marshals identically to the initial geometry. `ResizePseudoConsole` returns
    /// an HRESULT; a non-zero value — e.g. the pseudoconsole was already closed when the child exited — is
    /// surfaced as a typed `ProcessError.Io`, never a silent success. The geometry is validated positive
    /// and `SHORT`-bounded by the caller (`RunningProcess.ResizeAsync`), matching the `Command.Pty` builder.
    let resizePseudoConsole (hPC: nativeint) (cols: int) (rows: int) : Result<unit, ProcessError> =
        let hr = ResizePseudoConsole(hPC, packCoord cols rows)

        if hr = 0 then
            Ok()
        else
            // A double-quoted format inside an interpolation hole is FS3373 (KB K-026) — bind first.
            let hrHex = hr.ToString("X8")
            Error(ProcessError.Io $"ResizePseudoConsole failed (HRESULT 0x{hrHex})")

    /// Spawn `command` attached to a Windows pseudoconsole (ConPTY) — see the ConPTY section comment above.
    /// The STARTUPINFOEX attribute list carries ONLY the pseudoconsole; Job membership (kill-on-dispose
    /// containment) is achieved the proven `spawnWindowsCore` way — CREATE_SUSPENDED, AssignProcessToJobObject
    /// while still suspended, then ResumeThread — NOT via a PROC_THREAD_ATTRIBUTE_JOB_LIST attribute (the ADR's
    /// D7-preferred form empirically leaves the child on the PARENT's console; suspended->assign->resume is
    /// D7's permitted fallback), so containment is unchanged. Returns the process handle, the parent-side
    /// MERGED output read stream (`Spawned.Stdout`; `Stderr` is always `None` under a PTY — one terminal
    /// stream, D3), and, when kept, the pty master input stream for interactive stdin.
    let private spawnWindowsPtyCore
        (job: nativeint)
        (command: Command)
        (pty: PtyConfig)
        : Result<Spawned, ProcessError> =
        let config = command.Config
        // `InheritStdin` has no meaning under a PTY (the child's stdin is the pty master, not the parent's
        // console), so the input write end is kept only for a feeder source or `KeepStdinOpen`.
        let stdinInherit = Stdin.isInherit config.StdinSource

        let stdinPipeKept =
            (config.StdinSource.IsSome && not stdinInherit) || config.KeepStdinOpen

        // Every parent/child pipe end created, torn down (best-effort, reverse order) if setup fails.
        let createdPipes = ResizeArray<IDisposable>()

        let disposeCreatedPipes () =
            for i in createdPipes.Count - 1 .. -1 .. 0 do
                try
                    createdPipes[i].Dispose()
                with _ ->
                    // Best-effort unwind after an earlier failure; the original failure is what we report.
                    ()

        // The pseudoconsole handle, set once created; cleared once its ownership is handed to the exit-wait
        // (success) or it has been closed on an error branch — read by the outer `with` so an exception
        // after CreatePseudoConsole succeeds still tears the sidecar down rather than leaking it.
        let mutable pendingPseudoConsole = IntPtr.Zero

        // Decide the launch (PATHEXT substitution / cmd.exe batch wrapper — T-181) and build the command
        // line up front: an unsafe batch argument is refused here, BEFORE the pseudoconsole or any pipe is
        // allocated, so a refusal leaks nothing.
        match buildWindowsCommandLine command with
        | Error error -> Error error
        | Ok commandLine ->

            try
                // Two async pipe pairs. Parent keeps the input WRITE end (pty master stdin) and the output READ
                // end (the single merged terminal stream); the child-side ends are handed to CreatePseudoConsole
                // and closed once it has duplicated them into the conhost sidecar.
                let inServer, inClient = createAsyncPipePair PipeDirection.Out
                createdPipes.Add inServer
                createdPipes.Add inClient
                let outServer, outClient = createAsyncPipePair PipeDirection.In
                createdPipes.Add outServer
                createdPipes.Add outClient

                let mutable hPC = IntPtr.Zero

                let hr =
                    CreatePseudoConsole(
                        packCoord pty.Cols pty.Rows,
                        inClient.SafePipeHandle.DangerousGetHandle(),
                        outClient.SafePipeHandle.DangerousGetHandle(),
                        0u,
                        &hPC
                    )

                if hr <> 0 then
                    disposeCreatedPipes ()
                    let hrHex = hr.ToString("X8")
                    Error(ProcessError.Spawn(command.Program, $"CreatePseudoConsole failed (HRESULT 0x{hrHex})"))
                else
                    pendingPseudoConsole <- hPC
                    // ConPTY duplicated the child-side ends into the sidecar; drop our copies so only the parent
                    // ends (kept below) remain. (Still listed in `createdPipes`; `Stream.Dispose` is safe to
                    // call twice, so a later failure's unwind is harmless.)
                    inClient.Dispose()
                    outClient.Dispose()

                    // A STARTUPINFOEX attribute list carrying ONE attribute: the pseudoconsole. Containment
                    // (Job membership) is done the proven way — CREATE_SUSPENDED, AssignProcessToJobObject while
                    // still suspended, then resume — NOT via a PROC_THREAD_ATTRIBUTE_JOB_LIST in the same list.
                    // The ADR (D7) preferred the job-list attribute, but empirically a job-list attribute
                    // alongside PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE leaves the child attached to the PARENT's
                    // console instead of the pseudoconsole — its output then escapes the captured merged stream.
                    // The suspended->assign->resume flow is D7's explicitly-permitted fallback: it keeps the
                    // kill-on-dispose guarantee intact (the child is contained before it runs a single
                    // instruction) and composes cleanly with the pseudoconsole, exactly as `spawnWindowsCore`.
                    let mutable listSize = IntPtr.Zero
                    // First call sizes the list (returns FALSE with ERROR_INSUFFICIENT_BUFFER — expected).
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1u, 0u, &listSize) |> ignore
                    let attrList = Marshal.AllocHGlobal listSize

                    // Release the initialized attribute list (post-CreateProcess or on error).
                    let cleanupInitializedScratch () =
                        DeleteProcThreadAttributeList attrList
                        Marshal.FreeHGlobal attrList

                    if not (InitializeProcThreadAttributeList(attrList, 1u, 0u, &listSize)) then
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                        // The list was never initialized — free the raw buffer WITHOUT DeleteProcThreadAttributeList.
                        Marshal.FreeHGlobal attrList
                        ClosePseudoConsole hPC
                        pendingPseudoConsole <- IntPtr.Zero
                        disposeCreatedPipes ()

                        Error(
                            ProcessError.Spawn(command.Program, $"InitializeProcThreadAttributeList failed: {message}")
                        )
                    // PSEUDOCONSOLE's value is the HPCON handle itself (passed by value in the lpValue slot),
                    // cbSize = pointer size.
                    elif
                        not (
                            UpdateProcThreadAttribute(
                                attrList,
                                0u,
                                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                hPC,
                                nativeint IntPtr.Size,
                                IntPtr.Zero,
                                IntPtr.Zero
                            )
                        )
                    then
                        let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                        cleanupInitializedScratch ()
                        ClosePseudoConsole hPC
                        pendingPseudoConsole <- IntPtr.Zero
                        disposeCreatedPipes ()
                        Error(ProcessError.Spawn(command.Program, $"UpdateProcThreadAttribute failed: {message}"))
                    else
                        let mutable startup = STARTUPINFOEX()
                        startup.StartupInfo.cb <- Marshal.SizeOf<STARTUPINFOEX>()
                        // Deliberately NO STARTF_USESTDHANDLES and no std handles — a ConPTY child's std handles
                        // come from the pseudoconsole. The fundamental divergence from the pipe path.
                        startup.lpAttributeList <- attrList

                        let mutable info = PROCESS_INFORMATION()

                        let workingDirectory =
                            config.WorkingDirectory |> Option.defaultWith Directory.GetCurrentDirectory

                        let environment = buildWindowsEnvironment command

                        let flags =
                            // Spawn SUSPENDED so the child is assigned to the Job before it runs (proven
                            // containment). EXTENDED_STARTUPINFO_PRESENT selects the STARTUPINFOEX form.
                            EXTENDED_STARTUPINFO_PRESENT
                            ||| CREATE_SUSPENDED
                            ||| (if environment = IntPtr.Zero then
                                     0u
                                 else
                                     CREATE_UNICODE_ENVIRONMENT)
                            ||| (if config.CreateNoWindow then CREATE_NO_WINDOW else 0u)
                            ||| (if config.WindowsCtrlSignals then
                                     CREATE_NEW_PROCESS_GROUP
                                 else
                                     0u)
                            ||| (match config.Priority with
                                 | Some priority -> PriorityMapping.windowsCreationFlag priority
                                 | None -> 0u)

                        let created =
                            CreateProcessExtended(
                                IntPtr.Zero,
                                commandLine,
                                IntPtr.Zero,
                                IntPtr.Zero,
                                // A ConPTY child inherits no handles; its stdio comes from the pseudoconsole.
                                false,
                                flags,
                                environment,
                                workingDirectory,
                                &startup,
                                &info
                            )

                        let lastError = Marshal.GetLastWin32Error()

                        if environment <> IntPtr.Zero then
                            Marshal.FreeHGlobal environment

                        cleanupInitializedScratch ()

                        if not created then
                            ClosePseudoConsole hPC
                            pendingPseudoConsole <- IntPtr.Zero
                            disposeCreatedPipes ()

                            if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                                Error(notFoundFromSpawnFailure command.Program)
                            else
                                Error(ProcessError.Spawn(command.Program, Win32Exception(lastError).Message))
                        elif not (AssignProcessToJobObject(job, info.hProcess)) then
                            // Suspended but uncontained — kill it rather than let it run free (mirrors
                            // `spawnWindowsCore`), and tear down the pseudoconsole + pipes.
                            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                            TerminateProcess(info.hProcess, 1u) |> ignore
                            CloseHandle info.hThread |> ignore
                            CloseHandle info.hProcess |> ignore
                            ClosePseudoConsole hPC
                            pendingPseudoConsole <- IntPtr.Zero
                            disposeCreatedPipes ()

                            Error(
                                ProcessError.Spawn(
                                    command.Program,
                                    $"could not assign process to job object: {message}"
                                )
                            )
                        elif resumeThreadHook info.hThread = UInt32.MaxValue then
                            // `ResumeThread` returned its `(DWORD)-1` failure sentinel: the child is contained
                            // but stuck SUSPENDED and would never run. Kill it and report honestly.
                            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                            TerminateProcess(info.hProcess, 1u) |> ignore
                            CloseHandle info.hThread |> ignore
                            CloseHandle info.hProcess |> ignore
                            ClosePseudoConsole hPC
                            pendingPseudoConsole <- IntPtr.Zero
                            disposeCreatedPipes ()

                            Error(
                                ProcessError.Spawn(command.Program, $"could not resume the suspended child: {message}")
                            )
                        else
                            CloseHandle info.hThread |> ignore

                            if not (closePseudoConsoleOnChildExit info.hProcess hPC) then
                                // Near-impossible (duplicating a just-created process handle failed): fail
                                // honestly rather than leak the conhost sidecar. The child is a Job member, so
                                // terminate it, close the pseudoconsole, and release the pipes.
                                ClosePseudoConsole hPC
                                pendingPseudoConsole <- IntPtr.Zero
                                TerminateProcess(info.hProcess, 1u) |> ignore
                                CloseHandle info.hProcess |> ignore
                                disposeCreatedPipes ()

                                Error(
                                    ProcessError.Spawn(
                                        command.Program,
                                        "could not register the pseudoconsole teardown wait"
                                    )
                                )
                            else
                                // Ownership of `hPC` is now the exit-wait's; it closes it exactly once.
                                pendingPseudoConsole <- IntPtr.Zero

                                let stdinStream =
                                    if stdinPipeKept then
                                        Some(inServer :> Stream)
                                    else
                                        // No feeder/interactive writer: close the pty master input end.
                                        inServer.Dispose()
                                        None

                                Ok
                                    { Handle = info.hProcess
                                      // One merged terminal stream (D3): stdout carries all output, no stderr.
                                      Stdout = Some(outServer :> Stream)
                                      Stderr = None
                                      Stdin = stdinStream
                                      WindowsCtrlGroup = config.WindowsCtrlSignals
                                      // Retain the pseudoconsole handle so `RunningProcess.ResizeAsync` can
                                      // `ResizePseudoConsole` it (Stage 4 / D6). The exit-wait still owns closing it
                                      // exactly once on child exit; a resize after that returns a typed error, never
                                      // a crash. Its value stays valid for the child's whole running lifetime.
                                      PtyControl = Some hPC }
            with ex ->
                if pendingPseudoConsole <> IntPtr.Zero then
                    ClosePseudoConsole pendingPseudoConsole

                disposeCreatedPipes ()
                Error(ProcessError.Spawn(command.Program, ex.Message))

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

        // Decide the launch (PATHEXT substitution / cmd.exe batch wrapper — T-181) and build the command
        // line up front: an unsafe batch argument is refused here, BEFORE any pipe/handle is allocated.
        match buildWindowsCommandLine command with
        | Error error -> Error error
        | Ok commandLine ->

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
                // after spawn (the child has its own inherited copy by then). `fileRedirect` (`Command.
                // StdoutToFile`/`StderrToFile`) takes precedence over `mode`: the child is handed an
                // inheritable file handle directly, so there is no parent read stream (`None`, like NUL) and
                // the file lives beyond the parent — the builder already rejected combining it with the
                // parent-side observation knobs.
                let setupOut
                    (fileRedirect: (string * bool) option)
                    (mode: StdioMode)
                    (stdHandleId: int)
                    : nativeint * Stream option * (unit -> unit) =
                    match fileRedirect with
                    | Some(path, append) ->
                        let handle = inheritableFile path append

                        if not (isValidHandle handle) then
                            // A bad redirect path / denied access is validated at the source, before it could
                            // reach `STARTUPINFO.hStdOutput`/`hStdError`; the outer `with` turns this into an
                            // honest `ProcessError.Spawn` rather than a child handed a broken handle.
                            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
                            failwith $"could not open the redirect file '{path}' for the child's output: {message}"

                        // Registered in the unwind list immediately (like the NUL branch): if a LATER step in
                        // this spawn throws, this handle has not been handed to a child yet and must not leak.
                        createdPipes.Add(disposableHandle handle)
                        handle, None, (fun () -> closeHandleIfValid handle)
                    | None ->
                        match mode with
                        | StdioMode.Piped ->
                            let server, client = createAsyncPipePair PipeDirection.In
                            createdPipes.Add server
                            createdPipes.Add client

                            client.SafePipeHandle.DangerousGetHandle(),
                            Some(server :> Stream),
                            (fun () -> client.Dispose())
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

                                failwith
                                    $"could not duplicate an inheritable copy of the parent's std handle: {message}"

                            createdPipes.Add(disposableHandle handle)
                            handle, None, (fun () -> closeHandleIfValid handle)

                let outChild, outStream, outCleanup =
                    setupOut config.StdoutFile config.StdoutMode STD_OUTPUT_HANDLE

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
                        setupOut config.StderrFile config.StderrMode STD_ERROR_HANDLE

                let mutable startup = STARTUPINFO()
                startup.cb <- Marshal.SizeOf<STARTUPINFO>()
                startup.dwFlags <- STARTF_USESTDHANDLES
                startup.hStdInput <- stdinChild
                startup.hStdOutput <- outChild
                startup.hStdError <- errChild

                let mutable info = PROCESS_INFORMATION()

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
                          WindowsCtrlGroup = config.WindowsCtrlSignals
                          // Not a PTY run — no pseudoconsole to resize (`ResizeAsync` → typed Unsupported).
                          PtyControl = None }
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
        | None, None, None, false, None ->
            match config.Pty with
            | Some pty ->
                // ConPTY needs Windows 10 1809+; probe the export rather than blind-calling so a pre-1809
                // host is a typed `ProcessError.Unsupported`, never a silent pipe downgrade (D9). The spawn
                // takes `windowsSpawnLock` for the same reason the pipe path does: a concurrent pipe spawn
                // with `bInheritHandles = true` must not snapshot this path's inheritable pipe-client ends
                // in its own child (this path itself passes `bInheritHandles = false`).
                if not (conptyAvailable ()) then
                    Error(ProcessError.Unsupported "Pty (needs Windows 10 1809+ / ConPTY)")
                else
                    lock windowsSpawnLock (fun () -> spawnWindowsPtyCore job command pty)
            | None -> lock windowsSpawnLock (fun () -> spawnWindowsCore job command)
